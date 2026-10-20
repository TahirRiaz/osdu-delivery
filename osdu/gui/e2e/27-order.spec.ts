import { E2E } from "../playwright.config";
import { adminSession, expect, test } from "./helpers";

// The Order page (osdu/docs/reference/concepts/osdu-order.md) over the estate the seed synced, whose flows join no
// schedule: the spec makes two schedules of its own through the API, disabled so neither ever fires, one running the
// wellbore delivery and one the well log delivery, whose records refer to wellbores. The Schedules tab draws an arrow
// from the first to the second and names the delivery behind it, and says the first does not fire. The Partition tab
// lists the partition's entity types level by level, the wellbores before the logs that refer to them. Nothing here
// reaches an OSDU; the schedules are deleted when the spec ends.

const WELLBORES = "wells-wellbore-03-header-delivery";
const LOGS = "welldb-welllog-03-header-delivery";

test.describe("Order", () => {
  test("shows which schedules run best before which, and why", async ({ adminPage, request }) => {
    const session = await adminSession(request);
    const headers = { Authorization: `Bearer ${session.token}` };
    const listed = await request.get(`${E2E.apiBaseUrl}/api/v1/pipelines?name=${WELLBORES}&pageSize=10`, { headers });
    expect(listed.ok(), await listed.text()).toBe(true);
    const pipeline = ((await listed.json()) as { items: { name: string; repoId: string }[] }).items.find((p) => p.name === WELLBORES);
    expect(pipeline, `the seed syncs ${WELLBORES}`).toBeDefined();

    const created: string[] = [];
    try {
      for (const [name, member] of [["e2e-order-wellbores", WELLBORES], ["e2e-order-logs", LOGS]]) {
        const response = await request.post(`${E2E.apiBaseUrl}/api/v1/schedules`, {
          headers,
          data: { repoId: pipeline!.repoId, members: [member], cron: "0 3 * * *", timezone: "UTC", enabled: false, name },
        });
        expect(response.ok(), await response.text()).toBe(true);
        created.push(((await response.json()) as { id: string }).id);
      }

      await adminPage.goto("/delivery/order");
      await adminPage.getByTestId("order-tab-schedules").click();
      const graph = adminPage.getByTestId("order-schedules-graph");
      await expect(graph).toBeVisible();
      await expect(adminPage.getByTestId("order-schedules-facts")).toContainText("schedules running deliveries");

      await graph.getByText("e2e-order-logs", { exact: true }).click();
      const detail = adminPage.getByTestId("order-schedule-detail");
      await expect(detail).toBeVisible();
      await expect(detail.getByTestId("order-first-e2e-order-wellbores")).toBeVisible();
      await expect(detail.getByTestId("order-first-e2e-order-wellbores-timing")).toHaveText("Not firing");
      const reasons = detail.getByTestId("order-first-e2e-order-wellbores-reasons");
      await expect(reasons).toContainText(LOGS);
      await expect(reasons).toContainText("master-data--Wellbore");
      await expect(reasons).toContainText(WELLBORES);
    } finally {
      for (const id of created) {
        const removed = await request.delete(`${E2E.apiBaseUrl}/api/v1/schedules/${id}`, { headers });
        expect(removed.ok(), await removed.text()).toBe(true);
      }
    }
  });

  test("lists a partition's entity types level by level, each after what it refers to", async ({ adminPage }) => {
    await adminPage.goto("/delivery/order");
    await adminPage.getByTestId("order-tab-partition").click();
    const levels = adminPage.getByTestId("order-partition-levels");
    await expect(levels).toBeVisible();
    const wellbore = levels.getByTestId("order-type-master-data--Wellbore");
    const log = levels.getByTestId("order-type-work-product-component--WellLog");
    await expect(wellbore).toBeVisible();
    await expect(log).toContainText("waits for");
    await expect(log).toContainText("Wellbore");

    // The logs sit at a later level than the wellbores they refer to.
    const levelOf = async (testId: string) => {
      const items = levels.locator("li[data-testid^='order-level-']");
      const count = await items.count();
      for (let index = 0; index < count; index++) {
        if (await items.nth(index).getByTestId(testId).count() > 0) {
          return index;
        }
      }

      return -1;
    };
    expect(await levelOf("order-type-work-product-component--WellLog")).toBeGreaterThan(await levelOf("order-type-master-data--Wellbore"));
  });

  test("offers to build a release of OSDU's lineage when none is built", async ({ adminPage }) => {
    await adminPage.goto("/delivery/order");
    await adminPage.getByTestId("order-tab-releases").click();
    const empty = adminPage.getByTestId("order-releases-empty");
    const tools = adminPage.getByTestId("order-release-tools");
    await expect(empty.or(tools)).toBeVisible();
    await expect(adminPage.getByTestId("order-build")).toBeVisible();
  });
});
