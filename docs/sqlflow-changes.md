# What this project changed in SQLFlow

`sqlflow/` is a squashed git subtree of SQLFlow, vendored at commit
**`fb8d5a64518b355bbb4f563ab6319ea74e6d3fe6`** (`a738c4d` here). This page is the inventory of every change made to it
since, why each was made, and which of them upstream should take. It is written for whoever carries these back to
`B:\SQLFlowV3`, or resolves a conflict during the next `git subtree pull`.

`tools/check-vendored-sqlflow.sh` lists the changed files and fails a commit that mixes `sqlflow/` with anything else,
or that adds a line mentioning OSDU inside it. This page says what the changes are.

## The rules these changes follow

From `CLAUDE.md`, and every change below keeps to them:

- **Generic only.** A change to `sqlflow/` is an extension point any module could use, or a fix to something that is
  wrong for every user of SQLFlow. No OSDU code, name, table or wording goes into it; the OSDU side of every extension
  point lives in `osdu/`.
- **Its own commits.** A commit that changes `sqlflow/` touches nothing outside it, and its subject starts with
  `sqlflow:`. The OSDU work that uses an extension point is a separate commit.
- **SQLFlow's standards apply inside it.** A catalog change ships with its EF Core migration, the solution builds with
  zero warnings, SQLFlow's existing flow kinds keep their behaviour, and its own suites pass, with new tests for each
  extension point.

## 1. Fixes upstream wants regardless of this project

These are not extension points. They are defects or debts in SQLFlow that any user of it has, found while building on
it, and they are the ones to carry upstream first.

| Commit | What was wrong | What it does now |
| --- | --- | --- |
| `05d6729` | The CLI took an option it did not know for a flag, and read the token after it as a positional argument. `sqlflow run flow.yaml --interface documents` ran the whole source and said nothing: the narrowing was silently lost. | Every option SQLFlow's verbs read is declared, value-taking ones beside flags, and a command carrying one that neither SQLFlow nor the module verb it names would read is refused, with the nearest declared option suggested. `CliOptionInventoryTests` reads the CLI's own sources and fails when a verb starts reading an option that is in neither set. |
| `477f2cd` | `SSH.NET` 2024.2.0 carries GHSA-q939-rpr3-3284 (high severity). NuGet reported NU1903 on every build of `SqlFlow.Acquire` and `SqlFlow.Sftp`, and through them on anything referencing them, so every build had a standing warning to ignore. | Pinned at 2026.0.0, which needs no code change; both projects build clean and their suites pass unchanged. |
| `21ef3bf` | The eight `Microsoft.Extensions.*` and `System.Diagnostics.DiagnosticSource` pins sat at 10.0.9 while the servicing band had moved to 10.0.12, so a host adding any library built against the current band could not restore (`NU1109`). | The pins move together, on one current band. |
| `6e0db96` | `--branch`, `--default-type` and `--drain-seconds` are read with the readers that consume the next token, and were not declared as taking one, so the token after each became a positional argument: `sqlflow worker --drain-seconds 30` read `30` as the verb's file and left the drain timeout at its default. | They are declared as value-taking, and the inventory test checks how each option is read rather than only that it is known. |
| `3c7ebbd` | The repo detail page merges two sources of truth that move at different speeds, and drew their disagreement as fact. The content listing is read from the branch on every request; the catalog only moves when a sync runs. Between a rename (or a delete) on the branch and the next sync, the catalog still held a pipeline whose file was gone, while the file that replaced it matched no registered path and fell through into the folder's plain files. Every renamed flow was therefore listed twice, once as a pipeline under the name it no longer had and once as a file under its new one, with nothing to tell them apart. A rename of eighteen flows drew thirty-six rows for eighteen files, which reads as a broken sync rather than as a sync that has not run yet. | The branch decides what the repo holds. A pipeline whose file is not on the branch is not listed, and the reader is told how many were left out and that the next sync retires them, rather than being left to wonder where a flow went. The conclusion is only drawn from a listing that covers the whole branch: with no readable listing, or one clipped at the server cap, nothing is known about what it left out and every pipeline is kept as before. |
| `cc18cc3` | The language engine headed every key's hover and completion documentation with the key and its type joined by an em dash, which SQLFlow's writing style forbids everywhere; with a host module's census registered, it showed on every key of the module's documents too. | The header reads `key`: `type`, and the hover test pins that and refuses the character. A test comment in the same crate lost its em dash as well. The rest of the tree still carries a few (the VS Code extension, the MCP server, one concepts page and some test data), left for SQLFlow itself so the vendored copy does not drift further. |
| `0ed0bda` | A run the CLI recorded into a repository (`sqlflow run flow.yaml --db ... --repo ...`) took the flow file's own folder as the repository root: it rewrote the repo's root path and upserted the pipeline with its bare file name, so every recorded run moved its pipeline to the repository's top level until the next sync. | A recorded run uses the root the sync would: the repo's recorded root when the flow lies inside it, else the root of the repository the flow is checked out in (the nearest `.git`), and only outside any repository the flow's folder. `CatalogRunRecordRootTests` covers each case. |
| `97a88c1` | An HTTP status failure carried no `Retry-After`, so a caller that wanted to honour a service's own wait had nothing to honour. | `HttpStatusException` carries the wait the service asked for. |
| `1616141` | Lineage recorded a flow's file locations as machine paths under the node's cache, so the same estate produced different nodes on different machines. | File locations are anchored at the declaring document's folder and made repository-relative. |
| `d81a817` | A declared file or dataset longer than the catalog's column silently failed the sync. | Declared files and datasets are bounded to the catalog's widths. |
| `ce119f2` | Nodes for files and datasets no declaration named any more were never removed, so a graph kept growing. | The sync sweeps them, and only the ones the syncing repository let go of (migration `SweepOrphanFileNodes`). |
| `1dfc15f` | The run group companion had no caller left. | Removed. |
| (this change) | `TruncatedText` put its pixel cap in an inline `style`, which beats the `max-w-full` class beside it. In a container narrower than the cap, the value rendered at the full cap and ran out of its cell: on a detail card a staged payload's path was drawn 260px wide in a 202px column, on top of the value in the next column. The component that exists so a long value can never blow out a layout was the one blowing it out. | The cap carries the container bound with it (`min(100%, <cap>px)`), so the value clips at whichever is smaller and the ellipsis and hover panel behave the same either way. It is a fix upstream wants regardless of this project: every detail grid, table and drawer in SQLFlow's own GUI renders values through this component. |

| `ae18cce` | `sqlflow validate` on a folder read a shared-schedule library (`schedules.yaml`, `*.schedules.yaml`) as if it were a flow document, so a library entry the estate scan would drop (a warning `YamlScheduleLibraryLoader` raises, leaving the flows that join it without a schedule) passed silently, or the file was reported broken for the wrong reason. A subscriber library (`*.subscribers.yaml`) is not a flow either. | A library is read by `FlowSetCollector.IsScheduleLibraryFile`/`IsSubscriberLibraryFile` on its own terms: a schedule library through the same `YamlScheduleLibraryLoader` the scan uses, any warning making it broken; a subscriber library is skipped, since it declares nothing the scan checks. `CliOfflineVerbTests` covers a library the scan reads clean and one with a warning the scan would drop. |

### Test determinism

Five suites failed on timing or on each other rather than on the code. Each is a fix upstream benefits from, because the
same races are there for anyone running the suites in parallel or at the wrong moment of the day.

| Commit | What raced |
| --- | --- |
| `a5d3e00` | The database integration tests, when run in parallel against one server. |
| `eefbb04` | The assertion store test deleted other integration tests' assertions. |
| `f39459c` | The kind-arguments trigger test raced the host's in-process worker. |
| `0e924f0` | The sync extension tests left lineage objects another test's sync swept up. |
| `fe71d0e` | The notification digest clamp test asked for "today" and expected a clamp to now; between 00:00 and 00:05 UTC, today so far is shorter than the smallest period a digest may cover, so the endpoint refused it and the test failed for a reason other than the one it checks. |

## 2. The extension points this project needed

Each of these lets a host add something of its own without SQLFlow knowing what it is. The OSDU side of each lives in
`osdu/` and is named here only to say what the point is for.

### Composition

| Commit | The extension point |
| --- | --- |
| `a96d215` | A host composes the control plane and the CLI with modules of its own (`IControlPlaneModule`, `ICliModule`, `ControlPlaneHost`, `CliHost`). |
| `7773685` | A host brands the product the control plane and the CLI name (`ProductBranding`). |
| `fba2ca6` | A host builds its GUI from SQLFlow's workbench with modules of its own (the GUI module registry and bootstrap). |
| `3561809` | A host module declares its own database, migrated, reported and verified alongside the catalog (`ModuleDatabase`), so a module's schema upgrades without touching SQLFlow's. |
| `a1742b2`, `f299024`, `30e8e4f` | A module asks whether its rows are reachable on a host's connection (`ModuleDatabase.IsReachableOn`), which is what decides whether it may share the host's transaction. A sync extension whose tables are in a database of its own cannot: Azure SQL has no cross-database statement, and the two databases may not share a server. The `ICatalogSyncExtension` contract says what each kind of extension must then do. |

| `PENDING` | The control plane reads the local development env file the CLI has always read (`ControlPlaneHost.ApplyLocalEnvFile`, `SQLFLOW_LOCAL_ENV_FILE` to opt out). A host and the documents it runs now live in one checkout, so the nearest `.sqlflow/env` at the content root or any parent is that deployment's, and the in-process node resolves a flow's `${env:...}` against this very process. The process environment still wins, and a test estate opts out so it never picks up a developer's real credentials. Upstream wants this regardless: it removes a launcher script's job of copying the file in by hand. |

### Flow kinds

| Commit | The extension point |
| --- | --- |
| `cf1af16` | A host registers flow kinds, companion documents and executors with the loader (`IFlowDocumentKind`, `ICompanionDocumentKind`, `IFlowDocumentExecutor`). |
| `6bcb15c` | The envelope's lifecycle (active, description, schedule) is stamped onto documents of a registered kind, so they behave like built-in ones in the catalog. |
| `879305b` | A run or schedule carries a registered kind's operation, values and payload, and who asked for it (`RunParameters`, `FlowKindOperation`, migration `RunKindArgumentsAndRequester`). |
| `85438d5` | A host module registers compute operations beside the built-in datasource ones (`IComputeOperation`). |
| `d054a9c` | A running flow fans its work out to member runs over the node protocol, and a registered kind's run result is recorded (`IRunFanOut`, migration `RunFanOutAndResult`). |
| `350342b` | The pipelines filter offers the flow kinds registered modules add. |
| `0b503c7` | A flow's file selection is read from its stored definition rather than re-read from disk. |

### The catalog, lineage and search

| Commit | The extension point |
| --- | --- |
| `876e5a9` | A host module reconciles its own documents inside the repository sync transaction (`ICatalogSyncExtension`), so a module's rows commit with the catalog's. |
| `4076a5c` | A caller enqueues a run group and commits its own rows in the same transaction. |
| `2dbfb54` | A registered flow kind declares the database objects it reads and writes, so lineage orders it in waves with everything else. |
| `d2cbd3a` | A registered flow kind describes its files and datasets in lineage (node kind `Dataset`, `GET /lineage/datasets`, migration `RequestLineageRecompute`). |
| `d70d00f` | A host module adds a search category beside the built-in catalog surfaces (`ISearchContributor`). |
| `0677c69` | A search contributor asserts the result contract, so a broken contributor is caught at startup rather than in a search. |
| `929e535` | A link sets the pipelines page's repo and kind filters. |
| `774401a` | The caller of an attributed write is named through one public rule a host module can use (`RequestActor`). |
| `f217fe1` | A repository's contents read as the folder tree they are, at any depth, instead of one flat list per top-level folder. A project's card renders through the shared workbench tree primitive (`components/Tree.tsx`), which gained an optional `testId` on a row; `features/pipelines/folderTree.ts` builds the tree from repo-relative paths. It matters to any repository laid out with folders inside a project, which is every repository that groups a source's flows, mappings and data together. |
| `ca779a3` | A host module registers key census files for its flow kinds (by `flowType`) and documents (by `documentType`), so the language server and the GUI's YAML editor document, colour and check them as they do SQLFlow's own flows (`census::register`, `initializationOptions.censusDirectories`, `register_census`, `GuiModule.census`). A census may say its loader refuses unknown keys (`strictKeys`), take the platform envelope (`includeEnvelope`) or the shared blocks (`includeShared`), and mark an entry `freeForm`. A `documentType` nothing registered is noted once rather than analysed as a file flow. The same commit documents `schedule.operation` and `schedule.values` in the file flow census, which the envelope schedule already read; that part is a fix upstream wants regardless. |
| (this change) | The catalog finds the runs that handled a file by its name (`catalog.RunFile(Name, RunId)`, migration `RunFileByName`). The catalog already answered "what did this run process"; this is the other direction, "what has been done with this file", which is what tracing a row back through the flows that landed and loaded it needs, and without it that question scans every processed file the estate has recorded. It is a fix upstream wants regardless of this project: any estate that asks where a file went pays the scan today. |

## 3. Taking these upstream

1. The fixes in section 1 stand alone: each is one commit against `sqlflow/`, with its tests, and none depends on
   anything in `osdu/`.
2. The extension points in section 2 are additive. SQLFlow's own flow kinds and endpoints keep their behaviour, and
   every point has tests in `sqlflow/tests` that exercise it with a probe module rather than with this project's.
3. Five catalog migrations came with them: `RunKindArgumentsAndRequester`, `RunFanOutAndResult`,
   `RequestLineageRecompute`, `SweepOrphanFileNodes` and `RunFileByName`. They are SQLFlow's own, in SQLFlow's schema.

When SQLFlow is pulled in again (`git subtree pull --prefix=sqlflow --squash B:/SQLFlowV3 main`), a conflict can only
arise in the files that carry the points above. Resolve it keeping both SQLFlow's change and the extension point, then
run `tools/check-vendored-sqlflow.sh`, build the solution clean, and run SQLFlow's suites and this project's.
