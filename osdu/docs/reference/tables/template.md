---
id: delivery-table-template
title: "osdu.Template: each saved OSDU schema version that mappings are checked and rendered against"
type: table
summary: "One row per saved template: the OSDU kind, the content version (a hash of the bundled schema), the bundled JSON Schema, where it came from and who saved it."
keywords:
  - osdu.Template
  - template table
  - saved template
  - OSDU schema
  - template version
  - schema capture
  - import
related:
  - delivery-concept-database
  - delivery-concept-templates
  - delivery-cli-template
  - delivery-table-mapping
  - delivery-table-schema-lineage
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
---

# osdu.Template: each saved OSDU schema version that mappings are checked and rendered against

A template: the OSDU schema of one kind, captured from OSDU or imported from a file, which every mapping for that kind is
checked and rendered against ([Templates](../concepts/templates.md)). Templates are owned by OSDU Delivery. A version is
identified by the kind and the hash of its schema and never changes; a mapping pins one ([`osdu.Mapping`](mapping.md)).
Not a ledger table: it has no `PartitionId`.

A version is deleted only through `DELETE /api/v1/delivery/templates`, which refuses one a synced mapping pins (409
`Template in use`).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Id` | `uniqueidentifier` | no | Stable id, derived from the kind and the version. |
| `Kind` | `nvarchar(200)` | no | The OSDU kind the schema describes (`osdu:wks:work-product-component--WellLog:1.4.0`). |
| `Version` | `nvarchar(64)` | no | The content version: the hash prefix of the canonical bundled schema. |
| `SchemaJson` | `nvarchar(max)` | no | The bundled JSON Schema, every reference resolved into its definitions. |
| `Origin` | `nvarchar(1000)` | no | Where the schema came from: the flow and endpoint reference it was captured through, or the imported file. |
| `CapturedBy` | `nvarchar(200)` | no | Who captured or imported it. |
| `CapturedUtc` | `datetime2` | no | When it was saved. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Template` | `Id` | primary key | none | none | A template by its id. |
| `IX_Template_Kind_Version` | `Kind`, `Version` | unique | none | none | A kind's version, once. |

## Written and read by

- **Written** by `sqlflow template capture` and the Templates page, through `POST /api/v1/delivery/templates`
  (`TemplateStore.cs`, `DeliveryTemplateEndpoints.cs`).
- **Read** by `sqlflow check`, the mapping builder, every render, the repository sync, and the OSDU order's lineage.
