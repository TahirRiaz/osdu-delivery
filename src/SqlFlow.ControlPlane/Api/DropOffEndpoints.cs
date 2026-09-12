using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// One file in a drop-off, as it landed. <c>HashSource</c> says where <c>Sha256</c> came from: <c>computed</c> when the
/// control plane hashed the bytes as they streamed past it, <c>client</c> when the uploader asserted it about a file
/// written straight to storage, and <c>none</c> when a signed upload asserted nothing. The distinction is kept because a
/// hash the control plane did not compute is a claim, and a ledger that showed both the same way would imply a check
/// that never happened.
/// </summary>
public sealed record DeliveryDropOffFileDto(string Name, long Bytes, string Sha256, string HashSource = DropOffHashSource.Computed);

/// <summary>
/// One drop-off: files uploaded into the deployment's drop-off area, for a submission to point at afterwards.
/// <c>Location</c> is what goes into a submission's <c>files</c>. <c>UploadMode</c> is how the bytes got there
/// (<c>stream</c> through the control plane, <c>signed</c> straight to storage), and <c>ReservedUntilUtc</c> is when a
/// signed reservation's URLs stop working.
/// </summary>
public sealed record DeliveryDropOffDto(
    Guid DropOffId, string Location, string Status, int FileCount, long TotalBytes, string? Label,
    DateTime UploadedUtc, string UploadedBy, DateTime? CompletedUtc, DateTime? DeletedUtc, string? Error,
    IReadOnlyList<DeliveryDropOffFileDto> Files, string UploadMode = DropOffUploadMode.Stream, DateTime? ReservedUntilUtc = null);

/// <summary>
/// Whether this deployment offers a drop-off area at all, and what one upload may carry. <c>Location</c> is the root
/// uploads land under, which is also what a submission may point inside; null when the deployment configures none.
/// <c>SignedUploads</c> says whether a caller can be handed URLs to write straight to storage, which is what a file too
/// large to send through the control plane needs; where it is false, every upload goes through the control plane and is
/// bounded by <c>MaxFileMegabytes</c>.
/// </summary>
public sealed record DeliveryDropOffAreaDto(
    bool Enabled, string? Location, int MaxFileMegabytes, int MaxFilesPerUpload, int RetentionDays,
    bool SignedUploads = false, int MaxSignedFileGigabytes = 0, int SignedUploadExpiryMinutes = 0);

/// <summary>One file a caller asks to upload itself: its name, and how large it will be.</summary>
public sealed record DeliveryDropOffReserveFile(string? Name, long Bytes);

/// <summary>
/// A request to upload files straight into the drop-off area rather than through the control plane. The answer carries
/// one write-only URL per file; the caller writes each one and then completes the reservation.
/// </summary>
public sealed record DeliveryDropOffReserveRequest(IReadOnlyList<DeliveryDropOffReserveFile>? Files, string? Label);

/// <summary>One file's write-only URL. The URL carries its own credential and is never stored or logged.</summary>
public sealed record DeliveryDropOffUploadDto(string Name, string Location, string Url, DateTime ExpiresUtc);

/// <summary>
/// A reservation: the drop-off it will become, and the URL to write each file to. Nothing has landed yet, and the
/// drop-off is not usable by a submission until the caller completes it.
/// </summary>
public sealed record DeliveryDropOffReservationDto(
    Guid DropOffId, string Location, string Status, string? Label, DateTime UploadedUtc, string UploadedBy,
    DateTime ReservedUntilUtc, IReadOnlyList<DeliveryDropOffUploadDto> Uploads);

/// <summary>One file a caller reports having uploaded, with the content hash it computed while writing it, if any.</summary>
public sealed record DeliveryDropOffCompleteFile(string? Name, string? Sha256);

/// <summary>
/// The caller reporting that a reservation's files are written. What actually landed is read from storage and is what
/// the ledger records; the hashes here are the uploader's own, taken as asserted.
/// </summary>
public sealed record DeliveryDropOffCompleteRequest(IReadOnlyList<DeliveryDropOffCompleteFile>? Files);

/// <summary>
/// The drop-off area (docs/delivery/submitting-records.md): a pre-step to a submission, where files are uploaded to a
/// place the compute node can read, and a submission afterwards points at what landed. The files go past the control
/// plane into storage as they arrive, never into the catalog, and the delivery itself still streams them from storage
/// to OSDU without passing through here.
/// <para>
/// Where uploads land is named by <c>SQLFLOW_DROPOFF_ROOT</c>, read by the control plane (which writes there) and by
/// the node (which reads there, and re-checks that a record points somewhere it is allowed to). One name on both sides
/// is what keeps the two from disagreeing; with it unset this surface answers that no drop-off area is configured.
/// </para>
/// </summary>
public static class DropOffEndpoints
{
    /// <summary>The most drop-offs one listing answers with.</summary>
    public const int MaxListed = 200;

    /// <summary>The longest name a file may carry, so a listing and the ledger row stay readable.</summary>
    public const int MaxFileNameLength = 260;

    public static RouteGroupBuilder MapDropOffReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var dropOffs = group.MapGroup("/delivery").WithTags("Delivery");
        dropOffs.MapGet("/dropoff-area", GetArea).WithName("GetDeliveryDropOffArea");
        dropOffs.MapGet("/dropoffs", ListAsync).WithName("ListDeliveryDropOffs");
        dropOffs.MapGet("/dropoffs/{dropOffId:guid}", GetAsync).WithName("GetDeliveryDropOff");
        return group;
    }

    public static RouteGroupBuilder MapDropOffWriteEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var dropOffs = group.MapGroup("/delivery").WithTags("Delivery");
        // The files ride in the body, so this route reads a far larger body than any other and no larger than what the
        // deployment allows. Antiforgery is disabled deliberately: this is a bearer-token API with no cookie
        // authentication, so there is no cross-site request to forge, and form binding would otherwise demand the
        // antiforgery middleware.
        dropOffs.MapPost("/dropoffs", UploadAsync).WithName("UploadDeliveryDropOff").DisableAntiforgery();
        dropOffs.MapPost("/dropoffs/reserve", ReserveAsync).WithName("ReserveDeliveryDropOff");
        dropOffs.MapPost("/dropoffs/{dropOffId:guid}/complete", CompleteAsync).WithName("CompleteDeliveryDropOff");
        dropOffs.MapDelete("/dropoffs/{dropOffId:guid}", DeleteAsync).WithName("DeleteDeliveryDropOff");
        return group;
    }

    /// <summary>What a caller needs to know before uploading: whether there is anywhere to upload to, and the ceilings.</summary>
    private static Ok<DeliveryDropOffAreaDto> GetArea(IOptions<ControlPlaneOptions> options, EngineContext engine)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engine);
        var dropOff = options.Value.DropOff;
        var root = PayloadRoots.DropOffRoot();
        var signed = root is not null && engine.Stores.CanSignUpload(root);
        return TypedResults.Ok(new DeliveryDropOffAreaDto(
            root is not null, root, dropOff.MaxFileMegabytes, dropOff.MaxFilesPerUpload, dropOff.RetentionDays,
            signed, signed ? dropOff.MaxSignedFileGigabytes : 0, signed ? dropOff.SignedUploadExpiryMinutes : 0));
    }

    /// <summary>The drop-offs, newest first. A deleted one stays on the list, saying when it went, because the ledger says what happened.</summary>
    private static async Task<Ok<IReadOnlyList<DeliveryDropOffDto>>> ListAsync(
        string? status, string? search, int? limit, CatalogDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var query = db.DeliveryDropOffs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status))
        {
            var wanted = status.Trim();
            query = query.Where(d => d.Status == wanted);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(d => (d.Label != null && d.Label.Contains(term)) || d.Location.Contains(term) || d.UploadedBy.Contains(term));
        }

        var rows = await query
            .OrderByDescending(d => d.UploadedUtc)
            .Take(Math.Clamp(limit ?? MaxListed, 1, MaxListed))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<DeliveryDropOffDto>>(rows.Select(Dto).ToList());
    }

    private static async Task<Results<Ok<DeliveryDropOffDto>, ProblemHttpResult>> GetAsync(
        Guid dropOffId, CatalogDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await db.DeliveryDropOffs.AsNoTracking().FirstOrDefaultAsync(d => d.DropOffId == dropOffId, ct).ConfigureAwait(false);
        return row is null
            ? TypedResults.Problem(detail: $"No drop-off has the id {dropOffId:D}.", statusCode: StatusCodes.Status404NotFound, title: "Not found")
            : TypedResults.Ok(Dto(row));
    }

    /// <summary>
    /// Takes the files of one upload into the drop-off area. The row is written before the first byte and completed
    /// after the last, so an upload interrupted halfway is visible as what it is rather than as a silent gap, and the
    /// files that did land can be dealt with by hand. Each file's content hash is computed as it streams past, so the
    /// submission that points at them can carry it without reading them again.
    /// </summary>
    private static async Task<Results<Ok<DeliveryDropOffDto>, ProblemHttpResult>> UploadAsync(
        HttpRequest request, CatalogDbContext db, EngineContext engine, IOptions<ControlPlaneOptions> options,
        TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        var limits = options.Value.DropOff;
        if (PayloadRoots.DropOffRoot() is not { } root)
        {
            return Invalid(
                $"This deployment configures no drop-off area, so there is nowhere to upload to. Set {PayloadRoots.DropOffEnvironmentVariable} on the control plane and on every node to a location both can reach, the control plane to write and the nodes to read.",
                "No drop-off area");
        }

        if (!request.HasFormContentType)
        {
            return Invalid("Upload the files as multipart/form-data. Every file part is taken; an optional 'label' field names the drop-off.");
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // A body that is not a readable multipart payload (no parts at all, a truncated upload) is the caller's
            // mistake, not the server's: it is refused here rather than escaping as a 500.
            return Invalid($"The upload could not be read as multipart/form-data: {SecretHygiene.RedactedMessage(ex)}");
        }

        var files = form.Files;
        if (files.Count == 0)
        {
            return Invalid("The upload carries no files.");
        }

        if (files.Count > limits.MaxFilesPerUpload)
        {
            return Invalid(string.Create(CultureInfo.InvariantCulture, $"One upload carries at most {limits.MaxFilesPerUpload} files; this one carries {files.Count}."));
        }

        var maxBytes = limits.MaxFileMegabytes * 1024L * 1024L;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (NameRefusal(file.FileName) is { } refusal)
            {
                return Invalid(refusal);
            }

            if (file.Length > maxBytes)
            {
                return Invalid(string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{file.FileName}' is {file.Length} bytes; one file is at most {limits.MaxFileMegabytes} MB. Prepare a set that large as a drop instead."));
            }

            if (!names.Add(file.FileName))
            {
                return Invalid($"'{file.FileName}' is uploaded twice; each file in a drop-off has its own name.");
            }
        }

        var label = form.TryGetValue("label", out var given) ? given.ToString() : null;
        if (LabelRefusal(label) is { } labelRefusal)
        {
            return Invalid(labelRefusal);
        }

        var dropOffId = Guid.CreateVersion7();
        var location = FileStoreRegistry.Join(root, dropOffId.ToString("D"));
        var now = clock.GetUtcNow().UtcDateTime;
        var row = new DeliveryDropOff
        {
            DropOffId = dropOffId,
            Location = location,
            Status = DropOffStatus.Uploading,
            UploadMode = DropOffUploadMode.Stream,
            FileCount = files.Count,
            TotalBytes = 0,
            FilesJson = "[]",
            Label = SubmissionReference.Normalize(label),
            UploadedUtc = now,
            UploadedBy = RequestActor.Label(user),
        };
        db.DeliveryDropOffs.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var landed = new List<DeliveryDropOffFileDto>(files.Count);
        try
        {
            foreach (var file in files)
            {
                var target = FileStoreRegistry.Join(location, file.FileName);
                await using var source = file.OpenReadStream();
                var (bytes, hash) = await CopyAsync(engine.Stores, target, source, maxBytes, ct).ConfigureAwait(false);
                landed.Add(new DeliveryDropOffFileDto(file.FileName, bytes, hash, DropOffHashSource.Computed));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // What landed before the failure stays where it is: it is recorded here, and removing files nobody asked
            // to remove is not this endpoint's call. The row says the upload failed, so the area never looks complete.
            row.Status = DropOffStatus.Failed;
            row.FileCount = landed.Count;
            row.TotalBytes = landed.Sum(f => f.Bytes);
            row.FilesJson = JsonSerializer.Serialize(landed);
            row.Error = SecretHygiene.RedactedMessage(ex);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return TypedResults.Problem(
                detail: $"The upload failed after {landed.Count} of {files.Count} file(s): {row.Error}",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Upload failed");
        }

        row.Status = DropOffStatus.Complete;
        row.FileCount = landed.Count;
        row.TotalBytes = landed.Sum(f => f.Bytes);
        row.FilesJson = JsonSerializer.Serialize(landed);
        row.CompletedUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(Dto(row));
    }

    /// <summary>
    /// Reserves a drop-off the caller uploads into itself: the row is written first, then one write-only URL per file is
    /// handed out. Nothing has landed when this answers, and the drop-off is not complete (so not something a submission
    /// should point at) until <see cref="CompleteAsync"/> has read back what actually arrived.
    /// <para>
    /// This exists for the files a streamed upload cannot reasonably carry. The bytes never touch the control plane, so
    /// nothing here can hash them; the caller's own hash is taken as asserted and recorded as such.
    /// </para>
    /// </summary>
    private static async Task<Results<Ok<DeliveryDropOffReservationDto>, ProblemHttpResult>> ReserveAsync(
        DeliveryDropOffReserveRequest request, CatalogDbContext db, EngineContext engine, IOptions<ControlPlaneOptions> options,
        TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        var limits = options.Value.DropOff;
        if (PayloadRoots.DropOffRoot() is not { } root)
        {
            return Invalid(
                $"This deployment configures no drop-off area, so there is nowhere to upload to. Set {PayloadRoots.DropOffEnvironmentVariable} on the control plane and on every node to a location both can reach, the control plane to write and the nodes to read.",
                "No drop-off area");
        }

        if (!engine.Stores.CanSignUpload(root))
        {
            return Invalid(
                $"The drop-off area '{root}' cannot hand out upload URLs; signed uploads are issued for Azure Storage locations. Upload the files through this API instead (POST /dropoffs), which takes files up to {limits.MaxFileMegabytes} MB each.",
                "Signed uploads unavailable");
        }

        if (request?.Files is not { Count: > 0 } requested)
        {
            return Invalid("A reservation names the files it will upload: files is a non-empty list of { name, bytes }.");
        }

        if (requested.Count > limits.MaxFilesPerUpload)
        {
            return Invalid(string.Create(CultureInfo.InvariantCulture, $"One reservation carries at most {limits.MaxFilesPerUpload} files; this one names {requested.Count}."));
        }

        var maxBytes = limits.MaxSignedFileGigabytes * 1024L * 1024L * 1024L;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in requested)
        {
            if (NameRefusal(file.Name) is { } refusal)
            {
                return Invalid(refusal);
            }

            if (file.Bytes <= 0)
            {
                return Invalid(string.Create(CultureInfo.InvariantCulture, $"'{file.Name}' declares {file.Bytes} bytes; a reservation says how large each file will be, and an empty file has nothing to deliver."));
            }

            if (file.Bytes > maxBytes)
            {
                return Invalid(string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{file.Name}' declares {file.Bytes} bytes; one reserved file is at most {limits.MaxSignedFileGigabytes} GB. Prepare a set that large as a drop instead."));
            }

            if (!names.Add(file.Name!))
            {
                return Invalid($"'{file.Name}' is named twice; each file in a drop-off has its own name.");
            }
        }

        if (LabelRefusal(request.Label) is { } labelRefusal)
        {
            return Invalid(labelRefusal);
        }

        var dropOffId = Guid.CreateVersion7();
        var location = FileStoreRegistry.Join(root, dropOffId.ToString("D"));
        var now = clock.GetUtcNow().UtcDateTime;
        var lifetime = TimeSpan.FromMinutes(limits.SignedUploadExpiryMinutes);
        var reservedUntil = now + lifetime;

        // The row goes in before a single URL is handed out. A reservation nobody ever completes is then visible as an
        // abandoned upload rather than as files in the area that no row accounts for.
        // The declared files are recorded now, so completion can hold what landed against what was reserved instead of
        // taking the caller's second word for it. While the row is uploading these are expectations, not facts, which is
        // exactly what its status says; completion replaces them with what storage actually holds.
        var declared = requested
            .Select(f => new DeliveryDropOffFileDto(f.Name!, f.Bytes, string.Empty, DropOffHashSource.None))
            .ToList();
        var row = new DeliveryDropOff
        {
            DropOffId = dropOffId,
            Location = location,
            Status = DropOffStatus.Uploading,
            UploadMode = DropOffUploadMode.Signed,
            FileCount = declared.Count,
            TotalBytes = 0,
            FilesJson = JsonSerializer.Serialize(declared),
            Label = SubmissionReference.Normalize(request.Label),
            UploadedUtc = now,
            UploadedBy = RequestActor.Label(user),
            ReservedUntilUtc = reservedUntil,
        };
        db.DeliveryDropOffs.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var uploads = new List<DeliveryDropOffUploadDto>(requested.Count);
        try
        {
            foreach (var file in requested)
            {
                var target = FileStoreRegistry.Join(location, file.Name!);
                var signed = await engine.Stores.CreateUploadAsync(target, lifetime, ct).ConfigureAwait(false);
                uploads.Add(new DeliveryDropOffUploadDto(file.Name!, target, signed.Url.ToString(), signed.ExpiresUtc.UtcDateTime));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No URL that was handed out can be un-handed, so the row records the failure and keeps the reservation
            // visible. Nothing has landed, and the caller is told why it cannot proceed.
            row.Status = DropOffStatus.Failed;
            row.Error = SecretHygiene.RedactedMessage(ex);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return TypedResults.Problem(
                detail: $"The upload URLs could not be issued: {row.Error}",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Reservation failed");
        }

        return TypedResults.Ok(new DeliveryDropOffReservationDto(
            dropOffId, location, row.Status, row.Label, row.UploadedUtc, row.UploadedBy, reservedUntil, uploads));
    }

    /// <summary>
    /// Closes a reservation: what actually landed is listed from storage and is what the ledger records, so a file that
    /// never arrived, or arrived a different size from the one reserved, fails the completion instead of leaving a
    /// drop-off that looks whole. The caller's hashes are recorded as asserted, because the bytes never came past here.
    /// </summary>
    private static async Task<Results<Ok<DeliveryDropOffDto>, ProblemHttpResult>> CompleteAsync(
        Guid dropOffId, DeliveryDropOffCompleteRequest? request, CatalogDbContext db, EngineContext engine,
        TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(clock);
        var row = await db.DeliveryDropOffs.FirstOrDefaultAsync(d => d.DropOffId == dropOffId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return TypedResults.Problem(detail: $"No drop-off has the id {dropOffId:D}.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        if (row.UploadMode != DropOffUploadMode.Signed)
        {
            return Conflict($"Drop-off {dropOffId:D} was uploaded through the control plane, which completed it as the bytes arrived. There is nothing to complete.");
        }

        if (row.Status == DropOffStatus.Complete)
        {
            // Completing twice is how a caller recovers from an answer it never saw. The second call reports the same
            // drop-off rather than re-reading storage, so a retry cannot turn a finished upload into a failed one.
            return TypedResults.Ok(Dto(row));
        }

        if (row.Status != DropOffStatus.Uploading)
        {
            return Conflict($"Drop-off {dropOffId:D} is {row.Status}; only a reservation still uploading can be completed.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (row.ReservedUntilUtc is { } until && now > until)
        {
            row.Status = DropOffStatus.Failed;
            row.Error = string.Create(CultureInfo.InvariantCulture, $"The reservation expired at {until:yyyy-MM-dd'T'HH:mm:ss'Z'} before it was completed.");
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Conflict(
                $"Drop-off {dropOffId:D} expired at {until:yyyy-MM-dd'T'HH:mm:ss'Z'}, so its upload URLs no longer work and what landed cannot be trusted to be whole. Reserve again and upload again; this one can be deleted.");
        }

        var asserted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in request?.Files ?? [])
        {
            if (string.IsNullOrWhiteSpace(file.Name))
            {
                return Invalid("Every entry in files names the file it reports on.");
            }

            if (HashRefusal(file.Sha256) is { } hashRefusal)
            {
                return Invalid($"'{file.Name}': {hashRefusal}");
            }

            if (!string.IsNullOrWhiteSpace(file.Sha256) && !asserted.TryAdd(file.Name.Trim(), file.Sha256.Trim().ToLowerInvariant()))
            {
                return Invalid($"'{file.Name}' is reported twice.");
            }
        }

        var declared = Files(row);
        var unreserved = asserted.Keys
            .Where(n => !declared.Any(d => d.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (unreserved.Count > 0)
        {
            return Invalid(
                $"{Join(unreserved)} {(unreserved.Count == 1 ? "was" : "were")} reported but not reserved. A reservation's URLs reach only the files it named, so a hash for anything else describes nothing that can be here.");
        }

        IReadOnlyList<FileRef> landed;
        try
        {
            landed = await engine.Stores.For(row.Location)
                .ListAsync(row.Location, new FileDiscovery { Pattern = "*", Recursive = false }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row is left as it is: the files may well be there, and failing a reservation over a storage read that
            // did not answer would throw away an upload that succeeded. The caller completes again.
            return TypedResults.Problem(
                detail: $"What landed in the drop-off could not be read: {SecretHygiene.RedactedMessage(ex)}",
                statusCode: StatusCodes.Status502BadGateway,
                title: "Storage unreadable");
        }

        var found = new Dictionary<string, FileRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in landed)
        {
            // One name can only be reached by one URL, so a second entry under it means storage listed something this
            // endpoint cannot reason about. Taking the first silently would hide it.
            if (!found.TryAdd(file.Name, file))
            {
                return Invalid($"Storage lists '{file.Name}' more than once under drop-off {dropOffId:D}; it cannot be completed. Delete it and reserve again.", "Drop-off unreadable");
            }
        }

        // One check covers both "nothing arrived" and "most of it arrived": either way the answer names exactly the
        // files that are not there, which is what the caller has to act on.
        var missing = declared.Where(d => !found.ContainsKey(d.Name)).Select(d => d.Name).ToList();
        if (missing.Count > 0)
        {
            return Invalid(
                $"{Join(missing)} {(missing.Count == 1 ? "has" : "have")} not landed in drop-off {dropOffId:D}. Write every reserved file to the URL the reservation gave for it, then complete again.",
                "Upload incomplete");
        }

        var wrongSize = declared
            .Where(d => found[d.Name].Size != d.Bytes)
            .Select(d => string.Create(CultureInfo.InvariantCulture, $"'{d.Name}' is {found[d.Name].Size} bytes, not the {d.Bytes} reserved"))
            .ToList();
        if (wrongSize.Count > 0)
        {
            // A short file is the ordinary shape of an upload that stopped partway, and a long one is not the file that
            // was reserved. Either way the drop-off does not hold what a submission would be pointing at, so it is not
            // completed and the caller is told exactly which file disagrees.
            return Invalid(
                $"What landed does not match the reservation: {string.Join("; ", wrongSize)}. Upload the file again, then complete again.",
                "Upload incomplete");
        }

        var extra = found.Keys
            .Where(n => !declared.Any(d => d.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (extra.Count > 0)
        {
            return Invalid(
                $"Drop-off {dropOffId:D} holds {Join(extra)}, which {(extra.Count == 1 ? "was" : "were")} never reserved. Nothing this reservation handed out could have written {(extra.Count == 1 ? "it" : "them")}, so the drop-off is not completed. Delete it and reserve again.",
                "Drop-off unexpected content");
        }

        var files = declared
            .Select(d => asserted.TryGetValue(d.Name, out var hash)
                ? new DeliveryDropOffFileDto(d.Name, found[d.Name].Size, hash, DropOffHashSource.Client)
                : new DeliveryDropOffFileDto(d.Name, found[d.Name].Size, string.Empty, DropOffHashSource.None))
            .ToList();

        row.Status = DropOffStatus.Complete;
        row.FileCount = files.Count;
        row.TotalBytes = files.Sum(f => f.Bytes);
        row.FilesJson = JsonSerializer.Serialize(files);
        row.CompletedUtc = now;
        row.Error = null;
        db.Entry(row).State = EntityState.Modified;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(Dto(row));
    }

    /// <summary>
    /// Takes a drop-off back: its files are removed from storage and the row says when and by whom. A submission that
    /// already pointed at them keeps its ledger trail, but re-processing it will no longer find its payload, which is
    /// why nothing removes a drop-off on its own unless the deployment asked for pruning.
    /// </summary>
    private static async Task<Results<Ok<DeliveryDropOffDto>, ProblemHttpResult>> DeleteAsync(
        Guid dropOffId, CatalogDbContext db, EngineContext engine, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(clock);
        var row = await db.DeliveryDropOffs.FirstOrDefaultAsync(d => d.DropOffId == dropOffId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return TypedResults.Problem(detail: $"No drop-off has the id {dropOffId:D}.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        if (row.Status == DropOffStatus.Deleted)
        {
            return TypedResults.Ok(Dto(row));
        }

        try
        {
            await engine.Stores.DeleteAsync(row.Location, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TypedResults.Problem(
                detail: $"The drop-off's files could not be removed: {SecretHygiene.RedactedMessage(ex)}",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Removal failed");
        }

        row.Status = DropOffStatus.Deleted;
        row.DeletedUtc = clock.GetUtcNow().UtcDateTime;
        row.Error = null;
        // The row came back from a query, which this context does not track, so the change is stated explicitly.
        // Without it the answer would say deleted while the ledger still said complete, and the files would be gone.
        db.Entry(row).State = EntityState.Modified;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(Dto(row));
    }

    /// <summary>
    /// Streams one file into storage, hashing it as it passes so neither the bytes nor a second read are needed to
    /// know what landed. The ceiling is enforced here as well as on the declared length, because a chunked upload
    /// declares none.
    /// </summary>
    private static async Task<(long Bytes, string Sha256)> CopyAsync(
        FileStoreRegistry stores, string target, Stream source, long maxBytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        await using (var sink = await stores.OpenWriteAsync(target, ct).ConfigureAwait(false))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw new DeliveryException($"'{target}' is larger than the {maxBytes / (1024 * 1024)} MB a drop-off file may be.");
                }

                hash.AppendData(buffer, 0, read);
                await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }

        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>Why a file name cannot be taken, or null when it can: it is one plain name, not a path.</summary>
    private static string? NameRefusal(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "A file in the upload carries no name.";
        }

        if (name.Length > MaxFileNameLength)
        {
            return $"A file name is at most {MaxFileNameLength} characters; '{name[..40]}...' is {name.Length}.";
        }

        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal))
        {
            return $"'{name}' is a path, not a file name. A drop-off holds plain file names, so nothing can be written outside it.";
        }

        return name.Any(char.IsControl) || name.Trim() != name
            ? $"'{name}' is not a usable file name: no control characters, and no leading or trailing spaces."
            : null;
    }

    /// <summary>Why a label cannot be taken, or null when it can. A drop-off's label is a name, under the same rules a submission's reference is.</summary>
    private static string? LabelRefusal(string? label) => SubmissionReference.Refusal(label, "label");

    /// <summary>Why an asserted content hash cannot be taken, or null when it can (including when none was given).</summary>
    private static string? HashRefusal(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return null;
        }

        var value = sha256.Trim();
        return value.Length != 64 || !value.All(char.IsAsciiHexDigit)
            ? "sha256 is a SHA-256 as 64 hexadecimal characters, or left out when the uploader computed none."
            : null;
    }

    /// <summary>The files a row records, as stored. A row whose JSON cannot be read holds no files rather than failing the read that showed it.</summary>
    private static IReadOnlyList<DeliveryDropOffFileDto> Files(DeliveryDropOff row)
    {
        try
        {
            return JsonSerializer.Deserialize<List<DeliveryDropOffFileDto>>(row.FilesJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Join(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

    private static DeliveryDropOffDto Dto(DeliveryDropOff row) => new(
        row.DropOffId, row.Location, row.Status, row.FileCount, row.TotalBytes, row.Label, row.UploadedUtc, row.UploadedBy,
        row.CompletedUtc, row.DeletedUtc, row.Error, Files(row),
        string.IsNullOrEmpty(row.UploadMode) ? DropOffUploadMode.Stream : row.UploadMode, row.ReservedUntilUtc);

    private static ProblemHttpResult Invalid(string detail, string title = "Invalid upload")
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: title);

    private static ProblemHttpResult Conflict(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status409Conflict, title: "Drop-off state");
}

/// <summary>How a drop-off's bytes reached storage.</summary>
public static class DropOffUploadMode
{
    /// <summary>Through the control plane, which hashed every byte as it passed on the way to storage.</summary>
    public const string Stream = "stream";

    /// <summary>Straight to storage under a signed URL. The control plane saw no bytes and computed no hash.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming", "CA1720:Identifier contains type name",
        Justification = "'signed' names how the upload was authorised (a signed URL), not a numeric type. The name matches the value the API answers with, which is the contract callers read.")]
    public const string Signed = "signed";
}

/// <summary>Where a drop-off file's recorded content hash came from, so a claim never reads as a check.</summary>
public static class DropOffHashSource
{
    /// <summary>The control plane computed it from the bytes as they streamed past.</summary>
    public const string Computed = "computed";

    /// <summary>The uploader asserted it about a file the control plane never saw. Nothing here verified it.</summary>
    public const string Client = "client";

    /// <summary>No hash: a signed upload whose uploader reported none. A flow that decides payload changes by content hash needs one.</summary>
    public const string None = "none";
}

/// <summary>What a drop-off row's status can be.</summary>
public static class DropOffStatus
{
    /// <summary>The row exists and the files are landing.</summary>
    public const string Uploading = "uploading";

    /// <summary>Every file landed; this is the only state a submission should point at, and the only one pruning sweeps.</summary>
    public const string Complete = "complete";

    /// <summary>The upload stopped partway; whatever landed is still there, for somebody to look at.</summary>
    public const string Failed = "failed";

    /// <summary>The files were removed, by hand or by a retention sweep.</summary>
    public const string Deleted = "deleted";
}
