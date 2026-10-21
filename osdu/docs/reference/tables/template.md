---
id: delivery-table-template
title: "osdu.Template: each saved OSDU schema version, the library the pages, the explorer and the builder read"
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
  - delivery-table-repository-template
  - delivery-table-schema-lineage
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Templates/TemplateStore.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryTemplateEndpoints.cs
---

# osdu.Template: each saved OSDU schema version, the library the pages, the explorer and the builder read

A saved template, the library ([Templates](../concepts/templates.md)): the OSDU schema of one kind, captured from the
OSDU data definitions, imported from a file, or held by a [template folder](../concepts/templates.md#template-folders) of a
synced repository, which the sync saves here ([`osdu.RepositoryTemplate`](repository-template.md)). A version is identified
by the kind and the hash of its schema and never changes. A repository's documents read their templates from their
template folders; the library is what the pages, the explorer and the mapping builder read, and a run reads a version
from here only to check a record against the exact version it was rendered for when no folder holds that version any
more. Not a ledger table: it has no `PartitionId`.

A version is deleted only through `DELETE /api/v1/delivery/templates` (or `sqlflow template delete`), which refuses one a
repository's template folder holds or a synced mapping fills (409 `Template in use`).

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Id` | `uniqueidentifier` | no | Stable id, derived from the kind and the version. |
| `Kind` | `nvarchar(200)` | no | The OSDU kind the schema describes (`osdu:wks:work-product-component--WellLog:1.4.0`). |
| `Version` | `nvarchar(64)` | no | The content version: the hash prefix of the canonical bundled schema. |
| `SchemaJson` | `nvarchar(max)` | no | The bundled JSON Schema, every reference resolved into its definitions. |
| `Origin` | `nvarchar(1000)` | no | Where the schema came from: the OSDU data definitions release and file, the imported file, or the template folder file and repository a sync saved it from (`template folder file <path> of repository <id>`). |
| `CapturedBy` | `nvarchar(200)` | no | Who captured or imported it, or `repository sync`. |
| `CapturedUtc` | `datetime2` | no | When it was saved. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Template` | `Id` | primary key | none | none | A template by its id. |
| `IX_Template_Kind_Version` | `Kind`, `Version` | unique | none | none | A kind's version, once. |

## Written and read by

- **Written** by `sqlflow template capture` and `import`, the Templates page, through `POST /api/v1/delivery/templates`
  (`TemplateStore.cs`, `DeliveryTemplateEndpoints.cs`), and the repository sync, for every version a template folder
  holds (`DeliveryCatalogSync.cs`).
- **Read** by the Templates page, the explorer, the mapping builder, the pages that settle a repository document's
  template (which read its schema here), the OSDU order, and a run checking a record against the version it was
  rendered for.
