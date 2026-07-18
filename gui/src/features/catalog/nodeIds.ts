// The catalog tree's single node-identity contract: every tree node, the ?node= deep link, and the
// details panel agree on these ids. An id is a type prefix plus URI-encoded segments joined with "|",
// so any server/database/schema/batch value round-trips regardless of the characters it contains. A null
// database/schema (an object whose identity was only partially resolved by an offline sync) encodes as
// the sentinel "~" and renders as "(unresolved)".

export type CatalogNode =
  | { type: "objectsRoot" }
  | { type: "server"; serverRef: string }
  | { type: "database"; serverRef: string; database: string | null }
  | { type: "schema"; serverRef: string; database: string | null; schema: string | null }
  | { type: "kind"; serverRef: string; database: string | null; schema: string | null; kind: string }
  | { type: "object"; objectKey: string }
  | { type: "flowsRoot" }
  | { type: "repo"; repoId: string }
  | { type: "batch"; repoId: string; batch: string }
  | { type: "flow"; repoId: string; pipelineId: string };

export const UNRESOLVED_LABEL = "(unresolved)";

const NULL_SENTINEL = "~";

function seg(value: string | null): string {
  return value === null ? NULL_SENTINEL : encodeURIComponent(value);
}

function unseg(value: string): string | null {
  return value === NULL_SENTINEL ? null : decodeURIComponent(value);
}

export function encodeNodeId(node: CatalogNode): string {
  switch (node.type) {
    case "objectsRoot":
      return "objects";
    case "server":
      return `srv:${seg(node.serverRef)}`;
    case "database":
      return `db:${seg(node.serverRef)}|${seg(node.database)}`;
    case "schema":
      return `sch:${seg(node.serverRef)}|${seg(node.database)}|${seg(node.schema)}`;
    case "kind":
      return `knd:${seg(node.serverRef)}|${seg(node.database)}|${seg(node.schema)}|${seg(node.kind)}`;
    case "object":
      return `obj:${encodeURIComponent(node.objectKey)}`;
    case "flowsRoot":
      return "flows";
    case "repo":
      return `repo:${seg(node.repoId)}`;
    case "batch":
      return `bat:${seg(node.repoId)}|${seg(node.batch)}`;
    case "flow":
      return `flw:${seg(node.repoId)}|${seg(node.pipelineId)}`;
  }
}

/** Decodes a tree/deep-link id back into its node; null for anything malformed (a hand-edited URL). */
export function decodeNodeId(id: string): CatalogNode | null {
  if (id === "objects") {
    return { type: "objectsRoot" };
  }
  if (id === "flows") {
    return { type: "flowsRoot" };
  }

  const colon = id.indexOf(":");
  if (colon <= 0) {
    return null;
  }
  const prefix = id.slice(0, colon);
  const parts = id.slice(colon + 1).split("|");
  try {
    switch (prefix) {
      case "srv":
        return parts.length === 1 && parts[0] !== NULL_SENTINEL
          ? { type: "server", serverRef: decodeURIComponent(parts[0]) }
          : null;
      case "db":
        return parts.length === 2 && parts[0] !== NULL_SENTINEL
          ? { type: "database", serverRef: decodeURIComponent(parts[0]), database: unseg(parts[1]) }
          : null;
      case "sch":
        return parts.length === 3 && parts[0] !== NULL_SENTINEL
          ? {
            type: "schema",
            serverRef: decodeURIComponent(parts[0]),
            database: unseg(parts[1]),
            schema: unseg(parts[2]),
          }
          : null;
      case "knd":
        return parts.length === 4 && parts[0] !== NULL_SENTINEL && parts[3] !== NULL_SENTINEL
          ? {
            type: "kind",
            serverRef: decodeURIComponent(parts[0]),
            database: unseg(parts[1]),
            schema: unseg(parts[2]),
            kind: decodeURIComponent(parts[3]),
          }
          : null;
      case "obj":
        return parts.length === 1 ? { type: "object", objectKey: decodeURIComponent(parts[0]) } : null;
      case "repo":
        return parts.length === 1 && parts[0] !== NULL_SENTINEL
          ? { type: "repo", repoId: decodeURIComponent(parts[0]) }
          : null;
      case "bat":
        return parts.length === 2 && parts[0] !== NULL_SENTINEL && parts[1] !== NULL_SENTINEL
          ? { type: "batch", repoId: decodeURIComponent(parts[0]), batch: decodeURIComponent(parts[1]) }
          : null;
      case "flw":
        return parts.length === 2 && parts[0] !== NULL_SENTINEL && parts[1] !== NULL_SENTINEL
          ? { type: "flow", repoId: decodeURIComponent(parts[0]), pipelineId: decodeURIComponent(parts[1]) }
          : null;
      default:
        return null;
    }
  } catch {
    // decodeURIComponent throws on a malformed escape sequence in a hand-edited URL.
    return null;
  }
}

/** The ancestor chain of ids to expand so the given node is visible in the tree (excludes the node itself). */
export function ancestorIds(node: CatalogNode): string[] {
  switch (node.type) {
    case "objectsRoot":
    case "flowsRoot":
      return [];
    case "server":
      return [encodeNodeId({ type: "objectsRoot" })];
    case "database":
      return [
        encodeNodeId({ type: "objectsRoot" }),
        encodeNodeId({ type: "server", serverRef: node.serverRef }),
      ];
    case "schema":
      return [
        ...ancestorIds({ type: "database", serverRef: node.serverRef, database: node.database }),
        encodeNodeId({ type: "database", serverRef: node.serverRef, database: node.database }),
      ];
    case "kind":
      return [
        ...ancestorIds({
          type: "schema", serverRef: node.serverRef, database: node.database, schema: node.schema,
        }),
        encodeNodeId({
          type: "schema", serverRef: node.serverRef, database: node.database, schema: node.schema,
        }),
      ];
    case "object":
      // The object key alone does not name its tree ancestors (the proper-cased hierarchy lives in the
      // registry row, not in the normalized key), so a deep-linked object opens with just the roots expanded
      // and the details panel carries the identity.
      return [encodeNodeId({ type: "objectsRoot" })];
    case "repo":
      return [encodeNodeId({ type: "flowsRoot" })];
    case "batch":
      return [
        encodeNodeId({ type: "flowsRoot" }),
        encodeNodeId({ type: "repo", repoId: node.repoId }),
      ];
    case "flow":
      // The flow id does not carry its batch, so a deep link expands down to the repo; the details panel
      // shows the flow either way.
      return [
        encodeNodeId({ type: "flowsRoot" }),
        encodeNodeId({ type: "repo", repoId: node.repoId }),
      ];
  }
}
