# OSDU WellLog 1.4.0: the schema as OSDU defines it

This is the official OSDU schema for `osdu:wks:work-product-component--WellLog:1.4.0`, laid out as tables. Nothing in it is ours: every property, type,
requiredness flag, relationship and description comes from the schema itself. The companion file,
[2-how-we-populate-welllog.md](2-how-we-populate-welllog.md), uses the same section numbers and says how OSDU Delivery
fills each one.

| | |
| --- | --- |
| Schema id | `https://schema.osdu.opengroup.org/json/work-product-component/WellLog.1.4.0.json` |
| Kind | `osdu:wks:work-product-component--WellLog:1.4.0` |
| Title | WellLog |
| Review status | Accepted |
| Inherits from | `osdu:wks:AbstractWPCGroupType:1.2.0` |
| Supported file formats | WITSML, DLIS, LIS, LAS2, LAS3, csv |
| JSON Schema dialect | `http://json-schema.org/draft-07/schema#` |
| Source file | `samples/recall-welllog/templates/osdu_wks_work-product-component--WellLog_1.4.0.json` |
| Template version | `26a3c3441882db4f`, the hash of the file's content; the schema was captured 2026-09-07T22:37:02Z |

The source file is the schema as the OSDU schema service serves it, with one mechanical change made when it was
captured: every `$ref` to another schema file was copied into the file's `definitions`, so the file reads on its own.
That bundled form is what a template is saved from: importing the file (`sqlflow template import`, or the Templates
page) saves it in the catalog as template version `26a3c3441882db4f`, which the sample mapping pins.
The descriptions below are verbatim; only line breaks and characters that would break a table are escaped.

> A well log is a data type that correlates a particular measurement or multiple measurements in a wellbore against depth and/or time within that wellbore. When plotted visually, well logs are typically long line graphs (called "curves") but may sometimes be discrete points or intervals. This schema object is intended for digital well logs, not raster log files or raster calibration files, but may be used for the latter in the absence of a defined OSDU schema for these use cases.

## The shape of a WellLog record

```text
{
  "id":          the record id                         section 1
  "kind":        "osdu:wks:work-product-component--WellLog:1.4.0"
  "version":     set by OSDU on every write
  "acl":         who owns it, who may view it          section 3, AbstractAccessControlList
  "legal":       legal tags and countries              section 3, AbstractLegalTags
  "tags":        free string key/value pairs
  "ancestry":    the records this one was derived from section 3, AbstractLegalParentList
  "meta":        unit and CRS context for numbers      section 3, AbstractMetaItem
  "createTime", "createUser", "modifyTime", "modifyUser": set by OSDU
  "data": {
      all of AbstractCommonResources                   section 2.1
      all of AbstractWPCGroupType                      section 2.2
      all of AbstractWorkProductComponent              section 2.3
      WellLog's own properties, Curves[] among them    section 2.4
  }
}
```

| Section | Properties |
| --- | --- |
| 1. Record root, `data` included | 13 |
| 2.1 AbstractCommonResources (1.0.0) | 8 |
| 2.2 AbstractWPCGroupType (1.2.0) | 7 |
| 2.3 AbstractWorkProductComponent (1.1.0) | 11 |
| 2.4 WellLog's own, nested ones included | 57 |

The schema also declares two search conveniences as virtual properties. OSDU derives them when it indexes a record; a
record never carries them.

- `data.VirtualProperties.DefaultLocation`: taken from `data.SpatialArea`, then `data.SpatialPoint`
- `data.VirtualProperties.DefaultName`: taken from `data.Name`

## 1. The record root

These keys sit at the top of every OSDU record, whatever its kind. `kind`, `acl` and `legal` are the only ones the schema requires. The root declares `additionalProperties: false`, so a key not listed here is refused.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `acl` | object, see [AbstractAccessControlList (1.0.0)](#abstractaccesscontrollist-100) | yes |  | The access control tags associated with this entity. |
| `ancestry` | object, see [AbstractLegalParentList (1.0.0)](#abstractlegalparentlist-100) |  |  | The links to data, which constitute the inputs, from which this record instance is derived. |
| `createTime` | string (date-time) |  | example `"2020-12-16T11:46:20.163Z"` | Timestamp of the time at which initial version of this OSDU resource object was created. Set by the System. The value is a combined date-time string in ISO-8601 given in UTC. |
| `createUser` | string |  | example `"some-user@some-company-cloud.com"` | The user reference, which created the first version of this resource object. Set by the System. |
| `data` | any |  |  |  |
| `id` | string |  | pattern `^[\w\-\.]+:work-product-component\-\-WellLog:[\w\-\.\:\%]+$`; example `"namespace:work-product-component--WellLog:c2c79f1c-90ca-5c92-b8df-04dbe438f414"` | Previously called ResourceID or SRN which identifies this OSDU resource object without version. |
| `kind` | string | yes | pattern `^[\w\-\.]+:[\w\-\.]+:[\w\-\.]+:[0-9]+.[0-9]+.[0-9]+$`; example `"osdu:wks:work-product-component--WellLog:1.4.0"` | The schema identification for the OSDU resource object following the pattern {Namespace}:{Source}:{Type}:{VersionMajor}.{VersionMinor}.{VersionPatch}. The versioning scheme follows the semantic versioning, https://semver.org/. |
| `legal` | object, see [AbstractLegalTags (1.0.0)](#abstractlegaltags-100) | yes |  | The entity's legal tags and compliance status. The actual contents associated with the legal tags is managed by the Compliance Service. |
| `meta` | array of [AbstractMetaItem (1.0.0)](#abstractmetaitem-100) |  |  | The Frame of Reference meta data section linking the named properties to self-contained definitions. |
| `modifyTime` | string (date-time) |  | example `"2020-12-16T11:52:24.477Z"` | Timestamp of the time at which this version of the OSDU resource object was created. Set by the System. The value is a combined date-time string in ISO-8601 given in UTC. |
| `modifyUser` | string |  | example `"some-user@some-company-cloud.com"` | The user reference, which created this version of this resource object. Set by the System. |
| `tags` | object |  | example `{"NameOfKey": "String value"}` | A generic dictionary of string keys mapping to string value. Only strings are permitted as keys and values. |
| `version` | integer (int64) |  | example `1562066009929332` | The version number of this OSDU resource; set by the framework. |

## 2. The `data` block

The schema does not declare `data` as one list of properties. It declares `data` as `allOf` four parts, and a record's `data` is all four merged into one object. Nothing inside `data` is required by this schema.

| Part | Where it comes from | Properties |
| --- | --- | --- |
| 2.1 | AbstractCommonResources (1.0.0), shared with other kinds | 8 |
| 2.2 | AbstractWPCGroupType (1.2.0), shared with other kinds | 7 |
| 2.3 | AbstractWorkProductComponent (1.1.0), shared with other kinds | 11 |
| 2.4 | Declared by WellLog itself | 34 top level |

### 2.1 From AbstractCommonResources (1.0.0)

Common resources to be injected at root 'data' level for every entity, which is persistable in Storage. The insertion is performed by the OsduSchemaComposer script.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `data.ExistenceKind` | string |  | points to reference-data--ExistenceKind | Where does this data resource sit in the cradle-to-grave span of its existence? |
| `data.ResourceCurationStatus` | string |  | points to reference-data--ResourceCurationStatus | Describes the current Curation status. |
| `data.ResourceHomeRegionID` | string |  | points to reference-data--OSDURegion | The name of the home [cloud environment] region for this OSDU resource object. |
| `data.ResourceHostRegionIDs` | array of string |  | points to reference-data--OSDURegion | The name of the host [cloud environment] region(s) for this OSDU resource object. |
| `data.ResourceLifecycleStatus` | string |  | points to reference-data--ResourceLifecycleStatus | Describes the current Resource Lifecycle status. |
| `data.ResourceSecurityClassification` | string |  | points to reference-data--ResourceSecurityClassification | Classifies the security level of the resource. |
| `data.Source` | string |  |  | The entity that produced the record, or from which it is received; could be an organization, agency, system, internal team, or individual. For informational purposes only, the list of sources is not governed. |
| `data.TechnicalAssuranceID` | string |  | points to reference-data--TechnicalAssuranceType | DEPRECATED: Describes a record's overall suitability for general business consumption based on data quality. Clarifications: Since Certified is the highest classification of suitable quality, any further change or versioning of a Certified record should be carefully considered and justified. If a Technical Assurance value is not populated then one can assume the data has not been evaluated or its quality is unknown (=Unevaluated). Technical Assurance values are not intended to be used for the identification of a single "preferred" or "definitive" record by comparison with other records. |

### 2.2 From AbstractWPCGroupType (1.2.0)

Generic reference object containing the universal group-type properties of a Work Product Component for inclusion in data type specific Work Product Component objects

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `data.Artefacts` | array of object |  |  | An array of Artefacts - each artefact has a Role, Resource tuple. An artefact is distinct from the file, in the sense certain valuable information is generated during loading process (Artefact generation process). Examples include retrieving location data, performing an OCR which may result in the generation of artefacts which need to be preserved distinctly |
| `data.Artefacts[].ResourceID` | string |  | points to dataset | The SRN which identifies this OSDU Artefact resource. |
| `data.Artefacts[].ResourceKind` | string |  | pattern `^[\w\-\.]+:[\w\-\.]+:[\w\-\.]+:[0-9]+.[0-9]+.[0-9]+$` | The kind or schema ID of the artefact. Resolvable with the Schema Service. |
| `data.Artefacts[].RoleID` | string |  | points to reference-data--ArtefactRole | The record id of this artefact's role. |
| `data.DDMSDatasets` | array of string |  | each item matches `^(([^:/?#]+):)(\/\/([^/?#]+))(\/[^?#]+)(\?([^#]*))?(#(.*))?` | An array of references to content in Domain Data Management Services represented by this work-product-component. The references are formed as URI following https://www.rfc-editor.org/rfc/rfc3986#page-16. This property is exclusively populated by DDMSs. If a work-product-component is represented in more than one DDMS, DDMSs are obliged to find the specific reference by inspecting the URI's authority values matching the DDMS id. |
| `data.Datasets` | array of string |  | points to dataset | The record id, which identifies this OSDU File or dataset resource. |
| `data.IsDiscoverable` | boolean |  |  | A flag that indicates if the work product component is searchable, which means covered in the search index. |
| `data.IsExtendedLoad` | boolean |  |  | A flag that indicates if the work product component is undergoing an extended load. It reflects the fact that the work product component is in an early stage and may be updated before finalization. |
| `data.NameAliases` | array of [AbstractAliasNames (1.0.0)](#abstractaliasnames-100) |  |  | Alternative names, including historical, by which this work-product-component is/has been known (it should include all the identifiers). |
| `data.TechnicalAssurances` | array of [AbstractTechnicalAssurance (1.2.0)](#abstracttechnicalassurance-120) |  |  | Describes a record's overall suitability for general business consumption based on data quality. Clarifications: Since Certified is the highest classification of suitable quality, any further change or versioning of a Certified record should be carefully considered and justified. If a Technical Assurance value is not populated then one can assume the data has not been evaluated or its quality is unknown (=Unevaluated). Technical Assurance values are not intended to be used for the identification of a single "preferred" or "definitive" record by comparison with other records. |

### 2.3 From AbstractWorkProductComponent (1.1.0)

Generic reference object containing the universal properties of a Work Product Component for inclusion in data type specific Work Product Component objects

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `data.AuthorIDs` | array of string |  |  | Array of Authors' names of the work product component. Could be a person or company entity. |
| `data.BusinessActivities` | array of string |  |  | Array of business processes/workflows that the work product component has been through (ex. well planning, exploration). |
| `data.CreationDateTime` | string (date-time) |  |  | Date that a resource (work product component here) is formed outside of OSDU before loading (e.g. publication date). |
| `data.Description` | string |  |  | Description. Summary of the work product component. Not the same as Remark which captures thoughts of creator about the wpc. |
| `data.GeoContexts` | array of [AbstractGeoContext (1.0.0)](#abstractgeocontext-100) |  |  | List of geographic entities which provide context to the WPC. This may include multiple types or multiple values of the same type. |
| `data.LineageAssertions` | array of object |  |  | Defines relationships with other objects (any kind of Resource) upon which this work product component depends. The assertion is directed only from the asserting WPC to ancestor objects, not children. It should not be used to refer to files or artefacts within the WPC -- the association within the WPC is sufficient and Artefacts are actually children of the main WPC file. They should be recorded in the data.Artefacts[] array. |
| `data.LineageAssertions[].ID` | string |  | pattern `^[\w\-\.]+:[\w\-\.]+:[\w\-\.\:\%]+:[0-9]*$` | The object reference identifying the DIRECT, INDIRECT, REFERENCE dependency. |
| `data.LineageAssertions[].LineageRelationshipType` | string |  | points to reference-data--LineageRelationshipType | Used by LineageAssertion to describe the nature of the line of descent of a work product component from a prior Resource, such as DIRECT, INDIRECT, REFERENCE. It is not for proximity (number of nodes away), it is not to cover all the relationships in a full ontology or graph, and it is not to describe the type of activity that created the asserting WPC. LineageAssertion does not encompass a full provenance, process history, or activity model. |
| `data.Name` | string |  |  | Name |
| `data.SpatialArea` | object, see [AbstractSpatialLocation (1.1.0)](#abstractspatiallocation-110) |  |  | A polygon boundary that reflects the locale of the content of the work product component (location of the subject matter). |
| `data.SpatialPoint` | object, see [AbstractSpatialLocation (1.1.0)](#abstractspatiallocation-110) |  |  | A centroid point that reflects the locale of the content of the work product component (location of the subject matter). |
| `data.SubmitterName` | string |  |  | Name of the person that first submitted the work product component to OSDU. |
| `data.Tags` | array of string |  |  | Array of key words to identify the work product, especially to help in search. |

### 2.4 Declared by WellLog itself

Objects the schema writes inline are expanded here with dotted paths, and `[]` marks a step into an array. `data.Curves[]` is the metadata of each curve; the curve values themselves are not part of the record.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `data.ActivityType` | string |  |  | General method or circumstance of logging - MWD, completion, ... |
| `data.BottomMeasuredDepth` | number |  | unit context: `UOM:length` | Informational Bottom Measured Depth of the Well Log. Always populate SamplingStart and SamplingStop, which represents the real sampling of the WellLog, including non-depth sampling. |
| `data.CandidateReferenceCurveIDs` | array of string |  |  | Secondary index curves, which are alternative candidates to act as ReferenceCurveID. Generally not populated, except in the cases where multiple reference curves are present, e.g. measured depth and time. |
| `data.CompanyID` | string |  | points to master-data--Organisation | The relationship to company who engaged the service company (ServiceCompanyID) to perform the logging. |
| `data.ConveyanceMethodID` | string |  | points to reference-data--ConveyanceMethod | The conveyance method used to acquire the log data - if not an acquired log leave empty/absent. |
| `data.Curves` | array of object |  |  |  |
| `data.Curves[].BaseDepth` | number |  | unit context: `UOM_via_property:DepthUnit` | The curve's maximum 'depth' i.e., the reference value at which the curve has its last non-absent value. The curve may contain further absent values in between TopDepth and BaseDepth. Note that the SamplingDomainType may not be a depth as the property name indicates. |
| `data.Curves[].CurveDescription` | string |  | example `"CBL Adjustment Factor, Resistivity Inversion Selection, Detector 1 Barite Constant"` | Mnemonic-level curve description is used during parsing or reading and ingesting LAS or DLIS files, to explain the type of measurement being looked at, specifically for that moment. Curve description is specific to that single (log) mnemonic and for the entire log (acquisition run) interval. In essence, curve description defines the internal factors such as what the "curve" or measurement ideally is representing, how is it calculated, what are the assumptions and the "constants". |
| `data.Curves[].CurveID` | string |  |  | The ID of the Well Log Curve |
| `data.Curves[].CurveQuality` | string |  |  | The Quality of the Log Curve. |
| `data.Curves[].CurveSampleTypeID` | string |  | points to reference-data--CurveSampleType; example `"namespace:reference-data--CurveSampleType:float:"` | The value type to be expected as curve sample values. |
| `data.Curves[].CurveUnit` | string |  | points to reference-data--UnitOfMeasure | Unit of Measure for the Log Curve |
| `data.Curves[].CurveVersion` | string |  |  | The Version of the Log Curve. |
| `data.Curves[].DateStamp` | string (date-time) |  | unit context: `DateTime` | Date curve was created in the database |
| `data.Curves[].DepthCoding` | string |  | pattern `^REGULAR\|DISCRETE$` | DEPRECATED: Replaced by boolean data.IsRegular. The Coding of the depth. |
| `data.Curves[].DepthUnit` | string |  | points to reference-data--UnitOfMeasure | Unit of Measure for TopDepth and BaseDepth. |
| `data.Curves[].Interpolate` | boolean |  |  | Whether curve can be interpolated or not |
| `data.Curves[].InterpreterName` | string |  |  | The name of person who interpreted this Log Curve. |
| `data.Curves[].IsProcessed` | boolean |  |  | Indicates if the curve has been (pre)processed or if it is a raw recording |
| `data.Curves[].LogCurveBusinessValueID` | string |  | points to reference-data--LogCurveBusinessValue | The related record id of the Log Curve Business Value Type. |
| `data.Curves[].LogCurveFamilyID` | string |  | points to reference-data--LogCurveFamily | The related record id of the Log Curve Family - which is the detailed Geological Physical Quantity Measured - such as neutron porosity |
| `data.Curves[].LogCurveMainFamilyID` | string |  | points to reference-data--LogCurveMainFamily | The related record id of the Log Curve Main Family Type - which is the Geological Physical Quantity measured - such as porosity. |
| `data.Curves[].LogCurveTypeID` | string |  | points to reference-data--LogCurveType | The related record id of the Log Curve Type - which is the standard mnemonic chosen by the company - OSDU provides an initial list |
| `data.Curves[].Mnemonic` | string |  | example `"PRES_HDRB.BAR"` | The Mnemonic of the Log Curve is the value as received either from Raw Providers or from Internal Processing team |
| `data.Curves[].NullValue` | boolean |  |  | Indicates that there is no measurement within the curve |
| `data.Curves[].NumberOfColumns` | integer |  | example `192` | The number of columns present in this Curve for a single reference value. For simple logs this is typically 1; for image logs this holds the number of image traces or property series. Further information about the columns can be obtained via the respective log or curve APIs of the Domain Data Management Service. |
| `data.Curves[].TopDepth` | number |  | unit context: `UOM_via_property:DepthUnit` | The curve's minimum 'depth', i.e., the reference value at which the curve has its first non-absent value. The curve may contain further absent values in between TopDepth and BaseDepth. Note that the SamplingDomainType may not be a depth as the property name indicates. |
| `data.DrillingFluidProperty` | string |  |  | DEPRECATED: Please use reference values from WellboreFluidType in WellboreFluidTypeID instead. Type of mud at time of logging (oil, water based,...) |
| `data.FrameIdentifier` | string |  | example `0` | For multi-frame or multi-section files, this identifier defines the source frame in the file. If the identifier is an index number the index starts with zero and is converted to a string for this property. |
| `data.HoleTypeLogging` | string |  | pattern `^OPENHOLE\|CASEDHOLE\|CEMENTEDHOLE$` | Description of the hole related type of logging - POSSIBLE VALUE : OpenHole / CasedHole / CementedHole |
| `data.IsRegular` | boolean |  |  | Boolean property indicating the sampling mode of the ReferenceCurveID. True means all reference curve values are regularly spaced (see SamplingInterval); false means irregular or discrete sample spacing. |
| `data.LogActivity` | string |  |  | Log Activity, used to describe the type of pass such as Calibration Pass - Main Pass - Repeated Pass |
| `data.LogRemark` | string |  | example `"tool failure, bad weather"` | Log remark provides contextual information during the actual log object acquisition. Explains how the measurement in the wellbore is taken on a point in time or depth. Additional information may be included such as bad weather, tool failure, etc. Usually a part of the log header, log remark contains info specific for an acquisition run, specific for a given logging tool (multiple measurements) and/or a specific interval. In essence, log remark represents the external factors and operational environment, directly or indirectly affecting the measurement quality/uncertainty (dynamically over time/depth) - adding both noise and bias to the measurements. |
| `data.LogRun` | string |  |  | Log Run - describe the run of the log - can be a number, but may be also a alphanumeric description such as a version name |
| `data.LogServiceDateInterval` | object |  |  | An interval built from two nested values : StartDate and EndDate. It applies to the whole log services and may apply to composite logs as [start of the first run job] and [end of the last run job]Log Service Date |
| `data.LogServiceDateInterval.EndDate` | string (date-time) |  |  |  |
| `data.LogServiceDateInterval.StartDate` | string (date-time) |  |  |  |
| `data.LogSource` | string |  |  | OSDU Native Log Source - will be updated for later releases - not to be used yet |
| `data.LogVersion` | string |  |  | Log Version |
| `data.LoggingDirection` | string |  |  | Specifies whether curves were collected downward or upward |
| `data.LoggingService` | string |  |  | Logging Service - mainly a short concatenation of the names of the tools |
| `data.PassNumber` | integer |  |  | Indicates if the Pass is the Main one (1) or a repeated one - and it's level repetition |
| `data.ReferenceCurveID` | string |  | example `"MD"` | The data.Curves[].CurveID, which holds the primary index (reference) values. |
| `data.SamplingDomainTypeID` | string |  | points to reference-data--WellLogSamplingDomainType; example `"namespace:reference-data--WellLogSamplingDomainType:Depth:"` | The sampling domain, e.g. measured depth, true vertical, travel-time, calendar-time. |
| `data.SamplingInterval` | number |  | unit context: `UOM`; example `0.0254` | For regularly sampled curves this property holds the sampling interval. For non regular sampling rate this property is not set. The IsRegular flag indicates whether SamplingInterval is required. |
| `data.SamplingStart` | number |  | unit context: `UOM`; example `2500` | The start value/first value of the ReferenceCurveID, typically the start depth of the logging. |
| `data.SamplingStop` | number |  | unit context: `UOM`; example `7500` | The stop value/last value of the ReferenceCurveID, typically the end depth of the logging. |
| `data.SeismicReferenceElevation` | object, see [AbstractFacilityVerticalMeasurement (1.0.0)](#abstractfacilityverticalmeasurement-100) |  |  | Populated only if the WellLog represents time-depth relationships or checkshots. It is expressed via the standard AbstractFacilityVerticalMeasurement. The following properties are expected to be present: VerticalMeasurementPathID (typically elevation), VerticalMeasurementTypeID as SeismicReferenceDatum, VerticalMeasurement holding the offset to either the VerticalCRSID or the chained VerticalReferenceID in the parent Wellbore. |
| `data.ServiceCompanyID` | string |  | points to master-data--Organisation | The relationship to a Service Company, typically the producer or logging contractor. |
| `data.ToolStringDescription` | string |  |  | Tool String Description - a long concatenation of the tools used for logging services such as GammaRay+NeutronPorosity |
| `data.TopMeasuredDepth` | number |  | unit context: `UOM:length` | Informational Top Measured Depth of the Well Log. Always populate SamplingStart and SamplingStop, which represents the real sampling of the WellLog, including non-depth sampling. |
| `data.VerticalMeasurement` | object, see [AbstractFacilityVerticalMeasurement (1.0.0)](#abstractfacilityverticalmeasurement-100) |  |  | The vertical measurement reference for the log curves, which defines the vertical reference datum for the logged depths. Either VerticalMeasurement or VerticalMeasurementID are populated. |
| `data.VerticalMeasurementID` | string |  |  | DEPRECATED: Use data.VerticalMeasurement.VerticalReferenceID instead. References an entry in the Vertical Measurement array for the Wellbore identified by WellboreID, which defines the vertical reference datum for all curve measured depths. Either VerticalMeasurementID or VerticalMeasurement are populated. |
| `data.WellLogTypeID` | string |  | points to reference-data--LogType | Well Log Type short Description such as Raw; Evaluated; Composite;.... |
| `data.WellboreFluidTypeID` | string |  | points to reference-data--WellboreFluidType | Type of fluid in the wellbore at time of logging (oil, water based mud, water, ...) |
| `data.WellboreID` | string |  | points to master-data--Wellbore | The Wellbore where the Well Log Work Product Component was recorded |
| `data.ZeroTime` | string (date-time) |  | unit context: `DateTime` | Optional time reference for (calender) time logs. The ISO date time string representing zero time. Not to be confused with seismic travel time zero. The latter is defined by SeismicReferenceDatum. |

## 3. Building blocks

Definitions the tables above point to, in alphabetical order. OSDU defines them once and reuses them across kinds. Paths in these tables are relative to the property that refers to the definition.

### AbstractAccessControlList (1.0.0)

The access control tags associated with this entity. This structure is included by the SystemProperties "acl", which is part of all OSDU records. Not extensible.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `owners` | array of string | yes | each item matches `^[a-zA-Z0-9_+&*-]+(?:\.[a-zA-Z0-9_+&*-]+)*@(?:[a-zA-Z0-9-]+\.)+[a-zA-Z]{2,7}$` | The list of owners of this data record formatted as an email (core.common.model.storage.validation.ValidationDoc.EMAIL_REGEX). |
| `viewers` | array of string | yes | each item matches `^[a-zA-Z0-9_+&*-]+(?:\.[a-zA-Z0-9_+&*-]+)*@(?:[a-zA-Z0-9-]+\.)+[a-zA-Z]{2,7}$` | The list of viewers to which this data record is accessible/visible/discoverable formatted as an email (core.common.model.storage.validation.ValidationDoc.EMAIL_REGEX). |

### AbstractAliasNames (1.0.0)

A list of alternative names for an object. The preferred name is in a separate, scalar property. It may or may not be repeated in the alias list, though a best practice is to include it if the list is present, but to omit the list if there are no other names. Note that the abstract entity is an array so the $ref to it is a simple property reference.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `AliasName` | string |  |  | Alternative Name value of defined name type for an object. |
| `AliasNameTypeID` | string |  | points to reference-data--AliasNameType | A classification of alias names such as by role played or type of source, such as regulatory name, regulatory code, company code, international standard name, etc. |
| `DefinitionOrganisationID` | string |  | points to reference-data--StandardsOrganisation, master-data--Organisation | The StandardsOrganisation (reference-data) or Organisation (master-data) that provided the name (the source). |
| `EffectiveDateTime` | string (date-time) |  |  | The date and time when an alias name becomes effective. |
| `TerminationDateTime` | string (date-time) |  |  | The data and time when an alias name is no longer in effect. |

### AbstractAnyCrsFeatureCollection (1.1.0)

A schema like GeoJSON FeatureCollection with a non-WGS 84 CRS context; based on https://geojson.org/schema/FeatureCollection.json. Attention: the coordinate order is fixed: Longitude/Easting/Westing/X first, followed by Latitude/Northing/Southing/Y, optionally height as third coordinate.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `CoordinateReferenceSystemID` | string |  | points to reference-data--CoordinateReferenceSystem | The CRS reference into the CoordinateReferenceSystem catalog. |
| `VerticalCoordinateReferenceSystemID` | string |  | points to reference-data--CoordinateReferenceSystem; example `"namespace:reference-data--CoordinateReferenceSystem:Vertical:EPSG::5714:"` | The explicit VerticalCRS reference into the CoordinateReferenceSystem catalog. This property stays empty for 2D geometries. Absent or empty values for 3D geometries mean the context may be provided by a CompoundCRS in 'CoordinateReferenceSystemID' or implicitly EPSG:5714 MSL height |
| `VerticalUnitID` | string |  | points to reference-data--UnitOfMeasure; example `"namespace:reference-data--UnitOfMeasure:m:"` | The explicit vertical unit ID, referring to a reference-data--UnitOfMeasure record; this is only required for features containing 3-dimensional coordinates and undefined vertical CoordinateReferenceSystems; if a VerticalCoordinateReferenceSystemID is populated, the VerticalUnitID is given by the VerticalCoordinateReferenceSystemID's data.CoordinateSystem.VerticalAxisUnitID. The VerticalUnitID definition overrides any self-contained definition in persistableReferenceUnitZ. |
| `bbox` | array of number |  |  |  |
| `features` | array of object | yes |  |  |
| `features[].bbox` | array of number |  |  |  |
| `features[].geometry` | one of: inline object, inline object, inline object, inline object, inline object, inline object, inline object, inline object | yes |  |  |
| `features[].properties` | one of: inline object, inline object | yes |  |  |
| `features[].type` | string | yes | one of `AnyCrsFeature` |  |
| `persistableReferenceCrs` | string | yes |  | The CRS reference as persistableReference string. If populated, the CoordinateReferenceSystemID takes precedence. |
| `persistableReferenceUnitZ` | string |  |  | The unit of measure for the Z-axis (only for 3-dimensional coordinates, where the CRS does not describe the vertical unit). Note that the direction is upwards positive, i.e. Z means height. |
| `persistableReferenceVerticalCrs` | string |  |  | The VerticalCRS reference as persistableReference string. If populated, the VerticalCoordinateReferenceSystemID takes precedence. The property is null or empty for 2D geometries. For 3D geometries and absent or null persistableReferenceVerticalCrs the vertical CRS is either provided via persistableReferenceCrs's CompoundCRS or it is implicitly defined as EPSG:5714 MSL height. |
| `type` | string | yes | one of `AnyCrsFeatureCollection` |  |

### AbstractContact (1.1.0)

An object with properties that describe a specific person or other point-of-contact (like an email distribution list) that is relevant in this context (like a given data set or business project). The contact specified may be either internal or external to the organisation (something denoted via the Organisation object that is referenced). Note that some properties contain personally identifiable information, so it might not be appropriate to populate all properties in all scenarios.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `Comment` | string |  |  | Additional information about the contact |
| `DataGovernanceRoleTypeID` | string |  | points to reference-data--DataGovernanceRoleType | The data governance role assigned to this contact if and only if the context has a data governance role (in context of TechnicalAssurance). The value is kept absent in all other cases. |
| `EmailAddress` | string |  | example `"support@company.com"` | Contact email address. Property may be left empty where it is inappropriate to provide personally identifiable information. |
| `Name` | string |  |  | Name of the individual contact. Property may be left empty where it is inappropriate to provide personally identifiable information. |
| `OrganisationID` | string |  | points to master-data--Organisation | Reference to the company the contact is associated with. |
| `PhoneNumber` | string |  | example `"1-555-281-5555"` | Contact phone number. Property may be left empty where it is inappropriate to provide personally identifiable information. |
| `RoleTypeID` | string |  | points to reference-data--ContactRoleType | The identifier of a reference value for the role of the contact within the associated organisation, such as Account owner, Sales Representative, Technical Support, Project Manager, Party Chief, Client Representative, Senior Observer. |
| `WorkflowPersonaTypeID` | string |  | points to reference-data--WorkflowPersonaType | The persona in context of workflows associated with this contact, as used in TechnicalAssurance. |

### AbstractFacilityVerticalMeasurement (1.0.0)

A location along a wellbore, _usually_ associated with some aspect of the drilling of the wellbore, but not with any intersecting _subsurface_ natural surfaces.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `EffectiveDateTime` | string (date-time) |  | unit context: `DateTime` | The date and time at which a vertical measurement instance becomes effective. |
| `TerminationDateTime` | string (date-time) |  | unit context: `DateTime` | The date and time at which a vertical measurement instance is no longer in effect. |
| `VerticalCRSID` | string |  | points to reference-data--CoordinateReferenceSystem | A vertical coordinate reference system defines the origin for height or depth values. It is expected that either VerticalCRSID or VerticalReferenceID reference is provided in a given vertical measurement array object, but not both. |
| `VerticalMeasurement` | number |  | unit context: `UOM_via_property:VerticalMeasurementUnitOfMeasureID` | The value of the elevation or depth. Depth is positive downwards from a vertical reference or geodetic datum along a path, which can be vertical; elevation is positive upwards from a geodetic datum along a vertical path. Either can be negative. |
| `VerticalMeasurementDescription` | string |  |  | Text which describes a vertical measurement in detail. |
| `VerticalMeasurementPathID` | string |  | points to reference-data--VerticalMeasurementPath | Specifies Measured Depth, True Vertical Depth, or Elevation. |
| `VerticalMeasurementSourceID` | string |  | points to reference-data--VerticalMeasurementSource | Specifies Driller vs Logger. |
| `VerticalMeasurementTypeID` | string |  | points to reference-data--VerticalMeasurementType | Specifies the type of vertical measurement (TD, Plugback, Kickoff, Drill Floor, Rotary Table...). |
| `VerticalMeasurementUnitOfMeasureID` | string |  | points to reference-data--UnitOfMeasure | The unit of measure for the vertical measurement. If a unit of measure and a vertical CRS are provided, the unit of measure provided is taken over the unit of measure from the CRS. |
| `VerticalReferenceEntityID` | string |  | points to master-data--Wellbore, master-data--Well, master-data--Rig | This relationship identifies the entity (aka record) in which the VerticalReferenceID is found; It could be a different OSDU entity or a self-reference. For example, a Wellbore VerticalMeasurement may reference a member of a VerticalMeasurements[] array in its parent Well record. Alternatively, VerticalReferenceEntityID may be populated with the ID of its own Wellbore record to make explicit that VerticalReferenceID is intended to be found in this record, not another. |
| `VerticalReferenceID` | string |  |  | The reference point from which the relative vertical measurement is made. This is only populated if the measurement has no VerticalCRSID specified. The value entered must match the VerticalMeasurementID for another vertical measurement array element in Wellbore or Well or in a related parent facility. The relationship should be declared explicitly in VerticalReferenceEntityID. Any chain of measurements must ultimately resolve to a Vertical CRS. It is expected that a VerticalCRSID or a VerticalReferenceID is provided in a given vertical measurement array object, but not both. |
| `WellboreTVDTrajectoryID` | string |  | points to work-product-component--WellboreTrajectory | Specifies what directional survey or wellpath was used to calculate the TVD. |

### AbstractFeatureCollection (1.0.0)

GeoJSON feature collection as originally published in https://geojson.org/schema/FeatureCollection.json. Attention: the coordinate order is fixed: Longitude first, followed by Latitude, optionally height above MSL (EPSG:5714) as third coordinate.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `bbox` | array of number |  |  |  |
| `features` | array of object | yes |  |  |
| `features[].bbox` | array of number |  |  |  |
| `features[].geometry` | one of: inline object, inline object, inline object, inline object, inline object, inline object, inline object, inline object | yes |  |  |
| `features[].properties` | one of: inline object, inline object | yes |  |  |
| `features[].type` | string | yes | one of `Feature` |  |
| `type` | string | yes | one of `FeatureCollection` |  |

### AbstractGeoBasinContext (1.0.0)

A single, typed basin entity reference, which is 'abstracted' to AbstractGeoContext and then aggregated by GeoContexts properties.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `BasinID` | string |  | points to master-data--Basin | Reference to Basin. |
| `GeoTypeID` | string |  | points to reference-data--BasinType | The BasinType reference of the Basin (via BasinID) for application convenience. |

### AbstractGeoContext (1.0.0)

A geographic context to an entity. It can be either a reference to a GeoPoliticalEntity, Basin, Field, Play or Prospect.

An object of this type takes exactly one of the following shapes.

- Shape 1: [AbstractGeoPoliticalContext (1.0.0)](#abstractgeopoliticalcontext-100)
- Shape 2: [AbstractGeoBasinContext (1.0.0)](#abstractgeobasincontext-100)
- Shape 3: [AbstractGeoFieldContext (1.0.0)](#abstractgeofieldcontext-100)
- Shape 4: [AbstractGeoPlayContext (1.0.0)](#abstractgeoplaycontext-100)
- Shape 5: [AbstractGeoProspectContext (1.0.0)](#abstractgeoprospectcontext-100)


### AbstractGeoFieldContext (1.0.0)

A single, typed field entity reference, which is 'abstracted' to AbstractGeoContext and then aggregated by GeoContexts properties.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `FieldID` | string |  | points to master-data--Field | Reference to Field. |
| `GeoTypeID` | string |  | always `Field` | The fixed type 'Field' for this AbstractGeoFieldContext. |

### AbstractGeoPlayContext (1.0.0)

A single, typed Play entity reference, which is 'abstracted' to AbstractGeoContext and then aggregated by GeoContexts properties.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `GeoTypeID` | string |  | points to reference-data--PlayType | The PlayType reference of the Play (via PlayID) for application convenience. |
| `PlayID` | string |  | points to master-data--Play | Reference to the play. |

### AbstractGeoPoliticalContext (1.0.0)

A single, typed geo-political entity reference, which is 'abstracted' to AbstractGeoContext and then aggregated by GeoContexts properties.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `GeoPoliticalEntityID` | string |  | points to master-data--GeoPoliticalEntity | Reference to GeoPoliticalEntity. |
| `GeoTypeID` | string |  | points to reference-data--GeoPoliticalEntityType | The GeoPoliticalEntityType reference of the GeoPoliticalEntity (via GeoPoliticalEntityID) for application convenience. |

### AbstractGeoProspectContext (1.0.0)

A single, typed Prospect entity reference, which is 'abstracted' to AbstractGeoContext and then aggregated by GeoContexts properties.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `GeoTypeID` | string |  | points to reference-data--ProspectType | The ProspectType reference of the Prospect (via ProspectID) for application convenience. |
| `ProspectID` | string |  | points to master-data--Prospect | Reference to the prospect. |

### AbstractLegalParentList (1.0.0)

A list of entity id:version references to record instances recorded in the data platform, from which the current record is derived and from which the legal tags must be derived. This structure is included by the SystemProperties "ancestry", which is part of all OSDU records. Not extensible.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `parents` | array of string |  | each item matches `^[\w\-\.]+:[\w\-\.]+:[\w\-\.\:\%]+:[0-9]+$`; example `[]` | An array of none, one or many entity references of 'direct parents' in the data platform, which mark the current record as a derivative. In contrast to other relationships, the source record version is required. During record creation or update the ancestry.parents[] relationships are used to collect the legal tags from the sources and aggregate them in the legal.legaltags[] array. As a consequence, should e.g., one or more of the legal tags of the source data expire, the access to the derivatives is also terminated. For details, see ComplianceService tutorial, 'Creating derivative Records'. |

### AbstractLegalTags (1.0.0)

Legal meta data like legal tags, relevant other countries, legal status. This structure is included by the SystemProperties "legal", which is part of all OSDU records. Not extensible.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `legaltags` | array of string | yes |  | The list of legal tags, which resolve to legal properties (like country of origin, export classification code, etc.) and rules with the help of the Compliance Service. |
| `otherRelevantDataCountries` | array of string | yes | each item matches `^[A-Z]{2}$` | The list of other relevant data countries as an array of two-letter country codes, see https://en.wikipedia.org/wiki/ISO_3166-1_alpha-2. |
| `status` | string |  | pattern `^(compliant\|uncompliant)$` | The legal status. Set by the system after evaluation against the compliance rules associated with the "legaltags" using the Compliance Service. |

### AbstractMetaItem (1.0.0)

A meta data item, which allows the association of named properties or property values to a Unit/Measurement/CRS/Azimuth/Time context.

An object of this type takes exactly one of the following shapes.

**Shape 1: FrameOfReferenceUOM**

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `kind` | string | yes | always `Unit` | The kind of reference, 'Unit' for FrameOfReferenceUOM. |
| `name` | string |  | example `"ft[US]"` | The unit symbol or name of the unit. |
| `persistableReference` | string | yes |  | The self-contained, persistable reference string uniquely identifying the Unit. |
| `propertyNames` | array of string |  | example `["HorizontalDeflection.EastWest", "HorizontalDeflection.NorthSouth"]` | The list of property names, to which this meta data item provides Unit context to. A full path like "StructureA.PropertyB" is required to define a unique context; "data" is omitted since frame-of reference normalization only applies to the data block. |
| `unitOfMeasureID` | string |  | points to reference-data--UnitOfMeasure; example `"namespace:reference-data--UnitOfMeasure:ftUS:"` | SRN to unit of measure reference. |

**Shape 2: FrameOfReferenceCRS**

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `coordinateReferenceSystemID` | string |  | points to reference-data--CoordinateReferenceSystem; example `"namespace:reference-data--CoordinateReferenceSystem:Projected:EPSG::32615:"` | SRN to CRS reference. |
| `kind` | string | yes | always `CRS` | The kind of reference, constant 'CRS' for FrameOfReferenceCRS. |
| `name` | string |  | example `"WGS 84 / UTM zone 15N"` | The name of the CRS. |
| `persistableReference` | string | yes |  | The self-contained, persistable reference string uniquely identifying the CRS. |
| `propertyNames` | array of string |  | example `["KickOffPosition.X", "KickOffPosition.Y"]` | The list of property names, to which this meta data item provides CRS context to. A full path like "StructureA.PropertyB" is required to define a unique context; "data" is omitted since frame-of reference normalization only applies to the data block. |

**Shape 3: FrameOfReferenceDateTime**

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `kind` | string | yes | always `DateTime` | The kind of reference, constant 'DateTime', for FrameOfReferenceDateTime. |
| `name` | string |  | example `"UTC"` | The name of the DateTime format and reference. |
| `persistableReference` | string | yes | example `"{\"format\":\"yyyy-MM-ddTHH:mm:ssZ\",\"timeZone\":\"UTC\",\"type\":\"DTM\"}"` | The self-contained, persistable reference string uniquely identifying DateTime reference. |
| `propertyNames` | array of string |  | example `["Acquisition.StartTime", "Acquisition.EndTime"]` | The list of property names, to which this meta data item provides DateTime context to. A full path like "StructureA.PropertyB" is required to define a unique context; "data" is omitted since frame-of reference normalization only applies to the data block. |

**Shape 4: FrameOfReferenceAzimuthReference**

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `kind` | string | yes | always `AzimuthReference` | The kind of reference, constant 'AzimuthReference', for FrameOfReferenceAzimuthReference. |
| `name` | string |  | example `"TrueNorth"` | The name of the CRS or the symbol/name of the unit. |
| `persistableReference` | string | yes | example `"{\"code\":\"TrueNorth\",\"type\":\"AZR\"}"` | The self-contained, persistable reference string uniquely identifying AzimuthReference. |
| `propertyNames` | array of string |  | example `["Bearing"]` | The list of property names, to which this meta data item provides AzimuthReference context to. A full path like "StructureA.PropertyB" is required to define a unique context; "data" is omitted since frame-of reference normalization only applies to the data block. |


### AbstractSpatialLocation (1.1.0)

A geographic object which can be described by a set of points.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `AppliedOperations` | array of string |  |  | The audit trail of operations applied to the coordinates from the original state to the current state. The list may contain operations applied prior to ingestion as well as the operations applied to produce the Wgs84Coordinates. The text elements refer to ESRI style CRS and Transformation names, which may have to be translated to EPSG standard names. |
| `AsIngestedCoordinates` | object, see [AbstractAnyCrsFeatureCollection (1.1.0)](#abstractanycrsfeaturecollection-110) |  | unit context: `CRS:` | The original or 'as ingested' coordinates (Point, MultiPoint, LineString, MultiLineString, Polygon or MultiPolygon). The name 'AsIngestedCoordinates' was chosen to contrast it to 'OriginalCoordinates', which carries the uncertainty whether any coordinate operations took place before ingestion. In cases where the original CRS is different from the as-ingested CRS, the AppliedOperations can also contain the list of operations applied to the coordinate prior to ingestion. The data structure is similar to GeoJSON FeatureCollection, however in a CRS context explicitly defined within the AbstractAnyCrsFeatureCollection. The coordinate sequence follows GeoJSON standard, i.e. 'eastward/longitude', 'northward/latitude' {, 'upward/height' unless overridden by an explicit direction in the AsIngestedCoordinates.VerticalCoordinateReferenceSystemID}. |
| `CoordinateQualityCheckDateTime` | string (date-time) |  | unit context: `DateTime` | The date of the Quality Check. |
| `CoordinateQualityCheckPerformedBy` | string |  |  | The user who performed the Quality Check. |
| `CoordinateQualityCheckRemarks` | array of string |  |  | Freetext remarks on Quality Check. |
| `QualitativeSpatialAccuracyTypeID` | string |  | points to reference-data--QualitativeSpatialAccuracyType | A qualitative description of the quality of a spatial location, e.g. unverifiable, not verified, basic validation. |
| `QuantitativeAccuracyBandID` | string |  | points to reference-data--QuantitativeAccuracyBand | An approximate quantitative assessment of the quality of a location (accurate to &gt; 500 m (i.e. not very accurate)), to &lt; 1 m, etc. |
| `SpatialGeometryTypeID` | string |  | points to reference-data--SpatialGeometryType | Indicates the expected look of the SpatialParameterType, e.g. Point, MultiPoint, LineString, MultiLineString, Polygon, MultiPolygon. The value constrains the type of geometries in the GeoJSON Wgs84Coordinates and AsIngestedCoordinates. |
| `SpatialLocationCoordinatesDate` | string (date-time) |  | unit context: `DateTime` | Date when coordinates were measured or retrieved. |
| `SpatialParameterTypeID` | string |  | points to reference-data--SpatialParameterType | A type of spatial representation of an object, often general (e.g. an Outline, which could be applied to Field, Reservoir, Facility, etc.) or sometimes specific (e.g. Onshore Outline, State Offshore Outline, Federal Offshore Outline, 3 spatial representations that may be used by Countries). |
| `Wgs84Coordinates` | object, see [AbstractFeatureCollection (1.0.0)](#abstractfeaturecollection-100) |  |  | The normalized coordinates (Point, MultiPoint, LineString, MultiLineString, Polygon or MultiPolygon) based on WGS 84 (EPSG:4326 for 2-dimensional coordinates, EPSG:4326 + EPSG:5714 (MSL) for 3-dimensional coordinates). This derived coordinate representation is intended for global discoverability only. The schema of this substructure is identical to the GeoJSON FeatureCollection https://geojson.org/schema/FeatureCollection.json. The coordinate sequence follows GeoJSON standard, i.e. longitude, latitude {, height} |

### AbstractTechnicalAssurance (1.2.0)

Describes a record's overall suitability for general business consumption based on level of trust.

| Property | Type | Required | Notes | Description (verbatim) |
| --- | --- | --- | --- | --- |
| `AcceptableUsage` | array of object |  |  | List of workflows and/or personas that the technical assurance value is valid for (e.g., This data is trusted for Seismic Processing) |
| `AcceptableUsage[].DataQualityID` | string |  | points to work-product-component--DataQuality | The relationship to a work-product-component--DataQuality assessment record, which was used as the basis for this decision. |
| `AcceptableUsage[].DataQualityRuleSetID` | string |  | points to reference-data--DataQualityRuleSet | The DataQualityRuleSet, which had to pass successfully to achieve this level of technical assurance. |
| `AcceptableUsage[].QualityDataRuleSetID` | string |  | points to reference-data--QualityDataRuleSet | DEPRECATED: superseded by DataQualityRuleSetID referring to DataQualityRuleSet. The QualityDataRuleSet, which had to pass successfully to achieve this level of technical assurance. |
| `AcceptableUsage[].ValueChainStatusTypeID` | string |  | points to reference-data--ValueChainStatusType | The stage of business where the record is acceptable for workflow usage. |
| `AcceptableUsage[].WorkflowPersona` | string |  | points to reference-data--WorkflowPersonaType; example `"namespace:reference-data--WorkflowPersonaType:SeismicProcessor:"` | DEPRECATED: superseded by WorkflowPersonaTypeID. Name of the role or personas that the record is technical assurance value is valid for. |
| `AcceptableUsage[].WorkflowPersonaTypeID` | string |  | points to reference-data--WorkflowPersonaType; example `"namespace:reference-data--WorkflowPersonaType:SeismicProcessor:"` | Name of the role or personas that the record's technical assurance value is valid for. |
| `AcceptableUsage[].WorkflowUsage` | string |  | points to reference-data--WorkflowUsageType; example `"namespace:reference-data--WorkflowUsageType:SeismicProcessing:"` | DEPRECATED: superseded by WorkflowUsageTypeID. Name of the business activities, processes, and/or workflows that the record is technical assurance value is valid for. |
| `AcceptableUsage[].WorkflowUsageTypeID` | string |  | points to reference-data--WorkflowUsageType; example `"namespace:reference-data--WorkflowUsageType:SeismicProcessing:"` | Name of the business activities, processes, and/or workflows that the record's technical assurance value is valid for. |
| `Comment` | string |  | example `"This is free form text from reviewer, e.g. restrictions on use"` | Any additional context to support the determination of technical assurance |
| `EffectiveDate` | string (date) |  | example `"2020-02-13"` | Date when the technical assurance determination for this record has taken place |
| `Reviewers` | array of [AbstractContact (1.1.0)](#abstractcontact-110) |  |  | The individuals, or roles, that reviewed and determined the technical assurance value |
| `TechnicalAssuranceTypeID` | string | yes | points to reference-data--TechnicalAssuranceType; example `"namespace:reference-data--TechnicalAssuranceType:Trusted:"` | Describes a record's overall suitability for general business consumption based on data quality. Clarifications: Since Certified is the highest classification of suitable quality, any further change or versioning of a Certified record should be carefully considered and justified. If a Technical Assurance value is not populated then one can assume the data has not been evaluated or its quality is unknown (=Unevaluated). Technical Assurance values are not intended to be used for the identification of a single "preferred" or "definitive" record by comparison with other records. |
| `UnacceptableUsage` | array of object |  |  | List of workflows and/or personas that the technical assurance value is not valid for (e.g., This data is not trusted for seismic interpretation) |
| `UnacceptableUsage[].DataQualityID` | string |  | points to work-product-component--DataQuality | The relationship to a work-product-component--DataQuality assessment record, which was used as the basis for this decision. |
| `UnacceptableUsage[].DataQualityRuleSetID` | string |  | points to reference-data--DataQualityRuleSet | The DataQualityRuleSet, which did not pass successfully to achieve this level of technical assurance. |
| `UnacceptableUsage[].QualityDataRuleSetID` | string |  | points to reference-data--QualityDataRuleSet | DEPRECATED: superseded by DataQualityRuleSetID referring to DataQualityRuleSet. The QualityDataRuleSet, which did not pass successfully to achieve this level of technical assurance. |
| `UnacceptableUsage[].ValueChainStatusTypeID` | string |  | points to reference-data--ValueChainStatusType | The stage of business where the record is not acceptable for workflow usage. |
| `UnacceptableUsage[].WorkflowPersona` | string |  | points to reference-data--WorkflowPersonaType; example `"namespace:reference-data--WorkflowPersonaType:SeismicInterpreter:"` | DEPRECATED: superseded by WorkflowPersonaTypeID. Name of the role or personas that the record is technical assurance value is not valid for. |
| `UnacceptableUsage[].WorkflowPersonaTypeID` | string |  | points to reference-data--WorkflowPersonaType; example `"namespace:reference-data--WorkflowPersonaType:SeismicProcessor:"` | Name of the role or personas that the record is technical assurance value is not valid for. |
| `UnacceptableUsage[].WorkflowUsage` | string |  | points to reference-data--WorkflowUsageType; example `"namespace:reference-data--WorkflowUsageType:SeismicInterpretation:"` | DEPRECATED: superseded by WorkflowUsageTypeID. Name of the business activities, processes, and/or workflows that the record is technical assurance value is not valid for. |
| `UnacceptableUsage[].WorkflowUsageTypeID` | string |  | points to reference-data--WorkflowUsageType; example `"namespace:reference-data--WorkflowUsageType:SeismicProcessing:"` | Name of the business activities, processes, and/or workflows that the record's technical assurance value is not valid for. |
