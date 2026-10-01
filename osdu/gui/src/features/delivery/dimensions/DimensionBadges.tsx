import { useState } from "react";
import { Ban, ChevronRight, CircleCheck, CircleX, Download, Loader2 } from "lucide-react";
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
  { set: "table", format: "csv", label: "Table as CSV", hint: "a row per key and value it collects, a column per attribute: what cascading selects read" },
  { set: "values", format: "csv", label: "Values as CSV", hint: "each value, its records, its keys and its search" },
  { set: "keys", format: "csv", label: "Keys as CSV", hint: "each key with its label, value and search" },
  { set: "table", format: "jsonl", label: "Table as JSON Lines", hint: "the same rows, for a program" },
  { set: "values", format: "jsonl", label: "Values as JSON Lines", hint: "every value exactly, for a program" },
  { set: "keys", format: "jsonl", label: "Keys as JSON Lines", hint: "every key exactly, for a program" },
];

/** What a download is called in the message that says it came down. */
const SET_NAME: Record<DimensionExportSet, string> = { values: "values", keys: "keys", table: "table" };

/** The way back to every dimension, as the first words of a page inside the Dimensions page. */
export function DimensionsCrumb({ onBack }: { onBack: () => void }) {
  return (
    <>
      <button
        type="button"
        onClick={onBack}
        className="rounded-sm font-normal text-muted-foreground outline-none hover:text-foreground hover:underline focus-visible:underline"
        data-testid="dimensions-back"
      >
        Dimensions
      </button>
      <ChevronRight className="size-4 shrink-0 text-muted-foreground/60" />
    </>
  );
}

/**
 * The whole of a dimension, downloaded: its table (a row per key and value it collects, a column per attribute), its
 * values or its keys, as CSV for a spreadsheet or JSON Lines for a program.
 */
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
      toast.success(`Downloaded the ${SET_NAME[set]} of ${name}`);
    } catch (error) {
      toast.error(`The ${SET_NAME[set]} of ${name} could not be downloaded: ${isApiError(error) ? error.detail ?? error.title : String(error)}`);
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
      <DropdownMenuContent align="end" className="w-80">
        <DropdownMenuLabel className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">The whole dimension</DropdownMenuLabel>
        {EXPORTS.map((entry, index) => (
          <div key={`${entry.set}-${entry.format}`}>
            {index === 3 && <DropdownMenuSeparator />}
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
