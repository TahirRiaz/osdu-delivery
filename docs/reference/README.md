# OSDU Delivery reference corpus

Reference documentation for the platform OSDU Delivery runs on, inherited from the SQLFlow V3 corpus and kept
for the pages that still describe shipped behavior: the CLI commands, the cross-cutting concepts (the control
plane, authentication, the catalog, run artifacts, connections and secrets, environment variables, flow
identity), and the deployment, getting-started, and notification guides.

Every page carries YAML frontmatter (`id`, `title`, `type`, `summary`, `keywords`, `related`, `sourceRefs`, plus
`cliCommand` on command pages), and `manifest.json` indexes them. Regenerate the manifest with
`python docs/reference/build_manifest.py` after adding, removing, or renaming a page.

## Layout

```text
docs/reference/
  manifest.json          machine index over every page
  cli/<command>.md       one page per CLI command that survived the platform strip
  concepts/<slug>.md     cross-cutting concepts
  guides/<slug>.md       task-oriented walkthroughs
```

## Where the corpus stands

These pages describe the platform as it was inherited. Their prose is accurate for the control plane, the
catalog, the run queue, authentication, and the CLI verbs that remain, but their examples still show the
SQLFlow flow kinds (`ing`, `file`, `hc`) that no longer exist. Each page is rewritten around the delivery flow
kind as that kind lands, and the pages for the delivery domain itself (the flow and mapping documents, the
ledger, the record operations) are added alongside it.
