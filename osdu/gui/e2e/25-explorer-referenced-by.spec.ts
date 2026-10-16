import { E2E } from "../playwright.config";
import { expect, test } from "./helpers";

// The explorer's Referenced by: the types whose schemas have a property naming records of a type, read from what the
// partition's Schema service lists, with the versions that do and those that do not, and where.
//
// Nothing here reaches an OSDU: the stand-in holds, for as long as the spec runs, two versions of the wellbore's schema
// (the newer naming a unit of measure through an abstract schema it refers to by id, the older not), a well log's naming
// a unit of measure from its curves and its wellbore, and the unit of measure's own, which names nothing. The platform's
// wellbores the stand-in holds are of the newer wellbore kind, so that is the one of them in use. Nothing is delivered
// and nothing is written.

const UNIT = "reference-data--UnitOfMeasure";
const WELLBORE_10 = "osdu:wks:master-data--Wellbore:1.0.0";
const WELLBORE_11 = "osdu:wks:master-data--Wellbore:1.1.0";
const WELL_LOG = "osdu:wks:work-product-component--WellLog:1.4.0";
const MEASUREMENT = "osdu:wks:AbstractE2eVerticalMeasurement:1.0.0";

/** A reference as OSDU writes one: the id pattern, and the relationship it declares. */
function reference(group: string, entity: string) {
  return {
    type: "string",
    pattern: `^[\\w\\-\\.]+:${group}\\-\\-${entity}:[\\w\\-\\.\\:\\%]+[0-9]*$`,
    "x-osdu-relationship": [{ GroupType: group, EntityType: entity }],
  };
}

/** A kind's schema: its data's properties, made of the schemas given by reference and its own. */
function kindSchema(kind: string, data: Record<string, unknown>, refs: string[] = []) {
  const own = { type: "object", properties: data };
  return {
    $schema: "http://json-schema.org/draft-07/schema#",
    "x-osdu-schema-source": kind,
    type: "object",
    properties: {
      id: { type: "string" },
      kind: { type: "string" },
      data: refs.length === 0 ? own : { allOf: [...refs.map((ref) => ({ $ref: ref })), own] },
    },
  };
}

const SCHEMAS: [string, object][] = [
  [MEASUREMENT, {
    $schema: "http://json-schema.org/draft-07/schema#",
    type: "object",
    properties: {
      VerticalMeasurements: {
        type: "array",
        items: { type: "object", properties: { VerticalMeasurementUnitOfMeasureID: reference("reference-data", "UnitOfMeasure") } },
      },
    },
  }],
  [WELLBORE_10, kindSchema(WELLBORE_10, { FacilityName: { type: "string" }, WellID: reference("master-data", "Well") })],
  [WELLBORE_11, kindSchema(WELLBORE_11, { FacilityName: { type: "string" }, WellID: reference("master-data", "Well") }, [MEASUREMENT])],
  [WELL_LOG, kindSchema(WELL_LOG, {
    WellboreID: reference("master-data", "Wellbore"),
    Curves: { type: "array", items: { type: "object", properties: { CurveUnit: reference("reference-data", "UnitOfMeasure") } } },
  })],
  ["osdu:wks:reference-data--UnitOfMeasure:1.0.0", kindSchema("osdu:wks:reference-data--UnitOfMeasure:1.0.0", { Code: { type: "string" } })],
];

test.describe.serial("explorer referenced by", () => {
  test.beforeAll(async ({ playwright }) => {
    const standIn = await playwright.request.newContext();
    try {
      for (const [id, schema] of SCHEMAS) {
        const held = await standIn.put(`${E2E.osdu.OSDU_URL}/__e2e/schemas`, { data: { id, schema } });
        expect(held.ok()).toBe(true);
      }
    } finally {
      await standIn.dispose();
    }
  });

  test.afterAll(async ({ playwright }) => {
    // The stand-in forgets what this spec gave it, so a later spec meets the platform as the suite starts it.
    const standIn = await playwright.request.newContext();
    try {
      await standIn.delete(`${E2E.osdu.OSDU_URL}/__e2e/schemas`);
    } finally {
      await standIn.dispose();
    }
  });

  test("lists the types naming a type, the versions that do and those that do not, and opens one", async ({ adminPage }) => {
    await adminPage.goto(`/delivery/explorer?kind=${encodeURIComponent(`*:*:${UNIT}:*`)}`);
    await adminPage.getByTestId("explorer-referenced-by").click();
    const dialog = adminPage.getByTestId("explorer-references");
    await expect(dialog).toContainText("Types that refer to UnitOfMeasure");

    // The partition's schemas are read once and kept by the control plane; reading them again here makes the answer this
    // spec's schemas whatever an earlier run of the same host read.
    const again = dialog.getByTestId("explorer-references-refresh");
    await expect(again).toBeEnabled({ timeout: 60_000 });
    await again.click();
    const types = dialog.getByTestId("explorer-references-types").getByTestId("explorer-references-type");
    await expect(types).toHaveCount(2, { timeout: 60_000 });
    await expect(dialog.getByTestId("explorer-references-summary")).toContainText("2 types in 2 versions");

    // The wellbore's newer version names it, through the abstract schema it is made of; the older does not.
    const wellbore = types.filter({ has: adminPage.getByTestId("explorer-references-type-open").filter({ hasText: /^Wellbore$/ }) });
    await expect(wellbore.getByTestId("explorer-references-version")).toHaveText(["1.0.0", "1.1.0"]);
    await expect(wellbore.getByTestId("explorer-references-version").first()).toHaveAttribute("data-names", "false");
    await expect(wellbore.getByTestId("explorer-references-version").last()).toHaveAttribute("data-names", "true");
    await expect(wellbore.getByTestId("explorer-references-place")).toHaveText(["data.VerticalMeasurements[].VerticalMeasurementUnitOfMeasureID"]);

    const log = types.filter({ has: adminPage.getByTestId("explorer-references-type-open").filter({ hasText: /^WellLog$/ }) });
    await expect(log.getByTestId("explorer-references-place")).toHaveText(["data.Curves[].CurveUnit"]);

    // A filter finds a type by a property path.
    await dialog.getByTestId("explorer-references-filter").fill("curve");
    await expect(types).toHaveCount(1);
    await expect(types.first()).toContainText("WellLog");
    await dialog.getByTestId("explorer-references-filter").fill("");
    await expect(types).toHaveCount(2);

    // Where it is used: the platform holds wellbores of the newer kind only, and no well log.
    await dialog.getByTestId("explorer-references-in-use").click();
    await expect(types).toHaveCount(1);
    await expect(types.first().getByTestId("explorer-references-version")).toHaveText(["1.1.0"]);
    await dialog.getByTestId("explorer-references-in-use").click();

    // A version opens the records of that kind, and the dialog closes.
    await log.getByTestId("explorer-references-version").filter({ hasText: "1.4.0" }).click();
    await expect(dialog).toHaveCount(0);
    await expect(adminPage).toHaveURL(new RegExp(`kind=${encodeURIComponent(WELL_LOG).replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`));
  });
});
