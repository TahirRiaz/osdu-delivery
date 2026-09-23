# How OSDU Delivery fills a WellLog 1.4.0 record

This file goes with [1-welllog-schema.md](1-welllog-schema.md), which lays out the OSDU schema as OSDU defines it. The
section numbers here are the same, so section 2.4 in this file explains how we fill section 2.4 in that one.

Everything below is taken from the sample estate:

| Input | File | What it decides |
| --- | --- | --- |
| The ingestion tables | `OsduSample.ing.WellLog` and `OsduSample.ing.WellLogCurve`, loaded by `wells-welllog-01-header-pre` and `wells-welllog-02-header-ing` from `samples/wells/data` | The values: one row per log, one row per curve. |
| The mapping | `samples/wells/mappings/WellLog@1.4.0.yaml` | Which template variable each entry fills, and where its value comes from: a source column, the OSDU cache, a search of the platform, or a static value. |
| The flow | `samples/wells/flows/wells-welllog-03-header-delivery.yaml` | Which mapping to use, the data partition, and where the record is sent. |
| The OSDU cache | `samples/wells/cache/wells-osdu-00-reference-cache.yaml`, captured into the catalog by its runs (or imported from `samples/cache-records`) | The OSDU ids that reference properties resolve to (units, business values). |
| The platform's search | The partition the flow delivers to, searched as a record needs it | The OSDU id of the wellbore each log names, found by its name or one of its aliases. |
| The template | `samples/templates/osdu_wks_work-product-component--WellLog_1.4.0.json`, saved in the catalog as template version `26a3c3441882db4f` | The types and structure. It is the file 1 schema. |

## How a value gets from the ingestion tables into the record

1. **The ingestion tables supply rows.** `ing.WellLog` holds one row per log, keyed by `(source_project, log_id)`.
   `ing.WellLogCurve` holds one row per curve, joined to its log by those same key columns and ordered by
   `curve_ordinal`. The flow reads the first as the dataset's row (`dataset.<column>`) and the second as the child
   dataset `curves` (`dataset.curves.<column>`), as its `source.datasets` declares.
2. **The mapping lists entries.** Each entry names a template variable (`osdu.data.SamplingStart`) and where its value
   comes from: `source: dataset.index_min` for a source column, `source: cache.<Type>.<field>` with `findBy` for the
   OSDU cache, or `static` for a fixed value. It can add modifiers (`trim`, `split`, `replace`, `equals`).
3. **The engine looks every target up in the template.** From file 1 it learns whether the variable exists, its type,
   and whether it is an object or an array. The mapping never states a type itself.
4. **The modifiers run, then the value is converted to the template's type.** `"1000"` becomes the number `1000`
   because the schema says `number`. A value that cannot be converted holds the record with a reason naming the target.
5. **`id` and `kind` come from the engine, not from entries.** `id` is derived from the mapping's `dataset.system` and
   `dataset.key`, and `kind` is the template's kind. `acl` and `legal` are static entries like the others, and every
   mapping must have them.
6. **Before anything renders, the preflight gate checks the mapping against the template.** A target the template does
   not have, a single value written to an object, or a required property without an entry stops the run.
7. **The flow's protocol sends it.** `ddms` writes the record to the wellbore DDMS (`POST /ddms/v3/welllogs`)
   and then streams the curve values to its `/data` endpoint from the folder the record's own `curve_folder` column
   names, under the flow's declared payload root. The curve values never appear in the record.

The status column in the tables below uses these words:

| Status | Meaning |
| --- | --- |
| **Mapped** | An entry fills it from a source column (`source: dataset...`) or from the OSDU cache (`source: cache...`). |
| **Static** | A `static` entry writes the same value on every record. |
| **Engine** | OSDU Delivery writes it: `id` from the mapping's `dataset.key`, `kind` from the template. |
| **OSDU** | The platform sets it. We never send it. |
| **Kept** | On an update, copied forward from the record OSDU already holds, because the flow lists it under `protocolOptions.preserveDataKeys`. A new record does not get it from us. |
| **Not filled** | Nothing writes it. |

The example values are for log `L-1001` in the sample estate.

## 1. The record root

| Property | Status | How it is filled | L-1001 |
| --- | --- | --- | --- |
| `id` | Engine | `{dataPartition}:work-product-component--WellLog:{key}`. The partition is the flow's `render.parameters.dataPartition`. The key is a UUIDv5 over the source system `recall` (`dataset.system`) and the values of the `dataset.key` columns, `source_project` and `log_id`. The same log always gets the same id. | `dev:work-product-component--WellLog:ea10870200ce5404ac1b49154b070e74` |
| `kind` | Engine | The template's kind, `template.kind` in the mapping. | `osdu:wks:work-product-component--WellLog:1.4.0` |
| `version` | OSDU | Assigned by OSDU on each write. The ledger records the version OSDU returns. | |
| `acl` | Static | `osdu.acl.owners` and `osdu.acl.viewers`, each a `static` list. | owners `data.default.owners@dev.dataservices.energy`, viewers `data.default.viewers@dev.dataservices.energy` |
| `legal` | Static | `osdu.legal.legaltags` and `osdu.legal.otherRelevantDataCountries`, each a `static` list. `status` is not sent. | `dev-reference-data-default`, `NO` |
| `tags` | Static and Mapped | `osdu.tags.DeliveredBy` is `static: osdu-delivery`. `osdu.tags.WellLogNativeUID` is `source: dataset.native_uid` with the modifier `split: { separator: ",", part: 1 }`, keeping the first part. | `DeliveredBy: osdu-delivery`, `WellLogNativeUID: NO_15_9:L-1001` |
| `ancestry` | Not filled | | |
| `meta` | Not filled | | |
| `createTime`, `createUser`, `modifyTime`, `modifyUser` | OSDU | Set by OSDU. | |
| `data` | | Sections 2.1 to 2.4. | |

## 2. The `data` block

### 2.1 From AbstractCommonResources

Nothing is filled. The eight properties are `ExistenceKind`, `ResourceCurationStatus`, `ResourceHomeRegionID`,
`ResourceHostRegionIDs`, `ResourceLifecycleStatus`, `ResourceSecurityClassification`, `Source` and
`TechnicalAssuranceID`.

### 2.2 From AbstractWPCGroupType

| Property | Status | How it is filled |
| --- | --- | --- |
| `Datasets` | Kept | Copied from OSDU's current record on an update. |
| `DDMSDatasets` | Kept | Copied from OSDU's current record on an update. The schema says this property is populated exclusively by DDMSs. |
| `Artefacts` | Not filled | |
| `IsDiscoverable` | Not filled | |
| `IsExtendedLoad` | Not filled | |
| `NameAliases` | Not filled | |
| `TechnicalAssurances` | Not filled | |

The flow also lists `ExtensionProperties` under `preserveDataKeys`. WellLog 1.4.0 has no such property, so that entry
does nothing for this kind.

### 2.3 From AbstractWorkProductComponent

| Property | Status | Entry | Modifiers | L-1001 |
| --- | --- | --- | --- | --- |
| `Name` | Mapped | `source: dataset.log_source` | `trim` | `"STAT_COMP"` |

The other ten are not filled: `AuthorIDs`, `BusinessActivities`, `CreationDateTime`, `Description`, `GeoContexts`,
`LineageAssertions`, `SpatialArea`, `SpatialPoint`, `SubmitterName` and `Tags`.

### 2.4 Declared by WellLog itself

Fifteen of the thirty-four properties are filled.

| Property | Status | Entry | Modifiers | Schema type | L-1001 | Does it match what the schema describes? |
| --- | --- | --- | --- | --- | --- | --- |
| `ActivityType` | Mapped | `source: dataset.creator` | none | string | `"SLB"` | **No.** The schema describes "General method or circumstance of logging - MWD, completion, ...". `SLB` is the logging company. |
| `BottomMeasuredDepth` | Mapped | `source: dataset.index_max` | none | number | `1004` | Yes, but see the unit finding below. |
| `Curves` | Mapped | `source: dataset.curves`, the repeater: one item per row of the `curves` child dataset, filled by the `osdu.data.Curves[]` entries | none | array | three items, section 2.4.1 | Yes. |
| `IsRegular` | Mapped | `source: dataset.depth_coding` | `equals: REGULAR` | boolean | `true` | Yes. |
| `LogActivity` | Mapped | `source: dataset.log_pass` | `split: { separator: ",", part: 1 }` | string | `"MAIN"` | Yes. The schema describes the type of pass. A value such as `MAIN,REPEAT` loses its second part. |
| `LogRun` | Mapped | `source: dataset.log_id` | none | string | `"L-1001"` | **No.** The schema describes the run of the log. The ingestion table has a `log_run` column (`"1"`) holding exactly that. |
| `LogSource` | Mapped | `source: dataset.source_project` | none | string | `"NO_15_9"` | **No.** The schema says "OSDU Native Log Source - will be updated for later releases - not to be used yet". |
| `LogVersion` | Mapped | `source: dataset.log_version` | none | string | `"1"` | Yes. |
| `ReferenceCurveID` | Static | `static: MD` | none | string | `"MD"` | Yes for this estate, since every log has an `MD` curve. The DDMS refuses a log whose reference curve is not among its curves, so the protocol holds such a record before sending it. |
| `SamplingInterval` | Mapped | `source: dataset.index_increment` | none | number | `0.5` | **Not always.** The schema says it is not set for irregular sampling. Log `L-2001` is `DISCRETE` and still gets `0.5`. |
| `SamplingStart` | Mapped | `source: dataset.index_min` | none | number | `1000` | Yes, but see the unit finding below. |
| `SamplingStop` | Mapped | `source: dataset.index_max` | none | number | `1004` | Yes, but see the unit finding below. |
| `TopMeasuredDepth` | Mapped | `source: dataset.index_min` | none | number | `1000` | Yes, but see the unit finding below. |
| `VerticalMeasurement` | Mapped | entries for three of its properties, section 2.4.2 | | object | section 2.4.2 | Partly. |
| `WellboreID` | Mapped | `source: search.Wellbore.id` with `findBy: search.Wellbore.data.FacilityName = dataset.wellbore_uwi`, then `search.Wellbore.data.NameAliases.AliasName` with the same value. Each line asks the platform for the one wellbore whose name, or one of whose aliases, is exactly the value. The entry is required, so the record is held if no wellbore matches, and held whatever `required` says if several do. | none | string, points to master-data--Wellbore | `"dev:master-data--Wellbore:OSDU-DEV-1-A:"` | Yes. |

The other nineteen are not filled: `CandidateReferenceCurveIDs`, `CompanyID`, `ConveyanceMethodID`,
`DrillingFluidProperty`, `FrameIdentifier`, `HoleTypeLogging`, `LogRemark`, `LogServiceDateInterval`,
`LoggingDirection`, `LoggingService`, `PassNumber`, `SamplingDomainTypeID`, `SeismicReferenceElevation`,
`ServiceCompanyID`, `ToolStringDescription`, `VerticalMeasurementID`, `WellLogTypeID`, `WellboreFluidTypeID` and
`ZeroTime`.

A reference property always ends in `:`. That is OSDU's form for "the latest version of that record".

#### 2.4.1 `data.Curves[]`

In file 1 these are the `data.Curves[].` rows of section 2.4. One item is written per row of the `curves` child dataset
(`ing.WellLogCurve`), joined to the log by `source_project` and `log_id` and ordered by `curve_ordinal`. Eight of the
twenty-one properties are filled.

| Property | Status | Entry | Modifiers | GR curve of L-1001 |
| --- | --- | --- | --- | --- |
| `CurveID` | Mapped | `source: dataset.curves.curve_id` | none | `"GR"` |
| `CurveUnit` | Mapped | `source: cache.UnitOfMeasure.id`, with `findBy` on `Code`, then `Name`, then `id`, each compared with `dataset.curves.curve_unit`. The record is held if nothing matches. | `replace: { M: m, METRE: m, METER: m, FT: ft, FEET: ft, GAPI: gAPI, G/CM3: g/cm3, V/V: m3/m3 }`, applied to the value `findBy` compares | `"dev:reference-data--UnitOfMeasure:gAPI:"` |
| `DepthUnit` | Mapped | `source: cache.UnitOfMeasure.id`, with `findBy` on `Code`, then `Name`, then `id`, each compared with `dataset.curves.index_unit` | `replace: { M: m, FT: ft }` | `"dev:reference-data--UnitOfMeasure:m:"` |
| `TopDepth` | Mapped | `source: dataset.curves.index_min` | none | `1000` |
| `BaseDepth` | Mapped | `source: dataset.curves.index_max` | none | `1004` |
| `CurveDescription` | Mapped | `source: dataset.curves.curve_description` | none | `"Gamma ray"` |
| `CurveVersion` | Mapped | `source: dataset.curves.curve_version` | none | `"1"` |
| `LogCurveBusinessValueID` | Mapped | `source: cache.LogCurveBusinessValue.id`, with `findBy` on `Code`, then `Name`, each compared with `dataset.curves.business_value`, and `required: false`, so the property is left out if nothing matches. | none | `"dev:reference-data--LogCurveBusinessValue:High:"` |

The other thirteen are not filled: `CurveQuality`, `CurveSampleTypeID`, `DateStamp`, `DepthCoding`, `Interpolate`,
`InterpreterName`, `IsProcessed`, `LogCurveFamilyID`, `LogCurveMainFamilyID`, `LogCurveTypeID`, `Mnemonic`,
`NullValue` and `NumberOfColumns`.

The schema says `TopDepth` and `BaseDepth` take their unit from `DepthUnit`, so each curve's depths declare their unit.

The `CurveUnit` entry reads the cache captured from OSDU. For `GAPI` the `replace` modifier makes the value `gAPI`, and
`findBy` finds this cached record by its `Code`, whose `id` the entry writes with a trailing `:`:

```json
{ "Code": "gAPI", "ID": "gAPI", "Name": "API gamma ray unit", "id": "dev:reference-data--UnitOfMeasure:gAPI" }
```

#### 2.4.2 `data.VerticalMeasurement`

The schema defines this object once, as the building block AbstractFacilityVerticalMeasurement in section 3 of file 1.
Three of its twelve properties are filled: two from the one column `elev_meas_ref`, which holds text such as `23.5 M`,
and one with a static value.

| Property | Status | Entry | Modifiers | L-1001 |
| --- | --- | --- | --- | --- |
| `VerticalMeasurement` | Mapped | `source: dataset.elev_meas_ref`, converted to a number | `split: { separator: " ", part: 1 }`, where a single space splits on any run of whitespace | `23.5` |
| `VerticalMeasurementUnitOfMeasureID` | Mapped | `source: cache.UnitOfMeasure.id`, with `findBy` on `Code`, then `Name`, then `id`, each compared with `dataset.elev_meas_ref` | `split: { separator: " ", part: 2 }`, then `replace: { M: m, FT: ft }`, applied to the value `findBy` compares | `"dev:reference-data--UnitOfMeasure:m:"` |
| `VerticalMeasurementTypeID` | Static | `static: "{param.dataPartition}:reference-data--VerticalMeasurementType:KellyBushing:"` | none | `"dev:reference-data--VerticalMeasurementType:KellyBushing:"` |

The other nine are not filled: `EffectiveDateTime`, `TerminationDateTime`, `VerticalCRSID`,
`VerticalMeasurementDescription`, `VerticalMeasurementPathID`, `VerticalMeasurementSourceID`,
`VerticalReferenceEntityID`, `VerticalReferenceID` and `WellboreTVDTrajectoryID`.

## Worked example: log L-1001

The log's row, as it sits in `ing.WellLog`:

| Column | Value |
| --- | --- |
| `source_project` | `NO_15_9` |
| `log_id` | `L-1001` |
| `wellbore_uwi` | `OSDU-DEV-1-A` |
| `log_name` | `STAT_COMP` |
| `log_run` | `1` |
| `index_min`, `index_max`, `index_increment`, `index_unit` | `1000.0`, `1004.0`, `0.5`, `M` |
| `depth_coding` | `REGULAR` |
| `elev_meas_ref` | `23.5 M` |
| `creator` | `SLB` |
| `log_version` | `1` |
| `log_pass` | `MAIN,REPEAT` |
| `native_uid` | `NO_15_9:L-1001,extra` |

Its curve rows in `ing.WellLogCurve`:

| `curve_ordinal` | `curve_id` | `curve_unit` | `index_unit` | `curve_description` | `business_value` |
| --- | --- | --- | --- | --- | --- |
| 0 | `MD` | `M` | `M` | Measured depth | empty |
| 1 | `GR` | `GAPI` | `M` | Gamma ray | `HIGH` |
| 2 | `RHOB` | `G/CM3` | `M` | Bulk density | `HIGH` |

The document below is the mapping's own fixture for this log. The preflight gate renders the fixture and compares it
with this text on every run, so it is exactly what the engine produces from those inputs. The fixture carries all three
curves, `MD` first, and `MD` has no `LogCurveBusinessValueID` because its `business_value` is empty. A second fixture,
for `L-2001`, covers a log in feet with a neutron porosity curve in `V/V`, which renders as `m3/m3`, and discrete
sampling.

```json
{
  "id": "dev:work-product-component--WellLog:ea10870200ce5404ac1b49154b070e74",
  "kind": "osdu:wks:work-product-component--WellLog:1.4.0",
  "acl": {
    "owners": ["data.default.owners@dev.dataservices.energy"],
    "viewers": ["data.default.viewers@dev.dataservices.energy"]
  },
  "legal": { "legaltags": ["dev-reference-data-default"], "otherRelevantDataCountries": ["NO"] },
  "tags": { "DeliveredBy": "osdu-delivery", "WellLogNativeUID": "NO_15_9:L-1001" },
  "data": {
    "LogSource": "NO_15_9",
    "LogRun": "L-1001",
    "Name": "STAT_COMP",
    "WellboreID": "dev:master-data--Wellbore:OSDU-DEV-1-A:",
    "LogVersion": "1",
    "LogActivity": "MAIN",
    "ActivityType": "SLB",
    "SamplingStart": 1000,
    "SamplingStop": 1004,
    "SamplingInterval": 0.5,
    "TopMeasuredDepth": 1000,
    "BottomMeasuredDepth": 1004,
    "ReferenceCurveID": "MD",
    "IsRegular": true,
    "VerticalMeasurement": {
      "VerticalMeasurement": 23.5,
      "VerticalMeasurementUnitOfMeasureID": "dev:reference-data--UnitOfMeasure:m:",
      "VerticalMeasurementTypeID": "dev:reference-data--VerticalMeasurementType:KellyBushing:"
    },
    "Curves": [
      {
        "CurveID": "MD",
        "CurveUnit": "dev:reference-data--UnitOfMeasure:m:",
        "DepthUnit": "dev:reference-data--UnitOfMeasure:m:",
        "TopDepth": 1000,
        "BaseDepth": 1004,
        "CurveDescription": "Measured depth",
        "CurveVersion": "1"
      },
      {
        "CurveID": "GR",
        "CurveUnit": "dev:reference-data--UnitOfMeasure:gAPI:",
        "DepthUnit": "dev:reference-data--UnitOfMeasure:m:",
        "TopDepth": 1000,
        "BaseDepth": 1004,
        "CurveDescription": "Gamma ray",
        "CurveVersion": "1",
        "LogCurveBusinessValueID": "dev:reference-data--LogCurveBusinessValue:High:"
      },
      {
        "CurveID": "RHOB",
        "CurveUnit": "dev:reference-data--UnitOfMeasure:g%2Fcm3:",
        "DepthUnit": "dev:reference-data--UnitOfMeasure:m:",
        "TopDepth": 1000,
        "BaseDepth": 1004,
        "CurveDescription": "Bulk density",
        "CurveVersion": "1",
        "LogCurveBusinessValueID": "dev:reference-data--LogCurveBusinessValue:High:"
      }
    ]
  }
}
```

## Coverage

| Section | In the schema | Filled by us | Set by OSDU or kept | Not filled |
| --- | --- | --- | --- | --- |
| 1. Record root, `data` aside | 12 | 5 | 5 set by OSDU | 2 |
| 2.1 AbstractCommonResources | 8 | 0 | 0 | 8 |
| 2.2 AbstractWPCGroupType | 7 | 0 | 2 kept on update | 5 |
| 2.3 AbstractWorkProductComponent | 11 | 1 | 0 | 10 |
| 2.4 WellLog's own, top level | 34 | 15 | 0 | 19 |
| 2.4.1 `Curves[]` | 21 | 8 | 0 | 13 |
| 2.4.2 `VerticalMeasurement` | 12 | 3 | 0 | 9 |

## Where the mapping does not match the schema

Every item below passes the preflight gate. The gate checks structure (the target exists in the template, the value fits
its shape) and cannot know what a column means, so these are for a person to judge.

1. **`LogRun` holds the log id.** Its entry reads `dataset.log_id` (`L-1001`), while the `log_run` column (`1`)
   holds the run the schema describes.
2. **`LogSource` holds the source project.** The schema says the property is not to be used yet.
3. **The first two are a choice.** The mapping's `dataset.key` names `source_project` and `log_id` itself, so a
   record's identity does not depend on either entry: `LogRun` can read `dataset.log_run`, and the `LogSource` entry can
   be removed, without changing any record's delivery key or OSDU id.
4. **`ActivityType` holds the logging company.** The schema wants a logging method. The company belongs in
   `ServiceCompanyID`, which is a reference to a `master-data--Organisation` record and would need Organisation in the
   OSDU cache.
5. **Log-level depths carry no unit.** The schema gives `SamplingStart`, `SamplingStop`, `SamplingInterval`,
   `TopMeasuredDepth` and `BottomMeasuredDepth` a unit context (the Notes column in file 1). OSDU states such units in
   the root `meta` block, which we do not fill. Log `L-2001` is in feet, and its `1500` reads the same as a value in
   metres. The curves are not affected, because each declares `DepthUnit`.
6. **`SamplingInterval` is written for irregular logs.** The schema says it is not set when sampling is not regular.
   `appliesWhen: dataset.depth_coding is REGULAR` on the entry would leave it out for such logs.
7. **`Name` does not tell logs apart.** Its entry reads `dataset.log_source`, and every log of the `STAT_COMP` source has
   the name `STAT_COMP`.
8. **`VerticalMeasurementTypeID` is always kelly bushing.** The source does not say what the elevation is measured from.
   The preflight gate checks only that the static id exists in the OSDU cache, which holds `KellyBushing`, not that it
   is right for the log.
9. **Both fixtures carry an `MD` curve.** Each names `MD` as the reference curve and lists `MD` among its curves, so the
   documents the gate compares are ones the well log protocol would send. The sample estate's logs have an `MD` curve
   too.
