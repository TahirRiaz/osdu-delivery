import { CopyButton } from "@/components/CopyButton";
import { RichTooltip } from "@/components/RichTooltip";
import { useClipped } from "@/components/useClipped";
import { cn } from "@/lib/utils";
import { idParts } from "./osduRecordModel";

/**
 * An OSDU record named the way a reader knows it: the unique part of its id in the mono face, its type before it in
 * a whisper, and nothing of the partition and group that every id of the flow repeats. The unique part clips at
 * whatever width it is given, so a chip, a crumb or a table cell stays one line whatever the estate mints as ids. The
 * whole id (and the kind, when known) is one hover away, and the copy beside it hands the id over verbatim. The
 * element carries the full id as data, so a test or a script reads it without depending on what is rendered.
 */
export function RecordName({ id, kind, className, copy = false, testId, copyTestId }: {
  id: string;
  kind?: string | null;
  /** Classes for the name (size, weight, a width cap). */
  className?: string;
  /** Adds the icon copy that yields the full id. */
  copy?: boolean;
  testId?: string;
  copyTestId?: string;
}) {
  const parts = idParts(id);
  const [uniqueRef, clipped] = useClipped(parts.unique);
  const name = (
    <span className={cn("inline-flex min-w-0 max-w-full items-baseline gap-1", className)} data-testid={testId} data-value={id}>
      {parts.type !== "" && <span className="shrink-0 text-[11px] font-sans font-normal text-muted-foreground">{parts.type}</span>}
      <span ref={uniqueRef} className="min-w-0 truncate font-mono">{parts.unique}</span>
    </span>
  );
  // The panel appears whenever something of the id is not on screen: its head, which the name never shows, or the
  // tail the width clipped. An id with no head (no partition, no group) and room to spare needs none.
  const shown = parts.partition !== "" || clipped
    ? <RichTooltip body={kind ? `${id}\n${kind}` : id} title="OSDU id" mono>{name}</RichTooltip>
    : name;
  if (!copy) {
    return shown;
  }

  return (
    <span className="inline-flex min-w-0 max-w-full items-center gap-0.5">
      {shown}
      <CopyButton iconOnly label="Copy the OSDU id" text={id} testId={copyTestId ?? "copy-osdu-id"} />
    </span>
  );
}
