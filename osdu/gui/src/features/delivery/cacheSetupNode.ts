/**
 * What the setup shows on its right: the partition's overview, one cache flow, one type, the OSDU feature flags, or how a
 * mapping reads the cache. It lives in the URL (`?node=`), so a link lands on the same view.
 */
export type SetupNode = "overview" | "flags" | "mapping" | `flow:${string}` | `type:${string}`;

/** The node a link names, or the overview for anything else. */
export function parseSetupNode(value: string | null): SetupNode {
  if (value === "flags" || value === "mapping") {
    return value;
  }

  if (value !== null && (value.startsWith("flow:") || value.startsWith("type:")) && value.length > 5) {
    return value as SetupNode;
  }

  return "overview";
}
