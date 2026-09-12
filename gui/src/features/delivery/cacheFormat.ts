import type { DeliveryCacheVersion } from "../../api/delivery";

/** A version as the pickers label it: the version, when it was captured, and whether it is the pinned one. */
export function versionLabel(version: DeliveryCacheVersion): string {
  const captured = version.capturedUtc ? new Date(version.capturedUtc).toLocaleString() : "never captured";
  return `${version.version} · ${captured}${version.current ? " · current" : ""}`;
}

/** A cached value on one line: a scalar as itself, a set as its values, an object as its JSON. */
export function cachedText(value: unknown): string {
  if (value === null || value === undefined) {
    return "-";
  }

  if (Array.isArray(value)) {
    return value.map((v) => cachedText(v)).join(", ");
  }

  return typeof value === "object" ? JSON.stringify(value) : String(value);
}

/** Every captured value of a cached record on one line, name by name. */
export function cachedFieldsText(fields: Record<string, unknown> | null): string {
  return Object.entries(fields ?? {}).map(([name, value]) => `${name}: ${cachedText(value)}`).join("  ·  ") || "-";
}
