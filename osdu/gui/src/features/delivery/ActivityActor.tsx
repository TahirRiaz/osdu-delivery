import { CalendarClock, CircleHelp, Server, Terminal, User, type LucideIcon } from "lucide-react";
import { TruncatedText } from "@/components/TruncatedText";

/** What started an activity, by the prefix of its actor: the icon, and what it is called on hover. */
const STARTERS: { prefix: string; icon: LucideIcon; what: string }[] = [
  { prefix: "schedule:", icon: CalendarClock, what: "A schedule" },
  { prefix: "cli:", icon: Terminal, what: "The command line on a workstation" },
  { prefix: "service:", icon: Server, what: "A service of the control plane" },
  { prefix: "manual:", icon: User, what: "Started by hand" },
  { prefix: "user:", icon: User, what: "A user" },
  { prefix: "gui:", icon: User, what: "A user" },
];

/**
 * An actor as the trail shows it: what started it as an icon, then its name; the whole actor on hover. In a narrow table
 * the icon stands alone and the name is left to the hover and to a screen reader. The name clips at `nameWidth`.
 */
export function Actor({ actor, nameWidth = 120 }: { actor: string; nameWidth?: number }) {
  const unknown = actor === "unknown";
  const starter = STARTERS.find((s) => actor.startsWith(s.prefix));
  const Icon = unknown ? CircleHelp : starter?.icon ?? User;
  const name = starter === undefined ? actor : actor.slice(starter.prefix.length);
  const hint = unknown ? "Recorded before the platform named who or what started a run." : `${starter?.what ?? "A user"}: ${actor}`;
  return (
    <span className="inline-flex max-w-full items-center gap-1.5" title={hint}>
      <Icon className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
      <span className="min-w-0 @max-3xl/table:sr-only">
        {unknown
          ? <span className="text-[12px] text-muted-foreground">unknown</span>
          : <TruncatedText text={name === "" ? actor : name} mono maxWidth={nameWidth} />}
      </span>
    </span>
  );
}
