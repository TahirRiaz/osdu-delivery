# Sample reference data

Source files for the sample reference snapshot. `capture-spec.json` is what `osdu-delivery snapshot references
--endpoint ... --spec ../capture-spec.json` would use against a real OSDU; the `*.json` type files are what
`osdu-delivery snapshot references --from-dir samples/recall-welllog/references` turns into an immutable,
versioned snapshot under `samples/recall-welllog/snapshots/references/`.

Ids are OSDU record ids without the trailing version colon; the renderer appends it.
