import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronRight, CircleHelp, Info, RefreshCw, TriangleAlert, Waypoints } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Progress } from "@/components/ui/progress";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { CopyButton } from "@/components/CopyButton";
import { EmptyState } from "@/components/EmptyState";
import { IconAction } from "@/components/IconAction";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { SearchInput } from "@/components/SearchInput";
import { cn } from "@/lib/utils";
import {
  explorerApi, type ExplorerReferencePlace, type ExplorerReferences, type ExplorerReferringType, type ExplorerSearchRequest, type ExplorerTypes,
} from "../../../api/explorer";
import { ExplorerProblem } from "./ExplorerProblem";
import { GroupGlyph } from "./ExplorerGlyphs";
import { counted, kindParts, useExplorerRead, type ExplorerScope } from "./explorerModel";

// The explorer's Referenced by (osdu/docs/reference/concepts/explorer.md): the types whose records name records of a
// type, as the partition's schemas declare it, with the versions that do and those that do not, and where. The control
// plane reads every schema of the partition once and keeps what each names; while it reads, the answer says where it
// stands, and this view asks again until it is done.

/** The search the type rail counts a whole partition's kinds by: the same question, so the same answer is reused. */
const WHOLE_PARTITION: ExplorerSearchRequest = { text: undefined, lucene: false, filters: [] };

/** How often a reading under way is asked about; the control plane holds each ask a few seconds while it reads. */
const READING_POLL_MS = 750;

const compact = new Intl.NumberFormat("en-US", { notation: "compact", maximumFractionDigits: 1 });

/** A record count as a list says it: exact up to a hundred thousand, compact past it. */
function records(count: number): string {
  return `${count >= 100_000 ? compact.format(count) : count.toLocaleString("en-US")} record${count === 1 ? "" : "s"}`;
}

/** The status a schema is in, as a chip says it; a published one says nothing. */
const STATUS_WORDS: Record<string, string> = { DEVELOPMENT: "dev", OBSOLETE: "obsolete" };

const WHY_READ = "The Schema service cannot be asked which schemas refer to a type: it lists schemas without their content. "
  + "So the control plane reads the schema of every kind the partition holds once, with the schemas it is made of, and keeps what each one names. "
  + "Every later ask answers at once; reading again fetches only the schemas added since and those in development.";

const WHAT_COUNTS = "A property counts when the schema marks it with x-osdu-relationship naming the type, wherever it sits: in data, in meta, "
  + "inside a list, inside an abstract schema the kind is made of, or in one form of a choice. Where a schema marks no relationship, "
  + "an id pattern naming the type counts too, marked by pattern. A reference names a type, never a version of it, so it holds for every version of the type.";

/** One version of a referring type, whether it names the type or not. */
interface VersionView {
  kind: string;
  version: string;
  status: string;
  authority: string;
  source: string;
  names: boolean;
  partial: boolean;
  places: ExplorerReferencePlace[];
  morePlaces: number;
  /** The records of the kind the partition holds; null where the search service did not count it. */
  records: number | null;
}

/** A place a referring type's records name the type, with the versions that hold it. */
interface PlaceView {
  at: string;
  byPattern: boolean;
  versions: VersionView[];
}

/** A referring type as the list shows it. */
interface TypeView {
  entityType: string;
  group: string;
  type: string;
  versions: VersionView[];
  places: PlaceView[];
  /** The records of its referring versions; null where any of them was not counted. */
  records: number | null;
  /** Whether its versions span more than one authority and source, so each chip names its own. */
  mixed: boolean;
}

function versionKey(version: string): string {
  return version.split(".").map((part) => (/^\d+$/.test(part) ? part.padStart(12, "0") : part)).join(".");
}

function byKind(a: VersionView, b: VersionView): number {
  return a.authority.toLowerCase().localeCompare(b.authority.toLowerCase())
    || a.source.toLowerCase().localeCompare(b.source.toLowerCase())
    || versionKey(a.version).localeCompare(versionKey(b.version))
    || a.kind.localeCompare(b.kind);
}

/** A referring type laid out for the list: every version in order, and each place with the versions holding it. */
function typeView(type: ExplorerReferringType, count: (kind: string) => number | null): TypeView {
  const parts = kindParts(`*:*:${type.entityType}:*`);
  const version = (kind: string, rest: Omit<VersionView, "kind" | "authority" | "source" | "records">): VersionView => {
    const of = kindParts(kind);
    return { kind, authority: of.authority, source: of.source, records: count(kind), ...rest };
  };
  const versions = [
    ...type.kinds.map((k) => version(k.kind, { version: k.version, status: k.status, names: true, partial: k.partial, places: k.places, morePlaces: k.morePlaces })),
    ...type.without.map((k) => version(k.kind, { version: k.version, status: k.status, names: false, partial: k.partial, places: [], morePlaces: 0 })),
  ].sort(byKind);
  const places = new Map<string, PlaceView>();
  for (const version of versions) {
    for (const place of version.places) {
      const key = `${place.at}|${place.byPattern}`;
      const held = places.get(key) ?? { at: place.at, byPattern: place.byPattern, versions: [] };
      held.versions.push(version);
      places.set(key, held);
    }
  }

  const naming = versions.filter((v) => v.names);
  return {
    entityType: type.entityType,
    group: parts.group,
    type: parts.type,
    versions,
    places: [...places.values()],
    records: naming.some((v) => v.records === null) ? null : naming.reduce((sum, v) => sum + (v.records ?? 0), 0),
    mixed: new Set(versions.map((v) => `${v.authority}:${v.source}`)).size > 1,
  };
}

/** Whether a type, or one of its places or kinds, holds what the filter asks for. */
function matches(type: TypeView, filter: string): boolean {
  if (filter === "") {
    return true;
  }

  const asked = filter.toLowerCase();
  return type.entityType.toLowerCase().includes(asked)
    || type.places.some((p) => p.at.toLowerCase().includes(asked))
    || type.versions.some((v) => v.kind.toLowerCase().includes(asked));
}

/** A type narrowed to its versions holding records, or null when none of those naming the type does. */
function inUse(type: TypeView): TypeView | null {
  const versions = type.versions.filter((v) => (v.records ?? 0) > 0);
  if (!versions.some((v) => v.names)) {
    return null;
  }

  const kept = new Set(versions.map((v) => v.kind));
  return {
    ...type,
    versions,
    places: type.places.map((p) => ({ ...p, versions: p.versions.filter((v) => kept.has(v.kind)) })).filter((p) => p.versions.length > 0),
    mixed: new Set(versions.map((v) => `${v.authority}:${v.source}`)).size > 1,
  };
}

/** The label of a version on its chip: the version, with its authority and source where the type's versions span several. */
function versionLabel(version: VersionView, mixed: boolean): string {
  return mixed ? `${version.authority}:${version.source} ${version.version}` : version.version;
}

/**
 * The versions holding a place, as a reader says them: in the order the type's versions stand, a run of three or more in
 * a row as "first to last", each authority and source named once where the type's versions span several
 * ("osdu:wks 1.1.0 to 1.3.0; eq:custom 1.0.0").
 */
function versionsSaid(held: VersionView[], all: VersionView[], mixed: boolean): string {
  const holding = new Set(held.map((v) => v.kind));
  const said: string[] = [];
  for (const origin of [...new Set(all.map((v) => `${v.authority}:${v.source}`))]) {
    const runs: VersionView[][] = [];
    let run: VersionView[] = [];
    for (const version of all.filter((v) => `${v.authority}:${v.source}` === origin)) {
      if (holding.has(version.kind)) {
        run.push(version);
      } else if (run.length > 0) {
        runs.push(run);
        run = [];
      }
    }

    if (run.length > 0) {
      runs.push(run);
    }

    if (runs.length > 0) {
      const versions = runs.map((r) => (r.length >= 3 ? `${r[0].version} to ${r[r.length - 1].version}` : r.map((v) => v.version).join(", "))).join(", ");
      said.push(mixed ? `${origin} ${versions}` : versions);
    }
  }

  return said.join("; ");
}

/** How many referring types a list draws at first, and how many more each time its end scrolls into view. */
const TYPES_PAGE = 60;

/** The end of a list drawn a page at a time: draws the next page as it scrolls into view, or when clicked. */
function MoreOnScroll({ left, onMore }: { left: number; onMore: () => void }) {
  const end = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const at = end.current;
    if (at === null) {
      return undefined;
    }

    const watcher = new IntersectionObserver((seen) => {
      if (seen.some((entry) => entry.isIntersecting)) {
        onMore();
      }
    }, { rootMargin: "320px" });
    watcher.observe(at);
    return () => watcher.disconnect();
  }, [onMore]);
  return (
    <div ref={end} className="flex justify-center py-1">
      <Button variant="ghost" size="sm" className="h-7 text-[12px] text-muted-foreground" onClick={onMore} data-testid="explorer-references-more">
        {`Show ${Math.min(left, TYPES_PAGE).toLocaleString("en-US")} more of ${counted(left, "type")}`}
      </Button>
    </div>
  );
}

/**
 * One version of a referring type as a chip: solid where its schema names the type, dashed where it does not. It opens
 * the records of that kind; its tooltip says what it names and how many records the partition holds of it.
 */
function VersionChip({ version, mixed, typeLabel, onOpen }: { version: VersionView; mixed: boolean; typeLabel: string; onOpen: () => void }) {
  const status = STATUS_WORDS[version.status];
  const said = [
    version.kind,
    version.names
      ? `Names ${typeLabel} at:\n${version.places.map((p) => `  ${p.at}${p.byPattern ? " (by pattern)" : ""}`).join("\n")}${version.morePlaces > 0 ? `\n  and ${version.morePlaces} more` : ""}`
      : `Does not name ${typeLabel}.`,
    version.status === "PUBLISHED" ? null : `Its schema is ${version.status.toLowerCase()}.`,
    version.partial ? "Its schema refers to a schema that could not be followed, so a place behind it is not seen." : null,
    version.records === null ? "Records: not counted by the search service." : records(version.records),
    "Click to browse its records.",
  ].filter((line) => line !== null).join("\n");
  return (
    <RichTooltip body={said} title={version.names ? "Names it" : "Does not name it"}>
      <button
        type="button"
        onClick={onOpen}
        aria-label={`${version.kind}: ${version.names ? "names" : "does not name"} ${typeLabel}`}
        className={cn(
          "inline-flex h-6 items-center gap-1 rounded-md border px-1.5 font-mono text-[11px] tabular-nums transition-colors hover:bg-accent",
          version.names ? "border-border bg-background text-foreground" : "border-dashed border-muted-foreground/50 text-muted-foreground",
        )}
        data-testid="explorer-references-version"
        data-names={version.names}
      >
        {versionLabel(version, mixed)}
        {status !== undefined && <span className="font-sans text-[10px] text-muted-foreground">{status}</span>}
        {version.partial && <CircleHelp className="size-3 text-muted-foreground" aria-hidden />}
      </button>
    </RichTooltip>
  );
}

/** One referring type: its name and records, every version of it, and where its records name the type. */
function TypeRow({ type, typeLabel, onScope }: { type: TypeView; typeLabel: string; onScope: (scope: ExplorerScope) => void }) {
  const naming = type.versions.filter((v) => v.names);
  return (
    <li className="flex flex-col gap-1 border-b px-3 py-2 last:border-b-0" data-testid="explorer-references-type" data-type={type.entityType}>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
        <button
          type="button"
          className="min-w-0 truncate text-left text-[13px] font-medium hover:underline"
          title={`Browse the records of ${type.entityType}`}
          onClick={() => onScope({ level: "type", entityType: type.entityType })}
          data-testid="explorer-references-type-open"
        >
          {type.type}
        </button>
        {type.records !== null && (
          <span className={cn("font-mono text-[11px] tabular-nums", type.records === 0 ? "text-muted-foreground/70" : "text-muted-foreground")} title={`${type.records.toLocaleString("en-US")} records of the versions naming ${typeLabel}`}>
            {records(type.records)}
          </span>
        )}
        <span className="ml-auto flex flex-wrap items-center justify-end gap-1">
          {type.versions.map((version) => (
            <VersionChip key={version.kind} version={version} mixed={type.mixed} typeLabel={typeLabel} onOpen={() => onScope({ level: "kind", kind: version.kind })} />
          ))}
        </span>
      </div>
      <ul className="flex flex-col gap-0.5 pl-3">
        {type.places.map((place) => (
          <li key={`${place.at}|${place.byPattern}`} className="group/place flex min-w-0 items-center gap-2 text-[12px]" data-testid="explorer-references-place">
            <span className="min-w-0 truncate font-mono text-[11.5px]" title={place.at}>{place.at}</span>
            {place.byPattern && (
              <RichTooltip title="Read from the pattern" body={`The schema marks no x-osdu-relationship here. ${typeLabel} is read from the pattern an id here must match, so it counts as a reference.`}>
                <span className="shrink-0 rounded border px-1 text-[10px] text-muted-foreground" data-testid="explorer-references-by-pattern">by pattern</span>
              </RichTooltip>
            )}
            {place.versions.length < naming.length && (
              <span className="min-w-0 truncate text-[11px] text-muted-foreground" data-testid="explorer-references-place-versions">
                {`in ${versionsSaid(place.versions, type.versions, type.mixed)}`}
              </span>
            )}
            <span className="opacity-0 transition-opacity group-hover/place:opacity-100 focus-within:opacity-100">
              <CopyButton iconOnly label="Copy the property path" text={place.at} testId="explorer-references-copy-place" />
            </span>
          </li>
        ))}
      </ul>
    </li>
  );
}

/**
 * The referring types, a section per group (master data first), each group under its glyph. A long list (a unit of measure
 * is named by nearly every kind) is drawn a page at a time as it is scrolled; a list given again starts from its first page.
 */
function TypeList({ types, typeLabel, onScope, testId }: { types: TypeView[]; typeLabel: string; onScope: (scope: ExplorerScope) => void; testId: string }) {
  const [drawn, setDrawn] = useState(TYPES_PAGE);
  const more = useCallback(() => setDrawn((count) => count + TYPES_PAGE), []);
  const groups: { group: string; types: TypeView[] }[] = [];
  for (const type of types.slice(0, drawn)) {
    const last = groups.at(-1);
    if (last?.group === type.group) {
      last.types.push(type);
    } else {
      groups.push({ group: type.group, types: [type] });
    }
  }

  return (
    <div className="flex flex-col gap-3" data-testid={testId}>
      {groups.map((group) => (
        <section key={group.group} className="rounded-md border">
          <header className="flex items-center gap-2 border-b bg-muted/30 px-3 py-1.5 text-[12px]">
            <GroupGlyph group={group.group} className="size-3.5" />
            <span className="font-medium">{group.group}</span>
            <span className="text-muted-foreground">{counted(group.types.length, "type")}</span>
          </header>
          <ul>
            {group.types.map((type) => <TypeRow key={type.entityType} type={type} typeLabel={typeLabel} onScope={onScope} />)}
          </ul>
        </section>
      ))}
      {types.length > drawn && <MoreOnScroll left={types.length - drawn} onMore={more} />}
    </div>
  );
}

/** Where the reading of the partition's schemas stands, and why it is read at all. */
function ReadingProgress({ answer }: { answer: ExplorerReferences }) {
  const progress = answer.progress;
  const again = answer.readUtc !== null;
  const label = progress === null || progress.listing
    ? `${again ? "Bringing the partition's schemas up to date" : "Reading the partition's schemas"}: listing them${progress !== null && progress.listed > 0 ? ` (${progress.listed.toLocaleString("en-US")} so far)` : ""}`
    : `${again ? "Bringing the partition's schemas up to date" : "Reading the partition's schemas"}: ${progress.read.toLocaleString("en-US")} of ${counted(progress.toRead, "kind")}${progress.failed > 0 ? `, ${progress.failed.toLocaleString("en-US")} not read` : ""}`;
  const value = progress === null || progress.listing || progress.toRead === 0 ? 3 : Math.max(3, Math.round(((progress.read + progress.failed) / progress.toRead) * 100));
  return (
    <div className="flex flex-col gap-1.5" data-testid="explorer-references-progress">
      <div className="flex items-center gap-1.5 text-[12px] text-muted-foreground">
        <span aria-live="polite">{label}</span>
        <RichTooltip title={again ? "Bringing them up to date" : "Read once, then kept"} body={WHY_READ}>
          <Info className="size-3.5" aria-label="Why the schemas are read" />
        </RichTooltip>
      </div>
      <Progress value={value} className="h-1.5" aria-label={label} />
    </div>
  );
}

/**
 * Referenced by: the types whose records name records of `entityType`, read from the partition's schemas, in a dialog
 * over the explorer. A type opens its records, a version the records of that kind.
 */
export function ExplorerReferencesDialog({ partition, entityType, open, onOpenChange, onScope }: {
  partition: string | null;
  /** The type asked about, `group--Type`. */
  entityType: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Opens a place in the explorer; the dialog closes first. */
  onScope: (scope: ExplorerScope) => void;
}) {
  const typeLabel = kindParts(`*:*:${entityType}:*`).type;
  const [filter, setFilter] = useState("");
  const [onlyInUse, setOnlyInUse] = useState(false);
  // The next ask reads the partition's schemas again; it is taken by that ask alone.
  const refreshNext = useRef(false);
  const read = useQuery({
    queryKey: ["explorer", "referenced-by", partition, entityType],
    queryFn: () => {
      const refresh = refreshNext.current;
      refreshNext.current = false;
      return explorerApi.referencedBy(partition, { type: entityType, refresh });
    },
    enabled: open,
    staleTime: 60_000,
    gcTime: 10 * 60_000,
    retry: false,
    refetchOnWindowFocus: false,
    refetchInterval: (query) => (query.state.data?.answer.state === "reading" ? READING_POLL_MS : false),
  });
  const counts = useExplorerRead<ExplorerTypes>(["types", partition, WHOLE_PARTITION], open ? () => explorerApi.types(partition, WHOLE_PARTITION) : null, 10 * 60_000);
  const answer = read.data?.answer;

  // The search service counts the records of each kind; where it named fewer kinds than there are, a kind it did not name
  // is not counted rather than empty.
  const tally = counts.data?.answer;
  const complete = tally !== undefined && !tally.refusal && tally.listed >= tally.total;
  const count = useMemo(() => {
    const byKind = new Map((tally?.kinds ?? []).map((k) => [k.kind, k.count]));
    return (kind: string): number | null => byKind.get(kind) ?? (complete ? 0 : null);
  }, [tally, complete]);

  const views = useMemo(() => (answer?.types ?? []).map((t) => typeView(t, count)), [answer, count]);
  const anyViews = useMemo(() => (answer?.anyOfGroup ?? []).map((t) => typeView(t, count)), [answer, count]);
  const narrow = (types: TypeView[]) => types
    .map((t) => (onlyInUse ? inUse(t) : t))
    .filter((t): t is TypeView => t !== null && matches(t, filter.trim()));
  const shown = narrow(views);
  const anyShown = narrow(anyViews);
  const naming = shown.reduce((sum, t) => sum + t.versions.filter((v) => v.names).length, 0);
  const group = kindParts(`*:*:${entityType}:*`).group;

  const openScope = (scope: ExplorerScope) => {
    onOpenChange(false);
    onScope(scope);
  };
  let inUseSaid = "Keeps the versions of which the partition holds records, and the types with such a version naming the type: where the type is used, not only where it may be.";
  if (counts.isError) {
    inUseSaid = "The records of each kind could not be counted, so the types cannot be narrowed to those holding records.";
  } else if (tally === undefined) {
    inUseSaid = "The records of each kind are being counted.";
  } else if (!complete) {
    inUseSaid = "The search service did not count the records of every kind in the partition, so the types cannot be narrowed to those holding records.";
  }
  const refresh = () => {
    refreshNext.current = true;
    void read.refetch();
  };

  let body;
  if (read.isPending) {
    body = (
      <div className="flex flex-col gap-2" data-testid="explorer-references-loading">
        {Array.from({ length: 6 }, (_, index) => <Skeleton key={index} className="h-9 rounded" />)}
      </div>
    );
  } else if (read.isError) {
    body = (
      <div className="flex flex-col items-start gap-3">
        <ExplorerProblem error={read.error} />
        <Button variant="outline" size="sm" onClick={() => void read.refetch()}>Try again</Button>
      </div>
    );
  } else if (answer !== undefined && answer.readUtc === null) {
    // Nothing read yet: where the first reading stands, or why it failed.
    body = answer.state === "failed"
      ? null
      : (
        <div className="flex flex-col gap-2">
          {Array.from({ length: 4 }, (_, index) => <Skeleton key={index} className="h-9 rounded opacity-60" />)}
        </div>
      );
  } else if (answer !== undefined && views.length === 0 && anyViews.length === 0) {
    body = (
      <EmptyState
        icon={<Waypoints />}
        title={`No schema refers to ${typeLabel}`}
        description={`None of the ${answer.kinds.toLocaleString("en-US")} kinds the partition's Schema service holds has a property naming ${entityType} records.`}
        data-testid="explorer-references-empty"
      />
    );
  } else if (answer !== undefined) {
    body = (
      <>
        {shown.length > 0
          ? <TypeList key={`${filter.trim()}|${onlyInUse}`} types={shown} typeLabel={typeLabel} onScope={openScope} testId="explorer-references-types" />
          : views.length > 0 && (
            <p className="px-1 text-[12px] text-muted-foreground" data-testid="explorer-references-none-shown">
              {onlyInUse && filter.trim() === ""
                ? `No type naming ${typeLabel} holds records in this partition.`
                : `No type naming ${typeLabel} matches "${filter.trim()}"${onlyInUse ? " among those holding records" : ""}.`}
            </p>
          )}
        {anyViews.length > 0 && (
          <Collapsible className="rounded-md border" data-testid="explorer-references-any">
            <CollapsibleTrigger className="group/any flex w-full items-center gap-2 px-3 py-1.5 text-left text-[12px] hover:bg-accent/50">
              <ChevronRight className="size-3.5 text-muted-foreground transition-transform group-data-[state=open]/any:rotate-90" aria-hidden />
              <span className="font-medium">{`Names any ${group} record`}</span>
              <span className="text-muted-foreground">{counted(anyShown.length, "type")}</span>
              <RichTooltip title="Any record of the group" body={`These properties name a record of any ${group} type, so a value there may be a ${typeLabel} record. Their schemas do not say which type.`}>
                <Info className="size-3.5 text-muted-foreground" aria-label="What these are" />
              </RichTooltip>
            </CollapsibleTrigger>
            <CollapsibleContent className="border-t p-2">
              <TypeList key={`${filter.trim()}|${onlyInUse}`} types={anyShown} typeLabel={typeLabel} onScope={openScope} testId="explorer-references-any-types" />
            </CollapsibleContent>
          </Collapsible>
        )}
      </>
    );
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="flex max-h-[85vh] flex-col gap-3 overflow-hidden sm:max-w-5xl" data-testid="explorer-references">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            {`Types that refer to ${typeLabel}`}
            <RichTooltip title="What counts as a reference" body={WHAT_COUNTS}>
              <Info className="size-4 text-muted-foreground" aria-label="What counts as a reference" />
            </RichTooltip>
          </DialogTitle>
          <DialogDescription>
            {`Every schema in the partition with a property naming ${entityType} records, and the versions that do.`}
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
          <SearchInput value={filter} onChange={setFilter} placeholder="Find a type or property" label="Find a referring type or property" className="sm:w-64" testId="explorer-references-filter" />
          <RichTooltip title="Only types with records" body={inUseSaid}>
            <span className="flex items-center gap-2">
              <Switch id="explorer-references-in-use" size="sm" checked={onlyInUse} onCheckedChange={setOnlyInUse} disabled={!complete} data-testid="explorer-references-in-use" />
              <Label htmlFor="explorer-references-in-use" className="text-[12px] font-normal">Only types with records</Label>
            </span>
          </RichTooltip>
          {answer !== undefined && answer.readUtc !== null && (
            <span className="flex items-center gap-1.5 text-[12px] text-muted-foreground" data-testid="explorer-references-summary">
              <span>
                <span className="font-mono tabular-nums text-foreground">{counted(shown.length, "type")}</span>
                {` in ${counted(naming, "version")}`}
              </span>
              <span className="inline-flex items-center gap-1" aria-hidden>
                <span className="inline-block h-3 w-4 rounded-sm border border-border bg-background" />
                <span>names it</span>
                <span className="ml-1 inline-block h-3 w-4 rounded-sm border border-dashed border-muted-foreground/50" />
                <span>does not</span>
              </span>
            </span>
          )}
          <span className="ml-auto flex items-center gap-1 text-[12px] text-muted-foreground">
            {answer !== undefined && answer.readUtc !== null && (
              <RichTooltip
                title="The partition's schemas"
                body={`${counted(answer.listed, "schema")} listed, ${counted(answer.kinds, "kind")} read.\n\n${WHY_READ}`}
              >
                <span data-testid="explorer-references-read">
                  {"Schemas read "}
                  <RelativeTime value={answer.readUtc} absolute={false} />
                </span>
              </RichTooltip>
            )}
            {answer !== undefined && (answer.unreadCount > 0 || answer.notes.length > 0) && (
              <RichTooltip
                title="What the answer may miss"
                body={[
                  ...answer.notes,
                  ...(answer.unreadCount > 0
                    ? [`${counted(answer.unreadCount, "kind")} could not be read:\n${answer.unread.map((u) => `${u.kind}: ${u.why}`).join("\n")}${answer.unreadCount > answer.unread.length ? `\nand ${answer.unreadCount - answer.unread.length} more` : ""}`]
                    : []),
                ].join("\n\n")}
              >
                <TriangleAlert className="size-3.5 text-warning" aria-label="What the answer may miss" data-testid="explorer-references-gaps" />
              </RichTooltip>
            )}
            <IconAction
              label="Read the partition's schemas again: those added since, and those in development"
              icon={<RefreshCw className={answer?.state === "reading" || read.isFetching ? "animate-spin" : undefined} />}
              variant="ghost"
              className="size-7"
              disabled={answer?.state === "reading"}
              onClick={refresh}
              data-testid="explorer-references-refresh"
            />
          </span>
        </div>

        {answer?.state === "reading" && <ReadingProgress answer={answer} />}
        {answer?.state === "failed" && (
          <Alert variant="destructive" data-testid="explorer-references-failed">
            <AlertTitle>The partition's schemas could not be read</AlertTitle>
            <AlertDescription className="flex flex-col items-start gap-2">
              <span className="whitespace-pre-wrap break-words">{answer.problem}</span>
              {answer.readUtc !== null && <span>The types below are from the last complete reading.</span>}
              <Button variant="outline" size="sm" onClick={refresh} data-testid="explorer-references-retry">Try again</Button>
            </AlertDescription>
          </Alert>
        )}

        <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-y-auto pr-1" data-testid="explorer-references-body">
          {body}
        </div>
      </DialogContent>
    </Dialog>
  );
}
