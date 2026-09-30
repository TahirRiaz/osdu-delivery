import { CircleCheck, CircleDashed, CircleX, Loader2, PencilLine, Unlink, type LucideIcon } from "lucide-react";
import { formatDurationSeconds, parseUtc } from "@/lib/time";
import {
  deliveryApi, type DeliveryDimension, type DeliveryDimensionBoard, type DeliveryDimensionBuild, type DeliveryDimensionField, type DeliveryDimensionFlow,
  type DimensionChangeKind, type DimensionExportFormat, type DimensionExportSet, type DimensionLeftOut,
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
    hint: "The flow's declaration of it changed since its values were read: build it again to read them with the new one.",
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
export type DimensionView = "values" | "keys" | "changes" | "builds" | "definition";

export const DIMENSION_VIEWS: readonly DimensionView[] = ["values", "keys", "changes", "builds", "definition"];

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
