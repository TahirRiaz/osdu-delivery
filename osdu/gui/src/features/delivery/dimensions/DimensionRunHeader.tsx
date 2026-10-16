import { useNavigate } from "react-router-dom";
import { Shapes } from "lucide-react";
import { Button } from "@/components/ui/button";
import type { RunDetail } from "@/api/types";
import { DetailPair } from "@/components/DetailPair";
import { runResult } from "../runOutcome";
import { runViewFailed, runViewsOf } from "./dimensionViewFormat";

/** A count a dimension run reported, or a muted dash when it reported none (a plan, a run that stopped before its builds). */
function Count({ value, testId }: { value: number | null; testId: string }) {
  return value !== null
    ? <span className="font-mono tabular-nums" data-testid={testId}>{value.toLocaleString("en-US")}</span>
    : <span className="text-muted-foreground" data-testid={testId}>-</span>;
}

function numberOf(result: Record<string, unknown> | null, field: string): number | null {
  const value = result?.[field];
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

/**
 * The values or keys a build run's dimensions hold together, summed from its result; null for a run that reported no
 * build. A run that finished before values and keys were named so reported them as members and originals, which are read
 * in their place.
 */
function sumOf(result: Record<string, unknown> | null, field: "values" | "keys"): number | null {
  const dimensions = result?.dimensions;
  if (!Array.isArray(dimensions) || result?.operation !== "build") {
    return null;
  }

  const earlier = field === "values" ? "members" : "originals";
  return dimensions.reduce<number>((sum, entry) => {
    const built = entry !== null && typeof entry === "object" ? entry as Record<string, unknown> : null;
    const value = built?.[field] ?? built?.[earlier];
    return sum + (typeof value === "number" && Number.isFinite(value) ? value : 0);
  }, 0);
}

/** A dimension run's way to the dimensions it built, on the page every partition's dimensions are read on. */
export function DimensionRunActions() {
  const navigate = useNavigate();
  return (
    <Button variant="outline" size="sm" onClick={() => navigate("/delivery/dimensions")} data-testid="run-open-dimensions">
      <Shapes />
      Open dimensions
    </Button>
  );
}

/** The views a run reported, with those it could not write or read and those it dropped; the card under the header lists them. */
function ViewCount({ result }: { result: Record<string, unknown> | null }) {
  const reported = runViewsOf(result);
  if (reported === null || (reported.views.length === 0 && reported.dropped.length === 0)) {
    return null;
  }

  const failed = reported.views.filter(runViewFailed).length;
  return (
    <DetailPair label="Views">
      <span className="inline-flex flex-wrap items-baseline gap-x-1.5" data-testid="run-dimensions-views">
        <span className="font-mono tabular-nums">{reported.views.length.toLocaleString("en-US")}</span>
        {failed > 0 && (
          <>
            <span className="text-muted-foreground/50" aria-hidden>·</span>
            <span className="text-destructive">{failed.toLocaleString("en-US")} failed</span>
          </>
        )}
        {reported.dropped.length > 0 && (
          <>
            <span className="text-muted-foreground/50" aria-hidden>·</span>
            <span className="text-muted-foreground">{reported.dropped.length.toLocaleString("en-US")} dropped</span>
          </>
        )}
      </span>
    </DetailPair>
  );
}

/** How the run's dimensions and views came out, in place of the row counts SQLFlow's own runs carry. */
export function DimensionRunCounts({ run }: { run: RunDetail }) {
  const result = runResult(run);
  return (
    <>
      <DetailPair label="Built"><Count value={numberOf(result, "built")} testId="run-dimensions-built" /></DetailPair>
      <DetailPair label="Failed"><Count value={numberOf(result, "failed")} testId="run-dimensions-failed" /></DetailPair>
      <DetailPair label="Skipped"><Count value={numberOf(result, "skipped")} testId="run-dimensions-skipped" /></DetailPair>
      <DetailPair label="Values"><Count value={sumOf(result, "values")} testId="run-dimensions-values" /></DetailPair>
      <DetailPair label="Keys"><Count value={sumOf(result, "keys")} testId="run-dimensions-keys" /></DetailPair>
      <ViewCount result={result} />
    </>
  );
}
