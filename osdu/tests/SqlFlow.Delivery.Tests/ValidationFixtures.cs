using System.Text.Json.Nodes;
using SqlFlow.Delivery.Snapshots;
using SqlFlow.Delivery.Validation;

namespace SqlFlow.Delivery.Tests;

/// <summary>
/// The schema and records the validation suites check against: one bundled schema that states every rule the checks apply,
/// composed the way OSDU composes its records (an envelope with <c>acl</c> and <c>legal</c> behind references, a <c>data</c>
/// built from <c>allOf</c> branches, lists of objects, a choice of forms, relationships with id patterns), and a record that
/// meets all of it, which each case breaks in one place.
/// </summary>
internal static class ValidationFixtures
{
    public const string Kind = "test:wks:master-data--Thing:1.0.0";

    /// <summary>The schema every case of the suites checks against.</summary>
    public static SchemaSnapshot Schema { get; } = SchemaSnapshot.Parse(Kind, SchemaJson, DateTimeOffset.UnixEpoch);

    /// <summary>The rules of <see cref="Schema"/>, compiled once for the process as a gate compiles them.</summary>
    public static SchemaRules Rules => SchemaRules.Of(Schema);

    public const string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string", "pattern": "^[\\w\\-\\.]+:master-data\\-\\-Thing:[\\w\\-\\.\\:\\%]+$" },
            "kind": { "type": "string", "pattern": "^[\\w\\-\\.]+:[\\w\\-\\.]+:[\\w\\-\\.]+:[0-9]+.[0-9]+.[0-9]+$" },
            "version": { "type": "integer", "format": "int64" },
            "acl": { "$ref": "#/definitions/Acl.1.0.0" },
            "legal": { "$ref": "#/definitions/Legal.1.0.0" },
            "tags": { "type": "object", "additionalProperties": { "type": "string" } },
            "data": {
              "allOf": [
                { "$ref": "#/definitions/Common.1.0.0" },
                {
                  "type": "object",
                  "properties": {
                    "Name": { "type": "string", "minLength": 1, "maxLength": 10 },
                    "Code": { "type": "string", "pattern": "^[A-Z]{2}-[0-9]+$" },
                    "Status": { "type": "string", "enum": ["Active", "Retired"] },
                    "Depth": { "type": "number", "minimum": 0, "exclusiveMaximum": 20000, "multipleOf": 0.1 },
                    "Count": { "type": "integer", "format": "int32" },
                    "SpudDate": { "type": "string", "format": "date-time" },
                    "WellID": {
                      "type": "string",
                      "pattern": "^[\\w\\-\\.]+:master-data\\-\\-Well:[\\w\\-\\.\\:\\%]+:[0-9]*$",
                      "x-osdu-relationship": [ { "GroupType": "master-data", "EntityType": "Well" } ]
                    },
                    "Measurements": { "type": "array", "items": { "$ref": "#/definitions/Measurement.1.0.0" } },
                    "Aliases": { "type": "array", "uniqueItems": true, "maxItems": 3, "items": { "type": "string" } },
                    "Context": { "oneOf": [ { "$ref": "#/definitions/FieldContext.1.0.0" }, { "$ref": "#/definitions/BasinContext.1.0.0" } ] },
                    "Remark": { "type": ["string", "null"] },
                    "Extra": { },
                    "Datasets": {
                      "type": "array",
                      "items": {
                        "type": "string",
                        "pattern": "^[\\w\\-\\.]+:dataset\\-\\-[\\w\\-\\.]+:[\\w\\-\\.\\:\\%]+:[0-9]*$",
                        "x-osdu-relationship": [ { "GroupType": "dataset" } ]
                      }
                    },
                    "Strict": { "type": "object", "properties": { "A": { "type": "string" } }, "additionalProperties": false }
                  },
                  "required": ["Name"]
                }
              ]
            }
          },
          "required": ["kind", "acl", "legal"],
          "additionalProperties": false,
          "definitions": {
            "Acl.1.0.0": {
              "type": "object",
              "properties": {
                "owners": { "type": "array", "items": { "type": "string", "pattern": "^[a-zA-Z0-9_+&*-]+(?:\\.[a-zA-Z0-9_+&*-]+)*@(?:[a-zA-Z0-9-]+\\.)+[a-zA-Z]{2,7}$" } },
                "viewers": { "type": "array", "items": { "type": "string", "pattern": "^[a-zA-Z0-9_+&*-]+(?:\\.[a-zA-Z0-9_+&*-]+)*@(?:[a-zA-Z0-9-]+\\.)+[a-zA-Z]{2,7}$" } }
              },
              "required": ["owners", "viewers"],
              "additionalProperties": false
            },
            "Legal.1.0.0": {
              "type": "object",
              "properties": {
                "legaltags": { "type": "array", "items": { "type": "string" } },
                "otherRelevantDataCountries": { "type": "array", "items": { "type": "string", "pattern": "^[A-Z]{2}$" } },
                "status": { "type": "string", "pattern": "^(compliant|uncompliant)$" }
              },
              "required": ["legaltags", "otherRelevantDataCountries"],
              "additionalProperties": false
            },
            "Common.1.0.0": {
              "type": "object",
              "properties": {
                "Source": { "type": "string" },
                "Name": { "type": "string", "pattern": "^[^ ]+$" }
              }
            },
            "Measurement.1.0.0": {
              "type": "object",
              "properties": {
                "TypeID": {
                  "type": "string",
                  "pattern": "^[\\w\\-\\.]+:reference-data\\-\\-MeasurementType:[\\w\\-\\.\\:\\%]+:[0-9]*$",
                  "x-osdu-relationship": [ { "GroupType": "reference-data", "EntityType": "MeasurementType" } ]
                },
                "Value": { "type": "number" }
              },
              "required": ["TypeID"]
            },
            "FieldContext.1.0.0": {
              "type": "object",
              "properties": {
                "FieldID": { "type": "string", "x-osdu-relationship": [ { "GroupType": "master-data", "EntityType": "Field" } ] }
              },
              "required": ["FieldID"]
            },
            "BasinContext.1.0.0": {
              "type": "object",
              "properties": {
                "BasinID": { "type": "string", "x-osdu-relationship": [ { "GroupType": "reference-data", "EntityType": "Basin" } ] }
              },
              "required": ["BasinID"]
            }
          }
        }
        """;

    /// <summary>A record that meets every rule of <see cref="Schema"/> and refers to three records.</summary>
    public static JsonObject ValidRecord() => JsonNode.Parse("""
        {
          "id": "dev:master-data--Thing:T-1",
          "kind": "test:wks:master-data--Thing:1.0.0",
          "acl": { "owners": ["data.default.owners@dev.example.com"], "viewers": ["data.default.viewers@dev.example.com"] },
          "legal": { "legaltags": ["dev-public"], "otherRelevantDataCountries": ["NO"] },
          "tags": { "DeliveredBy": "osdu-delivery" },
          "data": {
            "Name": "Alpha",
            "Code": "AB-12",
            "Status": "Active",
            "Depth": 1234.5,
            "Count": 3,
            "SpudDate": "2026-09-01T10:15:30Z",
            "WellID": "dev:master-data--Well:W-1:",
            "Measurements": [ { "TypeID": "dev:reference-data--MeasurementType:KB:", "Value": 12.5 } ],
            "Aliases": ["a", "b"],
            "Context": { "FieldID": "dev:master-data--Field:F-1:" },
            "Remark": null,
            "Extra": { "anything": [1, "two", { "three": 3 }] }
          }
        }
        """)!.AsObject();

    /// <summary><see cref="ValidRecord"/> with <paramref name="change"/> made to it.</summary>
    public static JsonObject Record(Action<JsonObject> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var record = ValidRecord();
        change(record);
        return record;
    }

    /// <summary>The <c>data</c> block of a record.</summary>
    public static JsonObject DataOf(JsonObject record) => record["data"]!.AsObject();

    /// <summary>A schema snapshot of <paramref name="json"/> under a test kind.</summary>
    public static SchemaSnapshot SchemaOf(string json, string kind = "test:wks:master-data--Other:1.0.0")
        => SchemaSnapshot.Parse(kind, json, DateTimeOffset.UnixEpoch);
}
