import { Link2 } from "lucide-react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import type { Schedule } from "../../api/types";
import { Mono } from "../../components/Mono";
import { cronSummary, intervalSummary, shortZone } from "./cadence";
import { chainedAfter } from "./chain";

/**
 * When a schedule runs, in one cell. The cadence reads in words; the exact expression, the zone it is
 * evaluated in, and whether missed occurrences are backfilled live in the hover, because they answer a
 * question an operator asks about ONE schedule and never about the whole list at once. The zone in particular
 * was its own column that repeated the same value down every row, and printed a meaningless value on chained
 * links: a schedule with no cron of its own is never evaluated against a clock, so it has no zone to speak of.
 */
export function TriggerCell({ schedule }: { schedule: Schedule }) {
  // A chained link has no cadence by design. Naming what fires it is the whole answer to "why does this never
  // run on its own"; a bare dash reads as a broken schedule.
  if (schedule.cron === null && schedule.intervalSeconds === null && chainedAfter(schedule)) {
    return (
      <Tooltip>
        <TooltipTrigger asChild>
          <span className="inline-flex items-center gap-1">
            <Link2 className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
            <span className="text-muted-foreground">after</span>
            <Mono>{chainedAfter(schedule)}</Mono>
          </span>
        </TooltipTrigger>
        <TooltipContent>
          Chained, not clocked: no cron and no timezone of its own. It becomes due exactly once, when
          {" "}{chainedAfter(schedule)} has finished, so the two can never overlap.
        </TooltipContent>
      </Tooltip>
    );
  }

  const summary = schedule.cron !== null
    ? cronSummary(schedule.cron)
    : schedule.intervalSeconds !== null ? intervalSummary(schedule.intervalSeconds) : null;

  if (schedule.cron === null && schedule.intervalSeconds === null) {
    return <span className="text-muted-foreground">-</span>;
  }

  // An interval fires every N seconds from the last fire, so no timezone applies to it. Only a cron is
  // evaluated against a wall clock, and only then is the zone worth showing.
  const zone = schedule.cron !== null ? shortZone(schedule.timezone) : null;
  const exact = schedule.cron !== null ? `cron: ${schedule.cron}` : `every ${schedule.intervalSeconds}s`;

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="inline-flex items-baseline gap-1.5">
          {/* An unrecognised expression shows itself rather than a guess at what it means. */}
          {summary === null ? <Mono>{exact}</Mono> : <span>{summary}</span>}
          {zone !== null && <span className="text-[11px] text-muted-foreground">{zone}</span>}
        </span>
      </TooltipTrigger>
      <TooltipContent className="max-w-xs">
        <span className="font-mono">{exact}</span>
        {schedule.cron !== null && <> evaluated in {schedule.timezone}.</>}
        {schedule.catchup
          ? " Missed occurrences are backfilled, one per scheduler tick."
          : " Missed occurrences are skipped, not backfilled."}
      </TooltipContent>
    </Tooltip>
  );
}
