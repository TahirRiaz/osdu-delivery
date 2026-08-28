import { useMemo } from "react";
import type { StreamDetectorName, StreamStatus } from "../../api/types";
import { useThemeMode } from "../../theme/ThemeModeContext";

/** The detectors as a person reads them, in the order the ensemble ranks them: the three that answer "is data
 * arriving at all" first, then the three that measure how much. */
export const detectorLabels: Record<StreamDetectorName, string> = {
  silence: "No data now",
  nullDays: "Missing days",
  cadence: "Not running",
  rateChange: "Volume rate",
  levelShift: "Level shift",
  volumeOutlier: "Outlying day",
};

/** What each detector actually tests, shown beside its verdict so a reader can weigh the evidence rather than
 * take the label on trust. */
export const detectorMethods: Record<StreamDetectorName, string> = {
  silence: "Gap since the last load against this stream's own tolerated gap",
  nullDays: "Empty days against the median week's delivery rate",
  cadence: "Gap since the last run against its execution cadence",
  rateChange: "Overdispersion-adjusted count rate, recent slice against baseline",
  levelShift: "PELT change point over the residuals",
  volumeOutlier: "Generalized ESD over trend-and-weekday residuals",
};

/** The verdicts as a person reads them. */
export const statusLabels: Record<StreamStatus, string> = {
  stalled: "Stopped",
  degraded: "Degraded",
  watch: "Watch",
  healthy: "Healthy",
  "insufficient-history": "Not enough history",
};

/** The finding categories, spelled out. The first four are the zero-data family. */
export const categoryLabels: Record<string, string> = {
  stalled: "No data arriving",
  failing: "Failing",
  "gap-days": "Missing days",
  "not-running": "Not running",
  "less-than-normal": "Less than normal",
  "more-than-normal": "More than normal",
  "never-loaded": "Never loaded",
  "insufficient-history": "Not enough history",
  healthy: "On pattern",
};

/**
 * Chart ink from the app's own custom properties (DESIGN.md 3.4), re-read when the theme flips. Slot 1 carries
 * the one data series; the expectation line and the axes wear recessive text ink because a reference is not a
 * second series; a flagged day wears the reserved destructive status color, which is why it always ships with
 * a label rather than standing on color alone.
 */
export function useChartInk() {
  const { mode } = useThemeMode();
  return useMemo(() => {
    const token = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return {
      series: token("--chart-1"),
      flagged: token("--destructive"),
      axis: token("--muted-foreground"),
      grid: token("--border"),
      cursor: token("--muted"),
    };
  }, [mode]);
}

/** Row counts, compact enough for an axis tick when asked. */
export function formatRows(rows: number, compact = false): string {
  if (!Number.isFinite(rows)) {
    return "-";
  }

  const value = Math.round(rows);
  if (!compact) {
    return value.toLocaleString();
  }

  if (Math.abs(value) >= 1_000_000) {
    return `${(value / 1_000_000).toFixed(1)}M`;
  }

  return Math.abs(value) >= 1_000 ? `${(value / 1_000).toFixed(0)}k` : String(value);
}

/** A day count as an age. Null means it has never happened, which is not the same as "0 days ago". */
export function formatDays(days: number | null): string {
  if (days === null) {
    return "never";
  }

  if (days < 1) {
    return "today";
  }

  return days < 2 ? "1 day ago" : `${Math.round(days)} days ago`;
}
