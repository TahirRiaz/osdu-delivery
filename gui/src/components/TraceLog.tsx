import { useEffect, useMemo, useRef, useState } from "react";
import { CheckCircle2, CircleAlert, Copy, Eraser, Loader2, Radio } from "lucide-react";
import { cn } from "@/lib/utils";
import { parseUtc } from "../lib/time";

/** One line of a trace, in the terminal-log shape both the run trace and the activity (sync/lineage/...) trace
 * render through. Statement/SQL lines carry `sql`; a failing line carries `error`; `groupKey` draws a separator
 * between successive groups (a run's attempts, an activity's runs). */
export interface TraceLine {
  key: string;
  timestampUtc: string | null;
  /** The short step/kind tag shown in the fixed tag column. */
  tag: string;
  level: "trace" | "debug" | "info" | "warning" | "error";
  message: string;
  sql?: string | null;
  error?: string | null;
  groupKey?: string;
}

/** The tint of one line by its level (never by color alone: the tag column always names the level/step). */
function levelClass(level: TraceLine["level"]): string {
  if (level === "error") {
    return "text-destructive";
  }

  if (level === "warning") {
    return "text-warning";
  }

  return level === "info" ? "text-foreground" : "text-muted-foreground";
}

/** A compact UTC clock stamp (HH:mm:ss.fff); "-" for a line without a timestamp. */
function fmtTime(value: string | null): string {
  return value ? parseUtc(value).toISOString().slice(11, 23) : "-";
}

/** The live/complete/failed pill for a trace panel header. */
function StatusPill({ connected, ended, failed }: { connected: boolean; ended: boolean; failed: boolean }) {
  if (ended) {
    return (
      <span
        className={cn(
          "inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-0.5 text-[11px] font-medium leading-4",
          failed ? "bg-destructive/12 text-destructive" : "bg-success/12 text-success",
        )}
      >
        {failed ? <CircleAlert className="size-3 shrink-0" /> : <CheckCircle2 className="size-3 shrink-0" />}
        {failed ? "failed" : "complete"}
      </span>
    );
  }

  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-0.5 text-[11px] font-medium leading-4",
        connected ? "bg-success/12 text-success" : "bg-warning/15 text-warning",
      )}
    >
      {connected ? <Radio className="size-3 shrink-0" /> : <Loader2 className="size-3 shrink-0 animate-spin" />}
      {connected ? "live" : "reconnecting"}
    </span>
  );
}

/** A small ghost icon+label button for the panel header (copy, clear). */
function HeaderButton({
  icon: Icon, label, onClick, disabled, "data-testid": testId,
}: {
  icon: typeof Copy;
  label: string;
  onClick: () => void;
  disabled?: boolean;
  "data-testid"?: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      data-testid={testId}
      className="inline-flex items-center gap-1 rounded-sm px-1.5 py-0.5 text-[11px] text-muted-foreground hover:bg-muted hover:text-foreground disabled:pointer-events-none disabled:opacity-40"
    >
      <Icon className="size-3 shrink-0" />
      {label}
    </button>
  );
}

/**
 * The shared bottom-panel trace view: a terminal-style output log for a stream of {@link TraceLine}s, newest line
 * at the bottom, auto-scrolling while it streams. One look for every trace (a pipeline run, a repository sync, a
 * lineage computation): level-tinted lines with a fixed tag column, indented SQL/error blocks, a thin separator
 * between groups, and a header carrying Copy trace, Clear (console-style, hides current lines while the stream
 * keeps running), and a live/complete/failed pill. Copy is delegated to the owner (which decides what the whole
 * trace is); Clear is a view-only action.
 */
export function TraceLog({
  lines,
  connected,
  ended,
  failed,
  onCopy,
  emptyLive = "Waiting for the first line…",
  emptyEnded = "No trace to show.",
}: {
  lines: TraceLine[];
  connected: boolean;
  ended: boolean;
  failed: boolean;
  onCopy: () => void | Promise<void>;
  emptyLive?: string;
  emptyEnded?: string;
}) {
  const scrollRef = useRef<HTMLDivElement>(null);
  // Clear hides every line up to and including this key (console-style). New lines still arrive; a wholesale
  // replacement of the array (the live->at-rest swap) drops the watermark, showing everything again.
  const [hiddenUpTo, setHiddenUpTo] = useState<string | null>(null);
  const [copying, setCopying] = useState(false);

  const visible = useMemo(() => {
    if (hiddenUpTo === null) {
      return lines;
    }

    const cut = lines.findIndex((l) => l.key === hiddenUpTo);
    return cut === -1 ? lines : lines.slice(cut + 1);
  }, [lines, hiddenUpTo]);

  // Follow the tail as lines arrive (and on connect), the way a terminal does.
  useEffect(() => {
    const el = scrollRef.current;
    if (el) {
      el.scrollTop = el.scrollHeight;
    }
  }, [visible.length, ended]);

  const doCopy = async () => {
    setCopying(true);
    try {
      await onCopy();
    } finally {
      setCopying(false);
    }
  };

  return (
    <div className="flex h-full flex-col">
      <div className="flex h-7 shrink-0 items-center justify-between border-b border-border px-3">
        <span className="text-[11px] text-muted-foreground">
          {visible.length} line{visible.length === 1 ? "" : "s"}
        </span>
        <div className="flex items-center gap-1">
          <HeaderButton
            icon={copying ? Loader2 : Copy}
            label="Copy trace"
            onClick={doCopy}
            disabled={copying || lines.length === 0}
            data-testid="trace-copy"
          />
          <HeaderButton
            icon={Eraser}
            label="Clear"
            onClick={() => setHiddenUpTo(visible.length > 0 ? visible[visible.length - 1].key : hiddenUpTo)}
            disabled={visible.length === 0}
            data-testid="trace-clear"
          />
          <StatusPill connected={connected} ended={ended} failed={failed} />
        </div>
      </div>

      <div ref={scrollRef} className="min-h-0 flex-1 overflow-auto px-3 py-2 font-mono text-[12px] leading-5">
        {visible.length === 0 ? (
          <p className="text-muted-foreground">{ended ? emptyEnded : emptyLive}</p>
        ) : (
          visible.map((line, i) => {
            const newGroup = i > 0 && line.groupKey !== undefined && visible[i - 1].groupKey !== line.groupKey;
            return (
              <div key={line.key}>
                {newGroup && <div className="my-1 border-t border-dashed border-border/60" />}
                <div className="flex gap-2 whitespace-pre-wrap break-words">
                  <span className="shrink-0 text-muted-foreground tabular-nums">{fmtTime(line.timestampUtc)}</span>
                  <span className="w-[88px] shrink-0 truncate uppercase text-muted-foreground" title={line.tag}>
                    {line.tag}
                  </span>
                  <span className={cn("min-w-0 flex-1", levelClass(line.level))}>{line.message}</span>
                </div>
                {line.sql ? (
                  <pre className="mt-0.5 ml-[calc(88px+1rem)] overflow-x-auto whitespace-pre rounded-sm bg-muted/40 px-2 py-1 text-muted-foreground">
                    {line.sql.trimEnd()}
                  </pre>
                ) : null}
                {line.error ? (
                  <div className="ml-[calc(88px+1rem)] text-destructive">!! {line.error}</div>
                ) : null}
              </div>
            );
          })
        )}
      </div>
    </div>
  );
}
