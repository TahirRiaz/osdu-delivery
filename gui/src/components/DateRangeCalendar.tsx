import { useMemo, useState } from "react";
import {
  addMonths, eachDayOfInterval, endOfMonth, endOfWeek, format, isAfter, isBefore,
  isSameDay, isSameMonth, parse, startOfMonth, startOfWeek, subMonths,
} from "date-fns";
import { CalendarRange, ChevronLeft, ChevronRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/utils";

/** A datetime-local value ("yyyy-MM-ddThh:mm") split into its date and "HH:mm" time halves. */
interface Parsed {
  date: Date | null;
  time: string;
}

/** Parse a datetime-local string into a local date (at midnight) plus its "HH:mm" time, using `fallbackTime`
 * when the value is empty or carries no time. */
function parseValue(value: string, fallbackTime: string): Parsed {
  const trimmed = value.trim();
  if (trimmed === "") {
    return { date: null, time: fallbackTime };
  }
  const [datePart, timePart] = trimmed.split("T");
  const date = parse(datePart, "yyyy-MM-dd", new Date(2000, 0, 1));
  return { date: Number.isNaN(date.getTime()) ? null : date, time: (timePart ?? "").slice(0, 5) || fallbackTime };
}

/** Compose a local date and "HH:mm" time back into the "yyyy-MM-ddThh:mm" a datetime-local input expects. */
function buildValue(date: Date, time: string): string {
  return `${format(date, "yyyy-MM-dd")}T${time}`;
}

/** Human summary for the trigger button ("Jul 15, 2026, 11:22 AM"). */
function describe({ date, time }: Parsed): string {
  if (date === null) {
    return "";
  }
  const [h, m] = time.split(":");
  const withTime = new Date(date.getFullYear(), date.getMonth(), date.getDate(), Number(h), Number(m));
  return format(withTime, "MMM d, yyyy, h:mm a");
}

const WEEKDAYS = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];
const DEFAULT_FROM_TIME = "00:00";
const DEFAULT_TO_TIME = "23:59";

interface MonthGridProps {
  month: Date;
  start: Date | null;
  end: Date | null;
  hovered: Date | null;
  onPick: (day: Date) => void;
  onHover: (day: Date | null) => void;
}

/** One month's 6x7 grid, painting the connected range fill and the two rounded endpoints. */
function MonthGrid({ month, start, end, hovered, onPick, onHover }: MonthGridProps) {
  const days = useMemo(() => {
    const gridStart = startOfWeek(startOfMonth(month), { weekStartsOn: 1 });
    const gridEnd = endOfWeek(endOfMonth(month), { weekStartsOn: 1 });
    return eachDayOfInterval({ start: gridStart, end: gridEnd });
  }, [month]);

  // While an end is being chosen, the hovered day previews the closing edge so the fill tracks the cursor.
  const effectiveEnd = end ?? (start !== null && hovered !== null && !isBefore(hovered, start) ? hovered : null);

  return (
    <div className="w-[15.5rem]">
      <div className="mb-1.5 text-center text-[13px] font-medium">{format(month, "MMMM yyyy")}</div>
      <div className="grid grid-cols-7">
        {WEEKDAYS.map((label) => (
          <div key={label} className="pb-1 text-center text-[11px] font-normal text-muted-foreground">{label}</div>
        ))}
        {days.map((day) => {
          const outside = !isSameMonth(day, month);
          const isStart = start !== null && isSameDay(day, start);
          const isEnd = effectiveEnd !== null && isSameDay(day, effectiveEnd);
          const isEndpoint = isStart || isEnd;
          const inRange = start !== null && effectiveEnd !== null
            && !isBefore(day, start) && !isAfter(day, effectiveEnd);
          const weekday = (day.getDay() + 6) % 7; // Monday index 0
          return (
            <div
              key={day.toISOString()}
              className={cn(
                "flex justify-center py-0.5",
                inRange && !isEndpoint && "bg-accent",
                inRange && isStart && "rounded-l-full bg-accent",
                inRange && isEnd && "rounded-r-full bg-accent",
                inRange && !isEndpoint && weekday === 0 && "rounded-l-full",
                inRange && !isEndpoint && weekday === 6 && "rounded-r-full",
              )}
            >
              <button
                type="button"
                onClick={() => onPick(day)}
                onMouseEnter={() => onHover(day)}
                className={cn(
                  "flex size-8 items-center justify-center rounded-full text-[13px] tabular-nums transition-colors",
                  outside ? "text-muted-foreground/50" : "text-foreground",
                  !isEndpoint && "hover:bg-primary/15",
                  isEndpoint && "bg-primary font-medium text-primary-foreground hover:bg-primary",
                )}
                data-selected={isEndpoint ? "true" : undefined}
              >
                {day.getDate()}
              </button>
            </div>
          );
        })}
      </div>
    </div>
  );
}

export interface DateRangeCalendarProps {
  /** Datetime-local strings ("yyyy-MM-ddThh:mm") or "". */
  from: string;
  to: string;
  onChange: (from: string, to: string) => void;
  disabled?: boolean;
  /** data-testid stem; the trigger, both time inputs, and Clear derive their ids from it. */
  testId?: string;
}

/**
 * A single-window range picker (two months side by side, click a start then an end, the days between marked)
 * over the same "yyyy-MM-ddThh:mm" datetime-local contract the two plain inputs used, so the surrounding form
 * and its submit path are unchanged. Per-edge time inputs keep the backfill window's hours selectable.
 */
export function DateRangeCalendar({ from, to, onChange, disabled, testId }: DateRangeCalendarProps) {
  const parsedFrom = parseValue(from, DEFAULT_FROM_TIME);
  const parsedTo = parseValue(to, DEFAULT_TO_TIME);
  const [open, setOpen] = useState(false);
  const [hovered, setHovered] = useState<Date | null>(null);
  const [viewMonth, setViewMonth] = useState(() => startOfMonth(parsedFrom.date ?? new Date()));

  const pick = (day: Date) => {
    const choosingStart = parsedFrom.date === null || parsedTo.date !== null || isBefore(day, parsedFrom.date);
    if (choosingStart) {
      // Start a fresh range: set the from edge (keeping its chosen time) and clear the to edge.
      onChange(buildValue(day, parsedFrom.time), "");
    } else {
      onChange(from, buildValue(day, parsedTo.time));
    }
  };

  const setFromTime = (time: string) => {
    if (parsedFrom.date !== null && time !== "") {
      onChange(buildValue(parsedFrom.date, time), to);
    }
  };
  const setToTime = (time: string) => {
    if (parsedTo.date !== null && time !== "") {
      onChange(from, buildValue(parsedTo.date, time));
    }
  };

  const summary = parsedFrom.date === null
    ? "Select a date range"
    : parsedTo.date === null
      ? `${describe(parsedFrom)}  →  ...`
      : `${describe(parsedFrom)}  →  ${describe(parsedTo)}`;

  return (
    <Popover open={open} onOpenChange={(next) => !disabled && setOpen(next)}>
      <PopoverTrigger asChild>
        <button
          type="button"
          disabled={disabled}
          data-testid={testId}
          className={cn(
            "flex h-8 w-full items-center gap-2 rounded-md border border-input bg-transparent px-3 text-[13px]",
            "shadow-xs outline-none transition-colors focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50",
            "disabled:cursor-not-allowed disabled:opacity-50",
            parsedFrom.date === null && "text-muted-foreground",
          )}
        >
          <CalendarRange className="size-4 shrink-0 text-muted-foreground" />
          <span className="truncate">{summary}</span>
        </button>
      </PopoverTrigger>
      <PopoverContent className="w-auto p-3" align="start" onMouseLeave={() => setHovered(null)}>
        <div className="mb-2 flex items-center justify-between">
          <Button
            type="button" variant="ghost" size="icon" className="size-7"
            onClick={() => setViewMonth((m) => subMonths(m, 1))}
            aria-label="Previous month"
          >
            <ChevronLeft className="size-4" />
          </Button>
          <Button
            type="button" variant="ghost" size="icon" className="size-7"
            onClick={() => setViewMonth((m) => addMonths(m, 1))}
            aria-label="Next month"
          >
            <ChevronRight className="size-4" />
          </Button>
        </div>
        <div className="flex gap-4">
          <MonthGrid
            month={viewMonth}
            start={parsedFrom.date} end={parsedTo.date} hovered={hovered}
            onPick={pick} onHover={setHovered}
          />
          <MonthGrid
            month={addMonths(viewMonth, 1)}
            start={parsedFrom.date} end={parsedTo.date} hovered={hovered}
            onPick={pick} onHover={setHovered}
          />
        </div>
        <div className="mt-3 flex items-end gap-3 border-t border-border pt-3">
          <div className="flex flex-1 flex-col gap-1">
            <Label className="text-xs font-normal text-muted-foreground">From time</Label>
            <Input
              type="time" className="h-8" value={parsedFrom.time}
              onChange={(e) => setFromTime(e.target.value)}
              disabled={parsedFrom.date === null}
              data-testid={testId ? `${testId}-from-time` : undefined}
            />
          </div>
          <div className="flex flex-1 flex-col gap-1">
            <Label className="text-xs font-normal text-muted-foreground">To time</Label>
            <Input
              type="time" className="h-8" value={parsedTo.time}
              onChange={(e) => setToTime(e.target.value)}
              disabled={parsedTo.date === null}
              data-testid={testId ? `${testId}-to-time` : undefined}
            />
          </div>
          <Button
            type="button" variant="ghost" size="sm" className="h-8"
            onClick={() => { onChange("", ""); setHovered(null); }}
            disabled={parsedFrom.date === null}
            data-testid={testId ? `${testId}-clear` : undefined}
          >
            Clear
          </Button>
        </div>
      </PopoverContent>
    </Popover>
  );
}
