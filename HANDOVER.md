# Handover: the mapping document as a record tree, with expressions

## Where things stand (2026-09-25)

The mapping document is laid out the way the rendered OSDU record is, the mapping language is marked with `$`, and a
mapping can compute values and decide conditions with a small expression language of its own. The loader, the
renderer, the preflight, the builder, the census, the GUI, the docs, the samples, the test fixtures and the tests are
all on the new form, and the recall repository (`B:\osdu-recall-metadata\recall`) matches `osdu/samples/recall` file for
file. That work is committed: `f438e4c` here, pushed, and `82b08c3` in the recall repository, which has no remote.
Everything downstream of the sample has since been brought to it (third part, below), committed as `04fa03f` (the
SQLFlow fix), `d882363` and `e17460b`, pushed; the recall repository's matching comment fix is `a33c177`.

```yaml
record:
  acl:
    owners: ["{$param.aclOwner}"]
  data:
    Name: { $from: log_source, $modifiers: [trim] }
    Description:
      $expr: coalesce(log_description, log_source & " run " & log_run)
    SamplingInterval:
      $from: index_increment
      $when: depth_coding = "REGULAR"
    SamplingDomainTypeID:
      $from: index_type
      $modifiers:
        - replace: { DEPTH: Depth }
        - ref
    WellboreID:
      $search: Wellbore
      $findBy: [data.FacilityName = wellbore_uwi, data.NameAliases.AliasName = wellbore_uwi]
    Curves:
      $forEach: curves
      $where: curve_id != "DEPT"
      $item:
        CurveUnit:
          $from: curve_unit
          $modifiers:
            - replace: $cache.RecallUnits
            - ref

fixtureDefaults:
  parameters: { dataPartition: dev, aclOwner: owners@dev }
```

### The record tree (first part of this work)

| Area | Change |
| --- | --- |
| Loader (`osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs`, `MappingMapper.cs`) | Every word of the language starts with `$`; every other key is a record property; `$$name` escapes a property starting with `$`; a map mixing the two is refused; an unknown word is refused with the nearest one. A bare column reads the node's row, `$dataset.<column>` the dataset's row. A literal reads only `{$param.name}`. `replace: $cache.<Type>`. |
| Id templates (`Model/IdTemplate.cs`) | Parsed in the authored form with the node's scope: `{$value}`, `{column}`, `{$dataset.column}`, `{$cache.Type.field}`, `{$param.name}`. |
| Document loader (`DeliveryDocumentLoader.cs`) | Duplicate keys in one map are refused for every document. |
| Builder (`Templates/MappingBuilder.cs`) | Writes the record tree from the flat draft through one `Layout`; entries with no place are left out with a `# Left out:` comment and reported. |
| Payload of a flow (`Model/PayloadParts.Streamed`) | One resolution of the payload a route streams, shared by the plan, the binding check, route checks and lineage. |

### Expressions, ref, fixtures (second part)

The expression language is OSDU Delivery's own, not a library: the user asked for a small, understandable toolset
over copying an engine (JSONata was vendored and then removed; nothing of it remains). Heavy computation belongs in the
ingestion SQL, and the docs say so first.

| Area | Change |
| --- | --- |
| Engine (`osdu/src/SqlFlow.Delivery/Expressions/`) | `ExpressionParser` (tokenizer and recursive descent, bounded at 2000 characters and 48 levels), `ExpressionNodes` (evaluation), `ExpressionValues` (no value, text ignoring case, numbers exact on decimals, dates, booleans), `ExpressionFunctions` (18 functions with SQL names, plus `iif`), `MappingExpression` (the public type: text, columns and parameters it reads, `TryEvaluate`, `TryTest`). Errors name the part and the value; near misses and other languages' spellings (`len`, `isnull`, `==`, `? :`, `status is FINAL`) are answered with what to write. |
| Model (`Model/MappingDefinition.cs`) | `MappingSourceKind.Expression` with `MappingSource.Expression`; `AppliesWhen` and `RowFilter` are `MappingExpression`; `MappingEntry.Expressions`; `Columns` includes what expressions read. `EntryCondition` and `ConditionOperator` are gone, and so are the parked `TextTemplate`, `$from` lists and `is one of`. |
| Loader | `$expr`, `$where` (on `$forEach`, read in the child's scope), `$when` as an expression. `ReadCondition` and `ReadExpression` are the one rule set the loader and the builder share. A condition in the old form is refused with the expression that replaces it (`OldCondition`). Parameters an expression reads must be declared. |
| Renderer (`Rendering/EntryValues.cs`, `MappingRenderer.cs`) | `$expr` values pass through modifiers and `$required` like a column's; `$when` and `$where` evaluate per row; a value an expression cannot work with holds the record. The shape names what `$where` keeps. |
| `ref` modifier | `- ref`, `- ref: UnitOfMeasure`, `- ref: reference-data--UnitOfMeasure`. Resolved once per renderer against the property's `x-osdu-relationship` (`MappingRenderer.ReferenceTemplate`), so render and preflight give the same answer; then it is an `id` template in every respect. The sample WellLog mapping's seven `{$value}` references use it. |
| Preflight (`Validation/Preflight.cs`) | Checks `ref`, expression parameters, and `$expr` like `$from` for written form and shape. `RenderFixtures` is the one fixture rendering, used by the gate and the update verb; `Check(..., fixtures: false)` and `RenderResolver.ResolveAsync(flow, checkFixtures: false)` leave fixtures to the caller. |
| Fixtures | `fixtureDefaults.parameters` merged under each fixture's own. `sqlflow fixtures update <flow.yaml> [--interface] [--dry-run]` (`Engine/FixtureUpdates.cs`, `Documents/FixtureRewriter.cs`, `Cli/DeliveryFixtureVerbs.cs`) writes each fixture's render into its `expected` block, only those lines, in the sample's readable layout; skips a held, failing or unanswerable fixture and a flow-mapping one, naming why, and exits 1 then; writes through a temporary file after reading the result back. |
| Builder and GUI | Draft entries carry `Expression`, `When` and `Where` (text, as the tree writes them) and `FixtureParameters`; `MappingDraftCondition` is gone. The editor has an Expression input, a one-line condition, a repeat's row filter, and `ref` in the modifier list. Build and lint pass. |
| Census, docs | `keys.mapping.json` (53 keys) describes `$expr`, `$where`, the new `$when`, `ref`, every function and `fixtureDefaults`. `mapping-templates.md` has an Expressions section (the rule, the grammar, how values behave, the functions, errors, YAML quoting), `ref`, Fixtures (defaults and the update verb) and YAML anchors; `documents.md`, the CLI reference, the walkthrough and the CHANGELOG are updated. |

Verification done: the solution rebuilds with 0 warnings and 0 errors; the delivery suite against SQL Server passes
everything but the three `SqlServerChainTests` fan-out tests that fail on 584c1b1 too (below); the search suite passes;
the control-plane suite, run in a worktree without `.sqlflow/env`, fails exactly the four tests it fails on the
pre-rebuild commit; the GUI builds and lints clean; `sqlflow validate` passes all 14 recall documents;
`tools/check-vendored-sqlflow.sh` passes (nothing under `sqlflow/` changed); no changed file holds an em dash.

### Everything downstream of the sample (third part)

`samples/wells` was deleted in a7cb146, and what copied or described it was left pointing at it. All of it now uses the
recall sample, in the new mapping format:

| Area | Change |
| --- | --- |
| GUI e2e suite (`osdu/gui/e2e`) | The global setup builds the fixture repository from `samples/recall` plus the fixture wellbore flows, interfaces source, retrieval flow, mappings and reference cache, the estate `SampleEstate.cs` composes for the control plane suites. The seed imports `Fixtures/cache-records`, runs the recall chain into `arc` tables and refreshes `recall-lookups-00-cache`; the OSDU stand-in holds the five Recall wellbores (spaces and slashes as hyphens in the id); every spec names the recall flows through the constants in `global-setup.ts`, and the cache and builder specs read the new `$cache:` vocabulary. |
| Coverage (`Validation/MappingCoverage.cs`) | A literal object or list fills the properties it holds, so the sample's `TechnicalAssurances` literal no longer shows `TechnicalAssuranceTypeID` as required and unfilled. |
| Control plane tests | `DeliveryModuleDatabaseTests` expect the two mappings a copy of the sample holds; the builder test expects the recall mapping as it is. |
| SQLFlow (`sqlflow/src/SqlFlow.Node/GitMaterializer.cs`) | The history clone behind a repo's tree and git history moves HEAD to what a fetch brings. It never did, so the repo page listed the tree the clone was first made from. Its own `sqlflow:` commit, recorded in `docs/sqlflow-changes.md`. |
| Docs | READMEs, operations, CLI references, the notifications guide and the test matrix name the recall estate; the WellLog walkthrough is rewritten against the recall mapping and its fixture; `samples/cache-records/README.md` says who reads those records now. |
| Outside the repository | `.sqlflow/live-e2e/repo/mappings` (git-ignored) converted to the record tree and proven to render byte-identical records to the old form; the recall repository's lookups flow comment matches the sample's. |

A local `OsduDeliveryE2E` seeded before this work holds the old estate's ingestion rows and cache versions, which fail
the cache and records specs; it was dropped once here and recreated by the next run.

## Known failures that predate this work

- Run from the main checkout, control-plane tests pick up `.sqlflow/env` through the test host (`OSDU_ACL_OWNER` among
  others). Set `SQLFLOW_LOCAL_ENV_FILE=false` for the run, which is what a clean checkout gets; SQLFlow's own
  `ControlPlaneLocalEnvFileTests` need the file read, so leave the variable unset for SQLFlow's suites.
- SQLFlow's CLI tests run the Release build of `sqlflow.dll` when one exists, so a stale Release build fails them;
  rebuild `sqlflow/src/SqlFlow.Cli` in Release first.
- `SqlServerChainTests` fan-out tests: see the repository memory note; compare with a baseline run before attributing a
  failure there to a change.

## After this: full OSDU coverage

The tree has the syntax for these and the loader refuses them by name:

- **Arrays of values from rows** (`data.Datasets[]` ids, string arrays): a `$forEach` whose `$item` is a value node.
- **Fixed arrays whose items read columns** (a `meta[]` frame of reference, GeoJSON coordinates): a literal list holding
  value nodes; needs an index step in `TemplatePath` and in the renderer's `SetPath`.
- **A `$forEach` inside a `$forEach`** (`TechnicalAssurances[].Reviewers[]`): the grandchild dataset read like every
  child (joined on the record key) and filtered per item by join columns at render time; `TemplatePath` allows more
  than one array step, and the renderer, preflight, coverage and shape recurse.

## Local environment notes

- On this machine `.sqlflow/env` names two local databases: `SQLFlow` (the catalog) and `OsduDelivery` (the module,
  which also holds the chain's `pre` and `arc` tables). It sets no `OSDU_DATA_DB`, so a dev run of a flow that reads the
  ingestion tables cannot resolve its connection. It also sets `ControlPlane__ManagedSync__Enabled=true`.
- The dev catalog's one repo source is `e2e-repo`, the fixture repository `osdu/gui/e2e/.fixtures/SQLFlow-repo`, synced
  every 300 seconds while the dev control plane runs. The e2e global setup writes it, and running only
  `e2e/03-seed.spec.ts` against the dev pair seeds it (`SQLFLOW_E2E_CATALOG_DB` naming `SQLFlow`, `SQLFLOW_E2E_OSDU_DB`
  and `SQLFLOW_E2E_DATA_DB` naming `OsduDelivery`, `SQLFLOW_E2E_GUI_PORT=5174`). No repo points at the recall repository
  (`B:\osdu-recall-metadata`, which has no remote); register it to sync it on its own.
- A running control plane locks the Debug bins; stop it before building the solution.
