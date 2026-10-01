using SqlFlow.Core.Secrets;
using SqlFlow.Delivery.Model;
using SqlFlow.Delivery.Rendering;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Templates;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// What a mapping reads from the cache of the partition it renders in, as lineage shows it (docs/lineage-design.md section
/// 3.1). A mapping reads the cache two ways. It reads a type by name: a cache source, a lookup, a replace's table, a token
/// of an id (<see cref="MappingDefinition.CacheTypesRead"/>). And it reads records by what they are: every id an
/// <c>id</c> or a <c>ref</c> modifier builds is looked for among the cached records of the entity type it names, in every
/// type of the cache holding that entity type, whatever the type is called. Lineage shows both, so a cache type a mapping
/// only ever checks its references against is not left without a reader.
/// </summary>
/// <param name="Types">The cached types read by name, in name order.</param>
/// <param name="EntityTypes">The entity types whose cached records the ids the mapping builds are looked for among, in order.</param>
/// <param name="Untold">
/// The template variables filled by a <c>ref</c> whose entity type only the mapping's template tells, when no template was
/// at hand to tell it: what they are checked against is not known here. In the order the mapping writes them.
/// </param>
public sealed record MappingCacheReads(IReadOnlyList<string> Types, IReadOnlyList<string> EntityTypes, IReadOnlyList<string> Untold)
{
    /// <summary>
    /// What <paramref name="mapping"/> reads. <paramref name="schema"/> is the template the mapping pins, which says which
    /// entity type a <c>ref</c> written without one references; without it, a <c>ref</c> that writes its entity type in
    /// full and an <c>id</c> whose template writes it as text are still known, and the rest are <see cref="Untold"/>.
    /// </summary>
    /// <remarks>
    /// An id whose entity type a token gives (<c>{$param.dataPartition}:{group}--{entity}:{$value}:</c>) is of a type
    /// only a render knows, and is left out. A <c>ref</c> the template cannot settle (no relationship, or several) is the
    /// preflight's to report, which refuses the mapping; lineage shows nothing for it.
    /// </remarks>
    public static MappingCacheReads Of(MappingDefinition mapping, SchemaSnapshot? schema)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var entityTypes = new SortedSet<string>(StringComparer.Ordinal);
        var untold = new List<string>();
        foreach (var node in mapping.Entries.SelectMany(e => e.ValueNodes))
        {
            // The ids a node builds are its own modifiers' and, for a node reading every row a lookup's record names, the
            // lookup's: the same modifiers the cached types read by name are counted over.
            foreach (var modifier in node.Modifiers.Concat(node.FindAll?.Operand.Lookup?.Modifiers ?? []))
            {
                if (modifier is { Kind: ModifierKind.Id, Id.EntityType: { } built })
                {
                    entityTypes.Add(built);
                }
            }

            if (node.Modifiers.LastOrDefault(m => m.Kind == ModifierKind.Ref) is not { } reference)
            {
                continue;
            }

            if (reference.Id is { EntityType: { } written })
            {
                entityTypes.Add(written);
            }
            else if (schema is null)
            {
                if (!untold.Contains(node.Target.Text, StringComparer.Ordinal))
                {
                    untold.Add(node.Target.Text);
                }
            }
            else if (MappingRenderer.ResolveReference(node, reference, schema).Template is { EntityType: { } resolved })
            {
                entityTypes.Add(resolved);
            }
        }

        return new MappingCacheReads(mapping.CacheTypesRead(), entityTypes.ToList(), untold);
    }
}

/// <summary>How a read of the template a mapping pins came out.</summary>
public enum MappingTemplateOutcome
{
    /// <summary>The template is saved, and its schema is at hand.</summary>
    Found,

    /// <summary>The host reads no templates: it has no module database.</summary>
    NoStore,

    /// <summary>The module database holds no such template version.</summary>
    NotSaved,

    /// <summary>The template could not be read; <see cref="MappingTemplateRead.Reason"/> says why.</summary>
    Failed,
}

/// <summary>The template a mapping pins as lineage read it: its schema, or why there is none.</summary>
/// <param name="Outcome">How the read came out.</param>
/// <param name="Schema">The template's schema, when <see cref="MappingTemplateOutcome.Found"/>.</param>
/// <param name="Reason">Why the read failed, redacted, when <see cref="MappingTemplateOutcome.Failed"/>.</param>
public sealed record MappingTemplateRead(MappingTemplateOutcome Outcome, SchemaSnapshot? Schema = null, string? Reason = null)
{
    /// <summary>
    /// Why lineage has no template to read a mapping's references by, as the clause a warning says of the template after
    /// "which"; null when it has one.
    /// </summary>
    public string? Missing => Outcome switch
    {
        MappingTemplateOutcome.Found => null,
        MappingTemplateOutcome.NoStore => "this host has no osdu database to read from",
        MappingTemplateOutcome.NotSaved => "is not saved",
        _ => $"could not be read ({Reason})",
    };
}

/// <summary>
/// Reads the templates mappings pin, for lineage: the one thing a mapping's lineage needs that the repository does not
/// hold. A <c>ref</c> written without its entity type takes it from the template variable it fills, and templates live in
/// the module database, so a host with that database reads the pinned version from it. A saved template version never
/// changes, so what lineage reads of it is as fixed as the documents are. A host without the database, a version that is
/// not saved and a database that does not answer each come back as a reason, never as an exception: lineage then shows
/// what the documents alone tell, and says what it left out.
/// </summary>
public sealed class MappingTemplateSource
{
    /// <summary>How long a failed read stands for every read after it, so one database that does not answer costs a scan
    /// of many flows one wait and not one per flow.</summary>
    internal static readonly TimeSpan FailureHold = TimeSpan.FromSeconds(30);

    private readonly Func<ITemplateStore?> _store;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private MappingTemplateRead? _failure;
    private DateTimeOffset _failedUntil;

    /// <summary>A source over the store <paramref name="store"/> gives each time it is asked: null on a host that has
    /// no module database, which may be known only once the host is built.</summary>
    public MappingTemplateSource(Func<ITemplateStore?> store, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>A source over <paramref name="store"/>.</summary>
    public MappingTemplateSource(ITemplateStore store, TimeProvider? time = null)
        : this(() => store, time)
    {
        ArgumentNullException.ThrowIfNull(store);
    }

    /// <summary>The template <paramref name="reference"/> names, or why there is none to read.</summary>
    public MappingTemplateRead Read(TemplateReference reference)
    {
        lock (_gate)
        {
            if (_failure is not null && _time.GetUtcNow() < _failedUntil)
            {
                return _failure;
            }
        }

        try
        {
            if (_store() is not { } store)
            {
                return new MappingTemplateRead(MappingTemplateOutcome.NoStore);
            }

            // Describing lineage is synchronous and the store is not; the read runs on the pool so no caller's
            // synchronization context is waited on from inside itself.
            var schema = Task.Run(() => store.LoadAsync(reference)).GetAwaiter().GetResult();
            return schema is null
                ? new MappingTemplateRead(MappingTemplateOutcome.NotSaved)
                : new MappingTemplateRead(MappingTemplateOutcome.Found, schema);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = new MappingTemplateRead(
                MappingTemplateOutcome.Failed, Reason: $"{ex.GetType().Name}: {SecretHygiene.RedactedMessage(ex.Message)}");
            lock (_gate)
            {
                _failure = failure;
                _failedUntil = _time.GetUtcNow() + FailureHold;
            }

            return failure;
        }
    }
}
