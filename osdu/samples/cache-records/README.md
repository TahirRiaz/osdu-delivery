# Sample cache records

> **These are made-up records, and they carry `dev:` ids.** They stand in for a partition's reference data so a suite
> or a first-time setup can render with no OSDU to capture from. They are not that partition's reference data, and
> importing them into an estate that delivers for real puts ids into its cache that its platform may never have held.
> A record delivered against them carries references to whatever these files say, so a unit or a type code would point
> at something that does not exist. Wellbores are not among them: the mappings search the platform for those. Fill a
> real partition's cache by running its cache flow with the `refresh` operation, which captures what the platform
> actually holds.

Not repository content, and not part of any source: a cache lives in the module's database
(`[osdu].[CacheVersion]`), captured there by a run of the cache flow that defines it, or imported with the CLI. A
repository sync never reads these files. They sit here, beside the bundled schemas the templates are saved from, for
the same reason those do: so a suite or a first-time setup can fill a cache with no OSDU platform to capture from.

One file per cached type, named after the type, holding its entity type and its records (each an `id` and the values
the cache flow captures). The set has to match what the cache flow declares, type by type and path by path. These
files answer to `wells/cache/wells-osdu-00-reference-cache.yaml`, which is the document that defines the cache and the only
part of it a repository holds.

```bash
sqlflow cache import wells/cache/wells-osdu-00-reference-cache.yaml --from-dir <this folder> --db <conn-ref>
```

merges them into the cache of partition `dev`, the partition the cache flow fills, as that flow's capture, exactly
as a refresh against OSDU would: when that changes what the cache holds, a version is written and becomes current.

Ids are OSDU record ids without the trailing version colon; the renderer appends it.

`LogCurveType.json`, `LogCurveMainFamily.json` and `LogCurveFamily.json` hold one record for every code petrodb-api's
curve dictionary (`wells/cache/data/curve-dictionary`) gives, and `UnitOfMeasure.json` one for every unit its unit maps
(`wells/cache/data/curve-units`, `depth-units`) give, each id ending with that code exactly as the lookup table writes it.
They stand in for the reference data a partition that petrodb-api delivers to holds, so every translation the sample
lookup tables make resolves in the sample cache, as it does on that platform.
