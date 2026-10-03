// The OSDU platform the e2e suite's flows reach, as far as a plan needs one: a token for the flows' client credentials,
// and the search service answering the lookups the sample mappings make when they render. A WellLog names its wellbore,
// and a render finds that wellbore by searching the platform; here the platform holds the wellbores of the five sample
// Recall logs and the two the fixture documents name, in whichever partition the request names.
//
// A record can be read back as well, which is what a record page's In OSDU and Compare tabs do: through the storage
// service, or through the wellbore DDMS a well log flow delivers by. The wellbores are held as records too, and a spec
// can hold a record of its own for as long as it needs one, through /__e2e/records: a path of this stand-in, not of the
// platform, so no flow reaches it. Holding a record again keeps a version of it, so a record can have a history. The
// explorer's searches are answered over the same records: the kinds they are of, a page of them by type, a phrase, the
// start of an id or the records that name one, and the dimension builder's: the records holding any of several values,
// every record but those, and a property's values grouped commonest first. Everything else answers 404, so a spec that
// tried to send a record would fail loudly rather than reach a real OSDU.
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

/** The records a spec asked the stand-in to hold, by id (its version set aside): every version held, oldest first. */
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

/** The record a read of `id` finds at its latest: one a spec holds, or a wellbore of the platform; null when there is none. */
function readRecord(id) {
  const versions = recordVersions(id);
  return versions.length === 0 ? null : versions[versions.length - 1];
}

/** Every version of the record `id` names, oldest first: those a spec held, or the one a wellbore of the platform has. */
function recordVersions(id) {
  const key = withoutVersion(id);
  const own = held.get(key);
  if (own !== undefined) {
    return own;
  }

  const wellbore = wellboreRecord(key);
  return wellbore === null ? [] : [wellbore];
}

/** Every record of a partition the stand-in holds, at its latest: the platform's wellbores and what specs hold. */
function partitionRecords(partition) {
  const wellbores = [...WELLBORES].map((name) => wellboreRecord(`${partition}:master-data--Wellbore:${wellboreId(name)}`));
  const own = [...held.values()].map((versions) => versions[versions.length - 1]).filter((record) => record.id.startsWith(`${partition}:`));
  return [...wellbores, ...own].sort((a, b) => a.id.localeCompare(b.id));
}

/** Whether `kind` is one a search's kind pattern names, each `*` standing for any run of characters. */
function kindMatches(pattern, kind) {
  const expression = String(pattern).split("*").map((part) => part.replace(/[.+?^${}()|[\]\\]/g, "\\$&")).join(".*");
  return new RegExp(`^${expression}$`).test(kind);
}

/** Every text a record holds, at any depth. */
function texts(value) {
  if (typeof value === "string") {
    return [value];
  }

  if (Array.isArray(value)) {
    return value.flatMap(texts);
  }

  return value !== null && typeof value === "object" ? Object.values(value).flatMap(texts) : [];
}

/** A Lucene term the explorer wrote with its reserved characters escaped, read back as the text it stands for. */
function unescape(term) {
  return term.replace(/\\(.)/g, "$1");
}

/**
 * Whether a record answers one clause of the queries the explorer and the dimension builder write: every record, a phrase
 * anywhere in it, an id exactly, an id that starts with a prefix or ends with a unique part, a property's whole value, one
 * of several whole values, or a property that is there; and a clause of clauses joined by AND, OR and AND NOT. Enough of
 * Lucene to answer them, and nothing more: what it does not read matches nothing.
 */
function matches(record, query) {
  const text = query.trim();
  if (text === "*") {
    return true;
  }

  if (text.startsWith("(") && text.endsWith(")") && balanced(text.slice(1, -1))) {
    return matches(record, text.slice(1, -1));
  }

  for (const [joint, all] of [[" OR ", false], [" AND ", true]]) {
    const parts = splitTop(text, joint);
    if (parts.length > 1) {
      return all
        ? parts.every((part) => (part.startsWith("NOT ") ? !matches(record, part.slice(4)) : matches(record, part)))
        : parts.some((part) => matches(record, part));
    }
  }

  const phrase = /^"((?:[^"\\]|\\.)*)"$/.exec(text);
  if (phrase !== null) {
    const wanted = unescape(phrase[1]).toLowerCase();
    return texts(record).some((value) => value.toLowerCase().includes(wanted));
  }

  const exactId = /^id:"((?:[^"\\]|\\.)*)"$/.exec(text);
  if (exactId !== null) {
    return record.id === unescape(exactId[1]);
  }

  const idPattern = /^id:(.+)\*$/.exec(text);
  if (idPattern !== null) {
    const [head, tail] = idPattern[1].split("\\:*\\:");
    return tail === undefined
      ? record.id.startsWith(unescape(head))
      : record.id.startsWith(`${unescape(head)}:`) && record.id.split(":").slice(2).join(":").startsWith(unescape(tail));
  }

  const property = /^([\w.]+?)(?:\.keyword)?:"((?:[^"\\]|\\.)*)"$/.exec(text);
  if (property !== null) {
    return texts(valueAt(record, property[1])).includes(unescape(property[2]));
  }

  // One of several whole values, as a dimension's filter and the reads of the records a key names ask: id:("a" OR "b").
  const anyOf = /^([\w.]+?)(?:\.keyword)?:\((.+)\)$/.exec(text);
  if (anyOf !== null && balanced(anyOf[2])) {
    const wanted = splitTop(anyOf[2], " OR ").map((part) => /^"((?:[^"\\]|\\.)*)"$/.exec(part));
    if (wanted.some((phrase) => phrase === null)) {
      return false;
    }

    const held = anyOf[1] === "id" ? [record.id] : texts(valueAt(record, anyOf[1]));
    return wanted.some((phrase) => held.includes(unescape(phrase[1])));
  }

  // A property that is there, which is how every record is asked for alongside a NOT: _exists_:id.
  const exists = /^_exists_:([\w.]+)$/.exec(text);
  if (exists !== null) {
    return exists[1] === "id" ? record.id !== "" : texts(valueAt(record, exists[1])).length > 0;
  }

  return false;
}

/** Whether a query's parentheses and quotes balance, so a pair around it encloses the whole of it. */
function balanced(text) {
  let depth = 0;
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === "\\") {
      i++;
    } else if (c === '"') {
      quoted = !quoted;
    } else if (!quoted && c === "(") {
      depth++;
    } else if (!quoted && c === ")" && --depth < 0) {
      return false;
    }
  }

  return depth === 0 && !quoted;
}

/** A query split at `joint` where it stands outside quotes and parentheses. */
function splitTop(text, joint) {
  const parts = [];
  let depth = 0;
  let quoted = false;
  let start = 0;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === "\\") {
      i++;
    } else if (c === '"') {
      quoted = !quoted;
    } else if (!quoted && c === "(") {
      depth++;
    } else if (!quoted && c === ")") {
      depth--;
    } else if (!quoted && depth === 0 && text.startsWith(joint, i)) {
      parts.push(text.slice(start, i));
      start = i + joint.length;
      i += joint.length - 1;
    }
  }

  parts.push(text.slice(start));
  return parts.map((part) => part.trim());
}

/** The value a record holds at a dotted path, for grouping (a `.keyword` sub-field read as its property). */
function valueAt(record, path) {
  return path.replace(/\.keyword$/, "").split(".").reduce((node, key) => (node !== null && typeof node === "object" ? node[key] : undefined), record);
}

/**
 * An explorer's search (POST /query with trackTotalCount): the records of the partition of the kinds asked that answer the
 * query, a page of them from the offset, the exact count, and the groups of the property asked for.
 */
function explore(body, partition) {
  const found = partitionRecords(partition)
    .filter((record) => kindMatches(body.kind ?? "*:*:*:*", record.kind))
    .filter((record) => typeof body.query !== "string" || body.query.trim() === "" || matches(record, body.query));
  const offset = Number(body.offset ?? 0);
  const limit = Number(body.limit ?? 10);
  const answer = { results: found.slice(offset, offset + limit), totalCount: found.length };
  if (typeof body.aggregateBy === "string") {
    const groups = new Map();
    for (const record of found) {
      for (const value of texts(valueAt(record, body.aggregateBy))) {
        groups.set(value, (groups.get(value) ?? 0) + 1);
      }
    }

    // Commonest first, and by value among groups as common, as the service's terms aggregation orders them.
    answer.aggregations = [...groups.entries()]
      .sort(([a, x], [b, y]) => y - x || (a < b ? -1 : a > b ? 1 : 0))
      .map(([key, count]) => ({ key, count }));
  }

  return answer;
}

/** A record read: storage's (GET /api/storage/v2/records/{id}) or a wellbore DDMS collection's (GET .../ddms/v3/{collection}/{id}). */
const RECORD_READ = /^\/api\/(?:storage\/v2\/records|os-wellbore-ddms\/ddms\/v3\/[A-Za-z]+)\/([^/]+)$/;
/** The version list beside a record (GET /api/storage/v2/records/versions/{id}); this stand-in keeps one version per record. */
const RECORD_VERSIONS = /^\/api\/storage\/v2\/records\/versions\/([^/]+)$/;
/** A record at one version (GET /api/storage/v2/records/{id}/{version}); only the version held answers. */
const RECORD_AT_VERSION = /^\/api\/storage\/v2\/records\/([^/]+)\/(\d+)$/;

/** The id a storage path names, decoded; null when it cannot be. */
function decodeId(encoded) {
  try {
    return decodeURIComponent(encoded);
  } catch {
    return null;
  }
}

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

          // Holding a record again keeps a version of it, numbered after the last, as storage numbers a record written again.
          const key = withoutVersion(record.id);
          const versions = held.get(key) ?? [];
          const version = record.version ?? (versions.length === 0 ? VERSION : versions[versions.length - 1].version + 1000);
          held.set(key, [...versions, { ...record, version }]);
          send(response, 200, { id: record.id, version, held: held.size });
        })
        .catch((error) => send(response, 400, { message: `The e2e OSDU stand-in could not read the record: ${error instanceof Error ? error.message : String(error)}` }));
      return;
    }
  }

  const versions = request.method === "GET" ? RECORD_VERSIONS.exec(path) : null;
  if (versions !== null) {
    const id = decodeId(versions[1]);
    const kept = id === null ? [] : recordVersions(id);
    if (kept.length === 0) {
      send(response, 404, { code: 404, reason: "Record not found", message: `The e2e OSDU stand-in holds no record ${id ?? path}.` });
    } else {
      send(response, 200, { recordId: withoutVersion(id), versions: kept.map((record) => record.version) });
    }

    return;
  }

  const atVersion = request.method === "GET" ? RECORD_AT_VERSION.exec(path) : null;
  if (atVersion !== null) {
    const id = decodeId(atVersion[1]);
    const record = id === null ? null : recordVersions(id).find((kept) => String(kept.version) === atVersion[2]) ?? null;
    if (record === null) {
      send(response, 404, { code: 404, reason: "Record version not found", message: `The e2e OSDU stand-in holds no version ${atVersion[2]} of ${id ?? path}.` });
    } else {
      send(response, 200, record);
    }

    return;
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
      .then((text) => {
        const body = JSON.parse(text);
        const partition = request.headers["data-partition-id"];
        // The explorer asks for the exact count; a render's lookup asks for a handful of ids by a wellbore's name.
        send(response, 200, body.trackTotalCount === true && typeof partition === "string" && partition !== "" ? explore(body, partition) : search(body, partition));
      })
      .catch((error) => send(response, 400, { message: `The e2e OSDU stand-in could not read the search: ${error instanceof Error ? error.message : String(error)}` }));
    return;
  }

  send(response, 404, { message: `The e2e OSDU stand-in answers a token, the search service and record reads, not ${request.method} ${path}.` });
});

server.listen(port, "127.0.0.1", () => {
  console.log(`e2e OSDU stand-in listening on http://127.0.0.1:${port}`);
});
