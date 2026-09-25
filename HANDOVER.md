# Handover: the mapping document as a record tree

## Where things stand (2026-09-25)

The mapping document is being rebuilt from a flat list of entries into a tree laid out the way the rendered OSDU record
is. The loader side is written and compiles with zero warnings. Nothing else has moved to the new form yet, so **the
mapping documents in the repository, the builder's YAML output and most mapping tests still use the old flat
`mappings:` list and fail to load**. The next steps below finish the change.

What is in the commit:

| File | Change |
| --- | --- |
| `osdu/src/SqlFlow.Delivery/Documents/YamlModels.cs` | `MappingYaml.Mappings` (flat list) is replaced by `Record` (the tree, read as the author wrote it); `MappingEntryYaml` is gone; a fixture's source row is `row`, not `record`. |
| `osdu/src/SqlFlow.Delivery/Documents/MappingMapper.Record.cs` | New. Reads the `record` tree into the same `MappingEntry` list the renderer, preflight, coverage, lineage and builder already use: literals, value nodes (`from`, `value`, `cache`, `search` with `findBy`, `modifiers`, `when`, `required`, `ignoreSeparators`, `description`) and `forEach` arrays with `item`. Columns are scoped: a bare name reads the current row (the item's row under a `forEach`), `dataset.<column>` reads the dataset's own row. Id template tokens and the label are rewritten into the canonical internal form (`{dataset.column}`, `{dataset.child.column}`), so nothing downstream changed. |
| `osdu/src/SqlFlow.Delivery/Documents/MappingMapper.cs` | Reads `dataset.key`, `dataset.identity` and the label with bare column names, calls the tree reader, drops the flat parser (`ParseEntry`, `ResolveRepeaters`, the flat `Source`, `FindByLines`, `Condition`, `Column`), and speaks the new vocabulary in its messages. |
| `osdu/src/SqlFlow.Delivery/Model/MappingDefinition.cs` | `MappingEntry.Location` (`record.data.Curves.item.CurveUnit`); `Where` names an entry by it instead of `mappings[i]`. |

Rendered records are unchanged by design: the tree is read into the existing internal model, so a migrated mapping
renders byte for byte what its flat version rendered. A mapping's content hash is of its text, so a migrated mapping
re-renders every record once; the rendered hash does not move, so `onUnchanged: skip` sends nothing again.

## The decision still open: `$` marks the mapping language

The reader written so far uses plain keywords (`from`, `forEach`, `findBy`...) mixed with the record's own property
names, and refuses a property named like a keyword. The requirement is that naming collisions are impossible for any
template, generically. The agreed direction, to apply first:

- In the `record` tree every mapping keyword starts with `$`: `$from`, `$value`, `$cache`, `$search`, `$forEach`,
  `$item`, `$findBy`, `$modifiers`, `$when`, `$required`, `$ignoreSeparators`, `$description`. Every other key is a
  property of the record. No OSDU property name starts with `$`; a property that does is written `$$name`.
- A map holding any `$` key is a node, and every key in it starts with `$`; mixing the two is refused.
- References carry the marker too, so no column or dataset name can be read as the language: a bare name is a column
  of the current row, `$dataset.<column>` the dataset's own row, `$value`, `$param.<name>`, `$cache.<Type>.<field>`
  inside `{...}` template tokens and `replace: $cache.<Type>`.
- Internally the loaded model keeps its canonical, already unambiguous forms (`{dataset.column}`, `{param.name}`,
  `{value}`, `{cache.Type.field}`); the reader converts. One exception to settle: a literal holding the text
  `{param.x}` without the marker would still read as a parameter internally. Either refuse such literals with a clear
  message, or move the internal token to `{$param.x}` (about 90 uses in `osdu/src`, 40 in the GUI, 90 in tests).

The target shape, for `osdu/samples/recall/mappings/WellLog@1.4.0.yaml`:

```yaml
dataset:
  system: recall
  key: [source_project, log_id]
  label: "{wellbore_uwi} / {log_source} / {log_id}"
  identity: [wellbore_uwi, log_id]

record:
  acl:
    owners: ["{$param.aclOwner}"]
    viewers: ["{$param.aclViewer}"]
  legal:
    legaltags: ["{$param.legalTag}"]
    otherRelevantDataCountries: [NO]
  tags:
    DeliveredBy: osdu-delivery
    LogStatus: { $from: log_pass_type, $modifiers: [{ split: { separator: ",", part: 1 } }] }
  data:
    Name: { $from: log_source, $modifiers: [trim] }
    WellboreID:
      $search: Wellbore
      $findBy:
        - data.FacilityName = wellbore_uwi
        - data.NameAliases.AliasName = wellbore_uwi
    Curves:
      $forEach: curves
      $item:
        CurveID: { $from: curve_id }
        CurveUnit:
          $from: curve_unit
          $modifiers:
            - replace: $cache.RecallUnits
            - id: "{$param.dataPartition}:reference-data--UnitOfMeasure:{$value}:"
        CurveVersion: { $from: curve_version, $required: false }

fixtures:
  - name: ...
    row: { ... }
```

## Next steps, in order

1. **Apply the `$` marker** in `MappingMapper.Record.cs`: the key constants, `$dataset.` in `Column`, the `$value`,
   `$param.` and `$cache.` tokens in `IdColumns`, `replace: $cache.<Type>` before `ParseModifier`, the literal
   `{$param.x}` conversion (and the decision above), the `$$` escape, and refusing a map that mixes `$` keys with
   property keys. `PropertyName`'s reserved-word check then goes away.
2. **Builder** (`osdu/src/SqlFlow.Delivery/Templates/MappingBuilder.cs`): `ToYaml` writes the tree from the draft
   (the draft JSON the GUI sends stays flat and canonical, so the GUI needs no change). Place each entry by its target
   path; a repeater becomes `$forEach`/`$item`; convert canonical columns and tokens to the scoped form; write
   `{ $from: x }` in flow style when a node has nothing else; a draft entry whose target cannot be placed (invalid, or
   both a value and a parent of other entries) is reported by `Incomplete` and left out of the YAML with a comment.
   `Draft` should lay a new template's entries out in the template's own property order, so every template yields a
   mapping document shaped like its record. Key and identity are written bare; fixtures write `row:`.
3. **Census and editor** (`osdu/docs/census/keys.mapping.json`, `EditorCensusTests`): `record` is a map and
   `record.<name>` free-form (the editor's census format has no recursion; the wasm in `sqlflow/tools/sqlflow-lang`
   would need a generic extension point to document nodes at any depth). Replace the `mappings[]...` entries,
   document `record.acl`, `record.legal`, `record.tags`, `record.data` for completion, carry the node grammar and every
   modifier in the `record.<name>` description, rename `fixtures[].record` to `fixtures[].row`, and fix `keyCount`.
   Move the test's modifier check to the new place.
4. **Docs**: rewrite the syntax sections of `osdu/docs/mapping-templates.md` and the mapping parts of
   `osdu/docs/documents.md`; update the examples in `osdu/docs/design.md`, `osdu/docs/cache-lookups-plan.md`,
   `osdu/docs/walkthrough/2-how-we-populate-welllog.md`; add the CHANGELOG entry.
5. **Migrate every mapping**: `osdu/samples/recall/mappings/WellLog@1.4.0.yaml` and the same file in
   `C:\projects\osdu-recall-metadata\recall\mappings`; `osdu/tests/SqlFlow.Delivery.Tests/Fixtures/documents/mappings/`
   (`Document@1.0.0`, `Wellbore@1.0.0`, `WellboreTrajectory@1.3.0`). Keep the comments, they are the documentation.
6. **Migrate the tests**: `TestSupport.MappingDocument` and `BaseEntries` build a `record:` tree (a fragment of
   `record.data` properties merged under the base, plus an override for tests that write the envelope or tags);
   about 110 inline entries in 19 files (largest: `CoreTests`, `CachedReplaceTests`, `SearchSourceTests`,
   `MappingShapeTests`, `MappingCoverageTests`); messages that asserted `mappings[i]` now assert the location. Add
   tests for the tree itself: each node kind, scoping, the `$$` escape, mixed maps, reserved words, a list holding a
   value node, a `$forEach` inside a `$forEach`, and a builder round trip (YAML to draft to YAML is stable).
7. **Verify**: a clean rebuild with zero warnings, `SqlFlow.Delivery.Tests` and `SqlFlow.Delivery.ControlPlane.Tests`
   green (the known failures are listed in the repository memory notes), `tools/check-vendored-sqlflow.sh` passing.
   GUI build and lint need Node, which this machine does not have.

## After this: full OSDU coverage

The tree already has the syntax for these; the engine refuses them today with a clear message:

- **Arrays of values from rows**, such as `data.Datasets[]` ids or string arrays: a `$forEach` whose `$item` is a
  value node.
- **Fixed arrays whose items read columns**, such as a `meta[]` frame of reference or GeoJSON point coordinates: a
  list holding value nodes (needs an index step in `TemplatePath` and in the renderer's `SetPath`).
- **A `$forEach` inside a `$forEach`**, such as `TechnicalAssurances[].Reviewers[]`: the grandchild dataset is read
  like every child (joined on the record key) and filtered per item by `$join` columns at render time, so the source
  reader does not change; `TemplatePath` allows more than one array step, and the renderer, preflight, coverage and
  shape recurse.

## The flow document

Two changes proposed, not started:

- The bulk payload is declared twice, `source.payloads.curves` and `target.protocolOptions.payload: curves`; declare
  it once.
- The ingestion flows call their connection `sample`, a leftover from the old database name.

## Local environment notes

- The local incoming-data database is `OsduData` (schemas `pre` and `arc`; the lookup tables are `Cache*`), its
  connection `OSDU_DATA_DB` in `.sqlflow/env`. `.sqlflow/env` also sets `ControlPlane__ManagedSync__Enabled=true` so
  the local `recall` repo source re-syncs; `dev.bat` no longer forces it off.
- The registered `recall` repo is the local path `C:\projects\osdu-recall-metadata\recall`. Its pipelines in the
  catalog still point at the flow files from before the cache table rename until the next sync runs.
- A running control plane locks the Debug bins; stop it before building the solution.
