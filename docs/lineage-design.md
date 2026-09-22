# Lineage design: OSDU flows, OSDU types and file ingestion

The lineage graph shows every OSDU pipeline as a node, with its data on both sides: the files a pre-ingestion flow reads
and the tables it lands them in, the ingestion tables a delivery flow reads, and the OSDU types (record kinds in a data
partition) the delivery flow writes. Cache and retrieval flows read those OSDU types back and write the partition cache
and files. With every edge in place, SQLFlow's waves order the whole estate: files land, ingestion keys them, delivery
sends them, and the flows that read OSDU run after the flows that write it.

This page is the working design. It records what was missing, the node model, the edges each flow kind contributes, the
generic extension points added to the vendored SQLFlow, the OSDU module's side, the rules that keep the contribution
robust, and the tests that hold it.

## 1. What was missing

Checked against the synced e2e catalog (`SQLFlow_E2E`, then named `SqlFlowCatalogE2EOsdu`) and the lineage graph page on 2026-09-16.

1. **Delivery flows were dead ends.** A delivery flow declared only the ingestion tables it reads
   (`DeliveryLineage.DeclaredObjects`). Nothing downstream of it existed, so the graph ended at `wells-welllog-03-header-delivery` and
   `wells-wellbore-03-header-delivery`.
2. **Cache and retrieval flows floated.** `wells-osdu-00-reference-cache` and `wells-osdu-04-metadata-retrieval` declared nothing, so they had no
   edges and sat alone in the first wave.
3. **A registered kind could declare database tables only.** `RegisteredFlowDocument.DeclaredObjects` takes a connection
   reference and a three-part name. SQLFlow's own flows show HTTP endpoints and file locations as file nodes (an
   acquisition reads its URLs, a translate flow writes its folder), but a registered kind had no way to declare either,
   nor to declare the files it lands so the flow reading them is ordered after it.
4. **File nodes carried machine paths.** The collector resolved a file flow's relative `source.location` against the
   repository root, while the engine resolves it against the flow file's folder (`DocumentLoader`). A pre flow in
   `flows/` reading `../data/welllog` climbed out of the checkout and became
   `c:/users/.../node-cache/<hash>/data/welllog`: the wrong folder, and a different node on every machine. Producer and
   consumer matching compared raw strings, so `./data/x` and `data/x` never linked either.
5. **A mapping change did not recompute lineage.** The sync's lineage gate compares flow content hashes only. A delivery
   flow's OSDU type lives in its mapping, so editing the mapping left the stored graph stale.

## 2. The node model

| Node | Identity | Shown as |
| --- | --- | --- |
| Flow | The pipeline, as today. Every delivery, cache and retrieval flow is a flow node. | The flow's name, `delivery`/`cache`/`retrieval`, its wave |
| OSDU type | The OSDU platform (the flow's endpoint reference), the data partition, and the record kind | The kind (`osdu:wks:master-data--Wellbore:1.3.0`), captioned `osdu type · dev.master-data` |
| Partition cache type | The data partition and the cache type name | The type name (`UnitOfMeasure`), captioned `osdu cache · dev.cache` |
| File | The location relative to the repository root (a URL or an absolute path as written) | `data/welllog` |
| Table, view | As today | As today |

An OSDU type is a node per exact kind, version included: that is what OSDU stores records under, and two versions of a
type are two different things to deliver. A flow that reads with a wildcard (a cache type `osdu:wks:master-data--Wellbore:*`)
has its own pattern node, which stands for everything of that type the platform holds, and is also bound to every exact
kind in the estate the pattern matches (section 5).

The OSDU platform is part of an OSDU type's identity because it is part of what the record is: two environments may both
have a partition called `dev`. The platform is identified by the endpoint exactly as the flow declares it
(`${env:OSDU_URL}`), which is SQLFlow's rule for every server identity: two flows naming the same platform through two
different references are two platforms to lineage. The partition cache is keyed by partition alone, because that is how
the module keys the cache itself (`CacheScope`).

## 3. What each flow contributes

| Flow | Reads | Writes |
| --- | --- | --- |
| Pre-ingestion (`file`) | Its source files, at their repository-relative location | Its landing table and typed view (unchanged) |
| Ingestion (`ing`) | The typed view (unchanged) | Its keyed table (unchanged) |
| Delivery | The record and dataset ingestion tables (unchanged); the payload files under each `source.payloads.<name>.root`; each partition cache type its mapping reads (`cache.<Type>`) | The OSDU type its mapping fills (`template.kind`); for `file` and `manifest`, the dataset kind it registers files as (`protocolOptions.datasetKind`) |
| Cache | Each declared type's OSDU kind (`types[].kind`, wildcards allowed) | Each declared cache type (`types[].name`) in its partition's cache |
| Retrieval | Each OSDU kind it retrieves (`source.kinds`) | The record files (`part-*.jsonl`, `.gz` when compressed) and the manifest it lands under `target.location`, as file drops a pre flow can read |

The delivery flow's OSDU type and cache reads come from its mapping. Lineage reads the mapping the flow pins from the
repository checkout the sync scans (`render.mappings`, or the `mappings` folder `DeliveryLayout` finds), and only from
inside that checkout. A mapping that is missing, outside the checkout, or does not load is a sync warning naming the flow
and the mapping; the flow keeps the rest of its lineage.

The partition is `target.headers.data-partition-id` (delivery) or `source.headers.data-partition-id` (cache and
retrieval), normalized as the cache normalizes it. A header that is a reference stays the reference text: lineage never
resolves a secret or an environment variable.

## 4. How the graph reads for the sample estate

Each delivery flow is listed with everything it reads, and every chain is read right to left: a drop-off folder
lands through `01`, is keyed by `02`, and is delivered by `03`.

```text
wells-welllog-03-header-delivery ──► osdu type: work-product-component--WellLog:1.4.0
  ing.WellLog       ◄── wells-welllog-02-header-ing ◄── pre.WellLog, pre.v_WellLog ◄── wells-welllog-01-header-pre ◄── data/welllog
  ing.WellLogCurve  ◄── wells-welllog-02-curves-ing ◄── ...                        ◄── wells-welllog-01-curves-pre ◄── data/curves-meta
  data/curves (payload files, read at delivery rather than landed)
  osdu cache: UnitOfMeasure, LogCurveBusinessValue, VerticalMeasurementType, Wellbore

wells-wellbore-03-header-delivery ──► osdu type: master-data--Wellbore:1.3.0
  ing.Wellbore      ◄── wells-wellbore-02-header-ing  ◄── ... ◄── wells-wellbore-01-header-pre  ◄── data/wellbore
  ing.WellboreAlias ◄── wells-wellbore-02-aliases-ing ◄── ... ◄── wells-wellbore-01-aliases-pre ◄── data/wellbore-aliases

wells-osdu-00-reference-cache ──► osdu cache types, which wells-welllog-03-header-delivery reads
  osdu type patterns (reference-data--UnitOfMeasure:*, master-data--Wellbore:*, ...)

wells-osdu-04-metadata-retrieval ──► out/metadata (files)
  the same osdu type patterns
```

## 5. Wildcard kinds

A cache or retrieval flow reads kinds with a `*` in any segment. The collector binds each wildcard read, after every
document is scanned, to every OSDU type written in the same platform and partition whose kind matches the pattern
segment by segment (`:` separates the segments, so `*` never spans two). The flow then reads those exact kinds as well as
its pattern node. A pattern nothing in the estate writes stays a source node on its own: reference data the platform
owns is the typical case.

A write never carries a wildcard; a declaration that tries is refused.

## 6. Ordering

Reads and writes of OSDU types and cache types order flows exactly as table reads and writes do:

- A cache or retrieval flow reading a kind a delivery flow writes runs after that delivery flow, and a delivery flow
  reading a cache type runs after the cache flow that writes it. So "run a flow and its descendants" from a pre flow now
  also runs the cache and retrieval flows downstream of the deliveries, which is the order a fresh cache needs.
- SQLFlow already skips the ordering edge between two flows that each read what the other writes (a delivery flow whose
  mapping looks up the very type it writes, and the cache flow that captures that type). Longer loops are reported by
  the sync as a dependency cycle, and their flows go to the fallback wave, as for tables.

## 7. Extension points added to the vendored SQLFlow

All generic: no OSDU name, table or wording. Each lands in its own `sqlflow:` commit.

### 7.1 File locations are anchored at their document

`FlowSetCollector` resolves a relative local location against the folder of the document that declares it, then makes it
relative to the repository root. A location outside the root stays absolute; a URL is kept as written (Azure Storage in
its canonical form); a location that is a reference (`${env:...}`, `@alias`) is kept as written and never resolved.
Producer outputs and consumer sources are put in that same form before they are matched, so a drop and the flow reading
it link whatever folder each document sits in, and `./x` matches `x`.

This changes the node identity of a file read or written by a document that is not at the repository root. A catalog
migration requests a lineage recompute on every repository's next sync, so the stored graph moves to the new identities
at once.

### 7.2 A registered kind describes its lineage

```csharp
public abstract record RegisteredFlowDocument
{
    public virtual IReadOnlyList<DeclaredDataObject> DeclaredObjects => [];

    public virtual RegisteredFlowLineage DescribeLineage(RegisteredLineageContext context)
        => new() { Objects = DeclaredObjects };
}

public sealed record RegisteredLineageContext(string DocumentPath, string EstateRoot);

public sealed record RegisteredFlowLineage
{
    public IReadOnlyList<DeclaredDataObject> Objects { get; init; }
    public IReadOnlyList<DeclaredFileLocation> Files { get; init; }
    public IReadOnlyList<DeclaredDataset> Datasets { get; init; }
    public IReadOnlyList<string> Warnings { get; init; }
}
```

- `DeclaredFileLocation` (`Relation`, `Location`, `FilePattern`): a read is a file fact and a consumer; a write is a file
  producer, reconciled exactly as a copy flow's drop is. A location follows 7.1. A `{token}` in the location cuts it at
  the first tokened segment, since a token renders per run.
- `DeclaredDataset` (`Relation`, `System`, `Instance`, `Namespace`, `Group`, `Name`, `Separator`): a dataset of an
  external system. Its node is `dataset:<system>[:<instance identity>] | <namespace> | <group> | <name>` with the new
  node kind `Dataset`. `Instance` is a reference and goes through `ServerIdentity.From`, so a literal is hashed and never
  stored or echoed. A read's `Name` may carry `*`, bound as in section 5 with `Separator` as the segment separator.
- `Warnings` are added to the sync's warnings, prefixed with the document's path.
- The existing kind contract is unchanged: a kind that only overrides `DeclaredObjects` behaves as before.

Validation refuses the whole document, as it does for a bad `DeclaredDataObject`: a relation other than reads or writes,
a blank location, name, namespace or group, a `|` in a dataset part, a system that is not `[a-z][a-z0-9-]*`, a
wildcard on a write, a file pattern naming a folder, and anything wider than the catalog keeps (a part over 256
characters, an instance over 256, a dataset key over 900, an anchored file location over 512). A description that
throws skips the document with a redacted warning naming it; the scan goes on.

### 7.3 Dataset nodes in the catalog, the API and the GUI

- `LineageNodeKind.Dataset`. A dataset node always carries a database (namespace) and a schema (group), so none of the
  catalog's identity healing for database-less nodes applies to it. The schema browser leaves datasets out, as it leaves
  out files and subscribers.
- The graph captions a dataset node with its system (`osdu type`, `osdu cache`) and places it at `namespace.group`.
- `GET /api/v1/lineage/datasets` lists the dataset nodes for the catalog explorer, which shows them under Datasets,
  grouped by system, namespace and group. An object's page shows a dataset's pipelines as it shows a file's.

### 7.4 A sync extension can ask for a lineage recompute

`ICatalogSyncExtension.LineageInputsChangedAsync` (default: no) lets a module say that a document it owns changed in a
way lineage depends on. The sync consults every extension before its unchanged-estate shortcut.

### 7.5 Declared nodes no flow names any more are swept

A file or dataset node exists only because a flow declares it. When the repository that named one stops naming it (a
folder renamed, a location anchored differently, a mapping that fills another type) and no repository's edges reference
it, the sync deletes the row, as it already did for a database-less twin. Only nodes the syncing repository let go of
are considered, so a row another sync is writing is never touched. The migration `SweepOrphanFileNodes` removes the
file nodes earlier syncs had already left behind without an edge (the machine-path nodes of section 1, item 4).

## 8. The OSDU module's side

- `DeliveryLineage`, `CacheLineage` and `RetrievalLineage` build each flow kind's `RegisteredFlowLineage` from the parsed
  document (and, for delivery, its mapping), in a stable order, without throwing for any document the loader accepted.
  `OsduLineage` checks every OSDU declaration against the widths SQLFlow keeps first, so an unusual value costs one node
  and a warning rather than the flow's whole lineage.
- The mapping is found with `DeliveryLayout.ResolveWithin`, the run's own layout rule stopped at the checkout, and read
  with `MappingCatalog.Load`, which names files relative to the checkout in its messages and refuses a reference whose
  name or version carries a path before any file is looked at (a run gets the same refusal).
- OSDU kinds are split with the module's own kind rules: the group is the entity type's prefix before `--`
  (`master-data`, `reference-data`, `work-product-component`, `dataset`), or `*` for a wildcarded entity type.
- `DeliveryCatalogSync.LineageInputsChangedAsync` compares the repository's mapping documents on disk with the mapping
  rows it last reconciled, by reference and content hash, through the same discovery the mapping sync uses.

## 9. Rules that keep it robust

- Lineage never resolves a secret, an environment variable or a flow parameter; references stay text.
- Lineage reads only inside the repository checkout it scans.
- A problem with one flow's description is a warning naming the flow and what is wrong; it never blinds the estate and
  never fails the sync.
- Every declaration is bounded and checked before a node exists, so a stored key never exceeds its column.
- Output is deterministic: declarations are sorted and de-duplicated, so an unchanged repository produces the same
  edges on every sync.

## 10. Known limits

- **Endpoint and partition are compared as written.** A delivery flow naming its platform `${env:OSDU_URL}` and a
  cache flow naming it with a literal URL are two platforms to lineage.
- **A retrieval flow's relative `target.location`** is resolved by the runner against the process's working directory,
  not the flow file. Lineage places it relative to the flow file, as every other location of the module is resolved, and
  the sync warns about every such flow. The sample's `samples/wells/out/metadata` predates this repository's
  layout; changing the runner or the sample's location is a decision for the owner of the flow.
- **Payload files** are placed at the payload root; the per-record folder under it is only known when a record is read.

## 11. Tests

- **SQLFlow suites** (`LineageFileAnchorTests`, `LineageRegisteredDescriptionTests`, `LineageMigrationTests`,
  `CatalogSyncExtensionIntegrationTests`, `CatalogSyncEndpointSweepIntegrationTests`, `DatasetBrowseApiTests`). A nested file flow reads a repository-relative
  node; two flows in different folders reading `./data` are two nodes; a root producer links to a nested consumer; a
  trailing separator names the same folder; a location outside the root stays absolute; a reference location is kept
  as written. A registered kind's files join the file reconciliation; a tokened location is cut; datasets order their
  writers before their readers; a wildcard read binds segment by segment on its own instance and namespace only, never
  to its own flow; every refusal is named and never echoes a literal instance; a throwing description is a redacted
  warning; description warnings are reported against the document; companions are read from the estate only. The
  extension gate recomputes lineage and a failing gate fails the sync; a node no repository names any more is swept
  and one another repository reads, or none ever named, is kept; the migrations set the recompute flag and remove
  orphan file nodes; the datasets endpoint, a dataset's provenance and the graph caption.
- **OSDU suites** (`LineageTests`). Each flow kind's description for the sample estate, including the mapping's
  template kind, cache reads, payload root, the file protocol's dataset kind and the retrieval's drops and warning; a
  missing, invalid, misfiled or out-of-checkout mapping, a mapping reference naming a path, and a partition that names
  no cache are warnings that keep the rest; the whole sample estate orders files, pre, ing, delivery, cache and
  retrieval flows; the mapping change gate.
- **SQL Server chain suite.** The chain's waves with the delivery kind registered.
- **GUI e2e** (`07-lineage.spec.ts`). The synced fixture lists its OSDU types and cache types with their readers and
  writers; an OSDU type opens in the catalog with the pipeline that writes it and jumps to the graph, captioned
  `osdu type`; the catalog tree groups datasets by system, partition and group.
