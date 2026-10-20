# Sample cache records

> **These are what a refresh of the sample's reference cache captures once the sample's own reference data is
> delivered, and they carry `dev:` ids.** They stand in for that capture so a suite or a first-time setup can render with
> no OSDU to capture from. Importing them into an estate that delivers for real puts ids into its cache that its platform
> may not hold yet, so a record delivered against them could refer to something that does not exist. Fill a real
> partition's cache by delivering the reference data (`welldb-ref-unitquantity-03-delivery`, then
> `welldb-ref-03-delivery`) and running the cache flow with the `refresh` operation, which captures what the platform
> actually holds.

Not repository content, and not part of any source: a cache lives in the module's database
(`[osdu].[CacheVersion]`), captured there by a run of the cache flow that defines it, or imported with the CLI. A
repository sync never reads these files. They sit here, beside the bundled schemas the templates are saved from, for
the same reason those do: so a suite or a first-time setup can fill a cache with no OSDU platform to capture from.

One file per cached type, named after the type, holding its entity type and its records (each an `id` and the values
a cache flow captures): exactly the types `osdu/samples/welldb/cache/osdu-reference-00-cache.yaml` declares, one record
for every value the sample's reference data holds (`osdu/samples/welldb/reference/data`). Those are OSDU's own
published values, taken by `osdu/tools/SampleData` from the reference value manifests of the OSDU data definitions,
and the well database's own curve types (`WellDB:GR`, named as OSDU names a company's curve types). The generator
writes these files from the reference data, and the suites check that they are exactly what it writes. Each id is the
one the reference delivery gives the value: its code, as `dataset.idFrom: key` writes it.

The well log and well mappings check each id they build against these records, as they check it against the
partition's own once the reference flow has refreshed. The suites import them as that flow's capture, and so does the
control plane suite's mapping builder test, which seeds a partition's cache from them. The suites' fixture mappings and
the GUI end-to-end suite's seed step import records of their own instead
(`osdu/tests/SqlFlow.Delivery.Tests/Fixtures/cache-records`), exactly what their cache flow
(`Fixtures/documents/cache/fixtures-osdu-00-reference-cache.yaml`) declares.

```bash
sqlflow cache import osdu/samples/welldb/cache/osdu-reference-00-cache.yaml --from-dir osdu/samples/cache-records --db <conn-ref>
```

merges the files a cache flow declares into the cache of the partition it names, as that flow's capture, exactly
as a refresh against OSDU would: when that changes what the cache holds, a version is written and becomes current.

Ids are OSDU record ids without the trailing version colon; the renderer appends it.
