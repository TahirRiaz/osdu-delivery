using System.Globalization;
using SqlFlow.Delivery.Documents;
using SqlFlow.Delivery.Engine.Protocols;
using SqlFlow.Delivery.Identity;
using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine;

/// <summary>
/// Which records a removal acts on: either the exact keys an operator picked, or the listing filter they were
/// looking at when they asked for every record it matches. The filter form is resolved on the node against the
/// ledger at the moment the removal runs, so a selection of "everything this run touched" never has to travel as
/// tens of thousands of ids, and the resolved count is what the removal reports acting on.
/// </summary>
public sealed record RemovalSelection
{
    private RemovalSelection(IReadOnlyList<DeliveryKey>? keys, RecordQuery? filter)
    {
        Keys = keys;
        Filter = filter;
    }

    /// <summary>The exact records to remove, or null when the selection is a filter.</summary>
    public IReadOnlyList<DeliveryKey>? Keys { get; }

    /// <summary>The listing every matching record of which is to be removed, or null when the selection is a key list.</summary>
    public RecordQuery? Filter { get; }

    public static RemovalSelection Of(IReadOnlyList<DeliveryKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            throw new DeliveryException("A removal needs at least one record.");
        }

        if (keys.Count > RemovalLimits.MaxSelection)
        {
            throw new DeliveryException($"A removal takes at most {RemovalLimits.MaxSelection.ToString(CultureInfo.InvariantCulture)} records at a time; {keys.Count.ToString(CultureInfo.InvariantCulture)} were selected.");
        }

        return new RemovalSelection(keys, null);
    }

    public static RemovalSelection Of(RecordQuery filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return new RemovalSelection(null, filter);
    }

    /// <summary>The selection as the activity trail records it: what was asked for, never the resolved key list.</summary>
    public object Describe(RemovalScope scope) => Keys is not null
        ? new { scope = scope.ToString().ToLowerInvariant(), records = Keys.Count, keys = Keys.Take(20).Select(k => k.ToString()).ToList() }
        : new
        {
            scope = scope.ToString().ToLowerInvariant(),
            filter = new
            {
                status = Filter!.Status?.ToString().ToLowerInvariant(),
                search = Filter.Search,
                mode = Filter.Mode.ToString().ToLowerInvariant(),
                submissionId = Filter.SubmissionId,
                runId = Filter.RunId,
                drifted = Filter.Drifted,
            },
        };
}

/// <summary>
/// The three endpoints a removal of this flow's records would call, as the flow resolves them: the protocol's
/// defaults with the flow's own overrides applied. The GUI shows them next to the target so an operator can see
/// exactly which call each scope makes before asking for it, and the ddms route genuinely differs from the others,
/// so this is derived from the flow rather than assumed.
///
/// On the ddms route the endpoints are those of the collection serving the records' entity type
/// (<see cref="DdmsRouting"/>), which the kind the flow's mapping renders names. The history scope always names a
/// storage service path, because versions belong to storage for every kind of record, and so does the everything
/// scope of a record-only collection, whose DDMS deletes logically only. When the flow's endpoint is a DDMS itself,
/// a storage path resolves under the wrong base unless the flow declares it as an absolute URL; the endpoint is
/// reported as unconfigured in that case rather than as a URL that would not answer.
/// </summary>
public sealed record RemovalEndpoints(string Record, string History, string Everything)
{
    /// <summary>
    /// The method the record scope calls <see cref="Record"/> with: POST for storage's <c>:delete</c> and the dataset
    /// service's soft delete, DELETE for a DDMS's own removal.
    /// </summary>
    public string RecordMethod { get; init; } = "POST";

    /// <summary>What the history endpoint reads as when the flow cannot reach the storage service's version purge.</summary>
    public const string HistoryNotConfigured = "(not configured: set protocolOptions.ddmsRoot, or a root for the DDMS under target.ddms, when the endpoint is the platform root, or purgeVersionsPath to the storage service's URL)";

    /// <summary>What the everything endpoint of a record-only DDMS collection reads as when the flow cannot reach the storage service's purge.</summary>
    public const string PurgeNotConfigured = "(not configured: set protocolOptions.ddmsRoot, or a root for the DDMS under target.ddms, when the endpoint is the platform root, or purgePath to the storage service's URL)";

    /// <summary>What the record endpoint of a DDMS whose records are storage records reads as when the flow cannot reach the storage service.</summary>
    public const string DeleteNotConfigured = "(not configured: give the DDMS its root under target.ddms, the endpoint being the platform root)";

    /// <summary>What a ddms-route endpoint reads as while the kind the flow's mapping renders is not known.</summary>
    public const string CollectionNotKnown = "(the collection serving the records' entity type, known once the flow's mapping is synced)";

    /// <summary>What a ddms-route endpoint reads as while the registration of the DDMS that may serve the records is not read.</summary>
    public const string RegistrationNotRead = "(the collection the DDMS registered for the records' entity type, read from the Register service when the flow runs)";

    /// <summary>The endpoints of <paramref name="flow"/>; <paramref name="kind"/> is the kind its mapping renders, when known.</summary>
    public static RemovalEndpoints Of(FlowDefinition flow, string? kind)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var options = flow.Target.ProtocolOptions;
        if (DeliveryProtocols.ReachesDdms(flow.Target.Protocol))
        {
            return OfDdms(flow, kind);
        }

        if (flow.Target.Protocol == DeliveryProtocol.OsduDspdm)
        {
            // DSPDM keeps no deleted rows and no versions: a row is deleted for good, by the key DSPDM gave it.
            return new RemovalEndpoints(DspdmRecordRefused, DspdmHistoryRefused, (flow.Target.Dspdm.Root ?? string.Empty) + DspdmDelete) { RecordMethod = "DELETE" };
        }

        if (flow.Target.Protocol == DeliveryProtocol.OsduEtp)
        {
            // The Reservoir DDMS keeps no deleted objects and no earlier versions: an object is deleted for good, over
            // ETP rather than over HTTP, and no dataspace is ever deleted (its delete purges an OSDU record).
            return new RemovalEndpoints(EtpRecordRefused, EtpHistoryRefused, EtpDelete) { RecordMethod = "ETP" };
        }

        // A dataset the route registers itself is removed reversibly through the Dataset service, whose undelete restores it.
        var registersDataset = flow.Target.Protocol switch
        {
            DeliveryProtocol.OsduDataset => !string.IsNullOrWhiteSpace(kind) && OsduKind.EntityType(kind) is { } entityType && Protocols.DatasetService.IsDatasetType(entityType),
            DeliveryProtocol.OsduWorkflow => flow.Target.Workflow?.Anchor == Model.WorkflowAnchor.Dataset,
            _ => false,
        };
        if (registersDataset)
        {
            return new RemovalEndpoints(
                options.DatasetSoftDeletePath ?? Protocols.DatasetService.DefaultSoftDeletePath,
                options.PurgeVersionsPath ?? OsduRecordProtocol.DefaultPurgeVersionsPath,
                options.PurgePath ?? OsduRecordProtocol.DefaultPurgePath);
        }

        return new RemovalEndpoints(
            options.DeletePath ?? OsduRecordProtocol.DefaultDeletePath,
            options.PurgeVersionsPath ?? OsduRecordProtocol.DefaultPurgeVersionsPath,
            options.PurgePath ?? OsduRecordProtocol.DefaultPurgePath);
    }

    private static RemovalEndpoints OfDdms(FlowDefinition flow, string? kind)
    {
        var routing = DdmsRouting.Of(flow);
        var history = routing.HistoryPath ?? HistoryNotConfigured;
        if ((string.IsNullOrWhiteSpace(kind) ? null : OsduKind.EntityType(kind)) is not { } entityType)
        {
            return new RemovalEndpoints(CollectionNotKnown, history, CollectionNotKnown) { RecordMethod = "DELETE" };
        }

        if (!routing.NamesPaths && routing.Unread.Count > 0 && routing.Find(entityType) is null)
        {
            return new RemovalEndpoints(RegistrationNotRead, history, RegistrationNotRead) { RecordMethod = "DELETE" };
        }

        DdmsRecordPaths paths;
        try
        {
            paths = routing.For(entityType);
        }
        catch (DeliveryException ex)
        {
            var unroutable = $"(not routable: {ex.Message})";
            return new RemovalEndpoints(unroutable, history, unroutable) { RecordMethod = "DELETE" };
        }

        return paths.Shape switch
        {
            // The Well Delivery DDMS purges under its own path, and its versions are what other entities' references cite.
            DdmsShape.WellDeliveryV1 => new RemovalEndpoints(paths.Delete, HistoryRefusedByWellDelivery, paths.Delete + ":purge") { RecordMethod = "DELETE" },

            // RAFS deletes logically only; its records are storage records, which storage purges.
            DdmsShape.RafsV2 => new RemovalEndpoints(paths.Delete, history, routing.StoragePurgePath ?? PurgeNotConfigured) { RecordMethod = "DELETE" },

            // The historian's records are storage records, and nothing removes its points.
            DdmsShape.ProductionTimeSeriesV1 => new RemovalEndpoints(
                routing.StorageDeletePath ?? DeleteNotConfigured, history, routing.StoragePurgePath ?? PurgeNotConfigured),

            // Seismic Store's records are storage records; its datasets have no reversible delete, and on gc one dataset's
            // delete takes the files of every dataset in the subproject.
            DdmsShape.SeismicStoreV3 => new RemovalEndpoints(
                routing.StorageDeletePath ?? DeleteNotConfigured,
                history,
                paths.Route!.Service.SeismicStore?.Provider == DdmsProvider.Gc
                    ? SeismicGcDeleteRefused
                    : routing.StoragePurgePath is { } purge ? $"{paths.Data} (the dataset and its files), then {purge}" : PurgeNotConfigured),

            // The Reservoir Management DDMS's records are storage records; its own delete purges, so it is never called,
            // and the rows of its tables are deleted one by one before the purge.
            DdmsShape.ReservoirManagement => new RemovalEndpoints(
                routing.StorageDeletePath ?? DeleteNotConfigured,
                history,
                routing.StoragePurgePath is not { } storagePurge ? PurgeNotConfigured
                    : paths.Data is { } rows ? $"{rows}/{{key}} (each row the record's deliveries posted), then {storagePurge}"
                    : storagePurge),
            _ => new RemovalEndpoints(
                paths.Delete,
                history,
                paths.Route is { Collection.Bulk: false } ? routing.StoragePurgePath ?? PurgeNotConfigured : paths.Delete + "?purge=true")
            {
                RecordMethod = "DELETE",
            },
        };
    }

    /// <summary>What the record endpoint of the dspdm route reads as.</summary>
    public const string DspdmRecordRefused = "(refused: " + Protocols.OsduDspdmProtocol.RecordScopeRefused + ")";

    /// <summary>What the history endpoint of the dspdm route reads as.</summary>
    public const string DspdmHistoryRefused = "(refused: " + Protocols.OsduDspdmProtocol.HistoryScopeRefused + ")";

    /// <summary>The call the everything scope of the dspdm route makes for each row, under DSPDM's root.</summary>
    public const string DspdmDelete = "/delete/{businessObject}/{row id}";

    /// <summary>What the record endpoint of the etp route reads as.</summary>
    public const string EtpRecordRefused = "(refused: " + Protocols.OsduEtpProtocol.RecordScopeRefused + ")";

    /// <summary>What the history endpoint of the etp route reads as.</summary>
    public const string EtpHistoryRefused = "(refused: " + Protocols.OsduEtpProtocol.HistoryScopeRefused + ")";

    /// <summary>The call the everything scope of the etp route makes for each object, on its dataspace's session.</summary>
    public const string EtpDelete = "Store.DeleteDataObjects {object uri}";

    /// <summary>What the everything endpoint of a Seismic Store on gc reads as.</summary>
    public const string SeismicGcDeleteRefused = "(refused: Seismic Store on gc deletes the files of every dataset in a subproject when one dataset is deleted)";

    /// <summary>What the history endpoint of a Well Delivery DDMS collection reads as.</summary>
    public const string HistoryRefusedByWellDelivery = "(refused: the Well Delivery DDMS keys every version by the value other entities' references cite)";
}

/// <summary>What a removal did to one record: enough to answer "what happened to this one" without a second query.</summary>
public sealed record RemovalRecordResult(
    Guid DeliveryKey, string? SourceKey, string? Label, string? TargetId, string Outcome, string Detail, Guid? SubmissionId)
{
    public static RemovalRecordResult Removed(DeliveryKey key, string? sourceKey, string? label, string? targetId, string detail, Guid? submissionId)
        => new(key.Value, sourceKey, label, targetId, "removed", detail, submissionId);

    public static RemovalRecordResult AlreadyGone(DeliveryKey key, string? sourceKey, string? label, string? targetId, string detail, Guid? submissionId)
        => new(key.Value, sourceKey, label, targetId, "already-gone", detail, submissionId);

    public static RemovalRecordResult Skipped(DeliveryKey key, string? sourceKey, string? label, string? targetId, string detail, Guid? submissionId)
        => new(key.Value, sourceKey, label, targetId, "skipped", detail, submissionId);

    public static RemovalRecordResult Failed(DeliveryKey key, string? sourceKey, string? label, string? targetId, string detail, Guid? submissionId)
        => new(key.Value, sourceKey, label, targetId, "failed", detail, submissionId);
}

/// <summary>
/// What a removal did, in total and per record. The per-record list is capped at
/// <see cref="RemovalLimits.MaxReported"/> so a removal of thousands still returns a result a page can render;
/// every record's own outcome is in the ledger regardless, on its attempt, which is where a removal is audited.
/// Failures are kept ahead of successes when the cap bites, because they are what an operator needs to see.
/// </summary>
public sealed record RemovalSummary(
    RemovalScope Scope, int Selected, int Removed, int AlreadyGone, int Skipped, int Failed,
    IReadOnlyList<RemovalRecordResult> Records, bool Truncated)
{
    public static RemovalSummary Of(RemovalScope scope, int selected, IReadOnlyList<RemovalRecordResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var removed = results.Count(r => r.Outcome == "removed");
        var alreadyGone = results.Count(r => r.Outcome == "already-gone");
        var skipped = results.Count(r => r.Outcome == "skipped");
        var failed = results.Count(r => r.Outcome == "failed");
        var reported = results.Count <= RemovalLimits.MaxReported
            ? results
            : results.OrderBy(r => r.Outcome == "failed" ? 0 : r.Outcome == "skipped" ? 1 : 2).Take(RemovalLimits.MaxReported).ToList();
        return new RemovalSummary(scope, selected, removed, alreadyGone, skipped, failed, reported, results.Count > reported.Count);
    }

    /// <summary>The one line the activity trail carries for the whole removal.</summary>
    public string Describe()
    {
        var what = Scope switch
        {
            RemovalScope.Record => "removed from OSDU (reversible)",
            RemovalScope.History => "earlier versions purged",
            RemovalScope.Everything => "purged from OSDU with every version",
            _ => throw new InvalidOperationException($"Unknown removal scope '{Scope}'."),
        };
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Selected} record(s) selected: {Removed} {what}, {AlreadyGone} already gone, {Skipped} skipped, {Failed} failed");
    }
}
