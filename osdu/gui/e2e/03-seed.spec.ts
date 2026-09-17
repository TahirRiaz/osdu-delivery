import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { E2E, hostRun } from "../playwright.config";
import { connectionParts, connectionValue, LOADING_FLOWS } from "./global-setup";
import { adminSession, expect, test } from "./helpers";

// Seeds the estate THROUGH the product: saves the templates the sample mappings pin, imports the sample references as the
// cache's first version, registers the fixture git repo as a source from the Repos page in the GUI, then watches the
// control plane's managed sync pull it and the pipelines appear in the catalog. Everything after this spec runs against
// real synced data.

/** The OSDU module's folder: the sample estate and the hosts live beside the GUI. */
const moduleRoot = join(import.meta.dirname, "..", "..");

function fixtureMeta(): { repoDir: string; headSha: string; sampleDb: string } {
  const metaPath = join(import.meta.dirname, ".fixtures", "meta.json");
  return JSON.parse(readFileSync(metaPath, "utf8")) as { repoDir: string; headSha: string; sampleDb: string };
}

/** The ingestion tables the chain loads, each keyed by the identity column the delivery flows page and fan out by. */
const INGESTION_TABLES = ["WellLog", "WellLogCurve", "Wellbore", "WellboreAlias"] as const;

/**
 * Runs one batch against the sample database. sqlcmd is used because the suite already needs a local SQL Server; the
 * password, when the connection string carries one, travels in SQLCMDPASSWORD so it never appears on a command line.
 */
function sampleSql(connectionString: string, query: string, what: string): void {
  const parts = connectionParts(connectionString);
  const pick = (...keys: string[]) => connectionValue(parts, ...keys);
  const server = pick("server", "data source", "address", "addr");
  const database = pick("database", "initial catalog");
  if (!server || !database) {
    throw new Error(`The sample database connection string names no server or no database, so the suite cannot ${what}.`);
  }

  const args = ["-S", server, "-d", database, "-b", "-I", "-Q", query];
  const env: NodeJS.ProcessEnv = { ...process.env };
  const user = pick("user id", "uid", "user");
  if (user) {
    args.push("-U", user);
    env.SQLCMDPASSWORD = pick("password", "pwd") ?? "";
  } else {
    args.push("-E");
  }

  if (/^(true|yes)$/i.test(pick("trustservercertificate") ?? "")) {
    args.push("-C");
  }

  try {
    execFileSync("sqlcmd", args, { encoding: "utf8", stdio: "pipe", timeout: 60_000, env });
  } catch (error) {
    const failure = error as { stdout?: string; stderr?: string; message: string };
    throw new Error(
      `Could not ${what} on ${database} at ${server}: ${(failure.stdout || failure.stderr || failure.message).trim()}`,
      { cause: error },
    );
  }
}

/**
 * Lets the sample database serve snapshot reads. A delivery flow reads a record and its child rows as one moment, which
 * is snapshot isolation unless the flow says otherwise, and a database the suite has just created does not allow it. The
 * setting is idempotent, so a rerun against the same database changes nothing.
 */
function allowSnapshotIsolation(connectionString: string): void {
  sampleSql(connectionString, "ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;", "allow snapshot isolation");
}

/**
 * Drops the chain's ingestion tables that an earlier suite created without the identity key the delivery flows page by.
 * The ingestion flows create a table with its identity key only when the table is new, and they probe their watermark
 * from the table itself, so a dropped table is created again with the key and loaded in full on this run.
 */
function replaceTablesWithoutIdentityKey(connectionString: string): void {
  const names = INGESTION_TABLES.map((table) => `N'${table}'`).join(", ");
  sampleSql(
    connectionString,
    `DECLARE @sql nvarchar(max) = N'';
SELECT @sql = @sql + N'DROP TABLE ' + QUOTENAME(s.[name]) + N'.' + QUOTENAME(t.[name]) + N';'
FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.[schema_id] = t.[schema_id]
WHERE s.[name] = N'ing' AND t.[name] IN (${names})
  AND NOT EXISTS (SELECT 1 FROM sys.identity_columns AS c WHERE c.[object_id] = t.[object_id] AND c.[name] = N'RecId');
IF @sql <> N'' EXEC sp_executesql @sql;`,
    "replace the ingestion tables that lack their identity key",
  );
}

test.describe.serial("seed the estate via repo source sync", () => {
  // Templates live in the catalog, not the repository, so the ones the sample mappings pin are saved first: every plan
  // the later specs run renders against them.
  test("save the templates the sample mappings pin", async ({ request }) => {
    const session = await adminSession(request);
    const templates = [
      { kind: "osdu:wks:work-product-component--WellLog:1.4.0", file: "osdu_wks_work-product-component--WellLog_1.4.0.json", version: "26a3c3441882db4f" },
      { kind: "osdu:wks:master-data--Wellbore:1.3.0", file: "osdu_wks_master-data--Wellbore_1.3.0.json", version: "58d6bdbd9d066a06" },
      { kind: "osdu:wks:work-product-component--Document:1.0.0", file: "osdu_wks_work-product-component--Document_1.0.0.json", version: "5c6898ebc6775f6e" },
      { kind: "osdu:wks:work-product-component--WellboreTrajectory:1.3.0", file: "osdu_wks_work-product-component--WellboreTrajectory_1.3.0.json", version: "bfbc5973bbdeb7ec" },
    ];
    for (const template of templates) {
      const schema: unknown = JSON.parse(
        readFileSync(join(moduleRoot, "samples", "recall-welllog", "templates", template.file), "utf8"),
      );
      const response = await request.post(`${E2E.apiBaseUrl}/api/v1/delivery/templates`, {
        headers: { Authorization: `Bearer ${session.token}` },
        data: { kind: template.kind, schema, origin: `file ${template.file}` },
      });
      expect(response.status(), await response.text()).toBe(200);
      const saved = (await response.json()) as { template: { kind: string; version: string }; outcome: string };
      expect(saved.template.version).toBe(template.version);
      expect(["created", "unchanged"]).toContain(saved.outcome);
    }
  });

  // Cache versions live in the catalog too. A refresh would search the sample's OSDU target, so the suite imports the
  // sample reference files as the cache's first version through the OSDU Delivery CLI host, the offline path an operator
  // uses. Importing the same files again writes nothing, so a rerun against the same catalog keeps one version.
  test("import the sample references as the first version of the cache", () => {
    test.setTimeout(420_000);
    const meta = fixtureMeta();
    const output = execFileSync(
      "dotnet",
      [
        ...hostRun(join(moduleRoot, "hosts", "SqlFlow.Delivery.Cli.Host")), "--",
        "cache", "import", `${meta.repoDir}/caches/osdu-reference-cache.yaml`,
        "--from-dir", `${meta.repoDir}/references`,
        "--db", "${env:SQLFLOW_E2E_CACHE_DB}",
        "--json",
      ],
      { encoding: "utf8", timeout: 400_000, env: { ...process.env, SQLFLOW_E2E_CACHE_DB: E2E.catalogDb } },
    );
    expect(output).toContain("osdu-reference-cache");
  });

  // A delivery flow reads its records from ingestion tables, which the chain that fills them creates: the pre flows land
  // the sample files and the ingestion flows key them into the tables the delivery flows plan from. Running them through
  // the CLI host is the path a node takes, so what the later specs plan against is what production would hold. Landing
  // before keying is the order the waves run in, and running the chain again loads nothing new.
  test("load the sample ingestion tables by running the chain", () => {
    test.setTimeout(900_000);
    const meta = fixtureMeta();
    allowSnapshotIsolation(meta.sampleDb);
    replaceTablesWithoutIdentityKey(meta.sampleDb);
    for (const flow of LOADING_FLOWS) {
      const output = execFileSync(
        "dotnet",
        [...hostRun(join(moduleRoot, "hosts", "SqlFlow.Delivery.Cli.Host")), "--", "run", `${meta.repoDir}/flows/${flow}.yaml`],
        { encoding: "utf8", timeout: 600_000, env: { ...process.env, OSDU_SAMPLE_DB: meta.sampleDb } },
      );
      expect(output, `${flow} reported nothing`).not.toBe("");
    }
  });

  test("register the fixture repo as a source and watch it sync", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-repos").click();
    await expect(adminPage.getByTestId("page-repos")).toBeVisible();

    const meta = fixtureMeta();
    await adminPage.getByTestId("open-register-source").click();
    await adminPage.getByTestId("source-name").fill("e2e-repo");
    await adminPage.getByTestId("source-remote-url").fill(meta.repoDir);
    await adminPage.getByTestId("register-source-submit").click();

    // The row appears; force an immediate pull and wait for the FIXTURE's head commit specifically, so a sha
    // left over from a previous suite run can never satisfy this.
    const row = adminPage.getByTestId("table-row").filter({ hasText: "e2e-repo" }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });
    await row.getByTestId("source-sync-now").click();
    await expect(row.getByText(meta.headSha.slice(0, 10)).first()).toBeVisible({ timeout: 120_000 });
  });

  test("the synced pipeline appears in the catalog", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    await expect(adminPage.getByTestId("page-pipelines")).toBeVisible();
    await adminPage.getByTestId("filter-name").fill("recall-welllog");
    const row = adminPage.getByTestId("table-row").filter({ hasText: "recall-welllog" });
    await expect(row.first()).toBeVisible({ timeout: 60_000 });
  });

  test("the synced repo appears on the repos page with its pipelines", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-repos").click();
    await expect(adminPage.getByTestId("page-repos")).toBeVisible();
    const row = adminPage.getByTestId("table-row").filter({ hasText: "e2e-repo" });
    await expect(row.first()).toBeVisible({ timeout: 30_000 });
    await row.first().click();
    await expect(adminPage.getByTestId("page-repo-detail")).toBeVisible();
    // The project accordions start collapsed; a search opens the matching one and surfaces the flow row.
    await adminPage.getByTestId("repo-pipeline-search").fill("recall-welllog");
    await expect(adminPage.getByTestId("table-row").filter({ hasText: "recall-welllog" }).first()).toBeVisible();
  });
});
