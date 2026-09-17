import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { E2E, hostRun } from "../playwright.config";
import { expect, test } from "./helpers";

// A record's history where an operator already is. The GUI and the API have shown a record's attempts from the start;
// `sqlflow records` shows the same ledger from a terminal or a node, with no control plane to reach. The plan run of
// the earlier specs is what put these records in the ledger.

/** The OSDU module's folder: the sample estate and the hosts live beside the GUI. */
const moduleRoot = join(import.meta.dirname, "..", "..");

function fixtureMeta(): { repoDir: string; headSha: string; sampleDb: string } {
  return JSON.parse(readFileSync(join(import.meta.dirname, ".fixtures", "meta.json"), "utf8")) as {
    repoDir: string; headSha: string; sampleDb: string;
  };
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
      env: { ...process.env, OSDU_SAMPLE_DB: fixtureMeta().sampleDb, SQLFLOW_E2E_CATALOG_CONNECTION: E2E.catalogDb },
    },
  );
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
    const flow = `${fixtureMeta().repoDir}/flows/recall-welllog.yaml`;

    const listed = JSON.parse(cli("records", "list", flow, "--json")) as {
      flow: string;
      flowId: string;
      records: { deliveryKey: string; sourceKey: string; status: string; targetId: string | null }[];
    };
    expect(listed.flow).toBe("recall-welllog");
    expect(listed.records.length).toBeGreaterThan(0);
    const first = listed.records[0];
    expect(first.deliveryKey).toMatch(/^[0-9a-f]{32}$/);
    // A planned record already claims the OSDU id it will land as: the id is given at plan time, not at delivery.
    expect(first.targetId).toContain("work-product-component--WellLog");

    // The source key is what an operator holds, so it finds the record as surely as the delivery key does.
    const shown = JSON.parse(cli("records", "show", flow, "--key", first.sourceKey, "--json")) as {
      record: { deliveryKey: string; sourceKey: string; status: string; attempts: unknown[] };
    };
    expect(shown.record.deliveryKey).toBe(first.deliveryKey);
    expect(shown.record.sourceKey).toBe(first.sourceKey);
    expect(Array.isArray(shown.record.attempts)).toBe(true);

    // The plain listing is a person's view of the same thing, and a status the ledger does not know is refused.
    expect(cli("records", "list", flow, "--status", "pending")).toContain(first.sourceKey);
    expect(cliRefusal("records", "list", flow, "--status", "nonsense")).toMatch(/not a record status/);
  });

  test("a source is read one interface at a time, and an unknown interface says which there are", () => {
    const flow = `${fixtureMeta().repoDir}/flows/recall-source.yaml`;

    // A source delivers several interfaces, so a command that names none cannot tell which ledger it means.
    expect(cliRefusal("records", "list", flow)).toMatch(/delivers 2 interfaces/);
    expect(cliRefusal("records", "list", flow, "--interface", "nope")).toMatch(/has no interface 'nope'/);

    const listed = JSON.parse(cli("records", "list", flow, "--interface", "wellbores", "--json")) as {
      flow: string;
      records: unknown[];
    };
    expect(listed.flow).toBe("recall-source / wellbores");
    // The source's own ledgers are its own: nothing is read from the single-form flows beside it.
    expect(listed.records).toHaveLength(0);
  });
});
