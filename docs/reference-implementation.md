# How the working implementation delivers well logs

Two systems already publish Equinor's well logs into OSDU, and between them they answer questions this project is
still deciding. This page is what they do, read from their source, and what OSDU Delivery should take from each.

- `D:\Projects\eq\src\wl-pipelines-main`, the Databricks estate, package `recall_to_osdu`.
- `D:\Projects\eq\src\petrodb-api-main`, the C# service, `src/PetroDb`.

Neither is edited by this project. They are read as the working reference, the way `osdu/specs` is read for the
platform's own contracts.

## 1. The split: who shapes a record

The two halves divide the work at one line, and it is the line this project has been deciding on its own.

**`recall_to_osdu` moves data and shapes nothing.** It runs in two phases, as two Databricks tasks
(`functions/recall_to_osdu.py`):

| Phase | What it does |
| --- | --- |
| `prepare_osdu_payloads` | Reads `wl_pipelines_dsis_intermediate.recall_logcurve_enriched` in Unity Catalog, filtered to one `log_name`, and materializes a parquet payload per wellbore plus a Delta manifest. |
| `transfer_osdu_payloads` | Uploads those payloads to the service over HTTP. |

What it sends is **source-shaped**. `utils/builders.py` renames Recall columns to OSDU-ish names
(`recallcommonmodel:WellLog__wellbore_uwi` becomes `WellboreId`, `recall:LOG_RUN` becomes `LogRun`) and stops there.
The clearest evidence is the unit of measure: it splits `recall:ELEV_MEAS_REF` on whitespace and takes the second
token, so `VerticalMeasurementUnitOfMeasureID` leaves the pipeline as the literal string `M`, not as
`dev:reference-data--UnitOfMeasure:m:`. The pipeline resolves no reference data, mints no OSDU id, and holds no cache.

**`petrodb-api` turns that into an OSDU record.** Everything below is its work.

The consequence worth stating plainly: in the working system the pipeline is a transport, and one service owns every
decision about what an OSDU record looks like. `osdu/docs/decisions/0003-rendering-location.md` records this project
choosing the other arrangement, with the delivery engine rendering. That is a real divergence, not an accident, and
the patterns below are the ones that still apply once rendering moves.

## 2. Mapping is generated code, not interpreted configuration

`MappingGenerator` emits a C# mapper per source and target pair. `Api/Generated/RecallToWellLogMapper/RecallToWellLogMapper.g.cs`
is 180 lines of straight assignments:

```csharp
public class RecallToWellLogMapper : IRecallToWellLogMapper
{
    CurveUnit = _unitOfMeasureReferenceResolver.Resolve(
        source.CurveUnit, "reference-data", "UnitOfMeasure",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["G/CC"] = "g/cm3", ["G/CM3"] = "g/cm3", ["G/C_3"] = "g/cm3",
            ["GAPI"] = "gAPI",  ["API"]   = "nAPI",  ["DEGC"]  = "degC",
            // ~90 more
        }),
}
```

Three things are worth copying, and one is worth knowing about rather than copying.

**The value map is part of the mapping, inline.** Roughly ninety spellings of a unit collapse onto the OSDU code:
`G/CC`, `G/CM3` and `G/C_3` all become `g/cm3`; `Â°C` and `°C` and `C` all become `degC`. This is accumulated
knowledge of one source system's dirt, and it lives beside the field it cleans. OSDU Delivery has `modifiers` on a
mapping entry, and a `replace` modifier is the same shape; the sample estate uses one for `V/V` to `m3/m3`. The
difference is scale: the working system carries ninety entries for `CurveUnit` alone, because that is what real Recall
data needs.

**Normalization happens exactly once.** `ValueMapNormalizer.Normalize(value, valueMap)` is applied by the resolver,
and the fallback path deliberately passes the *original* value rather than the normalized one, with a comment saying
why: `FormatSchemaRef` applies the map itself, and applying it twice would be wrong. Worth remembering when a
modifier chain and a resolver both think they own cleaning.

**Generated, so the mapping is checked by the compiler.** A renamed target property breaks the build rather than a
run. OSDU Delivery interprets YAML mappings instead and catches the same class of mistake in the preflight gate. Both
work; the generated form fails earlier and the interpreted form ships without a rebuild.

## 3. Reference resolution: look up, then construct

`Api/Common/Mapping/UnitOfMeasureReferenceResolver.cs` is the piece this project should read closely, because it
answers the question that currently blocks the sample estate.

```csharp
public string? Resolve(string? value, string dataset, string entityType,
                       IReadOnlyDictionary<string, string>? valueMap = null)
{
    if (string.IsNullOrWhiteSpace(value)) return null;

    var originalValue = value;
    value = ValueMapNormalizer.Normalize(value, valueMap);

    if (value is { Length: > 0 })
    {
        var decodedInput = Uri.UnescapeDataString(value);
        foreach (var entity in _cache.GetAll())
        {
            var data = entity.Data;
            if (MatchesField(decodedInput, data.ID) ||
                MatchesField(decodedInput, data.Code) ||
                MatchesField(decodedInput, data.Name))
            {
                var id = entity.Id;
                if (id is { }) return id.EndsWith(':') ? id : id + ":";
            }
        }
    }

    // No match (or cache not loaded / empty): fall back to best-effort id construction.
    return _osduIdFormatter.FormatSchemaRef(dataset, entityType, originalValue, valueMap);
}
```

Four properties, in order of how much they matter here:

1. **A cache miss is not a failure.** It constructs the id from the partition, the dataset, the entity type and the
   value. A record is always produced. OSDU Delivery does the opposite: a `findBy` that matches nothing **holds the
   record**, and the run reports `no Wellbore matches 'X' by FacilityName in version ... of the cache of partition
   'dev'`. Neither is wrong, and the difference is a policy decision worth making deliberately rather than inheriting:
   construct-on-miss keeps delivery moving and can mint a reference to a record that does not exist;
   hold-on-miss guarantees every reference resolves and stops the run when reference data is incomplete.
2. **A value matches on any of three fields**: `ID`, `Code` or `Name`. The cache is searched by all three, decoded and
   case-insensitively. A source saying `metre`, `m` or the id itself all land on the same record. OSDU Delivery's
   `findBy` names one field, so `cache.Wellbore.FacilityName = dataset.wellbore_uwi` matches on `FacilityName` alone.
3. **Comparison is URL-decoded on both sides.** `Uri.UnescapeDataString` is applied to the input and the candidate,
   which is what makes `m3%2Fm3` and `m3/m3` the same value. Any cache holding OSDU ids has to do this, because the
   id segment is percent-encoded and the source value is not.
4. **Exactly one trailing colon.** `id.EndsWith(':') ? id : id + ":"`, mirroring the schema-ref resolver. The same
   rule appears in `OsduIdFormatter.FormatId`.

`OsduIdFormatter` is worth a look for the id rules alone: it validates the dataset against
`reference-data`, `master-data`, `work-product-component`, and recognises a dashless GUID, a dashed GUID (dashes
stripped) and an already-formatted id (returned verbatim).

## 4. The reference cache is a background refresh, not a captured version

`Api/Common/Cache/` holds a small generic cache per reference type: `IUnitOfMeasureCache`,
`IVerticalMeasurementTypeCache`, `IFieldCache`, `IGeoPoliticalEntityCache` and others, all
`IOsduReferenceCache<T>`. Each has a loader:

```csharp
public interface IOsduReferenceCacheLoader<T>
{
    static abstract string SectionName { get; }
    string? GetKey(T item);
    Task<IOsduSearchResponse<T>> FetchPageAsync(int limit, int offset, CancellationToken cancellationToken);
}
```

`OsduReferenceCacheRefresher<T, TLoader>` pages through `FetchPageAsync` on a configured `RefreshInterval`, from a
fresh DI scope per refresh so a loader may depend on scoped services.

This is the same idea as OSDU Delivery's cache and differs in two ways that matter:

- **It is in memory and always current-ish**, refreshed on an interval, with no version. OSDU Delivery captures a
  **versioned** cache into the module database, and the version enters the render context, so a record records which
  reference data rendered it. That is a real traceability gain and this project should keep it.
- **It is per type, per service instance.** OSDU Delivery scopes a cache by partition and shares it across every flow
  delivering there.

## 5. What this says about the sample estate's fixtures

The immediate problem in this repository is that `WellLog@1.4.0`'s fixtures pin `OSDU-DEV-1-A` and
`LogCurveBusinessValue:Medium`, the preflight runs those fixtures against the partition's live cache, and real `dev`
holds neither (its codes are `High`, `Low`, `Med`, `Undefined`).

The working implementation does not have this problem, and the reason is structural rather than clever: **it has no
fixtures that resolve against live reference data.** Its mapper is generated code, its tests
(`MappingGenerator.Tests`, `Api.Core.Tests`) test the generator and the resolver against stubs, and the reference
cache is only ever consulted at run time. Nothing pins an expected document containing a resolved id.

So there are two coherent positions, and this project currently sits between them:

1. **A fixture pins a document, so it must pin its reference data too.** Then a mapping's fixtures have to be
   evaluated against a fixed reference set rather than the partition's live cache, or they are only valid for the one
   partition and the one day they were captured.
2. **A fixture tests the mapping's own logic**, and reference resolution is covered separately, by tests over the
   resolver with a stub cache. This is what petrodb-api does.

Position 1 with a live cache is what is in place now, and it is why the sample estate cannot render against a real
partition: its fixtures assert facts about invented reference data.

## 6. Small things worth stealing

- **`ValueMapNormalizer` as a named thing.** Normalizing source spellings is a step with a name and one owner, not
  something each modifier does a bit of.
- **`OsduIdFormatter` taking the partition in its constructor.** Every id it mints carries the partition without a
  caller ever passing one, which is one way an id cannot be minted for the wrong partition.
- **A cache keyed by a selector the loader supplies** (`GetKey(T item)`), so the cache does not need to know what
  makes an item unique.
- **The refresher taking a fresh DI scope per refresh.** A background loop holding one scoped service for the life of
  the process is a class of bug this design removes rather than documents.
