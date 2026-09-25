import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { E2E, connectionParts, connectionValue, hostRun } from "../playwright.config";
import { CACHE, FixtureMeta, LOADING_FLOWS, LOOKUPS, REPO_NAME, SOURCE, folderOf } from "./global-setup";
import { adminSession, expect, test } from "./helpers";

// Seeds the estate THROUGH the product: saves the templates the sample mappings pin, imports the sample cache records as the
// cache's first version, registers the fixture git repo as a source from the Repos page in the GUI, then watches the
// control plane's managed sync pull it and the pipelines appear in the catalog. Everything after this spec runs against
// real synced data.

/** The OSDU module's folder: the sample estate and the hosts live beside the GUI. */
const moduleRoot = join(import.meta.dirname, "..", "..");

function fixtureMeta(): FixtureMeta {
  const metaPath = join(import.meta.dirname, ".fixtures", "meta.json");
  return JSON.parse(readFileSync(metaPath, "utf8")) as FixtureMeta;
}

/** The ingestion tables the chain loads, each keyed by the identity column the delivery flows page and fan out by. */
const INGESTION_TABLES = ["WellLog", "WellLogCurve", "Wellbore", "WellboreAlias", "CurveDictionary", "RecallUnits", "RecallDepthUnits"] as const;

/**
 * Runs one batch against the database a connection string names, or against master beside it. sqlcmd is used because
 * the suite already needs a local SQL Server; the password, when the connection string carries one, travels in
 * SQLCMDPASSWORD so it never appears on a command line.
 */
function sampleSql(connectionString: string, query: string, what: string, onMaster = false): void {
  const parts = connectionParts(connectionString);
  const pick = (...keys: string[]) => connectionValue(parts, ...keys);
  const server = pick("server", "data source", "address", "addr");
  const database = pick("database", "initial catalog");
  if (!server || !database) {
    throw new Error(`The connection string names no server or no database, so the suite cannot ${what}.`);
  }

  const args = ["-S", server, "-d", onMaster ? "master" : database, "-b", "-I", "-Q", query];
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
 * Creates the sample database when it is not there yet. By default it is the estate's one database, which the control
 * plane has created by the time the seed runs, and this does nothing. A sample database of its own
 * (SQLFLOW_E2E_DATA_DB) holds only the chain's source and ingestion tables, so nothing else provisions it: the control
 * plane creates its catalog and the module's database, and a pre flow creates schemas and tables but never a database.
 */
function ensureDataDatabase(connectionString: string): void {
  const database = connectionValue(connectionParts(connectionString), "database", "initial catalog");
  if (!database) {
    throw new Error("The e2e sample database connection string names no database, so the suite cannot create it.");
  }

  // EXECUTE takes literals and variables and no function call, so the name goes through a variable and sp_executesql.
  const quoted = database.replace(/'/g, "''");
  sampleSql(
    connectionString,
    `DECLARE @name sysname = N'${quoted}';
IF DB_ID(@name) IS NULL
BEGIN
  DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name);
  EXEC sp_executesql @sql;
END`,
    "create the sample database",
    true,
  );
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
  // Templates live in the module's database, not the repository, so the ones the sample mappings pin are saved first:
  // every plan the later specs run renders against them.
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
        readFileSync(join(moduleRoot, "samples", "templates", template.file), "utf8"),
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

  // A cache lives in the module database, never in the repository. A refresh would search the sample's OSDU target, so
  // the suite imports the sample cache records as the cache's first version through the OSDU Delivery CLI host, the
  // offline path an operator uses. Importing the same files again writes nothing, so a rerun keeps one version.
  test("import the sample cache records as the first version of the cache", () => {
    test.setTimeout(420_000);
    const meta = fixtureMeta();
    const output = execFileSync(
      "dotnet",
      [
        ...hostRun(join(moduleRoot, "hosts", "SqlFlow.Delivery.Cli.Host")), "--",
        "cache", "import", `${meta.sourceDir}/cache/${CACHE}.yaml`,
        // The records are not repository content: a cache lives in the module's database, so the sample records that
        // stand in for a capture sit beside the estate rather than inside the source that reads the cache.
        "--from-dir", join(moduleRoot, "samples", "cache-records"),
        "--db", "${env:SQLFLOW_E2E_CACHE_DB}",
        "--json",
      ],
      // The cache belongs to the partition the cache flow names, resolved as every other process of the estate resolves it.
      { encoding: "utf8", timeout: 400_000, env: { ...process.env, ...E2E.osdu, SQLFLOW_E2E_CACHE_DB: E2E.catalogDb, SQLFLOW_OSDU_DB: E2E.osduDb } },
    );
    expect(output).toContain("wells-osdu-00-reference-cache");
  });

  test("register the fixture repo as a source and watch it sync", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-repos").click();
    await expect(adminPage.getByTestId("page-repos")).toBeVisible();

    const meta = fixtureMeta();
    await adminPage.getByTestId("open-register-source").click();
    await adminPage.getByTestId("source-name").fill(REPO_NAME);
    await adminPage.getByTestId("source-remote-url").fill(meta.repoDir);
    await adminPage.getByTestId("register-source-submit").click();

    // The row appears; force an immediate pull and wait for the FIXTURE's head commit specifically, so a sha
    // left over from a previous suite run can never satisfy this.
    const row = adminPage.getByTestId("table-row").filter({ hasText: REPO_NAME }).first();
    await expect(row).toBeVisible({ timeout: 15_000 });
    await row.getByTestId("source-sync-now").click();
    await expect(row.getByText(meta.headSha.slice(0, 10)).first()).toBeVisible({ timeout: 120_000 });
  });

  // Runs AFTER the source is registered and synced, because each run records itself into that repo: a run that
  // recorded first would create the repo itself, as a manual one, and the registration would find the name taken.
  //
  // A delivery flow reads its records from ingestion tables, which the chain that fills them creates: the pre flows land
  // the sample files and the ingestion flows key them into the tables the delivery flows plan from. Running them through
  // the CLI host is the path a node takes, so what the later specs plan against is what production would hold. Landing
  // before keying is the order the waves run in, and running the chain again loads nothing new.
  //
  // Each run is given the catalog and the repo the fixture is registered under, so it records itself the way a run of
  // this estate does (`--db`, and `--repo` in place of the folder-name fallback, which would invent a repo of its own).
  // Without that the ingestion tables filled but no run existed: a record's chain could name no pre-ingestion or
  // ingestion run, and the stages of the only estate that exercises them were never recorded at all.
  test("load the sample ingestion tables by running the chain", () => {
    test.setTimeout(900_000);
    const meta = fixtureMeta();
    ensureDataDatabase(meta.dataDb);
    allowSnapshotIsolation(meta.dataDb);
    replaceTablesWithoutIdentityKey(meta.dataDb);
    for (const flow of LOADING_FLOWS) {
      const output = execFileSync(
        "dotnet",
        [
          ...hostRun(join(moduleRoot, "hosts", "SqlFlow.Delivery.Cli.Host")), "--",
          "run", `${meta.sourceDir}/${folderOf(flow)}/${flow}.yaml`,
          "--db", "${env:SQLFLOW_E2E_CATALOG_CONNECTION}", "--repo", REPO_NAME,
        ],
        {
          encoding: "utf8",
          timeout: 600_000,
          env: {
            ...process.env,
            ...E2E.osdu,
            OSDU_DATA_DB: meta.dataDb,
            SQLFLOW_OSDU_DB: meta.osduDb,
            SQLFLOW_E2E_CATALOG_CONNECTION: E2E.catalogDb,
          },
        },
      );
      expect(output, `${flow} reported nothing`).not.toBe("");
    }
  });

  // The lookup tables the mappings translate source spellings through: the unit dictionary of the repository, and the
  // curve dictionary the chain has just keyed into its ingestion table. Neither reaches OSDU, so the lookups cache flow is
  // refreshed for real through the CLI host, as a node runs it, and writes the next version of the partition's cache.
  test("refresh the lookup tables from the dictionary and the curve dictionary", async ({ request }) => {
    test.setTimeout(420_000);
    const meta = fixtureMeta();
    const output = execFileSync(
      "dotnet",
      [
        ...hostRun(join(moduleRoot, "hosts", "SqlFlow.Delivery.Cli.Host")), "--",
        "run", `${meta.sourceDir}/cache/${LOOKUPS}.yaml`,
        "--db", "${env:SQLFLOW_E2E_CATALOG_CONNECTION}", "--repo", REPO_NAME,
      ],
      {
        encoding: "utf8",
        timeout: 400_000,
        env: {
          ...process.env,
          ...E2E.osdu,
          OSDU_DATA_DB: meta.dataDb,
          SQLFLOW_OSDU_DB: meta.osduDb,
          SQLFLOW_E2E_CATALOG_CONNECTION: E2E.catalogDb,
        },
      },
    );
    expect(output, `${LOOKUPS} reported nothing`).not.toBe("");

    // The partition's cache now holds the lookup tables beside the imported reference data, each kept under its key.
    const session = await adminSession(request);
    const response = await request.get(`${E2E.apiBaseUrl}/api/v1/delivery/mapping-builder/caches`, {
      headers: { Authorization: `Bearer ${session.token}` },
    });
    expect(response.status(), await response.text()).toBe(200);
    const caches = (await response.json()) as { scope: string; flows: string[]; types: { name: string; key: string | null }[] }[];
    const cache = caches.find((candidate) => candidate.flows.includes(LOOKUPS));
    expect(cache, `no cache is filled by ${LOOKUPS}`).toBeDefined();
    expect(cache?.types.find((type) => type.name === "RecallUnits")?.key).toBe("source_unit");
    expect(cache?.types.find((type) => type.name === "RecallDepthUnits")?.key).toBe("source_unit");
    expect(cache?.types.find((type) => type.name === "CurveDictionary")?.key).toBe("mnemonic");
  });

  test("the synced pipeline appears in the catalog", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-pipelines").click();
    await expect(adminPage.getByTestId("page-pipelines")).toBeVisible();
    // The page lists a repo's flows as the folder tree they are; a search expands it onto the matching rows.
    await adminPage.getByTestId("filter-name").fill("wells-welllog-03-header-delivery");
    const row = adminPage.getByTestId("repo-pipeline").filter({ hasText: "wells-welllog-03-header-delivery" });
    await expect(row.first()).toBeVisible({ timeout: 60_000 });
  });

  test("the synced repo appears on the repos page with its pipelines", async ({ adminPage }) => {
    await adminPage.getByTestId("nav-repos").click();
    await expect(adminPage.getByTestId("page-repos")).toBeVisible();
    const row = adminPage.getByTestId("table-row").filter({ hasText: REPO_NAME });
    await expect(row.first()).toBeVisible({ timeout: 30_000 });
    await row.first().click();
    await expect(adminPage.getByTestId("page-repo-detail")).toBeVisible();
    // The repository is laid out per source, and a project is a top-level folder, so the estate is one project named
    // after the source rather than one per kind of file it holds.
    const projects = adminPage.getByTestId("repo-project");
    await expect(projects).toHaveCount(1, { timeout: 30_000 });
    await expect(projects.first()).toContainText(SOURCE);
    // The project accordions start collapsed; a search opens the matching one and surfaces the flow row.
    await adminPage.getByTestId("repo-pipeline-search").fill("wells-welllog-03-header-delivery");
    await expect(adminPage.getByTestId("repo-pipeline").filter({ hasText: "wells-welllog-03-header-delivery" }).first()).toBeVisible();
  });
});
