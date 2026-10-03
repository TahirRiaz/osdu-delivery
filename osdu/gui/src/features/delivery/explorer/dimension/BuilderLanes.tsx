import { useState, type ReactNode } from "react";
import { CircleAlert, Loader2, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import type { DimensionDraft, DimensionSample, DimensionSampleTrail } from "../../../../api/explorer";
import { BuilderRecordTree, type RowMark, type TreeOffer } from "./BuilderRecordTree";
import {
  MAX_ATTRIBUTES, MAX_STEPS, entityTypeOf, isReadable, lastProperty, namesRecords, recordName, trailKey, typeName, usesAt, type PropertyPick,
} from "./dimensionDraft";

/**
 * The records a dimension reads, laid out left to right as a build reads them: the records of its kind, then the record
 * each key names, then the records those name, a lane a step. Each card holds the records of one trail as the search holds
 * them, with every property a person can make into the dimension's key, value or an attribute, or follow to the records it
 * names; what the dimension reads already is marked where it is read.
 */
export function BuilderLanes({ draft, sample, sampling, trails, opened, onPick, onClose }: {
  draft: DimensionDraft;
  sample: DimensionSample | undefined;
  sampling: boolean;
  trails: string[][];
  opened: string[][];
  onPick: (pick: PropertyPick) => void;
  onClose: (trail: string[]) => void;
}) {
  const byTrail = new Map((sample?.trails ?? []).map((trail) => [trailKey(trail.steps), trail]));
  const lanes = Array.from({ length: MAX_STEPS }, (_, depth) => trails.filter((trail) => trail.length === depth));
  const keyed = draft.path !== null && draft.path !== "";
  const own = sample?.trails.find((trail) => trail.steps.length === 0);

  return (
    <div className="flex min-h-0 min-w-0 flex-1 gap-3 overflow-x-auto p-3" data-testid="builder-lanes">
      <Lane number={1} title="Searched" sub={keyed ? "for every distinct key" : "pick the key"}>
        <SourceCard draft={draft} sample={sample} sampling={sampling} onPick={onPick} />
      </Lane>
      {lanes.map((lane, depth) => {
        if (depth > 0 && lane.length === 0) {
          return null;
        }

        return (
          <Lane
            key={depth}
            number={depth + 2}
            title="Found by id"
            sub={depth === 0 ? "the record each key names" : depth === 1 ? "the records those name" : "and the records they name"}
          >
            {depth === 0 && !keyed && (
              <Placeholder testId="builder-lane-no-key">
                Pick the key first: open the menu of a property of the record on the left and choose <span className="font-medium text-foreground">Key</span>.
                A key holding record ids (a wellbore's id) names a record, whose properties then give each key its value and attributes.
              </Placeholder>
            )}
            {depth === 0 && keyed && own?.problem !== null && own?.problem !== undefined && own.records.length === 0 && (
              <Placeholder testId="builder-lane-no-record">{own.problem}</Placeholder>
            )}
            {keyed && !(depth === 0 && own?.problem !== null && own?.problem !== undefined && own.records.length === 0) && lane
              .map((trail) => (
                <TrailCard
                  key={trailKey(trail)}
                  draft={draft}
                  trail={trail}
                  found={byTrail.get(trailKey(trail))}
                  sampling={sampling}
                  closable={opened.some((o) => trailKey(o) === trailKey(trail))}
                  onPick={onPick}
                  onClose={() => onClose(trail)}
                />
              ))}
          </Lane>
        );
      })}
    </div>
  );
}

function Lane({ number, title, sub, children }: { number: number; title: string; sub: string; children: ReactNode }) {
  return (
    <section className="flex min-h-0 min-w-[270px] max-w-[400px] flex-1 flex-col gap-2" data-testid="builder-lane">
      <header className="flex items-start gap-2 px-1">
        <span className="mt-0.5 flex size-5 shrink-0 items-center justify-center rounded-full border border-border text-[11px] tabular-nums text-muted-foreground">{number}</span>
        <div className="min-w-0">
          <h3 className="text-[13px] font-semibold leading-5">{title}</h3>
          <p className="text-[12px] leading-4 text-muted-foreground">{sub}</p>
        </div>
      </header>
      <div className="flex min-h-0 flex-1 flex-col gap-2">{children}</div>
    </section>
  );
}

function Placeholder({ children, testId }: { children: ReactNode; testId: string }) {
  return (
    <p className="rounded-lg border border-dashed border-border px-3 py-4 text-[12.5px] leading-5 text-muted-foreground" data-testid={testId}>
      {children}
    </p>
  );
}

function Card({ title, sub, aside, children, testId }: { title: ReactNode; sub?: ReactNode; aside?: ReactNode; children: ReactNode; testId: string }) {
  return (
    <div className="flex min-h-[220px] flex-1 flex-col overflow-hidden rounded-lg border border-border bg-card" data-testid={testId}>
      <header className="flex min-w-0 items-start gap-2 border-b border-border/60 px-3 py-2">
        <div className="min-w-0 flex-1">
          <div className="truncate text-[13px] font-semibold">{title}</div>
          {sub !== undefined && <div className="truncate text-[11.5px] text-muted-foreground">{sub}</div>}
        </div>
        {aside}
      </header>
      {children}
    </div>
  );
}

/** The dimension's own records: one at a time, its key and the attribute it collects picked from it. */
function SourceCard({ draft, sample, sampling, onPick }: {
  draft: DimensionDraft;
  sample: DimensionSample | undefined;
  sampling: boolean;
  onPick: (pick: PropertyPick) => void;
}) {
  const record = sample?.record ?? null;
  const entity = record?.kind?.split(":")[2] ?? draft.kind.split(":")[2] ?? draft.kind;
  const collected = draft.attributes.find((a) => a.collect !== null) ?? null;
  const offer: TreeOffer = {
    actions: (location, value) => {
      if (typeof location[location.length - 1] === "number") {
        return [];
      }

      const readable = isReadable(value);
      const why = readable ? null : "Only a text, a number or a boolean, or a list of them, is a value a dimension holds.";
      return [
        { action: "key", label: "Key: each distinct value", disabled: why },
        { action: "collect", label: collected === null ? "Collect into an attribute" : `Collect instead of ${collected.collect}`, disabled: why },
      ];
    },
    marks: (path) => {
      const marks: RowMark[] = [];
      if (path === draft.path) {
        marks.push({ role: "key", name: "key" });
      }

      for (const attribute of draft.attributes) {
        if (attribute.collect === path) {
          marks.push({ role: "collect", name: attribute.name });
        }
      }

      return marks;
    },
  };

  return (
    <Card
      testId="builder-source"
      title={<span>{typeName(entity)} <span className="font-normal text-muted-foreground">{draft.kind.split(":")[3] ?? ""}</span></span>}
      sub={sample === undefined ? "reading"
        : draft.path !== null && draft.path !== "" ? `${sample.total.toLocaleString("en-US")} ${sample.total === 1 ? "record holds" : "records hold"} the example key`
        : `${sample.total.toLocaleString("en-US")} records${draft.query ? " the query narrows to" : ""}`}
      aside={sampling ? <Loader2 className="size-3.5 animate-spin text-muted-foreground" aria-label="Reading" /> : undefined}
    >
      {sample?.refusal !== null && sample?.refusal !== undefined && (
        <Problem testId="builder-source-refused">The search service refused the records: {sample.refusal}</Problem>
      )}
      {sample !== undefined && sample.refusal === null && record === null && <Problem testId="builder-source-empty">No record of this kind{draft.query ? " matches the query" : ""} in the partition.</Problem>}
      {record !== null && (
        <BuilderRecordTree
          key={record.id}
          record={record.record}
          cut={record.cut}
          offer={offer}
          onPick={(action, location) => onPick({ action, trail: null, location, records: [record], chosen: 0 })}
          testId="builder-source-tree"
        />
      )}
    </Card>
  );
}

/** The records one trail reaches: a record at a time, with what can be read from them, or followed further. */
function TrailCard({ draft, trail, found, sampling, closable, onPick, onClose }: {
  draft: DimensionDraft;
  trail: string[];
  found: DimensionSampleTrail | undefined;
  sampling: boolean;
  closable: boolean;
  onPick: (pick: PropertyPick) => void;
  onClose: () => void;
}) {
  const [shownAt, setShownAt] = useState(0);
  const records = found?.records ?? [];
  const at = Math.min(shownAt, Math.max(0, records.length - 1));
  const record = records[at] ?? null;
  const types = [...new Set(records.map((r) => typeName(entityTypeOf(r.id))))];
  const follows = trail.length < MAX_STEPS - 1;
  const offer: TreeOffer = {
    actions: (location, value) => {
      if (typeof location[location.length - 1] === "number") {
        return [];
      }

      const readable = isReadable(value);
      const why = readable ? null : "Only a text, a number or a boolean, or a list of them, is a value a dimension keeps.";
      const actions: ReturnType<TreeOffer["actions"]> = [
        { action: "value", label: draft.label.length > 0 ? "Value of each key, instead" : "Value of each key", disabled: why },
        {
          action: "attribute",
          label: "Attribute of each key",
          disabled: why ?? (draft.attributes.length >= MAX_ATTRIBUTES ? `A dimension reads at most ${MAX_ATTRIBUTES} attributes.` : null),
        },
      ];
      if (namesRecords(value)) {
        actions.push({
          action: "follow",
          label: "Follow to the records it names",
          disabled: follows ? null : `A value or an attribute reads through at most ${MAX_STEPS} records; this is the last.`,
        });
      }

      return actions;
    },
    marks: (path) => usesAt(draft, trail, path).map((use): RowMark => use.last
      ? { role: use.chain.role === "label" ? "label" : "attribute", name: use.chain.role === "label" ? "value" : use.chain.name }
      : { role: "step", name: use.chain.role === "label" ? "value" : use.chain.name }),
  };

  return (
    <Card
      testId="builder-trail"
      title={types.length === 0 ? (sampling ? "Reading" : "No record") : types.join(", ")}
      sub={trail.length === 0 ? "named by each key" : (
        <RichTooltip title="Named by" body={trail.join("  >  ")} mono>
          <span>named by {lastProperty(trail[trail.length - 1])}</span>
        </RichTooltip>
      )}
      aside={(
        <span className="flex items-center gap-1">
          {sampling && <Loader2 className="size-3.5 animate-spin text-muted-foreground" aria-label="Reading" />}
          {closable && (
            <Button variant="ghost" size="icon-xs" onClick={onClose} aria-label="Close these records" data-testid="builder-trail-close">
              <X />
            </Button>
          )}
        </span>
      )}
    >
      {found?.problem !== null && found?.problem !== undefined && <Problem testId="builder-trail-problem">{found.problem}</Problem>}
      {records.length > 1 && (
        <div className="flex flex-wrap gap-1 border-b border-border/60 px-2 py-1.5" data-testid="builder-trail-records">
          {records.map((r, index) => (
            <button
              key={r.id}
              type="button"
              onClick={() => setShownAt(index)}
              className={cn(
                "max-w-full truncate rounded border px-1.5 py-0.5 text-[11.5px]",
                index === at ? "border-primary/60 bg-primary/10 text-foreground" : "border-border text-muted-foreground hover:text-foreground",
              )}
              title={r.id}
            >
              {recordName(r)}
            </button>
          ))}
        </div>
      )}
      {record !== null && (
        <BuilderRecordTree
          key={record.id}
          record={record.record}
          cut={record.cut}
          offer={offer}
          onPick={(action, location) => onPick({ action, trail, location, records, chosen: at })}
          testId="builder-trail-tree"
        />
      )}
    </Card>
  );
}

function Problem({ children, testId }: { children: ReactNode; testId: string }) {
  return (
    <p className="flex items-start gap-1.5 border-b border-border/60 px-3 py-2 text-[12px] text-muted-foreground" data-testid={testId}>
      <CircleAlert className="mt-0.5 size-3.5 shrink-0 text-warning" aria-hidden />
      <span className="min-w-0 break-words">{children}</span>
    </p>
  );
}
