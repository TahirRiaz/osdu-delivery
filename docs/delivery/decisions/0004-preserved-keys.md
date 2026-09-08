# 0004: Datasets, DDMSDatasets and ExtensionProperties are OSDU's

Status: proposed. Design reference: sections 7.6 and 16.5.

## Context

The wellbore DDMS populates `data.Datasets` and `data.DDMSDatasets` when bulk data is written, and
`data.ExtensionProperties` may be enriched downstream. None of them comes from the source, so none is in
the rendered document or its hash. An update that replaced the whole `data` block would erase them.

## Decision

They are OSDU's. The flow's `protocolOptions.preserveDataKeys` lists them; before an update the protocol
reads the current record and copies those keys into the outgoing document. They stay outside the content
hash and outside change detection.

## Consequences

- An update never erases bulk-data bookkeeping or downstream enrichment.
- A verify pass that detects drift on those keys alone does not exist: drift is judged by version, and
  reconciling re-sends the rendered document with the keys preserved again.
- If the domain decides one of them is ours (for example a curated `ExtensionProperties`), remove it from the
  list and map it in the mapping, at which point it enters the hash like any other property.
