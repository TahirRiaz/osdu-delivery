---
id: flow-subscribers
title: subscribers.yaml and consumption lineage
type: flow-reference
summary: Declare who consumes the warehouse in a subscribers.yaml; every subscriber query is parsed into read edges, so lineage answers which report uses which table.
keywords:
  - subscribers.yaml
  - data subscriber
  - subscriber
  - consumer
  - powerbi
  - tableau
  - dashboard
  - report
  - consumption lineage
  - who reads this table
  - impact analysis
  - downstream
  - DataSubscriber
  - DataSubscriberQuery
yamlPath: subscribers
related:
  - concept-lineage-graph-and-plan
  - concept-lineage-tiers
  - flow-schedule
  - flow-overview
sourceRefs:
  - src/SqlFlow.Core/Subscribers/DataSubscriber.cs
  - src/SqlFlow.Yaml/YamlSubscriberLibraryLoader.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Lineage/Collection/LineageFacts.cs
  - src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs
  - src/SqlFlow.Core/Lineage/LineageReport.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs
---

# subscribers.yaml

Lineage without the consumption side stops at the last table SQLFlow writes. `subscribers.yaml` continues it: it declares the reports, workbooks, notebooks, and applications that READ the warehouse, and the queries each one runs. Those queries are parsed, so the objects they touch become real lineage edges on the same nodes the loading flows write. The estate can then answer, from the graph rather than from memory, which dashboards break if a table changes.

This is the V3 form of the legacy `flw.DataSubscriber` and `flw.DataSubscriberQuery` tables. The legacy pair carried `FlowID`, `FlowType: 'sub'`, and a `Batch`, purely so the old catalog could log a row per subscriber; a subscriber runs nothing, so V3 drops that plumbing and identifies a subscriber by name like every other object.

```yaml
# subscribers.yaml
connections:
  dwh: ${env:SQLFLOW_CONN_DWH}

subscribers:
  Analyse_Bysykkel:
    type: PowerBI
    owner: analyse@kolumbus.no
    description: City bike usage and station occupancy
    url: https://app.powerbi.com/groups/me/reports/abc123
    server: dwh
    queries:
      - name: Turer
        sql: |
          SELECT t.TripId, t.StartedAt, s.StationName
          FROM   arc.Bysykkel_Trips AS t
          JOIN   arc.Bysykkel_Stations AS s ON s.StationId = t.StartStationId
      - name: Stasjoner
        sql: |
          SELECT * FROM pre.v_Bysykkel_Stations
```

## Where the file lives

A subscriber library is any file named `subscribers.yaml` or ending in `.subscribers.yaml`, anywhere under the scanned folder. One file for the whole estate is the intended shape; several are allowed (`analyse.subscribers.yaml`, `drift.subscribers.yaml`) and are merged. Like `schedules.yaml`, the file is NOT a flow document: it is excluded from the flow parse, never becomes a pipeline, never joins a schedule, and never appears in an execution wave. A subscriber that leaked into the flow set would sit in a wave forever waiting to run a Power BI report.

## Keys

| Key | Required | Meaning |
| --- | --- | --- |
| `connections` | yes, when any query names a server | The same `connections:` block every flow document uses: alias to a SQL Server reference. A bare alias resolves `${env:SQLFLOW_CONN_<NAME>}` by the canonical convention, so the file can stay reference-free. |
| `subscribers` | yes | Maps a subscriber's NAME to its declaration. The name is its identity across the estate and the label on its graph node. |
| `subscribers.<name>.type` | no (warns) | What consumes the data (legacy `SubscriberType`): `PowerBI`, `Tableau`, `Excel`, `Notebook`, `Application`, or any label the estate uses. Free text, as the legacy column was. Omitted records `Unknown` with a warning. |
| `subscribers.<name>.owner` | no | Who to contact before a breaking change to a table it reads (legacy `CreatedBy`). |
| `subscribers.<name>.description` | no | One line, for the catalog and the node's tooltip. |
| `subscribers.<name>.url` | no | Where the subscriber lives: report URL, workbook path, repository. |
| `subscribers.<name>.server` | no | The default connection alias for every query that does not name its own. |
| `subscribers.<name>.queries` | yes, in practice | The queries the subscriber runs. A subscriber with none is a node nothing connects to, which is warned. |
| `queries[].name` | no | The query's label (legacy `QueryName`): the dataset, page, or measure group. Defaults to `query<n>` by position. |
| `queries[].server` | yes, unless the subscriber sets one | The connection alias this query runs against (legacy `srcServer`). It is what pins two-part names to the right server and database. |
| `queries[].sql` | yes | The query text as the subscriber runs it (legacy `FullyQualifiedQuery`). Any T-SQL the parser accepts. |

A malformed entry is dropped with a warning rather than throwing, exactly as an unparseable flow document is: one bad subscriber must not blind the estate's lineage. A query whose `server` is not declared in `connections:` is refused, because its objects would otherwise land on an invented identity and quietly build a second, wrong graph.

## How a query becomes lineage

Each `sql` goes through the same `TSqlLineageExtractor` a stored-procedure body, a document hook, and a generated transform view go through. The resulting facts are attributed as MODULE facts: `Flow` is null and `ViaModule` is the subscriber's node key. That is precisely what a subscriber is to the graph, a body of SQL that reads objects but runs no pipeline, so nothing in the edge model, the execution plan, or the wave computation needed changing to hold it.

Because the identities go through the same completion (default database from the connection, identity unification, synonym follow) as every other fact, a report reading `arc.Bysykkel_Trips` lands on the SAME node the ingestion flow writes. A report reading a view lands on the same view node the flows read, and the view's own module edges continue the chain down to its base tables.

The subscriber itself becomes an object node of kind `Subscriber` on the synthetic server identity `subscriber`. It is the only node kind that lives outside the databases SQLFlow moves data between, and the only one no database inventory can supply.

Joins written inside a subscriber query also feed the interpreted data model (`LineageReport.Relationships`). The joins an analyst writes in a report are evidence of how the business actually relates these tables, and carry the same weight as a warehouse view's.

## What lands in the catalog

`sqlflow db sync` mirrors subscribers into `catalog.Subscriber` and their queries into `catalog.SubscriberQuery`, repo-scoped and replaced wholesale on each sync, so a subscriber deleted from the YAML stops being listed as a consumer. `FirstSeenUtc` survives the replacement, so the catalog can still say how long a report has been reading the warehouse. Query text is redacted on the same path module bodies take, since authored SQL can embed a literal credential.

The consumption itself is not stored twice: the read edges are ordinary `catalog.LineageEdge` rows, so "what consumes table X" is the same edge query as "what writes table X".

## API

| Endpoint | Answers |
| --- | --- |
| `GET /lineage/subscribers` | What consumes the warehouse. Filter by `type` (the tool) or `search` (name, owner, description). Each row carries how many queries it runs and how many distinct objects those queries read. |
| `GET /lineage/subscribers/dossier?key=<node key>` | What one subscriber consumes: its queries, and every object they read, named and located from the object registry, with the queries that reference each one. |
| `GET /lineage/objects/dossier?key=<node key>` | Now also returns `subscribers`: who consumes THIS object, with the specific queries that name it. |
