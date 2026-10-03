import type { TraceLine } from "@/components/TraceLog";

/**
 * One event line of a captured run log as SQLFlow's `RunLogger.Format` writes it: the UTC stamp with its `Z`, the level
 * padded to five, the step padded to twenty-two, then the message.
 */
const EVENT = /^(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2}\.\d{3})Z (INFO|DEBUG|TRACE) +(\S+) ?(.*)$/;

/** The prefix `RunLogger.Format` puts on each further line of a multi-line message. */
const CONTINUATION = /^ {4}\| ?/;

const LEVELS: Record<string, TraceLine["level"]> = { INFO: "info", DEBUG: "debug", TRACE: "trace" };

/**
 * A captured run log as the lines of a trace: each event with its time, level and step, and the further lines of a
 * multi-line message joined back under it. The step's padding is dropped, since the trace view gives the step a column of
 * its own; the message's own spacing is kept, so a submission plan's columns still line up when a line is opened whole.
 *
 * A line in no event's shape (the end of a log the ledger cut at its length cap, text written by something other than
 * the run logger) continues the event above it, or stands as a line of its own at the top, so no text of the log is lost.
 */
export function runLogLines(log: string): TraceLine[] {
  const lines: TraceLine[] = [];
  for (const raw of log.replace(/\r\n?/g, "\n").split("\n")) {
    const event = EVENT.exec(raw);
    if (event !== null) {
      lines.push({
        key: `${lines.length}`,
        timestampUtc: `${event[1]}T${event[2]}Z`,
        tag: event[4],
        level: LEVELS[event[3]],
        message: event[5].replace(/^ +/, ""),
      });
      continue;
    }

    const last = lines.at(-1);
    if (last !== undefined) {
      last.message = `${last.message}\n${raw.replace(CONTINUATION, "")}`;
    } else if (raw.trim() !== "") {
      lines.push({ key: `${lines.length}`, timestampUtc: null, tag: "", level: "info", message: raw });
    }
  }

  // The log ends with a line break, which leaves an empty continuation on the last event.
  for (const line of lines) {
    line.message = line.message.replace(/\n+$/, "");
  }

  return lines;
}
