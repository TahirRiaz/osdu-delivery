# 0003: Rendering runs in the delivery service

Status: proposed. Design reference: sections 4.2, 12.3 and 17.3.

## Context

The mapping could be interpreted in petrodb-api (which currently compiles it) or in the delivery service.
Either works; the pinned inputs are direction-neutral. The choice decides whether the translate renderer is
vendored and whether petrodb-api keeps a mapping at all.

## Decision

The delivery service interprets the mapping (`MappingRenderer`) against its pinned template (the OSDU
schema, saved in the catalog) and a version of the cache (also in the catalog), and sends finished OSDU records. petrodb-api, when used, is a pass-through for the record and the
bulk data; its generated mappers, endpoints and PySpark selects are no longer a contract this system depends
on.

## Consequences

- One interpreted mapping, one place, no generator drift.
- The content hash is computable offline, which is what makes `plan` work without a network and what makes
  change detection honest.
- The reference cache moves out of petrodb-api's per-replica memory into a versioned cache in the catalog, whose
  version the render context records.
- petrodb-api's routes are reached through `protocolOptions` path overrides (`recordPath: /welllogs` and so
  on). If petrodb-api should instead keep rendering, the protocol would post a source-shaped request and the
  service would lose offline planning; that is a regression this decision deliberately avoids.
