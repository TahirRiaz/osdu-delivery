using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using SqlFlow.Delivery.Model;

namespace SqlFlow.Delivery.Engine.Protocols.Dspdm;

/// <summary>One attribute of a DSPDM business object, as its metadata (<c>BUSINESS OBJECT ATTR</c>) describes it.</summary>
internal sealed record DspdmAttribute(string Name, string DataType, bool Mandatory, bool PrimaryKey, bool ReadOnly)
{
    public DspdmType Type { get; } = DspdmValues.TypeOf(DataType);

    /// <summary>Whether DSPDM fills the attribute itself when it saves a row.</summary>
    public bool Audit => DspdmValues.Audit.Contains(Name);
}

/// <summary>
/// A DSPDM business object as the dspdm route writes its rows: its name and entity, its primary key (one whole number
/// DSPDM draws from its sequence), the unique key a row is found again by, and its attributes. A business object whose key
/// cannot be told (<see cref="KeyProblem"/>) can still have its rows read back, verified and deleted by primary key; only a
/// save needs the key.
/// </summary>
internal sealed class DspdmObject
{
    public required string Name { get; init; }

    public required string Entity { get; init; }

    public required DspdmAttribute PrimaryKey { get; init; }

    /// <summary>The attributes a row is found again by; empty when <see cref="KeyProblem"/> says why there are none.</summary>
    public required IReadOnlyList<DspdmAttribute> Key { get; init; }

    /// <summary>Why the rows of the business object cannot be found again by a key, or null when they can.</summary>
    public string? KeyProblem { get; init; }

    public required IReadOnlyDictionary<string, DspdmAttribute> Attributes { get; init; }

    /// <summary>The attributes a read of a row's version selects: when DSPDM last changed it, and when it inserted it.</summary>
    public IReadOnlyList<string> VersionAttributes => new[] { DspdmValues.ChangedDate, DspdmValues.CreatedDate }.Where(Attributes.ContainsKey).ToList();

    /// <summary>What a lookup of rows selects: the primary key, the key, and the version.</summary>
    public IReadOnlyList<string> LookupAttributes => new[] { PrimaryKey.Name }.Concat(Key.Select(k => k.Name)).Concat(VersionAttributes).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The attributes a row must give to be inserted: mandatory, and neither its primary key nor one DSPDM fills
    /// (<c>AbstractDynamicDAOImpl.verifyMandatoryFieldsForSave</c>).
    /// </summary>
    public IEnumerable<DspdmAttribute> MandatoryOnInsert => Attributes.Values.Where(a => a.Mandatory && !a.PrimaryKey && !a.Audit);

    /// <summary>The key, or the reason a save of the business object's rows is refused.</summary>
    public IReadOnlyList<DspdmAttribute> RequireKey()
        => KeyProblem is null ? Key : throw new DeliveryException(KeyProblem);
}

/// <summary>
/// The business objects the dspdm route writes, read from DSPDM's own metadata through <c>POST /common</c> once per
/// protocol (osdu/specs/production-dspdm/INTEGRATION.md sections 2 and 3.1): the business object (<c>BUSINESS OBJECT</c>),
/// its attributes (<c>BUSINESS OBJECT ATTR</c>) and its unique constraints (<c>BUS OBJ ATTR UNIQ CONSTRAINTS</c>). What makes
/// a business object unfit for the route is refused with the reason: a name DSPDM does not know, another entity than the
/// kind names, an inactive business object, a metadata or equipment catalog table (DSPDM refuses the first and saves the
/// others its own way), and a primary key that is not one whole number. A business object whose rows cannot be found again
/// by one unique constraint has its saves refused: a save whose answer was lost would otherwise be saved again as a second
/// row.
/// </summary>
internal sealed class DspdmCatalog
{
    public const string BusinessObjects = "BUSINESS OBJECT";
    public const string Attributes = "BUSINESS OBJECT ATTR";
    public const string Constraints = "BUS OBJ ATTR UNIQ CONSTRAINTS";
    public const string BoName = "BO_NAME";
    public const string BoAttrName = "BO_ATTR_NAME";
    public const string ConstraintName = "CONSTRAINT_NAME";
    public const string Entity = "ENTITY";
    public const string IsActive = "IS_ACTIVE";
    public const string IsMetadataTable = "IS_METADATA_TABLE";
    public const string IsSpecificationCatalog = "IS_SPECIFICATION_CATALOG_TABLE";
    public const string IsMeasurementCatalog = "IS_MEASUREMENT_CATALOG_TABLE";
    public const string DataType = "ATTRIBUTE_DATATYPE";
    public const string IsMandatory = "IS_MANDATORY";
    public const string IsPrimaryKey = "IS_PRIMARY_KEY";
    public const string IsReadOnly = "IS_READ_ONLY";
    private const string Brief = "osdu/specs/production-dspdm/INTEGRATION.md";

    private readonly DspdmService _service;
    private readonly DspdmTarget _target;
    private readonly ConcurrentDictionary<string, Lazy<Task<DspdmObject>>> _loaded = new(StringComparer.Ordinal);

    public DspdmCatalog(DspdmService service, DspdmTarget target)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(target);
        _service = service;
        _target = target;
    }

    /// <summary>The business object the rows of <paramref name="kind"/> belong to.</summary>
    public Task<DspdmObject> ForKindAsync(string kind, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var entity = DspdmKinds.EntityOf(kind)
            ?? throw new RecordHeldException($"{kind} is not the kind of a DSPDM business object row (its source is not '{DspdmKinds.Source}'), so the dspdm route has no business object to save it in");
        return ForEntityAsync(entity, ct);
    }

    /// <summary>
    /// The business object the rows of <paramref name="entity"/> belong to, read once. A read that failed is not kept, so the
    /// next call asks again.
    /// </summary>
    public async Task<DspdmObject> ForEntityAsync(string entity, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        var loading = _loaded.GetOrAdd(entity, e => new Lazy<Task<DspdmObject>>(() => LoadAsync(e), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            return await loading.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (loading.Value.IsFaulted || loading.Value.IsCanceled)
        {
            _loaded.TryRemove(new KeyValuePair<string, Lazy<Task<DspdmObject>>>(entity, loading));
            throw;
        }
    }

    private async Task<DspdmObject> LoadAsync(string entity)
    {
        // A load is shared by every caller that waits for it, so it runs to its end whoever stops waiting.
        var ct = CancellationToken.None;
        var declared = _target.For(entity);
        var name = declared.Name ?? DspdmKinds.DefaultName(entity);
        var at = $"target.dspdm.businessObjects.{entity}";
        var named = new DspdmFilter(BoName, DspdmFilter.EqualsOperator, [JsonValue.Create(name)]);

        var objects = await _service.ReadAsync(
            BusinessObjects, [BoName, Entity, IsActive, IsMetadataTable, IsSpecificationCatalog, IsMeasurementCatalog], [named], BoName, ct).ConfigureAwait(false);
        if (objects.Count == 0)
        {
            throw new DeliveryException(
                $"DSPDM has no business object '{name}' for the rows of {entity}. Name the business object under {at}.name ({Brief} section 2).");
        }

        var row = objects[0];
        var actualEntity = Text(row, Entity);
        if (!string.Equals(actualEntity, entity, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeliveryException(
                $"DSPDM's business object '{name}' is the entity '{actualEntity ?? "(none)"}', and the kind names '{entity}'. Name the business object of entity '{entity}' under {at}.name.");
        }

        if (Flag(row, IsActive) == false)
        {
            throw new DeliveryException($"DSPDM's business object '{name}' is not active.");
        }

        if (Flag(row, IsMetadataTable) == true)
        {
            throw new DeliveryException($"'{name}' is one of DSPDM's metadata tables, which its save refuses to write ({Brief} section 3.1).");
        }

        if (Flag(row, IsSpecificationCatalog) == true || Flag(row, IsMeasurementCatalog) == true)
        {
            throw new DeliveryException($"'{name}' is an equipment catalog table, which DSPDM's save hands to its equipment save; the dspdm route does not write it ({Brief} section 3.1).");
        }

        var attributes = new Dictionary<string, DspdmAttribute>(StringComparer.Ordinal);
        foreach (var attribute in await _service.ReadAsync(
            Attributes, [BoAttrName, DataType, IsMandatory, IsPrimaryKey, IsReadOnly, IsActive], [named], BoAttrName, ct).ConfigureAwait(false))
        {
            if (Text(attribute, BoAttrName) is not { } attributeName || Flag(attribute, IsActive) == false)
            {
                continue;
            }

            var upper = attributeName.ToUpperInvariant();
            attributes[upper] = new DspdmAttribute(
                upper,
                Text(attribute, DataType) ?? string.Empty,
                Flag(attribute, IsMandatory) == true,
                Flag(attribute, IsPrimaryKey) == true,
                Flag(attribute, IsReadOnly) == true);
        }

        var keys = attributes.Values.Where(a => a.PrimaryKey).ToList();
        if (keys.Count != 1)
        {
            throw new DeliveryException(keys.Count == 0
                ? $"DSPDM's metadata gives the business object '{name}' no primary key, so a saved row could not be read back or deleted."
                : $"DSPDM's business object '{name}' has a primary key of {keys.Count} attributes ({string.Join(", ", keys.Select(k => k.Name))}); the dspdm route writes business objects keyed by one number, as DSPDM draws it from its sequence.");
        }

        if (!keys[0].Type.Whole)
        {
            throw new DeliveryException(
                $"DSPDM's business object '{name}' has the primary key {keys[0].Name} of type '{keys[0].DataType}'; the dspdm route writes business objects keyed by a whole number, as DSPDM draws it from its sequence.");
        }

        var constraints = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var constraint in await _service.ReadAsync(
            Constraints, [ConstraintName, BoAttrName, IsActive], [named], ConstraintName, ct).ConfigureAwait(false))
        {
            if (Text(constraint, ConstraintName) is not { } constraintName || Text(constraint, BoAttrName) is not { } attributeName || Flag(constraint, IsActive) == false)
            {
                continue;
            }

            if (!constraints.TryGetValue(constraintName, out var members))
            {
                members = new SortedSet<string>(StringComparer.Ordinal);
                constraints[constraintName] = members;
            }

            members.Add(attributeName.ToUpperInvariant());
        }

        var (key, problem) = KeyOf(name, at, declared.Key, constraints);
        if (problem is null)
        {
            if (key.Where(k => !attributes.ContainsKey(k)).ToList() is { Count: > 0 } missing)
            {
                problem = $"DSPDM's business object '{name}' has no active attribute {string.Join(", ", missing)}, which the key its rows are found by names.";
            }
            else if (key.Contains(keys[0].Name, StringComparer.Ordinal))
            {
                problem = $"The key the rows of '{name}' are found by names its primary key {keys[0].Name}, which DSPDM gives a row only when it inserts it; "
                    + $"name a key of the row's own attributes under {at}.key.";
            }
        }

        return new DspdmObject
        {
            Name = name,
            Entity = entity,
            PrimaryKey = keys[0],
            Key = problem is null ? key.Select(k => attributes[k]).ToList() : [],
            KeyProblem = problem,
            Attributes = attributes,
        };
    }

    /// <summary>
    /// The unique key the rows are found by: the one the flow names, which must be a unique constraint, or the only one there
    /// is; otherwise the reason there is none.
    /// </summary>
    private static (IReadOnlyList<string> Key, string? Problem) KeyOf(string name, string at, IReadOnlyList<string> declared, SortedDictionary<string, SortedSet<string>> constraints)
    {
        var listed = string.Join("; ", constraints.Select(c => $"{c.Key}: {string.Join(", ", c.Value)}"));
        if (declared.Count > 0)
        {
            if (constraints.Values.Any(c => c.SetEquals(declared)))
            {
                return (declared, null);
            }

            return ([], constraints.Count == 0
                ? $"{at}.key names {string.Join(", ", declared)}, and DSPDM's business object '{name}' has no unique constraint, so nothing keeps two of its rows from sharing a key."
                : $"{at}.key names {string.Join(", ", declared)}, which is not a unique constraint of DSPDM's business object '{name}' ({listed}).");
        }

        return constraints.Count switch
        {
            1 => (constraints.Values.Single().ToList(), null),
            0 => ([], $"DSPDM's business object '{name}' has no unique constraint, so a row could not be found again after a save whose answer was lost, and it would be saved twice. "
                + $"Give the business object a unique constraint in DSPDM's metadata, and name it under {at}.key ({Brief} section 7)."),
            _ => ([], $"DSPDM's business object '{name}' has {constraints.Count} unique constraints ({listed}); name the one its rows are found by under {at}.key."),
        };
    }

    private static string? Text(JsonObject row, string attribute)
        => row[attribute] is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { Length: > 0 } text ? text.Trim() : null;

    /// <summary>A flag of DSPDM's metadata: true or false, as JSON or as text (<c>Y</c>, <c>N</c> in the database); null when the row does not say.</summary>
    private static bool? Flag(JsonObject row, string attribute) => row[attribute] switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.True => true,
        JsonValue value when value.GetValueKind() == JsonValueKind.False => false,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>().Trim().ToUpperInvariant() switch
        {
            "TRUE" or "Y" or "YES" or "1" => true,
            "FALSE" or "N" or "NO" or "0" => false,
            _ => null,
        },
        _ => null,
    };
}
