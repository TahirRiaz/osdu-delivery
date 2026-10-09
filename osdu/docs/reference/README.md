# OSDU Delivery reference corpus

OSDU Delivery is an extension of SQLFlow, and so is its documentation. SQLFlow's corpus
([../../../sqlflow/docs/reference/](../../../sqlflow/docs/reference/README.md) and its wiki) documents the platform:
file and ingestion flows, connections and secrets, schedules, lineage, the run queue, nodes, the control plane, and the
`sqlflow` command line. This corpus documents only what the extension adds or changes, and links SQLFlow's pages for
the rest instead of repeating them.

It is written in SQLFlow's reference format, so the MCP server indexes and searches it exactly as it does SQLFlow's
pages. Every fact traces to the code in this repository (each page's `sourceRefs`); where an older design document
disagrees with the shipped code, the code wins. The design documents and plans in [..](..) and [../../../docs](../../../docs)
are the record of how the system was designed; they are not indexed and not maintained as reference.

## Layout

```text
osdu/docs/reference/
  manifest.json          machine index over every page (MCP search surface), written by build_manifest.py
  cli/<verb>.md          one page per OSDU verb of `sqlflow`, plus what OSDU adds to SQLFlow's run, validate, db, worker,
                         control-plane and auth
  flow/<document>.md     one page per document the extension adds (delivery, retrieval, cache, assertion, dimension and
                         inventory flows, the mapping and its parts, the dictionary), plus the delivery flow's interfaces,
                         routes and DDMSs
  concepts/<slug>.md     how the extension works: the ledger, record lifecycle, submissions, change detection, templates,
                         the partition cache, partitions, protocols, removal and reversal, lineage, the GUI, the API, ...
  guides/<slug>.md       task-oriented walkthroughs, and the pattern catalog that maps a problem to the shape that solves it
osdu/docs/decisions/     the decision records (type decision), indexed beside the reference pages
osdu/docs/census/        the key census of every document the extension adds (the editor and validate_flow surface)
```

Every id starts with `delivery-`, so no page can collide with one of SQLFlow's.

## Page format

Each page is Markdown with YAML frontmatter, then `# <title>`, then the body, as SQLFlow's reference pages are:

```yaml
---
id: delivery-flow-cache
title: "Cache flow (flowType: cache): OSDU reference data and lookup tables in a partition's cache"
type: flow-reference        # cli-command | flow-reference | concept | guide (decision in osdu/docs/decisions)
summary: "One sentence, at most 160 characters, in the words a user asks with."
keywords:
  - lookup table
  - "$cache"
yamlPath: "(root, flowType: cache)"   # flow-reference pages
cliCommand: cache                     # pages of an OSDU verb
related:
  - delivery-concept-partition-cache
  - flow-ing                          # SQLFlow's ids are allowed
sourceRefs:
  - osdu/src/SqlFlow.Delivery/Documents/CacheDocumentMapper.cs
---
```

The search ranks pages on their title, keywords and summary, so those are written for the reader's question: a title
that says what the page is and what it is for, keywords that include the words a user would type (synonyms included),
and a one-sentence summary.

Writing rules: one topic per page; never repeat what a SQLFlow page says, link it; examples use one generic estate (a
well database called `welldb`, the `dev` and `test` partitions) and are validated with `sqlflow validate`; secrets are
references only (`${env:NAME}`, `${keyvault:NAME}`); no em dash.

## Maintenance

Run after adding, removing or renaming a page, or after editing any page's frontmatter:

```bash
python sqlflow/docs/reference/build_manifest.py --reference osdu/docs/reference --wiki osdu/docs/decisions \
  --product "OSDU Delivery" --known sqlflow/docs/reference/manifest.json
```

`osdu/hosts/osdu-delivery-mcp/build.rs` embeds every page the manifest lists into the MCP server, and the server's
tests fail when the manifest is older than a page's frontmatter, a page is missing from it, a related id or a relative
link leads nowhere, or a page carries an em dash.

```bash
cargo test --manifest-path osdu/hosts/osdu-delivery-mcp/Cargo.toml
```
