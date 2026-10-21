---
id: delivery-table-mapping
title: "osdu.Mapping: each mapping document a repository sync found, the read model behind the mappings page"
type: table
summary: "One row per mapping document a repository holds: its reference, the kind and template version it pins, its text, a parsed summary, and whether it loads."
keywords:
  - osdu.Mapping
  - mapping table
  - mapping read model
  - repository sync
  - template version
  - mapping reference
related:
  - delivery-concept-database
  - delivery-flow-mapping
  - delivery-concept-templates
  - delivery-table-template
  - delivery-table-interface
sourceRefs:
  - osdu/src/SqlFlow.Delivery.Data/DeliveryEntities.cs
  - osdu/src/SqlFlow.Delivery/Catalog/DeliveryCatalogSync.cs
  - osdu/src/SqlFlow.Delivery/Ledger/OsduLedger.Mappings.cs
  - osdu/src/SqlFlow.Delivery.ControlPlane/Api/DeliveryEndpoints.cs
---

# osdu.Mapping: each mapping document a repository sync found, the read model behind the mappings page

A [mapping document](../flow/mapping.md) as the repository sync found it: the read model behind the mappings page. The
repository is the only place a mapping is defined; every sync writes the rows of its repository again, and deletes the
row of a mapping the repository no longer holds. Not a ledger table: it has no `PartitionId`, and names its repository by
id, with no foreign key into SQLFlow's catalog.

## Columns

| Column | Type | Null | What it holds |
| --- | --- | --- | --- |
| `Id` | `uniqueidentifier` | no | Stable id, derived from the repository id and the mapping reference. |
| `RepoId` | `uniqueidentifier` | no | The SQLFlow repository the sync found it in, by id. |
| `Reference` | `nvarchar(200)` | no | The mapping's reference, `<name>@<version>`. |
| `Name` | `nvarchar(150)` | no | The mapping's name. |
| `Version` | `nvarchar(50)` | no | The mapping's version. |
| `Kind` | `nvarchar(200)` | no | The OSDU kind of the template the mapping fills. |
| `TemplateVersion` | `nvarchar(64)` | no | The template version the mapping pins, also for a document that fails to load; empty when it names none ([`osdu.Template`](template.md)). |
| `RelativePath` | `nvarchar(1000)` | no | The mapping's file, relative to the repository root. |
| `ContentHash` | `nvarchar(64)` | no | The hash of the document as synced. |
| `Yaml` | `nvarchar(max)` | no | The document text, secret-redacted, as it was when synced. |
| `SummaryJson` | `nvarchar(max)` | no | A parsed summary for listings: the template, the dataset system, key and label, child datasets, entry and fixture counts, the envelope. |
| `Status` | `nvarchar(16)` | no | `valid` or `invalid`. |
| `Message` | `nvarchar(4000)` | yes | Why the document does not load, for an invalid one. |
| `FirstSeenUtc` | `datetime2` | no | When a sync first found it. |
| `LastSeenUtc` | `datetime2` | no | When a sync last found it. |

## Keys and indexes

| Index | Columns | Kind | Filter | Includes | What it serves |
| --- | --- | --- | --- | --- | --- |
| `PK_Mapping` | `Id` | primary key | none | none | A mapping by its id. |
| `IX_Mapping_RepoId_Reference` | `RepoId`, `Reference` | unique | none | none | A repository's mapping by its reference, once. |
| `IX_Mapping_RepoId_Kind` | `RepoId`, `Kind` | index | none | none | A repository's mappings of one kind. |
| `IX_Mapping_Kind_TemplateVersion` | `Kind`, `TemplateVersion` | index | none | none | The mappings pinning a template version: what a template delete asks. |

## Written and read by

- **Written** by the repository sync (`DeliveryCatalogSync.cs`).
- **Read** by the mappings page, a template's page and its delete, the mapping builder, lineage, the search terms, and
  the delivery API (`OsduLedger.Mappings.cs`, `DeliveryEndpoints.cs`).
