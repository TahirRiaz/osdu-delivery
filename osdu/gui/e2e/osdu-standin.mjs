// The OSDU platform the e2e suite's flows reach, as far as a plan needs one: a token for the flows' client credentials,
// and the search service answering the lookups the sample mappings make when they render. A WellLog names its wellbore,
// and a render finds that wellbore by searching the platform; here the platform holds the wellbores of the five sample
// Recall logs and the two the fixture documents name, in whichever partition the request names.
//
// A record can be read back as well, which is what a record page's In OSDU and Compare tabs do: through the storage
// service, or through the wellbore DDMS a well log flow delivers by. The wellbores are held as records too, and a spec
// can hold a record of its own for as long as it needs one, through /__e2e/records: a path of this stand-in, not of the
// platform, so no flow reaches it. Everything else answers 404, so a spec that tried to send a record would fail loudly
// rather than reach a real OSDU.
//
// Started by playwright.config.ts beside the control plane, on SQLFLOW_E2E_OSDU_PORT (5301 by default).
import { createServer } from "node:http";

const port = Number(process.env.SQLFLOW_E2E_OSDU_PORT ?? 5301);

/**
 * The wellbores the platform holds, by the name a search asks for: the wellbores of the sample Recall logs
 * (osdu/samples/recall/data/welllog) and the two the fixture documents name.
 */
const WELLBORES = new Set([
  "NO 15/5-7 AT2",
  "NO 33/9-A-24 AT2",
  "NO 33/9-C-28 A",
  "NO 33/9-C-28 B",
  "NO 34/10-B-31 AT2",
  "OSDU-DEV-1-A",
  "OSDU-DEV-1-B",
]);

/**
 * The id a wellbore of that name has here: its name with spaces and slashes as hyphens (NO 33/9-C-28 B is
 * NO-33-9-C-28-B), which is the id the delivery suites give it and one the WellboreID pattern takes.
 */
function wellboreId(name) {
  return name.replace(/[ /]/g, "-");
}

/** The kind the sample mappings search for wellbores in. */
const WELLBORE_KIND = /^osdu:wks:master-data--Wellbore:/;

/** The query a render sends to find a wellbore by name: the name's keyword, quoted and escaped. */
const BY_NAME = /^data\.FacilityName\.keyword:"((?:[^"\\]|\\.)*)"$/;

/** Answers with a JSON body. */
function send(response, status, body) {
  const text = JSON.stringify(body);
  response.writeHead(status, { "content-type": "application/json", "content-length": Buffer.byteLength(text) });
  response.end(text);
}

/** The request's body as text. */
function read(request) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    request.on("data", (chunk) => chunks.push(chunk));
    request.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    request.on("error", reject);
  });
}

/** The records a search finds: the sample wellbore the query names, in the partition that asked, or none. */
function search(body, partition) {
  const match = BY_NAME.exec(typeof body.query === "string" ? body.query : "");
  const name = match === null ? null : match[1].replace(/\\(.)/g, "$1");
  const found = typeof partition === "string" && partition !== ""
    && typeof body.kind === "string" && WELLBORE_KIND.test(body.kind)
    && name !== null && WELLBORES.has(name);
  return found
    ? { results: [{ id: `${partition}:master-data--Wellbore:${wellboreId(name)}` }], totalCount: 1 }
    : { results: [], totalCount: 0 };
}

/** The records a spec asked the stand-in to hold, by id (its version set aside). */
const held = new Map();

/** A record reference without its version: p:t:k: and p:t:k:123 read as p:t:k, as the control plane reads them. */
function withoutVersion(id) {
  const last = id.lastIndexOf(":");
  const colons = id.split(":").length - 1;
  return last >= 0 && colons >= 3 && /^\d*$/.test(id.slice(last + 1)) ? id.slice(0, last) : id;
}

/** The version every record the stand-in holds reads at: a storage version is a microsecond timestamp. */
const VERSION = 1727280000000000;

/** A wellbore of the platform as a record: what a read of one of the wellbores the search finds gives back. */
function wellboreRecord(id) {
  const [partition, type, unique] = id.split(":");
  const name = [...WELLBORES].find((candidate) => wellboreId(candidate) === unique);
  if (type !== "master-data--Wellbore" || name === undefined) {
    return null;
  }

  return {
    id,
    kind: "osdu:wks:master-data--Wellbore:1.1.0",
    version: VERSION,
    acl: { viewers: [`data.default.viewers@${partition}.dataservices.energy`], owners: [`data.default.owners@${partition}.dataservices.energy`] },
    legal: { legaltags: [`${partition}-reference-data-default`], otherRelevantDataCountries: ["NO"], status: "compliant" },
    data: { FacilityName: name },
    createUser: "e2e-stand-in",
    createTime: "2026-09-25T00:00:00.000Z",
  };
}

/** The record a read of `id` finds: one a spec holds, or a wellbore of the platform; null when there is none. */
function readRecord(id) {
  const key = withoutVersion(id);
  const own = held.get(key);
  return own !== undefined ? { ...own, version: own.version ?? VERSION } : wellboreRecord(key);
}

/** A record read: storage's (GET /api/storage/v2/records/{id}) or a wellbore DDMS collection's (GET .../ddms/v3/{collection}/{id}). */
const RECORD_READ = /^\/api\/(?:storage\/v2\/records|os-wellbore-ddms\/ddms\/v3\/[A-Za-z]+)\/([^/]+)$/;

const server = createServer((request, response) => {
  const path = new URL(request.url ?? "/", "http://stand-in").pathname;
  if (request.method === "GET" && path === "/health") {
    send(response, 200, { status: "up" });
    return;
  }

  if (path === "/__e2e/records") {
    if (request.method === "DELETE") {
      held.clear();
      send(response, 200, { held: 0 });
      return;
    }

    if (request.method === "PUT") {
      read(request)
        .then((text) => {
          const record = JSON.parse(text);
          if (record === null || typeof record !== "object" || typeof record.id !== "string" || record.id === "") {
            send(response, 400, { message: "A record to hold is a JSON object with an id." });
            return;
          }

          held.set(withoutVersion(record.id), record);
          send(response, 200, { id: record.id, held: held.size });
        })
        .catch((error) => send(response, 400, { message: `The e2e OSDU stand-in could not read the record: ${error instanceof Error ? error.message : String(error)}` }));
      return;
    }
  }

  const reading = request.method === "GET" ? RECORD_READ.exec(path) : null;
  if (reading !== null) {
    let id;
    try {
      id = decodeURIComponent(reading[1]);
    } catch {
      send(response, 400, { message: `The e2e OSDU stand-in cannot read the id in ${path}.` });
      return;
    }

    const record = readRecord(id);
    if (record === null) {
      send(response, 404, { code: 404, reason: "Record not found", message: `The e2e OSDU stand-in holds no record ${id}.` });
    } else {
      send(response, 200, record);
    }

    return;
  }

  if (request.method === "POST" && path === "/token") {
    // The flows authenticate with client credentials; any will do here, since nothing here holds anything to protect.
    send(response, 200, { access_token: "e2e-stand-in", token_type: "Bearer", expires_in: 3600 });
    return;
  }

  if (request.method === "POST" && path === "/api/search/v2/query") {
    read(request)
      .then((text) => send(response, 200, search(JSON.parse(text), request.headers["data-partition-id"])))
      .catch((error) => send(response, 400, { message: `The e2e OSDU stand-in could not read the search: ${error instanceof Error ? error.message : String(error)}` }));
    return;
  }

  send(response, 404, { message: `The e2e OSDU stand-in answers a token, the search service and record reads, not ${request.method} ${path}.` });
});

server.listen(port, "127.0.0.1", () => {
  console.log(`e2e OSDU stand-in listening on http://127.0.0.1:${port}`);
});
