import { useState } from "react";
import { Ban, CircleCheck, CircleX, Download, Loader2 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { OutcomePill } from "@/components/StatusBadge";
import { RichTooltip } from "@/components/RichTooltip";
import { isApiError } from "@/api/client";
import { cn } from "@/lib/utils";
import type { DimensionBuildStatus, DimensionExportFormat, DimensionExportSet } from "../../../api/delivery";
import { STANDING_VISUALS, downloadDimension, type DimensionStanding } from "./dimensionFormat";

/** Where a dimension stands, as a glyph in its status colour with the words on hover. */
export function StandingGlyph({ standing, testId }: { standing: DimensionStanding; testId?: string }) {
  const visual = STANDING_VISUALS[standing];
  const Icon = visual.icon;
  return (
    <RichTooltip title={visual.label} body={visual.hint}>
      <span className="inline-flex shrink-0" data-testid={testId} data-standing={standing}>
        <Icon className={cn("size-4", visual.tone, visual.spin && "animate-spin")} aria-label={visual.label} />
      </span>
    </RichTooltip>
  );
}

/** What a build came to, as the outcome pill every run status wears. */
export function BuildStatusPill({ status, testId = "dimension-build-status" }: { status: DimensionBuildStatus; testId?: string }) {
  switch (status) {
    case "completed":
      return <OutcomePill tone="success" label="completed" icon={CircleCheck} testId={testId} />;
    case "failed":
      return <OutcomePill tone="destructive" label="failed" icon={CircleX} testId={testId} />;
    case "running":
      return <OutcomePill tone="info" label="running" icon={Loader2} testId={testId} />;
    default:
      return <OutcomePill tone="muted" label="cancelled" icon={Ban} testId={testId} />;
  }
}

const EXPORTS: readonly { set: DimensionExportSet; format: DimensionExportFormat; label: string; hint: string }[] = [
  { set: "values", format: "csv", label: "Values as CSV", hint: "each value, its records, its keys and its filter" },
  { set: "keys", format: "csv", label: "Keys as CSV", hint: "each key with its label, value and filter" },
  { set: "values", format: "jsonl", label: "Values as JSON Lines", hint: "every value exactly, for a program" },
  { set: "keys", format: "jsonl", label: "Keys as JSON Lines", hint: "every key exactly, for a program" },
];

/** The whole of a dimension, downloaded: its values or its keys, as CSV for a spreadsheet or JSON Lines for a program. */
export function DimensionExportMenu({ dimensionId, flowName, partition, name }: {
  dimensionId: number;
  flowName: string;
  partition: string | null;
  name: string;
}) {
  const [busy, setBusy] = useState(false);
  const download = async (set: DimensionExportSet, format: DimensionExportFormat) => {
    setBusy(true);
    try {
      await downloadDimension(dimensionId, flowName, partition, name, set, format);
      toast.success(`Downloaded the ${set} of ${name}`);
    } catch (error) {
      toast.error(`The ${set} of ${name} could not be downloaded: ${isApiError(error) ? error.detail ?? error.title : String(error)}`);
    } finally {
      setBusy(false);
    }
  };

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="sm" disabled={busy} data-testid="dimension-export">
          {busy ? <Loader2 className="animate-spin" /> : <Download />}
          Export
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="min-w-64">
        <DropdownMenuLabel className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">The whole dimension</DropdownMenuLabel>
        {EXPORTS.map((entry, index) => (
          <div key={`${entry.set}-${entry.format}`}>
            {index === 2 && <DropdownMenuSeparator />}
            <DropdownMenuItem onSelect={() => void download(entry.set, entry.format)} data-testid={`dimension-export-${entry.set}-${entry.format}`}>
              <Download />
              <span className="flex flex-col">
                <span>{entry.label}</span>
                <span className="text-[11px] text-muted-foreground">{entry.hint}</span>
              </span>
            </DropdownMenuItem>
          </div>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/**
 * The foot of a list read a page at a time by cursor: how many rows are in view, and the button that reads the next page
 * while there is one.
 */
export function MoreFooter({ shown, noun, hasMore, loading, onMore, testId }: {
  shown: number;
  noun: string;
  hasMore: boolean;
  loading: boolean;
  onMore: () => void;
  testId: string;
}) {
  if (shown === 0) {
    return null;
  }

  return (
    <div className="flex items-center justify-between gap-3 border-t border-border px-3 py-1 text-xs text-muted-foreground" data-testid={testId}>
      <span className="font-mono tabular-nums">
        {shown.toLocaleString("en-US")} {noun}{shown === 1 ? "" : "s"}{hasMore ? " so far" : ""}
      </span>
      {hasMore && (
        <Button variant="ghost" size="xs" onClick={onMore} disabled={loading} data-testid={`${testId}-more`}>
          {loading && <Loader2 className="animate-spin" />}
          Load more
        </Button>
      )}
    </div>
  );
}
