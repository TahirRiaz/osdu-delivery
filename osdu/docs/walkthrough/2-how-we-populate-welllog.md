# How OSDU Delivery fills a WellLog 1.4.0 record

This file goes with [1-welllog-schema.md](1-welllog-schema.md), which lays out the OSDU schema as OSDU defines it. The
section numbers here are the same, so section 2.4 in this file explains how we fill section 2.4 in that one.

Everything below is taken from the sample estate, `osdu/samples/recall`, which delivers Recall well logs the way
petrodb-api's `RecallToWellLogMapper` fills them:

| Input | File | What it decides |
| --- | --- | --- |
| The ingestion tables | `OsduData.arc.WellLog` and `OsduData.arc.WellLogCurve`, loaded from `samples/recall/data` by `recall-welllog-01-header-pre` and `recall-welllog-01-curves-pre`, then keyed by `recall-welllog-02-header-ing` and `recall-welllog-02-curves-ing` | The values: one row per log, one row per curve. |
| The mapping | `samples/recall/mappings/WellLog@1.4.0.yaml` | Which property each node of the record fills, and where its value comes from: a source column, a search of the platform, or a literal, with the modifiers that translate it and build references from it. |
| The flow | `samples/recall/flows/recall-welllog-03-header-delivery.yaml` | Which mapping to use, the Recall log source it delivers (its `logSource` parameter), and where the record is sent. |
| The lookup tables | `RecallUnits`, `RecallDepthUnits` and `CurveDictionary` in the partition's cache, captured by `samples/recall/cache/recall-lookups-00-cache.yaml` from the tables its own pre and ingestion flows load from `samples/recall/cache/data` | Recall's unit spellings as the partition's unit codes, and what each curve mnemonic is. The cache holds no OSDU reference data: every reference the record carries is built from a value. |
| The platform's search | The partition the flow delivers to, searched as a record needs it | The OSDU id of the wellbore each log names, found by its name or one of its aliases. |
| The template | `samples/templates/osdu_wks_work-product-component--WellLog_1.4.0.json`, saved in the catalog as template version `26a3c3441882db4f` | The types and structure. It is the file 1 schema. |

## How a value gets from the ingestion tables into the record

1. **The ingestion tables supply rows.** `OsduData.arc.WellLog` holds one row per log, keyed by
   `(source_project, log_id)`, and the flow reads only the rows of its log source (`scope: { log_source: logSource }`).
   `OsduData.arc.WellLogCurve` holds one row per curve, joined to its log by those same key columns and ordered by
   `curve_ordinal`. The flow reads the first as the dataset's row, whose columns a property names as they are, and the
   second as the child dataset `curves`, which a `$forEach: curves` node repeats and whose columns its `$item` reads by
   name, as the flow's `source.datasets` declares.
2. **The mapping lays out the record.** Its `record` block is written the way the record is, so each property sits where
   the template has it (`data: { SamplingStart: ... }` fills `osdu.data.SamplingStart`) and says where its value comes
   from: `$from: index_min` for a source column, `$search: Wellbore` with `$findBy` for a record found on the platform,
   or a literal for a fixed value. It can add `$modifiers`: `split`, `trim` and `equals` shape the value,
   `replace` translates it (through a one-entry map written beside it, or a lookup table of the partition's cache such as
   `$cache.RecallUnits`), and `ref` or `id` builds the OSDU id of the record it points to. Every word of the mapping
   language starts with `$`; every other key is a property of the record.
3. **The engine looks every property up in the template.** From file 1 it learns whether the variable exists, its type,
   whether it is an object or an array, and which entity type a reference points to. The mapping never states a type
   itself, and `ref` takes its entity type from the template.
4. **The modifiers run, then the value is converted to the template's type.** `"78.4"` becomes the number `78.4`
   because the schema says `number`. A value that cannot be converted holds the record with a reason naming the variable.
5. **`id` and `kind` come from the engine, not from the mapping.** `id` is derived from the mapping's `dataset.system`
   and `dataset.key`, and `kind` is the template's kind. `acl` and `legal` are lists like any other, and every mapping
   must have them.
6. **Before anything renders, the preflight gate checks the mapping against the template.** A property the template does
   not have, a single value written to an object, a reference that does not match its property's pattern and
   relationship, or a required property nothing fills stops the run. The gate also renders the mapping's fixtures and
   compares each with the record it names.
7. **The flow's protocol sends it.** `ddms` writes the record to the wellbore DDMS under the flow's `ddmsRoot`
   (`/api/os-wellbore-ddms`), then streams the curve values as the log's bulk data from the folder the row's own
   `curve_folder` column names, under the flow's declared payload root: one chunk to the log's data endpoint, or several
   through a session. The curve values never appear in the record.

The status column in the tables below uses these words:

| Status | Meaning |
| --- | --- |
| **Mapped** | The mapping fills it from a source column (`$from`) or by searching the platform (`$search`). Its modifiers may translate the value and build a reference from it. |
| **Parameter** | A flow parameter (`{$param.<name>}`) writes the same value on every record the flow delivers. When the flow gives no value, the delivery kind takes `dataPartition`, `aclOwner`, `aclViewer` and `legalTag` from `${env:OSDU_DATA_PARTITION}`, `${env:OSDU_ACL_OWNER}`, `${env:OSDU_ACL_VIEWER}` and `${env:OSDU_LEGAL_TAG}`. |
| **Static** | A literal writes the same value on every record of the partition. |
| **Engine** | OSDU Delivery writes it: `id` from the mapping's `dataset.key`, `kind` from the template. |
| **OSDU** | The platform sets it. We never send it. |
| **Kept** | On an update, copied forward from the record OSDU already holds, because the flow lists it under `protocolOptions.preserveDataKeys`. A new record does not get it from us. |
| **Not filled** | Nothing writes it. |

The example values are for log `12359/1` of wellbore `NO 33/9-C-28 B` in the sample estate, rendered with the mapping's
fixture parameters.

## 1. The record root

| Property | Status | How it is filled | 12359/1 |
| --- | --- | --- | --- |
| `id` | Engine | `{dataPartition}:work-product-component--WellLog:{key}`. The key is a UUIDv5 over the source system `recall` (`dataset.system`) and the values of the `dataset.key` columns, `source_project` and `log_id`. The same log always gets the same id. | `dev:work-product-component--WellLog:da57bceaa21e58e48abacec2e63d0267` |
| `kind` | Engine | The template's kind, `template.kind` in the mapping. | `osdu:wks:work-product-component--WellLog:1.4.0` |
| `version` | OSDU | Assigned by OSDU on each write. The ledger records the version OSDU returns. | |
| `acl` | Parameter | `acl.owners` is `["{$param.aclOwner}"]` and `acl.viewers` is `["{$param.aclViewer}"]`. | owners `data.welllogsrecall.owners@dev.dataservices.energy`, viewers `data.sdd-well-logs.viewers@dev.dataservices.energy` |
| `legal` | Parameter and Static | `legal.legaltags` is `["{$param.legalTag}"]`. `legal.otherRelevantDataCountries` is the literal list `[NO]`, since Recall's wellbores are Norwegian. `status` is not sent. | `dev-equinor-osdu-reference-default`, `NO` |
| `tags` | Static and Mapped | `DeliveredBy` is the literal `osdu-delivery`. `LogStatus` is `$from: log_pass_type` and `WellLogNativeUID` is `$from: native_uid`, each with `split: { separator: ",", part: 1 }` keeping the first part. `SourceProject` is `$from: source_project`. | `DeliveredBy: osdu-delivery`, `LogStatus: FINAL`, `WellLogNativeUID: 12359/1`, `SourceProject: NORWAY_WELLDB` |
| `ancestry` | Not filled | | |
| `meta` | Not filled | | |
| `createTime`, `createUser`, `modifyTime`, `modifyUser` | OSDU | Set by OSDU. | |
| `data` | | Sections 2.1 to 2.4. | |

## 2. The `data` block

### 2.1 From AbstractCommonResources

| Property | Status | Entry | 12359/1 | Does it match what the schema describes? |
| --- | --- | --- | --- | --- |
| `Source` | Mapped | `$from: creator` | `"RECALL"` | Yes. The schema describes the system the record was received from, and the list of sources is not governed. |

The other seven are not filled: `ExistenceKind`, `ResourceCurationStatus`, `ResourceHomeRegionID`,
`ResourceHostRegionIDs`, `ResourceLifecycleStatus`, `ResourceSecurityClassification` and `TechnicalAssuranceID`.

### 2.2 From AbstractWPCGroupType

| Property | Status | How it is filled |
| --- | --- | --- |
| `Datasets` | Kept | Copied from OSDU's current record on an update. |
| `DDMSDatasets` | Kept | Copied from OSDU's current record on an update. The schema says this property is populated exclusively by DDMSs. |
| `TechnicalAssurances` | Static | One item whose `TechnicalAssuranceTypeID` is `"{$param.dataPartition}:reference-data--TechnicalAssuranceType:Unevaluated:"`: every Recall log is delivered unevaluated, as petrodb-api delivers it. For 12359/1 it is `dev:reference-data--TechnicalAssuranceType:Unevaluated:`. |
| `Artefacts` | Not filled | |
| `IsDiscoverable` | Not filled | |
| `IsExtendedLoad` | Not filled | |
| `NameAliases` | Not filled | |

The flow also lists `ExtensionProperties` under `preserveDataKeys`. The template declares it in a fifth part of `data`,
an object of its own beside the four file 1 describes, so it is kept on an update the same way.

### 2.3 From AbstractWorkProductComponent

| Property | Status | Entry | Modifiers | 12359/1 |
| --- | --- | --- | --- | --- |
| `Name` | Mapped | `$from: log_source` | `trim` | `"STAT_COMP"` |

The other ten are not filled: `AuthorIDs`, `BusinessActivities`, `CreationDateTime`, `Description`, `GeoContexts`,
`LineageAssertions`, `SpatialArea`, `SpatialPoint`, `SubmitterName` and `Tags`.

### 2.4 Declared by WellLog itself

Fifteen of the thirty-four properties are filled.

| Property | Status | Entry | Modifiers | Schema type | 12359/1 | Does it match what the schema describes? |
| --- | --- | --- | --- | --- | --- | --- |
| `ActivityType` | Mapped | `$from: log_service` | none | string | `"HYBRID"` | **Unclear.** The schema describes "General method or circumstance of logging - MWD, completion, ...". `HYBRID` is Recall's log service, which petrodb-api writes here too. |
| `Curves` | Mapped | `$forEach: curves`, the repeated array: one item per row of the `curves` child dataset, filled by the properties under its `$item` | none | array | three items, section 2.4.1 | Yes. |
| `IsRegular` | Mapped | `$from: depth_coding` | `equals: REGULAR` | boolean | `true` | Yes. |
| `LogActivity` | Mapped | `$from: log_pass` | `split: { separator: ",", part: 1 }` | string | `"C"` | Yes. The schema describes the type of pass, and Recall records it as a code. A value such as `MAIN,REPEAT` would lose its second part. |
| `LogRun` | Mapped | `$from: log_run` | none | string | `"C"` | Yes. The schema describes the run of the log, which may be alphanumeric. |
| `LogSource` | Mapped | `$from: recall_log_source` | none | string | `"EQUINOR"` | **No.** The schema says "OSDU Native Log Source - will be updated for later releases - not to be used yet". petrodb-api writes the same column here. |
| `LogVersion` | Mapped | `$from: log_version` | none | string | `"1"` | Yes. |
| `ReferenceCurveID` | Static | `MD` | none | string | `"MD"` | Yes for this estate, since every log has an `MD` curve. The DDMS refuses a log whose reference curve is not among its curves, so the protocol holds such a record before sending it. |
| `SamplingDomainTypeID` | Mapped | `$from: index_type` | `replace: { DEPTH: Depth }`, then `ref` | string, points to reference-data--WellLogSamplingDomainType | `"dev:reference-data--WellLogSamplingDomainType:Depth:"` | Yes. |
| `SamplingInterval` | Mapped | `$from: index_increment` | none | number | `0.1524` | Yes for this estate, where every log is `REGULAR`. See the findings below for an irregular log. |
| `SamplingStart` | Mapped | `$from: index_min` | none | number | `2715.6155` | Yes, but see the unit finding below. |
| `SamplingStop` | Mapped | `$from: index_max` | none | number | `2741.676` | Yes, but see the unit finding below. |
| `VerticalMeasurement` | Mapped | properties for three of its members, section 2.4.2 | | object | section 2.4.2 | Yes. The schema asks for either `VerticalMeasurement` or `VerticalMeasurementID`, and the mapping fills the first. |
| `WellboreID` | Mapped | `$search: Wellbore` with `$findBy: data.FacilityName = wellbore_uwi`, then `data.NameAliases.AliasName = wellbore_uwi`. Each line asks the platform for the one wellbore whose name, or one of whose aliases, is exactly the value. The property is required, so the record is held if no wellbore matches, and held whatever `$required` says if several do. | none | string, points to master-data--Wellbore | `"dev:master-data--Wellbore:NO-33-9-C-28-B:"` | Yes. |
| `WellLogTypeID` | Mapped | `$from: data_type` | `replace: { INTERPRETED: Interpreted }`, then `ref` | string, points to reference-data--LogType | `"dev:reference-data--LogType:Interpreted:"` | Yes. |

The other nineteen are not filled: `BottomMeasuredDepth`, `CandidateReferenceCurveIDs`, `CompanyID`,
`ConveyanceMethodID`, `DrillingFluidProperty`, `FrameIdentifier`, `HoleTypeLogging`, `LogRemark`,
`LogServiceDateInterval`, `LoggingDirection`, `LoggingService`, `PassNumber`, `SeismicReferenceElevation`,
`ServiceCompanyID`, `ToolStringDescription`, `TopMeasuredDepth`, `VerticalMeasurementID`, `WellboreFluidTypeID` and
`ZeroTime`.

A reference property always ends in `:`. That is OSDU's form for "the latest version of that record". `ref` builds it
as `{$param.dataPartition}:<group>--<Entity>:{$value}:`, taking `<group>--<Entity>` from the property's relationship in
the template, so `SamplingDomainTypeID` becomes a `reference-data--WellLogSamplingDomainType` id without the mapping
naming the type.

#### 2.4.1 `data.Curves[]`

In file 1 these are the `data.Curves[].` rows of section 2.4. One item is written per row of the `curves` child dataset
(`OsduData.arc.WellLogCurve`), joined to the log by `source_project` and `log_id` and ordered by `curve_ordinal`. A bare
column name under `$item` reads the curve's row. Thirteen of the twenty-one properties are filled.

| Property | Status | Entry | Modifiers | GR curve of 12359/1 |
| --- | --- | --- | --- | --- |
| `CurveID` | Mapped | `$from: curve_id` | none | `"GR"` |
| `Mnemonic` | Mapped | `$from: curve_id` | none | `"GR"` |
| `CurveUnit` | Mapped | `$from: curve_unit` | `replace: $cache.RecallUnits`, then `ref`. A spelling the table does not list is kept as it is. | `"dev:reference-data--UnitOfMeasure:gAPI:"` |
| `DepthUnit` | Mapped | `$from: index_unit` | `replace: $cache.RecallDepthUnits`, then `ref` | `"dev:reference-data--UnitOfMeasure:m:"` |
| `TopDepth` | Mapped | `$from: index_min`, the curve's own | none | `2715.6156` |
| `BaseDepth` | Mapped | `$from: index_max`, the curve's own | none | `2739.8472` |
| `CurveDescription` | Mapped | `$from: curve_description` | none | `"Gamma Ray"` |
| `CurveVersion` | Mapped | `$from: curve_version`, with `$required: false`, since the index curve has no version | none | `"1"` |
| `DateStamp` | Mapped | `$from: update_date`, with `$required: false`, since the index curve has no update time | `date` | `"2021-11-15T23:32:06Z"` |
| `LogCurveBusinessValueID` | Mapped | `$from: business_value`, with `$required: false`, so a curve without a business value goes out without the property rather than holding its log | `replace: { HIGH: High }`, then `ref` | left out: this curve has no business value |
| `LogCurveTypeID` | Mapped | `$from: curve_id`, with `$required: false` | `id: "{$param.dataPartition}:reference-data--LogCurveType:{$cache.CurveDictionary.log_curve_type_id}:"` | `"dev:reference-data--LogCurveType:Equinor-GR:"` |
| `LogCurveMainFamilyID` | Mapped | `$from: curve_id`, with `$required: false` | `id: "{$param.dataPartition}:reference-data--LogCurveMainFamily:{$cache.CurveDictionary.log_curve_main_family_id}:"` | `"dev:reference-data--LogCurveMainFamily:GammaRay:"` |
| `LogCurveFamilyID` | Mapped | `$from: curve_id`, with `$required: false` | `id: "{$param.dataPartition}:reference-data--LogCurveFamily:{$cache.CurveDictionary.log_curve_family_id}:"` | `"dev:reference-data--LogCurveFamily:Gamma%20Ray:"` |

The other eight are not filled: `CurveQuality`, `CurveSampleTypeID`, `DepthCoding`, `Interpolate`, `InterpreterName`,
`IsProcessed`, `NullValue` and `NumberOfColumns`.

The schema says `TopDepth` and `BaseDepth` take their unit from `DepthUnit`, so each curve's depths declare their unit.

The `CurveUnit` property reads the `RecallUnits` lookup table, which holds petrodb-api's unit map as rows keyed by the
Recall spelling. For `GAPI` the replace finds this row, and `ref` writes its unit as the reference to that
`UnitOfMeasure`:

```text
source_unit,osdu_unit
GAPI,gAPI
```

The three curve classifications read the `CurveDictionary` lookup table by the curve's mnemonic. Each `id` template
names the one field it needs, so one row fills all three:

```text
mnemonic,log_curve_type_id,log_curve_main_family_id,log_curve_family_id,unit_quantity_id,unit,comment
GR,Equinor-GR,GammaRay,Gamma%20Ray,API%20gamma%20ray,gAPI,null
```

A mnemonic the dictionary does not list gives none of the three, and the curve goes out without them.

#### 2.4.2 `data.VerticalMeasurement`

The schema defines this object once, as the building block AbstractFacilityVerticalMeasurement in section 3 of file 1.
Three of its twelve properties are filled: two from the one column `elev_meas_ref`, which holds text such as `78.4 M`,
and one from `logs_meas_from`, the point the depths are measured from.

| Property | Status | Entry | Modifiers | 12359/1 |
| --- | --- | --- | --- | --- |
| `VerticalMeasurement` | Mapped | `$from: elev_meas_ref`, converted to a number | `split: { separator: " ", part: 1 }`, where a single space splits on any run of whitespace | `78.4` |
| `VerticalMeasurementUnitOfMeasureID` | Mapped | `$from: elev_meas_ref` | `split: { separator: " ", part: 2 }`, then `replace: $cache.RecallDepthUnits`, then `ref` | `"dev:reference-data--UnitOfMeasure:m:"` |
| `VerticalMeasurementTypeID` | Mapped | `$from: logs_meas_from` | `replace: { KB: KellyBushing }`, then `ref`. The partition holds both a `KB` and a `KellyBushing` record, and petrodb-api files Recall's logs under `KellyBushing`. | `"dev:reference-data--VerticalMeasurementType:KellyBushing:"` |

The other nine are not filled: `EffectiveDateTime`, `TerminationDateTime`, `VerticalCRSID`,
`VerticalMeasurementDescription`, `VerticalMeasurementPathID`, `VerticalMeasurementSourceID`,
`VerticalReferenceEntityID`, `VerticalReferenceID` and `WellboreTVDTrajectoryID`.

## Worked example: log 12359/1

The log's row, as it sits in `OsduData.arc.WellLog` (the columns the mapping reads):

| Column | Value |
| --- | --- |
| `source_project` | `NORWAY_WELLDB` |
| `log_id` | `12359/1` |
| `wellbore_uwi` | `NO 33/9-C-28 B` |
| `log_source` | `STAT_COMP` |
| `log_run` | `C` |
| `recall_log_source` | `EQUINOR` |
| `index_type`, `index_unit` | `DEPTH`, `M` |
| `index_min`, `index_max`, `index_increment` | `2715.6155`, `2741.676`, `0.1524` |
| `depth_coding` | `REGULAR` |
| `elev_meas_ref`, `logs_meas_from` | `78.4 M`, `KB` |
| `creator`, `log_service`, `log_version`, `data_type` | `RECALL`, `HYBRID`, `1`, `INTERPRETED` |
| `log_pass`, `log_pass_type` | `C`, `FINAL` |
| `native_uid` | `12359/1` |

Its curve rows in `OsduData.arc.WellLogCurve`:

| `curve_ordinal` | `curve_id` | `curve_unit` | `index_unit` | `index_min`, `index_max` | `curve_description` | `curve_version` | `business_value` | `update_date` |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 0 | `MD` | `M` | `M` | `2715.6155`, `2741.676` | Measured depth | empty | `HIGH` | empty |
| 1 | `GR` | `GAPI` | `M` | `2715.6156`, `2739.8472` | Gamma Ray | `1` | empty | `2021-11-15T23:32:06Z` |
| 2 | `RD` | `OHMM` | `M` | `2717.5968`, `2741.676` | Deep resistivity | `1` | `HIGH` | `2021-11-15T23:32:06Z` |

The document below is the mapping's own fixture for this log, which the preflight gate renders and compares before every
render, so it is exactly what the engine produces from those inputs. The fixture stands in for the platform's search
with the wellbore id it names; a real render asks the partition. `MD` has no `CurveVersion` or `DateStamp` because its
row has neither, and `GR` has no `LogCurveBusinessValueID` because its business value is empty. A second fixture, for
`22494/1` of `NO 34/10-B-31 AT2`, covers a gamma ray of high business value.

```json
{
  "id": "dev:work-product-component--WellLog:da57bceaa21e58e48abacec2e63d0267",
  "kind": "osdu:wks:work-product-component--WellLog:1.4.0",
  "acl": {
    "owners": ["data.welllogsrecall.owners@dev.dataservices.energy"],
    "viewers": ["data.sdd-well-logs.viewers@dev.dataservices.energy"]
  },
  "legal": { "legaltags": ["dev-equinor-osdu-reference-default"], "otherRelevantDataCountries": ["NO"] },
  "tags": { "DeliveredBy": "osdu-delivery", "LogStatus": "FINAL", "WellLogNativeUID": "12359/1", "SourceProject": "NORWAY_WELLDB" },
  "data": {
    "Name": "STAT_COMP",
    "WellboreID": "dev:master-data--Wellbore:NO-33-9-C-28-B:",
    "LogRun": "C",
    "LogSource": "EQUINOR",
    "LogVersion": "1",
    "LogActivity": "C",
    "ActivityType": "HYBRID",
    "Source": "RECALL",
    "SamplingStart": 2715.6155,
    "SamplingStop": 2741.676,
    "SamplingInterval": 0.1524,
    "SamplingDomainTypeID": "dev:reference-data--WellLogSamplingDomainType:Depth:",
    "WellLogTypeID": "dev:reference-data--LogType:Interpreted:",
    "ReferenceCurveID": "MD",
    "IsRegular": true,
    "TechnicalAssurances": [
      { "TechnicalAssuranceTypeID": "dev:reference-data--TechnicalAssuranceType:Unevaluated:" }
    ],
    "VerticalMeasurement": {
      "VerticalMeasurement": 78.4,
      "VerticalMeasurementUnitOfMeasureID": "dev:reference-data--UnitOfMeasure:m:",
      "VerticalMeasurementTypeID": "dev:reference-data--VerticalMeasurementType:KellyBushing:"
    },
    "Curves": [
      {
        "CurveID": "MD",
        "Mnemonic": "MD",
        "CurveUnit": "dev:reference-data--UnitOfMeasure:m:",
        "DepthUnit": "dev:reference-data--UnitOfMeasure:m:",
        "TopDepth": 2715.6155,
        "BaseDepth": 2741.676,
        "CurveDescription": "Measured depth",
        "LogCurveBusinessValueID": "dev:reference-data--LogCurveBusinessValue:High:",
        "LogCurveTypeID": "dev:reference-data--LogCurveType:Equinor-DEPTH:",
        "LogCurveMainFamilyID": "dev:reference-data--LogCurveMainFamily:Reference:",
        "LogCurveFamilyID": "dev:reference-data--LogCurveFamily:EQ-Measured%20Depth:"
      },
      {
        "CurveID": "GR",
        "Mnemonic": "GR",
        "CurveUnit": "dev:reference-data--UnitOfMeasure:gAPI:",
        "DepthUnit": "dev:reference-data--UnitOfMeasure:m:",
        "TopDepth": 2715.6156,
        "BaseDepth": 2739.8472,
        "CurveDescription": "Gamma Ray",
        "CurveVersion": "1",
        "DateStamp": "2021-11-15T23:32:06Z",
        "LogCurveTypeID": "dev:reference-data--LogCurveType:Equinor-GR:",
        "LogCurveMainFamilyID": "dev:reference-data--LogCurveMainFamily:GammaRay:",
        "LogCurveFamilyID": "dev:reference-data--LogCurveFamily:Gamma%20Ray:"
      },
      {
        "CurveID": "RD",
        "Mnemonic": "RD",
        "CurveUnit": "dev:reference-data--UnitOfMeasure:ohm.m:",
        "DepthUnit": "dev:reference-data--UnitOfMeasure:m:",
        "TopDepth": 2717.5968,
        "BaseDepth": 2741.676,
        "CurveDescription": "Deep resistivity",
        "CurveVersion": "1",
        "DateStamp": "2021-11-15T23:32:06Z",
        "LogCurveBusinessValueID": "dev:reference-data--LogCurveBusinessValue:High:",
        "LogCurveTypeID": "dev:reference-data--LogCurveType:Equinor-RD:",
        "LogCurveMainFamilyID": "dev:reference-data--LogCurveMainFamily:Resistivity:",
        "LogCurveFamilyID": "dev:reference-data--LogCurveFamily:Resistivity%20-%20Deep:"
      }
    ]
  }
}
```

## Coverage

| Section | In the schema | Filled by us | Set by OSDU or kept | Not filled |
| --- | --- | --- | --- | --- |
| 1. Record root, `data` aside | 12 | 5 | 5 set by OSDU | 2 |
| 2.1 AbstractCommonResources | 8 | 1 | 0 | 7 |
| 2.2 AbstractWPCGroupType | 7 | 1 | 2 kept on update | 4 |
| 2.3 AbstractWorkProductComponent | 11 | 1 | 0 | 10 |
| 2.4 WellLog's own, top level | 34 | 15 | 0 | 19 |
| 2.4.1 `Curves[]` | 21 | 13 | 0 | 8 |
| 2.4.2 `VerticalMeasurement` | 12 | 3 | 0 | 9 |
| `ExtensionProperties`, the template's fifth part | 1 | 0 | 1 kept on update | 0 |

## Where the mapping does not match the schema

Every item below passes the preflight gate. The gate checks structure (the target exists in the template, the value fits
its shape, a reference matches its pattern and relationship) and cannot know what a column means, so these are for a
person to judge. Most follow petrodb-api, whose record the mapping reproduces.

1. **`LogSource` holds Recall's log source.** The schema says the property is not to be used yet.
2. **`ActivityType` holds Recall's log service.** The schema wants a general method or circumstance of logging, such as
   MWD or completion, and `HYBRID` is how Recall describes the service.
3. **`Name` does not tell logs apart.** Its property reads `log_source`, and every log of the `STAT_COMP` source has the
   name `STAT_COMP`.
4. **Log-level depths carry no unit.** The schema gives `SamplingStart`, `SamplingStop` and `SamplingInterval` a unit
   context (the Notes column in file 1). OSDU states such units in the root `meta` block, which we do not fill. Every
   sample log is indexed in metres, and a log in feet would read the same. The curves are not affected, because each
   declares `DepthUnit`.
5. **`SamplingInterval` would be written for an irregular log.** The schema says it is not set when sampling is not
   regular. Every sample log is `REGULAR`; `$when: depth_coding = "REGULAR"` on the property would leave it out for any
   other.
6. **No reference is checked against the partition.** Every reference the record carries is built from a value by
   `ref` or `id`. The gate checks each against its property's pattern and relationship, and nothing checks that the
   partition holds the record it names. A unit spelling the lookup tables do not list is kept as it is, and a
   `logs_meas_from` other than `KB` passes the one-entry map unchanged, so either would name a record the partition may
   not hold. Every unit, mnemonic and measurement point in the sample data is listed.
7. **`CompanyID` is left out.** petrodb-api finds it by searching Organisation records, a template this estate does not
   pin.
8. **Both fixtures carry an `MD` curve.** Each names `MD` as the reference curve and lists `MD` among its curves, so the
   documents the gate compares are ones the well log protocol would send. The sample estate's logs have an `MD` curve
   too.
