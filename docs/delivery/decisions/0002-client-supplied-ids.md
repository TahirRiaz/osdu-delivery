# 0002: Deterministic, client-supplied OSDU ids

Status: proposed; needs confirmation that the data partition accepts client-supplied ids for the kinds in
scope. Design reference: sections 5.3 and 17.2.

## Context

OSDU record ids may be client-supplied in the form `{partition}:{entityType}:{unique}`. The previous design
let OSDU assign ids and then had to remember them, which is where the duplicate-record defects came from.

## Decision

The unique segment is the delivery key without hyphens. The renderer stamps `id` on every document; the
storage and wellbore DDMS writes are upserts by that id. The ledger's `TargetId` is a convenience, not a
dependency: it can be recomputed from the key at any time.

## Consequences

- Upsert is native; create-versus-update disappears as a concept. A replayed submission cannot create a
  second record.
- References to records this system also delivers can be computed (`deliveredReference`) rather than
  searched.
- If a partition rejects client-supplied ids for a kind, the protocol would need a lookup-by-natural-key
  step before the write and the ledger's `TargetId` would become authoritative. That is a protocol-level
  change; the mapping, hashing and ledger are unaffected.
