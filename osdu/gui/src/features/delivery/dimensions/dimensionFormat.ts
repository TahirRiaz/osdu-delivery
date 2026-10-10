import { CircleCheck, CircleDashed, CircleX, Loader2, PencilLine, Unlink, type LucideIcon } from "lucide-react";
import { formatDurationSeconds, parseUtc } from "@/lib/time";
import {
  deliveryApi, type DeliveryDimension, type DeliveryDimensionBoard, type DeliveryDimensionBuild, type DeliveryDimensionField, type DeliveryDimensionFlow,
  type DimensionAttributeCondition, type DimensionChangeKind, type DimensionExportFormat, type DimensionExportSet, type DimensionLeftOut,
} from "../../../api/delivery";

/**
 * Where a dimension stands in the partition in view: its newest build failed or is running, its declaration changed since
 * the values it holds were read, it holds values, no build has read it yet, or its flow no longer declares it.
 */
export type DimensionStanding = "failed" | "running" | "changed" | "built" | "notBuilt" | "undeclared";

export interface StandingVisual {
  icon: LucideIcon;
  label: string;
  /** The text colour of the glyph: a status token, or muted. */
  tone: string;
  /** What the standing means, as hovering it says. */
  hint: string;
  spin?: boolean;
}

export const STANDING_VISUALS: Record<DimensionStanding, StandingVisual> = {
  failed: {
    icon: CircleX, label: "Build failed", tone: "text-destructive",
    hint: "Its newest build failed and wrote nothing: the dimension holds what the build before it wrote.",
  },
  running: { icon: Loader2, label: "Building", tone: "text-info", hint: "A build is reading its values now.", spin: true },
  changed: {
    icon: PencilLine, label: "Changed", tone: "text-warning",
    hint: "The flow's declaration of it changed since its values were read: run the pipeline to read them with the new one.",
  },
  built: { icon: CircleCheck, label: "Built", tone: "text-success", hint: "It holds the values its last build read." },
  notBuilt: { icon: CircleDashed, label: "Not built", tone: "text-muted-foreground", hint: "No build has read it in this partition yet." },
  undeclared: {
    icon: Unlink, label: "No longer declared", tone: "text-muted-foreground",
    hint: "Its flow no longer declares it, so no build reads it again; it keeps what its last build wrote.",
  },
};

/** Where a dimension stands; the newest build's trouble comes first, since it is what needs doing. */
export function standingOf(dimension: DeliveryDimension): DimensionStanding {
  if (!dimension.declared) {
    return "undeclared";
  }

  if (dimension.latest?.status === "running") {
    return "running";
  }

  if (dimension.latest?.status === "failed") {
    return "failed";
  }

  if (dimension.current === null) {
    return "notBuilt";
  }

  return dimension.changed ? "changed" : "built";
}

/** The five questions a dimension's page answers, a tab each. */
export type DimensionView = "table" | "values" | "keys" | "changes" | "builds" | "definition";

/** A dimension's tabs, in order; the first is the one a dimension opens on. */
export const DIMENSION_VIEWS: readonly DimensionView[] = ["table", "values", "keys", "changes", "builds", "definition"];

/** How the page's link names the search builder in place of a dimension. */
export const SEARCH_REF = "search";

/** One dimension on a board, with the flow that declares it, how a link names it, and where it stands. */
export interface DimensionEntry {
  flow: DeliveryDimensionFlow;
  dimension: DeliveryDimension;
  ref: string;
  standing: DimensionStanding;
}

/** Every dimension of the flows that build in the board's partition, flow by flow, each flow's in the order it declares them. */
export function dimensionEntries(board: DeliveryDimensionBoard | undefined): DimensionEntry[] {
  return (board?.flows ?? [])
    .filter((flow) => flow.buildsPartition)
    .flatMap((flow) => flow.dimensions.map((dimension) => ({
      flow, dimension, ref: dimensionRef(flow, dimension), standing: standingOf(dimension),
    })));
}

/** How a dimension is named in a link: by its id once a build has registered it, else by its flow and name. */
export function dimensionRef(flow: DeliveryDimensionFlow, dimension: DeliveryDimension): string {
  return dimension.dimensionId !== null ? String(dimension.dimensionId) : `${flow.pipelineId}/${dimension.name}`;
}

/** How the index stores a field, in a line: its shape, the nested array it sits in, and whether a record holds it more than once. */
export function fieldText(field: DeliveryDimensionField): string {
  const shape = field.index === "text" ? "text, read by its keyword sub-field" : field.index;
  const nested = field.nestedPath === null ? "" : `, in nested array ${field.nestedPath}`;
  return `${shape}${nested}${field.repeats ? ", several per record" : ""}`;
}

/**
 * How complete a build's keys are: the share of the records its query matched that hold a key the index can read, with
 * the words for it. Null when the build could not count them.
 */
export function coverage(build: DeliveryDimensionBuild): { share: number; text: string } | null {
  if (build.records === null || build.withValue === null || build.records === 0) {
    return null;
  }

  const share = Math.min(1, build.withValue / build.records);
  return {
    share,
    text: `${build.withValue.toLocaleString("en-US")} of ${build.records.toLocaleString("en-US")} records hold a key`,
  };
}

/** A time a window is bounded by, as a person reads it: the date and the minute, in UTC. */
function windowTime(value: string | null | undefined): string {
  return value === null || value === undefined ? "the beginning" : `${value.slice(0, 16).replace("T", " ")} UTC`;
}

/**
 * How a build loaded its dimension, as a grid shows it (`full` or `incremental`) and the words a tooltip explains it with:
 * a full load read every key; an incremental one the records that changed in its window and the keys it read again.
 */
export function loadText(build: DeliveryDimensionBuild): { label: string; text: string } {
  if (build.load !== "incremental") {
    return { label: "full", text: "Read every key the index holds, and removed what it no longer found." };
  }

  const changed = build.changedRecords ?? 0;
  const touched = build.touchedKeys ?? 0;
  return {
    label: "incremental",
    text: `Read the ${changed.toLocaleString("en-US")} record${changed === 1 ? "" : "s"} that changed between ${windowTime(build.windowFrom)} and ${windowTime(build.windowTo)}, and read again the ${touched.toLocaleString("en-US")} key${touched === 1 ? "" : "s"} they hold or held, or were read through. Every other key stayed as it was.`,
  };
}

/** A share as a percentage a reader takes in at a glance: whole above 10%, one decimal below, and never 100% unless it is. */
export function percent(share: number): string {
  if (share >= 1) {
    return "100%";
  }

  const value = share * 100;
  const shown = value >= 10 ? Math.min(99, Math.round(value)).toString() : value.toFixed(1);
  return `${shown}%`;
}

/** How long a build ran, or has been running. */
export function buildDuration(build: DeliveryDimensionBuild, now = Date.now()): string {
  const started = parseUtc(build.startedUtc).getTime();
  const ended = build.completedUtc === null ? now : parseUtc(build.completedUtc).getTime();
  return formatDurationSeconds(Math.max(0, (ended - started) / 1000));
}

/** The most characters of a key that stay in view when it is cut short: enough of an id to tell it from the next. */
const KEY_TAIL = 24;

/**
 * How many characters at the end of a key tell it from its neighbours: for an OSDU record id, the code after its entity
 * type (with the colon or version that ends it), which is all that differs between the wellbores of a partition, at most
 * the last 24 of them; none for any other key. A grid keeps that much of a key in view when it has to cut it short, and
 * shows more of it where there is room.
 */
export function keyTail(key: string): number {
  const match = /^[^:\s]+:[^:\s]+--[^:\s]+:(.+)$/.exec(key);
  return match === null ? 0 : Math.min(Array.from(match[1]).length, KEY_TAIL);
}

/** Why a key belongs to no value, in words. */
export const LEFT_OUT_TEXT: Record<DimensionLeftOut, string> = {
  empty: "cleaning left nothing",
  tooLong: "its value would be longer than a value may be",
  dropped: "a map step left it out",
  failed: "a clean step could not run on it",
};

/** What a change did to a key, as a filter chip and a row name it. */
export const CHANGE_TEXT: Record<DimensionChangeKind, { label: string; verb: string }> = {
  added: { label: "Arrived", verb: "arrived" },
  removed: { label: "Left", verb: "left" },
  moved: { label: "Moved", verb: "moved" },
  restored: { label: "Came back", verb: "came back" },
};

/** The request body of an OSDU search for a filter: the kind and the query, as the search service takes them (POST /api/search/v2/query). */
export function osduSearchRequest(kind: string, query: string): string {
  return JSON.stringify({ kind, query, limit: 1000 }, null, 2);
}

/** The run payload that builds only the dimensions named; none builds every one. */
export function dimensionsPayload(names: readonly string[]): Record<string, unknown> {
  return names.length === 0 ? {} : { dimensions: [...names] };
}

const EXPORT_TYPES: Record<DimensionExportFormat, { type: string; extension: string }> = {
  csv: { type: "text/csv;charset=utf-8", extension: "csv" },
  jsonl: { type: "application/x-ndjson;charset=utf-8", extension: "jsonl" },
};

/** A part of a file name: letters, digits, dots, dashes and underscores, anything else a dash, as the control plane names it. */
function safe(part: string): string {
  const cleaned = part.replace(/[^A-Za-z0-9._-]/g, "-");
  return cleaned === "" ? "dimension" : cleaned;
}

/** Downloads the whole of a dimension's values or keys, named as the control plane names the file. */
export async function downloadDimension(
  dimensionId: number, flowName: string, partition: string | null, name: string, set: DimensionExportSet, format: DimensionExportFormat,
): Promise<void> {
  const text = await deliveryApi.dimensionExport(dimensionId, set, format);
  const url = URL.createObjectURL(new Blob([text], { type: EXPORT_TYPES[format].type }));
  const anchor = document.createElement("a");
  anchor.href = url;
  anchor.download = `${[flowName, partition ?? "partition", name, set].map(safe).join("-")}.${EXPORT_TYPES[format].extension}`;
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

/** A search's picks as a link carries them: each value as `<dimensionId>:<valueId>`, comma separated. */
export function picksText(picks: ReadonlyMap<number, ReadonlySet<number>>): string {
  return [...picks.entries()]
    .flatMap(([dimensionId, values]) => [...values].sort((a, b) => a - b).map((valueId) => `${dimensionId}:${valueId}`))
    .join(",");
}

/** The picks a link carries; what is not a pick is passed over. */
export function picksOf(text: string | null): Map<number, Set<number>> {
  const picks = new Map<number, Set<number>>();
  for (const part of (text ?? "").split(",")) {
    const match = /^(\d{1,10}):(\d{1,19})$/.exec(part.trim());
    if (match === null) {
      continue;
    }

    const dimensionId = Number(match[1]);
    const valueId = Number(match[2]);
    if (Number.isSafeInteger(dimensionId) && Number.isSafeInteger(valueId)) {
      (picks.get(dimensionId) ?? picks.set(dimensionId, new Set()).get(dimensionId)!).add(valueId);
    }
  }

  return picks;
}

/**
 * A search's attribute picks as a link carries them: each as `<dimensionId>:<name>:<value>`, the name and the value
 * escaped, comma separated.
 */
export function attributePicksText(picks: ReadonlyMap<number, readonly DimensionAttributeCondition[]>): string {
  return [...picks.entries()]
    .flatMap(([dimensionId, conditions]) => conditions.map((c) => `${dimensionId}:${encodeURIComponent(c.name)}:${encodeURIComponent(c.value)}`))
    .join(",");
}

/** The attribute picks a link carries; what is not one is passed over. */
export function attributePicksOf(text: string | null): Map<number, DimensionAttributeCondition[]> {
  const picks = new Map<number, DimensionAttributeCondition[]>();
  for (const part of (text ?? "").split(",")) {
    const [id, name, value, ...rest] = part.trim().split(":");
    const dimensionId = Number(id);
    if (rest.length > 0 || name === undefined || value === undefined || !Number.isSafeInteger(dimensionId) || dimensionId <= 0) {
      continue;
    }

    try {
      const condition = { name: decodeURIComponent(name), value: decodeURIComponent(value) };
      if (condition.name !== "" && condition.value !== "") {
        (picks.get(dimensionId) ?? picks.set(dimensionId, []).get(dimensionId)!).push(condition);
      }
    } catch {
      // An escape that is not one names no pick.
    }
  }

  return picks;
}
