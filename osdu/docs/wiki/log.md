# Wiki log

Append-only, chronological. Newest entries go at the bottom. Every entry starts with the same
prefix so the log stays greppable:

```
grep "^## \[" osdu/docs/wiki/log.md | tail -5
```

Entry format, enforced by SQLFlow's [lint_wiki.py](../../../sqlflow/docs/wiki/lint_wiki.py):

```
## [YYYY-MM-DD] <ingest|query|lint> | <short title>
```

## [2026-10-21] ingest | Wiki instantiated from SQLFlow's internals wiki

Source: SQLFlow's wiki (`sqlflow/docs/wiki/`: its index, its log, `lint_wiki.py`, and the "Internals Wiki"
section of `sqlflow/CLAUDE.md`).

Established the same three layers for OSDU Delivery: raw sources are the code under `osdu/` with the vendored
`sqlflow/` it builds on, the git history, the sample estate under `osdu/samples/`, and the production estate's
metadata repository; the wiki is `osdu/docs/wiki/`; the schema is the "OSDU Wiki" section of `CLAUDE.md`. Two
conventions differ from SQLFlow's, both because one MCP index holds both wikis: an id is `delivery-wiki-` plus the
filename stem, so no page collides with SQLFlow's `wiki-` pages, and `referenceRefs` and `related` may name
SQLFlow's pages as well as this corpus's. SQLFlow's `lint_wiki.py` lints this wiki through the options a product's
wiki takes (`--wiki`, `--root`, `--manifest`, `--id-prefix`, `--known`), so there is one linter, as there is one
manifest builder.

Moved the seventeen decision records from `osdu/docs/decisions/` into [decisions/](decisions/0001-delivery-grain.md),
keeping their numbered filenames, and folded their README's framing and open questions into
[index.md](index.md). The reference page ids each record listed moved from `related` to `referenceRefs`, `related`
names the records each one links to, and `updated` is the date of each record's last change. Moved the pattern
catalog from the reference guides into [maps/pattern-catalog.md](maps/pattern-catalog.md), where SQLFlow keeps its
own; its rows still point at reference pages until the patterns are ingested.
