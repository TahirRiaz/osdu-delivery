# Lineage design: OSDU flows, OSDU types and file ingestion

The lineage graph shows every OSDU pipeline as a node, with its data on both sides: the files a pre-ingestion flow reads
and the tables it lands them in, the ingestion tables a delivery flow reads, the mapping it renders with, and the OSDU
types (record kinds in a data partition) the delivery flow writes. Cache and retrieval flows read those OSDU types back
and write the partition cache and files, and each mapping reads the cache types and the kinds it resolves against. With
every edge in place, SQLFlow's waves order the whole estate: files land, ingestion keys them, delivery sends them, and
the flows that read OSDU run after the flows that write it.

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
6. **A mapping was not in the graph, and most of what it reads was missing** (found on the Recall estate, 2026-10-01).
   A delivery flow declared only the cache types its mapping names (`$cache`, a lookup, a replace's table, a token of an
   id). But a mapping also reads the cache without naming a type: every id a `ref` or an `id` modifier builds is looked
   up among the cached records of the entity type it names. Lineage showed none of that, so 8 of the 14 cache types the
   Recall reference flow fills (LogType, LogCurveType, LogCurveFamily, LogCurveMainFamily, LogCurveBusinessValue,
   TechnicalAssuranceType, VerticalMeasurementType, WellLogSamplingDomainType) had a writer and no reader: leaf nodes
   that in fact feed the WellLog records. And nothing in the graph was the mapping itself, so the picture ran from a
   cache type straight to a flow with no sign of the document that decides what is read.

## 2. The node model

| Node | Identity | Shown as |
| --- | --- | --- |
| Flow | The pipeline, as today. Every delivery, cache and retrieval flow is a flow node. | The flow's name, `delivery`/`cache`/`retrieval`, its wave |
| OSDU type | The OSDU platform (the flow's endpoint reference), the data partition, and the record kind | The kind (`osdu:wks:master-data--Wellbore:1.3.0`), captioned `osdu type · dev.master-data` |
| Partition cache type | The data partition and the cache type name | The type name (`UnitOfMeasure`), captioned `osdu cache · dev.cache` |
| Mapping | The OSDU platform, the data partition, the directory the mapping is filed in (relative to the repository root) and the mapping's reference | The reference (`WellLog@1.5.0`), captioned `osdu mapping · dev.recall/mappings` |
| File | The location relative to the repository root (a URL or an absolute path as written) | `data/welllog` |
| Table, view | As today | As today |

An OSDU type is a node per exact kind, version included: that is what OSDU stores records under, and two versions of a
type are two different things to deliver. A flow that reads with a wildcard (a cache type such as
`osdu:wks:reference-data--UnitOfMeasure:*`, or a mapping's search such as `osdu:wks:master-data--Wellbore:*`) has its own
pattern node, which stands for everything of that type the platform holds, and is also bound to every exact
kind in the estate the pattern matches (section 5).

The OSDU platform is part of an OSDU type's identity because it is part of what the record is: two environments may both
have a partition called `dev`. The platform is identified by the endpoint exactly as the flow declares it
(`${env:OSDU_URL}`), which is SQLFlow's rule for every server identity: two flows naming the same platform through two
different references are two platforms to lineage. The partition cache is keyed by partition alone, because that is how
the module keys the cache itself (`CacheScope`).

A mapping is a node per mapping document as one platform's partition renders it. The document itself names neither,
but what it reads does: the cache types are that partition's and the kinds it searches are that platform's. So the same
document pinned for two partitions is two nodes, as its cache types are, each reading its own partition's cache; two
flows pinning it for the same partition of the same platform read one node. A flow that leaves its partitions to the
registry has its mapping under the partition that stands for every registered one (`*`), like everything else of it.

## 3. What each flow contributes

| Flow | Reads | Writes |
| --- | --- | --- |
| Pre-ingestion (`file`) | Its source files, at their repository-relative location | Its landing table and typed view (unchanged) |
| Ingestion (`ing`) | The typed view (unchanged) | Its keyed table (unchanged) |
| Delivery | The record and dataset ingestion tables (unchanged); the payload files under each `source.payloads.<name>.root`; the mapping it pins (`render.mapping`), a node of its own (section 3.1) | The OSDU type its mapping fills (`template.kind`); for `file` and `manifest`, the dataset kind it registers files as (`protocolOptions.datasetKind`) |
| Mapping (no flow: a node the delivery flow reads) | Each partition cache type it names; each cache type of the partition holding records of an entity type its ids are checked against; each OSDU kind its searches look in | Nothing: the flow reading it writes the OSDU type |
| Cache | Each declared type's OSDU kind (`types[].kind`, wildcards allowed) | Each declared cache type (`types[].name`) in its partition's cache |
| Retrieval | Each OSDU kind it retrieves (`source.kinds`) | The record files (`part-*.jsonl`, `.gz` when compressed) and the manifest it lands under `target.location`, as file drops a pre flow can read |
| Assertion | Each test's OSDU kind (`tests[].kind`, one type in one version, never a wildcard), in every partition the flow tests: the node the delivery flow whose mapping names that type writes | Nothing: its reports are kept in the module's database ([assertions-design.md](assertions-design.md)) |

The delivery flow's OSDU type comes from its mapping. Lineage reads the mapping the flow pins from the
repository checkout the sync scans (`render.mappings`, or the `mappings` folder `DeliveryLayout` finds), and only from
inside that checkout. A mapping that is missing, outside the checkout, or does not load is a sync warning naming the flow
and the mapping; the flow keeps the rest of its lineage.

The partition is `target.headers.data-partition-id` (delivery) or `source.headers.data-partition-id` (cache and
retrieval), normalized as the cache normalizes it. A header that is a reference stays the reference text: lineage never
resolves a secret or an environment variable.

### 3.1 The mapping is a node, and it reads what a render reads through it

The mapping is where a record's values are decided, so it is where the graph shows them coming from. The delivery flow
reads the mapping; the mapping reads the cache and the platform; the flow inherits those reads, exactly as a flow
reading a view inherits the view's base tables. So the graph draws

```text
cache flow ──writes──► cache type ──reads──► mapping ──reads──► delivery flow ──writes──► OSDU type
```

and the delivery flow is still ordered after the cache flow, and after the flow delivering the records a search looks
for. Nothing a render reads is left out, because a mapping reads the cache in two ways and lineage follows both:

| How the mapping reads | Written as | What lineage declares |
| --- | --- | --- |
| A cache type by name | `$cache: UnitOfMeasure.id`, a `lookups` entry, `replace: $cache.RecallUnits`, a `{$cache.CurveDictionary.field}` token of an id | That cache type in the flow's partition (`MappingDefinition.CacheTypesRead`) |
| Cached records by what they are | `id: "{$param.dataPartition}:reference-data--LogCurveType:{...}:"`, `ref`, `ref: reference-data--LogType` | Every cache type of the partition holding records of the entity type the id names |
| The platform, as it renders | `$search: Wellbore` under a `searches` entry | The kind the search looks in, wildcards allowed (section 5) |

The second row is the one that was missing. A render looks every id it builds up among the cached records of the entity
type the id names, in every type of the cache holding that entity type, whatever the type is called
(`CachedReferences.Holding`). Lineage answers the same two questions the render does:

- **Which entity type does the id name?** An `id` whose template writes it as text says so itself, and so does a `ref`
  written in full. A `ref` written bare, or by the entity's name alone, takes it from the template variable the node
  fills, through the very function the render uses (`MappingRenderer.ResolveReference`). The template is the one thing a
  mapping's lineage needs that the repository does not hold: it lives in the module database, so a host with that
  database reads the pinned version from it (`MappingTemplateSource`). A saved version never changes, so what lineage
  reads of it is as fixed as the documents are.
- **Which cache types hold that entity type?** The types the cache flows of the scanned estate declare for the same
  partition (`CacheLineage.Held`, read from the documents the scan already parsed).

When the template cannot be read, the flow keeps everything the documents alone tell (the types read by name, the ids
whose entity type is written, the searches) and a warning says exactly what was left out and why: the host has no osdu
database (the offline `sqlflow lineage`), the template version is not saved, or the database did not answer. A template
saved later is picked up by the next sync (section 8).

A mapping that cannot be a node (a directory or reference too long for a node part, a character a node key cannot
carry) is a warning, and the flow then reads what the mapping reads itself, so its order is never lost.

## 4. How the graph reads for the sample estate

Each delivery flow is listed with everything it reads, and every chain is read right to left: a drop-off folder
lands through `01`, is keyed by `02`, and is delivered by `03`.

```text
wells-welllog-03-header-delivery ──► osdu type: work-product-component--WellLog:1.4.0
  ing.WellLog       ◄── wells-welllog-02-header-ing ◄── pre.WellLog, pre.v_WellLog ◄── wells-welllog-01-header-pre ◄── data/welllog
  ing.WellLogCurve  ◄── wells-welllog-02-curves-ing ◄── ...                        ◄── wells-welllog-01-curves-pre ◄── data/curves-meta
  data/curves (payload files, read at delivery rather than landed)
  osdu mapping: WellLog@1.4.0
    osdu cache, named: CurveDictionary, RecallUnits, RecallDepthUnits, UnitOfMeasure
    osdu cache, by the entity type its ids are checked against: LogType, WellLogSamplingDomainType,
      VerticalMeasurementType, LogCurveBusinessValue, LogCurveType, LogCurveMainFamily, LogCurveFamily
    osdu type pattern: master-data--Wellbore:* (searched on the platform as each log is rendered)

wells-wellbore-03-header-delivery ──► osdu type: master-data--Wellbore:1.3.0
  ing.Wellbore      ◄── wells-wellbore-02-header-ing  ◄── ... ◄── wells-wellbore-01-header-pre  ◄── data/wellbore
  ing.WellboreAlias ◄── wells-wellbore-02-aliases-ing ◄── ... ◄── wells-wellbore-01-aliases-pre ◄── data/wellbore-aliases
  osdu mapping: Wellbore@1.0.0

wells-osdu-00-reference-cache ──► osdu cache types, which the WellLog@1.4.0 mapping reads
  osdu type patterns (reference-data--UnitOfMeasure:*, reference-data--LogCurveBusinessValue:*, ...)

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

A mapping's search is a wildcard read of the mapping's node, bound the same way. A flow's own writes are never bound to
its reads, and the same rule holds one step removed: a mapping's pattern is never bound to what a flow rendering with
that mapping writes, so a mapping that looks up the very type its flow delivers does not point the flow at itself.

## 6. Ordering

Reads and writes of OSDU types and cache types order flows exactly as table reads and writes do:

- A cache, retrieval or assertion flow reading a kind a delivery flow writes runs after that delivery flow, and a
  delivery flow whose mapping reads a cache type runs after the cache flow that writes it: what a mapping reads, the flow
  reading the mapping inherits. So "run a flow and its descendants" from a pre flow now
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

### 7.6 A registered kind declares a dataset derived from other datasets

```csharp
public sealed record DeclaredDerivation
{
    public required DeclaredDataset Dataset { get; init; }   // the derived dataset: a read of the flow, no wildcard
    public IReadOnlyList<DeclaredDataset> From { get; init; } // what it is derived from: reads, wildcards allowed
}

public sealed record RegisteredFlowLineage
{
    ...
    public IReadOnlyList<DeclaredDerivation> Derivations { get; init; }
}
```

A derived dataset is a dataset a flow reads that no flow produces: a companion document the flow renders through, a
model, a rule set. Nothing new is stored for it. The collector turns the declaration into the facts the graph already
understands for a view over its base tables: the flow reads the dataset's node, and each source is a read attributed to
no flow, with the dataset's node as its module. Everything that follows is the existing module expansion:

- **Ordering.** A flow reading the dataset inherits its sources, so it is ordered after whatever writes one; the
  inherited read is a flow-attributed edge carrying the dataset as its module, which is also what lets a graph walk reach
  the flow from the flow writing a source.
- **Shared nodes.** Every flow declaring the same dataset shares its node, the sources they declare add up, and each
  source is one fact however many flows declare it.
- **Wildcards.** A wildcard source is bound after the scan like a flow's own (section 5), never to what a flow reading
  the dataset writes.
- **Validation.** Positions are named (`declared derivation 2 source 1`), a derived dataset that is a write, a wildcard
  or its own source is refused, and every check a dataset declaration gets (7.2) applies to both ends.

Three places learn that a dataset can have sources:

- **The drawable graph** (`GET /lineage/project-graph`) draws each source into the dataset (`reads`, no pipeline) and
  the dataset into the flow, and does not also draw the inherited read as a second, direct edge from the source to the
  flow. A read the flow makes in its own right still draws.
- **Object levels** place the dataset after its sources and before what the flow reading it writes.
- **The sync** does not keep a stale inherited edge. An offline sync keeps every stored `Derived` edge, because module
  bodies are only known when a live catalog was reached. A dataset is described by the documents alone, so what a flow
  inherits through one is fully re-derived by every pass, and an inherited edge the pass did not produce is one the
  documents let go of: it is dropped, and the node it pointed at is swept if nothing else names it (7.5).

### 7.7 A description sees the registered documents of the estate

`RegisteredLineageContext.Estate` lists every document of a registered kind the scan found, in path order and as the
loader parsed them. The collector loads every document before it describes any, so a flow whose reads depend on what
another flow declares can describe them from the one parse the scan makes. A document described on its own, outside a
scan, sees an empty estate.

## 8. The OSDU module's side

- `DeliveryLineage`, `CacheLineage`, `RetrievalLineage` and `AssertionLineage` build each flow kind's `RegisteredFlowLineage` from the parsed
  document (and, for delivery, its mapping), in a stable order, without throwing for any document the loader accepted.
  `OsduLineage` checks every OSDU declaration against the widths SQLFlow keeps first, so an unusual value costs one node
  and a warning rather than the flow's whole lineage.
- `MappingCacheReads.Of` says what a mapping reads of the cache: the types it names, and the entity types the ids it
  builds are checked against. It walks the same value nodes and modifiers `MappingDefinition.CacheTypesRead` and the
  renderer walk, and resolves a `ref` through `MappingRenderer.ResolveReference`, the function a render uses, so
  lineage and the render cannot disagree about which records an id is looked up among.
- `CacheLineage.Held` lists the types a cache flow holds in each partition it serves, by the partition as lineage keys
  it: exactly the cache type nodes the flow writes. `DeliveryLineage` reads them from `RegisteredLineageContext.Estate`.
- `MappingTemplateSource` reads the template a mapping pins from the module database, on a host that has one. It is
  given to the delivery kind in the composition root and asked lazily, since whether the host has the database is known
  only once it is built. A host without it, a version that is not saved and a database that does not answer each come
  back as a reason, never as an exception; a failed read stands for 30 seconds, so one database that is down costs a
  scan of many flows one wait and not one per flow. The read is cached per document described and, for a saved version,
  per process by the template store.
- `OsduLineage.Mapping` builds the mapping's node, and `DeliveryLineage` declares the mapping as a derivation
  (section 7.6) whose sources are what `MappingCacheReads` and the mapping's searches give.
- The mapping is found with `DeliveryLayout.ResolveWithin`, the run's own layout rule stopped at the checkout, and read
  with `MappingCatalog.Load`, which names files relative to the checkout in its messages and refuses a reference whose
  name or version carries a path before any file is looked at (a run gets the same refusal).
- OSDU kinds are split with the module's own kind rules: the group is the entity type's prefix before `--`
  (`master-data`, `reference-data`, `work-product-component`, `dataset`), or `*` for a wildcarded entity type.
- `DeliveryCatalogSync.LineageInputsChangedAsync` compares the repository's mapping documents on disk with the mapping
  rows it last reconciled, by reference and content hash, through the same discovery the mapping sync uses. It also asks
  whether a template one of those mappings pins was saved after the sync that last saw the mapping
  (`PinnedTemplateSavedSinceAsync`): a mapping synced before its template was saved had its lineage computed without
  it, and the first sync after the save computes it again. A saved template never changes, so this holds for one sync.
- The same gate asks whether the stored graph holds what the documents describe at all
  (`StoredGraphLacksMappingsAsync`): a delivery flow stored as writing an OSDU type, with no read of a mapping's node, was
  drawn by a build from before mappings were nodes. Upgrading the build changes no document, so without this question
  the old picture (every named cache type wired straight to the flow, the others wired to nothing, no mapping) stayed
  until someone edited a flow or pressed sync now. With it the first ordinary sync after an upgrade computes the lineage
  again. The question is asked of the catalog, where the stored graph is, so once the mapping's node is stored it is
  false and stays false. Two misconfigured flows keep it true, and have their repository's lineage computed on every
  sync until they are put right, each already under a sync warning: one whose mapping cannot be a node, and one whose
  mapping cannot be read while its protocol still registers a dataset kind.
- The cache page's streams (`DeliveryCacheStreams`) read the same edges. A delivery flow is a reader of every cache type
  it reads through its mapping, the ones its ids are checked against included, and a mapping's node is never taken for a
  place a stream's content starts.

## 9. Rules that keep it robust

- Lineage never resolves a secret, an environment variable or a flow parameter; references stay text.
- Lineage reads documents only inside the repository checkout it scans. The one thing it reads elsewhere is the
  template a mapping pins, from the module database: a saved template version is immutable, so the graph is as
  repeatable with it as without, and a host that cannot read it says so and shows the rest.
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
- **Which cache types hold an entity type is read from the scanned repository.** A render looks an id up in every type
  of the partition's cache holding its entity type, whichever repository's cache flow fills it. Lineage sees the cache
  flows of the repository it scans, so a type held only by another repository's cache flow is not shown as read by an
  `id` or a `ref`. A type the mapping names is a node whoever writes it, so that link holds across repositories.
- **An id whose entity type a token gives** (`{$param.dataPartition}:{group}--{entity}:{$value}:`) is of a type only a
  render knows; lineage shows nothing for what it is checked against.
- **A bare `ref` needs the template.** The offline `sqlflow lineage`, which has no module database, shows the types a
  mapping names and the ids whose entity type is written, and warns for each flow about the rest. The sync, which has
  the database, shows them all once the pinned template is saved.
- **A mapping no delivery flow pins is not in the graph.** A mapping is a node because a flow reads it; one filed in
  the repository and pinned by nothing has no reader and no partition to be rendered for.

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
- **SQLFlow suites for the derived dataset** (`LineageDerivedDatasetTests`, `CatalogSyncDerivedDatasetIntegrationTests`,
  `LineageProjectGraphApiTests`). The flow reads the dataset and the dataset its sources, with no flow of its own; the
  flow inherits them and runs after the writer; a wildcard source binds to what the estate writes and never to what a
  flow reading the dataset writes; flows declaring one dataset share its node and each source is one fact; a dataset
  derived from a derived dataset carries its sources through; every refusal names its position and never echoes a
  literal instance; a description sees the estate's registered documents in path order. Against the catalog: the edges
  as stored, the reader's wave, the levels of source, dataset and output, an offline sync dropping the inherited edge
  of a source the documents let go of and sweeping its node, and the dataset's node going with its last reader. The
  project graph draws source, dataset, flow and output with no direct edge from a source to the flow, keeps a read the
  flow makes itself, reaches the reading flow from the flow writing a source, and opens on the dataset's own key.
- **OSDU suites** (`LineageTests`). Each flow kind's description for the sample estate, including the mapping's
  template kind, the mapping's node and everything it reads (by name, by the entity type its ids are checked against,
  and by search), the payload root, the file protocol's dataset kind and the retrieval's drops and warning; a
  missing, invalid, misfiled or out-of-checkout mapping, a mapping reference naming a path, and a partition that names
  no cache are warnings that keep the rest; the whole sample estate orders files, pre, ing, delivery, cache and
  retrieval flows, with one node per mapping shared by the flows pinning it; the mapping change gate. For the mapping's
  reads: another cache flow's type of the same entity type is read too, while a type of another entity, a lookup table
  and a type built for another partition are not; what `MappingCacheReads` tells with and without the template; a host
  with no template store, a template that is not saved and a store that fails each say so in the warning, the failure
  redacted and read once for the whole scan; a mapping that cannot be a node leaves its reads with the flow; a mapping
  rendered for two partitions is a node in each; a mapping filed at the checkout's root; and a template saved after its
  mapping was synced is a lineage input change for one sync. Through a real repository sync with the module's
  extension: a graph stored without its mappings, the documents unchanged, is computed again by the next sync, which
  stores the same edges a first sync does, and the sync after that computes nothing.
- **Cache streams** (`DeliveryCacheStreamsTests`). A type whose content passes through a delivery flow starts at that
  flow's table and files, never at the mapping it renders with or at a cache type nothing fills.
- **SQL Server chain suite.** The chain's waves with the delivery kind registered.
- **GUI e2e** (`07-lineage.spec.ts`). The synced fixture lists its OSDU types and cache types with their readers and
  writers, and the well log mapping as one node both flows pinning it read, with the cache types it never names
  (LogCurveFamily through an `id`, LogType through a `ref`) read by both; an OSDU type opens in the catalog with the
  pipeline that writes it and jumps to the graph, captioned `osdu type`, where the mapping is drawn captioned
  `osdu mapping` with a cache type it only checks its ids against feeding it; the catalog tree groups datasets by
  system, partition and group.
