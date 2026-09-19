# Sample cache records

Not repository content, and not part of any source: a cache lives in the module's database
(`[osdu].[CacheVersion]`), captured there by a run of the cache flow that defines it, or imported with the CLI. A
repository sync never reads these files. They sit here, beside the bundled schemas the templates are saved from, for
the same reason those do: so a suite or a first-time setup can fill a cache with no OSDU platform to capture from.

One file per cached type, named after the type, holding its entity type and its records (each an `id` and the values
the cache flow captures). The set has to match what the cache flow declares, type by type and path by path. These
files answer to `wells/cache/osdu-cache.yaml`, which is the document that defines the cache and the only
part of it a repository holds.

```bash
sqlflow cache import wells/cache/osdu-cache.yaml --from-dir <this folder> --db <conn-ref>
```

merges them into the cache of partition `opendes`, the partition the cache flow fills, as that flow's capture, exactly
as a refresh against OSDU would: when that changes what the cache holds, a version is written and becomes current.

Ids are OSDU record ids without the trailing version colon; the renderer appends it.
