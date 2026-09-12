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
using SqlFlow.Core.Secrets;
using SqlFlow.Delivery;
using SqlFlow.Delivery.Engine;
using SqlFlow.Delivery.Storage;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One file in a drop-off, as it landed.</summary>
public sealed record DeliveryDropOffFileDto(string Name, long Bytes, string Sha256);

/// <summary>
/// One drop-off: files uploaded through the API into the deployment's drop-off area, for a submission to point at
/// afterwards. <c>Location</c> is what goes into a submission's <c>files</c>.
/// </summary>
public sealed record DeliveryDropOffDto(
    Guid DropOffId, string Location, string Status, int FileCount, long TotalBytes, string? Label,
    DateTime UploadedUtc, string UploadedBy, DateTime? CompletedUtc, DateTime? DeletedUtc, string? Error,
    IReadOnlyList<DeliveryDropOffFileDto> Files);

/// <summary>
/// Whether this deployment offers a drop-off area at all, and what one upload may carry. <c>Location</c> is the root
/// uploads land under, which is also what a submission may point inside; null when the deployment configures none.
/// </summary>
public sealed record DeliveryDropOffAreaDto(
    bool Enabled, string? Location, int MaxFileMegabytes, int MaxFilesPerUpload, int RetentionDays);

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
        dropOffs.MapDelete("/dropoffs/{dropOffId:guid}", DeleteAsync).WithName("DeleteDeliveryDropOff");
        return group;
    }

    /// <summary>What a caller needs to know before uploading: whether there is anywhere to upload to, and the ceilings.</summary>
    private static Ok<DeliveryDropOffAreaDto> GetArea(IOptions<ControlPlaneOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var dropOff = options.Value.DropOff;
        var root = PayloadRoots.DropOffRoot();
        return TypedResults.Ok(new DeliveryDropOffAreaDto(
            root is not null, root, dropOff.MaxFileMegabytes, dropOff.MaxFilesPerUpload, dropOff.RetentionDays));
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

        var label = form.TryGetValue("label", out var given) ? given.ToString().Trim() : null;
        if (!string.IsNullOrEmpty(label) && label.Length > 200)
        {
            return Invalid("label is at most 200 characters.");
        }

        var dropOffId = Guid.CreateVersion7();
        var location = FileStoreRegistry.Join(root, dropOffId.ToString("D"));
        var now = clock.GetUtcNow().UtcDateTime;
        var row = new DeliveryDropOff
        {
            DropOffId = dropOffId,
            Location = location,
            Status = DropOffStatus.Uploading,
            FileCount = files.Count,
            TotalBytes = 0,
            FilesJson = "[]",
            Label = string.IsNullOrEmpty(label) ? null : label,
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
                landed.Add(new DeliveryDropOffFileDto(file.FileName, bytes, hash));
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

    private static DeliveryDropOffDto Dto(DeliveryDropOff row) => new(
        row.DropOffId, row.Location, row.Status, row.FileCount, row.TotalBytes, row.Label, row.UploadedUtc, row.UploadedBy,
        row.CompletedUtc, row.DeletedUtc, row.Error,
        JsonSerializer.Deserialize<List<DeliveryDropOffFileDto>>(row.FilesJson) ?? []);

    private static ProblemHttpResult Invalid(string detail, string title = "Invalid upload")
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: title);
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
