// The OSDU platform the e2e suite's flows reach, as far as a plan needs one: a token for the flows' client credentials,
// and the search service answering the lookups the sample mappings make when they render. A WellLog or a trajectory
// names its wellbore, and a render finds that wellbore by searching the platform; here the platform holds the two sample
// wellbores, in whichever partition the request names. Everything else answers 404, so a spec that tried to send a
// record would fail loudly rather than reach a real OSDU.
//
// Started by playwright.config.ts beside the control plane, on SQLFLOW_E2E_OSDU_PORT (5301 by default).
import { createServer } from "node:http";

const port = Number(process.env.SQLFLOW_E2E_OSDU_PORT ?? 5301);

/** The sample wellbores, by the names the sample drops give them. */
const WELLBORES = new Set(["OSDU-DEV-1-A", "OSDU-DEV-1-B"]);

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
    ? { results: [{ id: `${partition}:master-data--Wellbore:${name}` }], totalCount: 1 }
    : { results: [], totalCount: 0 };
}

const server = createServer((request, response) => {
  const path = new URL(request.url ?? "/", "http://stand-in").pathname;
  if (request.method === "GET" && path === "/health") {
    send(response, 200, { status: "up" });
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

  send(response, 404, { message: `The e2e OSDU stand-in answers a token and the search service, not ${request.method} ${path}.` });
});

server.listen(port, "127.0.0.1", () => {
  console.log(`e2e OSDU stand-in listening on http://127.0.0.1:${port}`);
});
