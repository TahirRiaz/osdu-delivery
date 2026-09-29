import { format } from "date-fns";
import { IconBadge, OutcomePill } from "@/components/StatusBadge";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import type { AssertionRunStatus, AssertionSeverity, DeliveryAssertionPoint } from "../../../api/delivery";
import { OUTCOME_VISUALS, SEVERITY_TONES, runStatusVisual, type TestStanding } from "./assertionFormat";

/** Where a test stands, as the outcome pill every board, sheet and report shows it with. */
export function TestOutcomeBadge({ outcome, testId = "test-outcome" }: { outcome: TestStanding; testId?: string }) {
  const visual = OUTCOME_VISUALS[outcome];
  return <OutcomePill tone={visual.tone} label={visual.label} icon={visual.icon} testId={testId} />;
}

/** The same, as the tinted glyph alone, for a dense cell whose column already says what it holds. */
export function TestOutcomeIcon({ outcome, testId = "test-outcome-icon" }: { outcome: TestStanding; testId?: string }) {
  const visual = OUTCOME_VISUALS[outcome];
  return <IconBadge tone={visual.tone} label={visual.label} icon={visual.icon} testId={testId} />;
}

/** An assertion run's status. */
export function AssertionRunStatusBadge({ status, testId = "assertion-run-status" }: { status: AssertionRunStatus; testId?: string }) {
  const visual = runStatusVisual(status);
  return <OutcomePill tone={visual.tone} label={visual.label} icon={visual.icon} testId={testId} />;
}

/** The weight an assertion's failure carries: an outlined chip, so it never reads as an outcome. */
export function SeverityChip({ severity }: { severity: AssertionSeverity }) {
  return (
    <span
      className={cn("inline-flex items-center rounded-sm px-1.5 py-px font-mono text-[10.5px] leading-4 ring-1 ring-inset", SEVERITY_TONES[severity])}
      data-testid="assertion-severity"
    >
      {severity}
    </span>
  );
}

/** A test's tag as a toggle: pressed, it picks or filters by the tests carrying it. */
export function TagChip({ tag, pressed, onToggle, count }: { tag: string; pressed: boolean; onToggle: () => void; count?: number }) {
  return (
    <button
      type="button"
      aria-pressed={pressed}
      onClick={onToggle}
      className={cn(
        "inline-flex h-6 items-center gap-1 rounded-full border px-2 font-mono text-[11px] transition-colors",
        pressed ? "border-primary/70 bg-primary/10 text-foreground dark:bg-primary/20" : "text-muted-foreground hover:bg-muted",
      )}
      data-testid={`tag-${tag}`}
    >
      #{tag}
      {count !== undefined && <span className="tabular-nums text-muted-foreground">{count}</span>}
    </button>
  );
}

/** The fill of an outcome square; the square is always paired with its word on hover and for assistive tech. */
const SQUARE_CLASSES: Record<TestStanding, string> = {
  passed: "bg-success/70",
  failed: "bg-destructive",
  errored: "bg-destructive/45 ring-1 ring-inset ring-destructive",
  warned: "bg-warning/80",
  skipped: "bg-muted-foreground/25",
  notRun: "border border-dashed border-border",
  elsewhere: "border border-dashed border-border",
};

/**
 * One outcome as a small square, for a strip or a grid of many: the fill says the family at a glance, the tooltip and the
 * accessible name say the word and what it was, and a click opens it when there is something to open.
 */
export function OutcomeSquare({ outcome, title, onClick, size = "size-2.5", testId = "outcome-square" }: {
  outcome: TestStanding;
  title: string;
  onClick?: () => void;
  size?: string;
  testId?: string;
}) {
  const label = `${OUTCOME_VISUALS[outcome].label}: ${title}`;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          type="button"
          aria-label={label}
          className={cn(
            "shrink-0 rounded-[2px] transition-transform focus-visible:outline-2",
            onClick !== undefined ? "cursor-pointer hover:scale-125" : "cursor-default",
            size,
            SQUARE_CLASSES[outcome],
          )}
          onClick={onClick}
          data-testid={testId}
          data-outcome={outcome}
        />
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}

/**
 * A test's last runs, oldest on the left: one square per run, the word, the run and the time on hover, and the tally
 * beside it in text, so the pattern reads at a glance and the colour never carries it alone.
 */
export function HistoryStrip({
  points, slots = 12, onPick,
}: {
  /** Newest first, as the board serves them. */
  points: readonly DeliveryAssertionPoint[];
  slots?: number;
  onPick?: (point: DeliveryAssertionPoint) => void;
}) {
  const recent = points.slice(0, slots).reverse();
  const failing = recent.filter((p) => p.outcome === "failed" || p.outcome === "errored").length;
  const empty = Math.max(0, slots - recent.length);
  return (
    <div className="flex items-center gap-2" data-testid="history-strip">
      <div className="flex items-center gap-[3px]" role="group" aria-label={`last ${recent.length} run(s), ${failing} not passing`}>
        {Array.from({ length: empty }, (_, i) => (
          <span key={`empty-${i}`} className="size-2.5 rounded-[2px] border border-dashed border-border" aria-hidden />
        ))}
        {recent.map((point) => (
          <OutcomeSquare
            key={point.assertionRunId}
            outcome={point.outcome}
            title={`report ${point.assertionRunId}, ${format(new Date(point.completedUtc), "yyyy-MM-dd HH:mm")}`
              + (point.failedAssertions > 0 ? `, ${point.failedAssertions} assertion(s) not holding` : "")}
            onClick={onPick === undefined ? undefined : () => onPick(point)}
            testId="history-cell"
          />
        ))}
      </div>
      {recent.length > 0 && (
        <span className="font-mono text-[11px] tabular-nums text-muted-foreground" data-testid="history-tally">
          {recent.length - failing}/{recent.length}
        </span>
      )}
    </div>
  );
}

/** How a set of tests came out, as a stacked bar: each outcome's share in its fill, the counts on hover and in its label. */
export function OutcomeBar({ counts, className, testId = "outcome-bar" }: {
  counts: { passed: number; warned: number; failed: number; errored: number; skipped: number };
  className?: string;
  testId?: string;
}) {
  const parts: { outcome: TestStanding; count: number }[] = [
    { outcome: "passed", count: counts.passed },
    { outcome: "warned", count: counts.warned },
    { outcome: "failed", count: counts.failed },
    { outcome: "errored", count: counts.errored },
    { outcome: "skipped", count: counts.skipped },
  ];
  const total = parts.reduce((sum, part) => sum + part.count, 0);
  const label = total === 0 ? "no test" : parts.filter((p) => p.count > 0).map((p) => `${p.count} ${OUTCOME_VISUALS[p.outcome].label}`).join(", ");
  return (
    <div className={cn("flex h-2 w-full overflow-hidden rounded-full bg-muted", className)} role="img" aria-label={label} title={label} data-testid={testId}>
      {total > 0 && parts.filter((p) => p.count > 0).map((part) => (
        <span key={part.outcome} className={cn("h-full", SQUARE_CLASSES[part.outcome], "rounded-none ring-0")} style={{ width: `${(100 * part.count) / total}%` }} />
      ))}
    </div>
  );
}

/**
 * The share of tests passing as a ring, with the count it is a share of: the one headline number of a board. The ring is
 * a measure, not a status, so it wears the primary tone; the outcomes themselves are in the tiles beside it.
 */
export function PassRateRing({ rate, evaluated, size = 64 }: { rate: number | null; evaluated: number; size?: number }) {
  const stroke = 7;
  const radius = (size - stroke) / 2;
  const circumference = 2 * Math.PI * radius;
  const shown = rate ?? 0;
  return (
    <div className="flex items-center gap-3" data-testid="pass-rate">
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} role="img" aria-label={rate === null ? "no test evaluated" : `${shown}% of ${evaluated} tests passing`}>
        <circle cx={size / 2} cy={size / 2} r={radius} fill="none" className="stroke-muted" strokeWidth={stroke} />
        <circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          className="stroke-primary transition-[stroke-dashoffset] duration-500 ease-out"
          strokeWidth={stroke}
          strokeLinecap="round"
          strokeDasharray={circumference}
          strokeDashoffset={circumference * (1 - shown / 100)}
          transform={`rotate(-90 ${size / 2} ${size / 2})`}
        />
        <text x="50%" y="50%" dominantBaseline="central" textAnchor="middle" className="fill-foreground font-mono text-[13px] font-semibold">
          {rate === null ? "-" : `${Math.round(shown)}%`}
        </text>
      </svg>
      <div className="flex flex-col">
        <span className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Passing</span>
        <span className="text-xs text-muted-foreground">
          {rate === null ? "nothing evaluated yet" : `of ${evaluated.toLocaleString("en-US")} evaluated`}
        </span>
      </div>
    </div>
  );
}
