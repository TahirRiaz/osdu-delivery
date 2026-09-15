# Sample cache records

A small capture of what `../caches/osdu-reference-cache.yaml` caches, for work without an OSDU platform: one
file per cached type, named after the type, holding its entity type and its records (each an `id` and the values the
cache flow captures). The file set has to match what the cache flow declares, type by type and path by path.

```bash
sqlflow cache import caches/osdu-reference-cache.yaml --from-dir references --db <conn-ref>
```

merges them into the cache of partition `opendes`, the partition the cache flow fills, as that flow's capture, exactly as
a refresh against OSDU would: when that changes what the cache holds, a version is written and becomes current.
Nothing about the cache is kept in this repository beyond these sample files and the cache flow that defines it.

Ids are OSDU record ids without the trailing version colon; the renderer appends it.
