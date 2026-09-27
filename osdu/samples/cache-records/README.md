# Sample cache records

> **These are made-up records, and they carry `dev:` ids.** They stand in for a partition's reference data so a suite
> or a first-time setup can render with no OSDU to capture from. They are not that partition's reference data, and
> importing them into an estate that delivers for real puts ids into its cache that its platform may never have held.
> A record delivered against them carries references to whatever these files say, so a unit or a type code would point
> at something that does not exist. Fill a real partition's cache by running its cache flow with the `refresh`
> operation, which captures what the platform actually holds.

Not repository content, and not part of any source: a cache lives in the module's database
(`[osdu].[CacheVersion]`), captured there by a run of the cache flow that defines it, or imported with the CLI. A
repository sync never reads these files. They sit here, beside the bundled schemas the templates are saved from, for
the same reason those do: so a suite or a first-time setup can fill a cache with no OSDU platform to capture from.

One file per cached type, named after the type, holding its entity type and its records (each an `id` and the values
a cache flow captures): exactly the nine types `osdu/samples/recall/cache/recall-reference-00-cache.yaml` declares, with
a record for every reference the recall estate's well log mapping (`osdu/samples/recall/mappings/WellLog@1.4.0.yaml`)
builds from the lookup tables of `recall/cache/recall-lookups-00-cache.yaml`. The mapping checks each id it builds
against these records, as it checks it against the partition's own once the reference flow has refreshed. The suites
import them as that flow's capture, and so does the control plane suite's mapping builder test, which seeds a
partition's cache from them. The suites' fixture mappings and the GUI
end-to-end suite's seed step import records of their own instead (`osdu/tests/SqlFlow.Delivery.Tests/Fixtures/cache-records`),
exactly what their cache flow (`Fixtures/documents/cache/fixtures-osdu-00-reference-cache.yaml`) declares.

```bash
sqlflow cache import osdu/samples/recall/cache/recall-reference-00-cache.yaml --from-dir osdu/samples/cache-records --db <conn-ref>
```

merges the files a cache flow declares into the cache of the partition it names, as that flow's capture, exactly
as a refresh against OSDU would: when that changes what the cache holds, a version is written and becomes current.

Ids are OSDU record ids without the trailing version colon; the renderer appends it.

