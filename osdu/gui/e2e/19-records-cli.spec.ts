import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { E2E, hostRun } from "../playwright.config";
import { FixtureMeta, REPO_NAME } from "./global-setup";
import { expect, test } from "./helpers";

// A record's history where an operator already is. The GUI and the API have shown a record's attempts from the start;
// `sqlflow records` shows the same ledger from a terminal or a node, with no control plane to reach. The plan run of
// the earlier specs is what put these records in the ledger.

/** The OSDU module's folder: the sample estate and the hosts live beside the GUI. */
const moduleRoot = join(import.meta.dirname, "..", "..");

function fixtureMeta(): FixtureMeta {
  return JSON.parse(readFileSync(join(import.meta.dirname, ".fixtures", "meta.json"), "utf8")) as FixtureMeta;
}

/**
 * Runs the module's CLI host against the e2e catalog, returning stdout. The catalog reaches it as a reference, the way
 * a deployment's does, so a connection string never lands in an argument list or a failure message.
 */
function cli(...args: string[]): string {
  return execFileSync(
    "dotnet",
    [...hostRun(join(moduleRoot, "hosts", "SqlFlow.Delivery.Cli.Host")), "--", ...args, "--db", "${env:SQLFLOW_E2E_CATALOG_CONNECTION}"],
    {
      encoding: "utf8",
      timeout: 300_000,
      env: {
        ...process.env,
        OSDU_SAMPLE_DB: fixtureMeta().sampleDb,
        SQLFLOW_E2E_CATALOG_CONNECTION: E2E.catalogDb,
        // The ledger these commands read is the module's database, which is not the catalog's: a node reaches it
        // through exactly this reference, and so does a command run beside one.
        SQLFLOW_OSDU_DB: E2E.osduDb,
        // The flow's target, as references it resolves but never uses: an intake reaches no OSDU (the fixture turns
        // the legal check off), and an address nothing listens on is what a run that tried to send would meet.
        OSDU_URL: "http://127.0.0.1:9",
        OSDU_TOKEN_URL: "http://127.0.0.1:9/token",
        OSDU_CLIENT_ID: "the-e2e-authenticates-with-nothing",
        OSDU_CLIENT_SECRET: "the-e2e-authenticates-with-nothing",
        OSDU_SCOPE: "the-e2e-authenticates-with-nothing",
        APIM_KEY: "the-e2e-authenticates-with-nothing",
      },
    },
  );
}

/**
 * The JSON document in what the CLI printed. `dotnet run` builds the host on its way and prints what that says first,
 * so the document is taken from its first brace rather than from the first byte of the output.
 */
function json<T>(output: string): T {
  const start = output.indexOf("{");
  if (start < 0) {
    throw new Error(`no JSON in the CLI's output: ${output.slice(0, 400)}`);
  }

  return JSON.parse(output.slice(start, output.lastIndexOf("}") + 1)) as T;
}

/** What the CLI said when it refused: the command must fail, and its reason is what the test is about. */
function cliRefusal(...args: string[]): string {
  try {
    cli(...args);
  } catch (error) {
    const failure = error as { stderr?: string; stdout?: string; message?: string };
    return `${failure.stderr ?? ""}${failure.stdout ?? ""}${failure.message ?? ""}`;
  }

  throw new Error(`'records ${args.join(" ")}' was expected to fail and did not.`);
}

test.describe.serial("records from the CLI", () => {
  test("the ledger's records and one record's attempts are readable from the command line", () => {
    test.setTimeout(600_000);
    const flow = `${fixtureMeta().sourceDir}/flows/wells-welllog-03-header-delivery.yaml`;

    // Records reach the ledger when a submission is planned, which is what an intake does: it renders and stages every
    // record of the scope and sends nothing. A plan run reports what it would do and stages nothing, so the earlier
    // specs' plan leaves the ledger empty by design.
    //
    // The run records itself into the repo the fixture is registered under, like every other run of this estate.
    // Without --repo it would name the repo after the folder it was started from, leaving a second repo called
    // "flows" in the catalog holding a copy of this flow.
    expect(cli("run", flow, "--operation", "intake", "--set", "logSource=STAT_COMP", "--repo", REPO_NAME)).toContain("record(s)");

    const listed = json<{
      flow: string;
      flowId: string;
      records: { deliveryKey: string; sourceKey: string; status: string; targetId: string | null }[];
    }>(cli("records", "list", flow, "--json"));
    expect(listed.flow).toBe("wells-welllog-03-header-delivery");
    expect(listed.records.length).toBeGreaterThan(0);
    const first = listed.records[0];
    expect(first.deliveryKey).toMatch(/^[0-9a-f]{32}$/);
    // A planned record already claims the OSDU id it will land as: the id is given at plan time, not at delivery.
    expect(first.targetId).toContain("work-product-component--WellLog");

    // The source key is what an operator holds, so it finds the record as surely as the delivery key does.
    const shown = json<{
      record: { deliveryKey: string; sourceKey: string; status: string; attempts: unknown[] };
    }>(cli("records", "show", flow, "--key", first.sourceKey, "--json"));
    expect(shown.record.deliveryKey).toBe(first.deliveryKey);
    expect(shown.record.sourceKey).toBe(first.sourceKey);
    expect(Array.isArray(shown.record.attempts)).toBe(true);

    // The plain listing is a person's view of the same thing, and a status the ledger does not know is refused.
    expect(cli("records", "list", flow, "--status", "pending")).toContain(first.sourceKey);
    expect(cliRefusal("records", "list", flow, "--status", "nonsense")).toMatch(/not a record status/);
  });

  test("a source is read one interface at a time, and an unknown interface says which there are", () => {
    const flow = `${fixtureMeta().sourceDir}/flows/wells-source-03-interfaces-delivery.yaml`;

    // A source delivers several interfaces, so a command that names none cannot tell which ledger it means.
    expect(cliRefusal("records", "list", flow)).toMatch(/delivers 4 interfaces/);
    expect(cliRefusal("records", "list", flow, "--interface", "nope")).toMatch(/has no interface 'nope'/);

    const listed = json<{ flow: string; records: unknown[] }>(cli("records", "list", flow, "--interface", "wellbores", "--json"));
    expect(listed.flow).toBe("wells-source-03-interfaces-delivery / wellbores");
    // The source's own ledgers are its own: nothing is read from the single-form flows beside it.
    expect(listed.records).toHaveLength(0);
  });
});
