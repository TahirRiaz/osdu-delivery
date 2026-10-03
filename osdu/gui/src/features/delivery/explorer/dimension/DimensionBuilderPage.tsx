import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { Link, useLocation, useSearchParams } from "react-router-dom";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { ArrowLeft, ChevronLeft, ChevronRight, RotateCcw, Telescope, Unplug } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { useAuth } from "@/auth/AuthContext";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { RichTooltip } from "@/components/RichTooltip";
import type { DeliveryDimensionKey } from "../../../../api/delivery";
import {
  composeDimension, explorerApi, sampleDimension, type DimensionCompose, type DimensionExample, type ExplorerTypes,
} from "../../../../api/explorer";
import { useActivePartition } from "../../activePartition";
import { failureText } from "../../answers";
import { BlueprintDiagram } from "../../dimensions/BlueprintDiagram";
import { examplesOf, layoutOf } from "../../dimensions/blueprintModel";
import { useWindowFit } from "../../useWindowFit";
import { ExplorerProblem } from "../ExplorerProblem";
import { kindParts, useExplorerRead } from "../explorerModel";
import { BuilderColumns } from "./BuilderColumns";
import { BuilderExpression } from "./BuilderExpression";
import { BuilderFilterDialog } from "./BuilderFilterDialog";
import { BuilderLanes } from "./BuilderLanes";
import {
  EXAMPLES, addressOf, ambiguityOf, applyPick, backOf, buildOf, emptyDraft, recordName, trailKey, trailsOf, typeName,
  type AmbiguousPick, type BuilderBuild, type PropertyPick, type SegmentFilter,
} from "./dimensionDraft";

/** How long a draft rests before it is written and checked again: a person typing a name is not asked about every letter. */
const SETTLE_MS = 350;

/** What stays under the builder: the page's bottom padding and the workbench's status bar. */
const BELOW = 46;

/** The least height the builder keeps. */
const MIN_HEIGHT = 520;

/** How long a read of the records stands before the same one is read again. */
const FRESH_MS = 60_000;

/** What the builder marks the history entries it writes with, so it never reads its own address back over a newer build. */
const BUILDER_WRITE = { dimensionBuilder: true } as const;

/** Whether a history entry's state is one the builder wrote. */
function isBuilderWrite(state: unknown): boolean {
  return typeof state === "object" && state !== null && (state as { dimensionBuilder?: unknown }).dimensionBuilder === true;
}

/** A change to the build: the parts it replaces, the rest kept. */
type BuildChange = Partial<BuilderBuild>;

/** `value`, once it has stopped changing for `ms`. */
function useSettled<T>(value: T, ms: number): T {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const timer = window.setTimeout(() => setSettled(value), ms);
    return () => window.clearTimeout(timer);
  }, [value, ms]);
  return settled;
}

/** The example row a dimension's pages draw a key from, made of what the builder's example read. */
function keyOf(example: DimensionExample): DeliveryDimensionKey {
  return {
    keyId: 0,
    key: example.key,
    label: example.label,
    labelFrom: example.labelFrom,
    valueId: null,
    value: example.value,
    leftOut: null,
    note: example.note,
    count: example.records ?? 0,
    filterable: example.filter !== null,
    filter: example.filter,
    firstSeenBuildId: 0,
    firstSeenUtc: "",
    valueSinceBuildId: 0,
    removedBuildId: null,
    removedUtc: null,
    attributes: example.attributes,
  };
}

/**
 * The explorer's dimension builder (osdu/docs/explorer.md, Building a dimension): a workspace of its own, opened from the
 * explorer and leaving it as it was, where a person builds a dimension by picking from the records OSDU holds. The records
 * of a kind, the record each key names and the records those name stand left to right as a build reads them; a property's
 * menu makes it the key, the value, an attribute or the attribute collected, or follows the records it names. Where a pick
 * reaches several values, the builder asks which one is meant and writes the filter that keeps it. The table it makes, the
 * YAML a flow lists and an example key's row are beside it, all written and checked by the control plane exactly as a flow
 * of that YAML is read and built. Everything is in the address, so a link brings the same draft back.
 */
export default function DimensionBuilderPage() {
  const [params, setParams] = useSearchParams();
  const location = useLocation();
  const [active] = useActivePartition();
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");

  // The build is held here and changed at once, as any input is, and the address follows it: the router applies an address
  // as a transition, so a build read back from it could lag a pick made a moment before, and a change made from that would
  // lose the pick. An address the builder did not write (a link to another build) brings its build in.
  const [build, setBuild] = useState<BuilderBuild>(() => buildOf(params));
  const [seenEntry, setSeenEntry] = useState(location.key);
  if (location.key !== seenEntry) {
    setSeenEntry(location.key);
    if (!isBuilderWrite(location.state)) {
      setBuild(buildOf(params));
    }
  }

  useEffect(() => {
    const next = addressOf(params, build);
    if (next.toString() !== params.toString()) {
      setParams(next, { replace: true, state: BUILDER_WRITE });
    }
  }, [build, params, setParams]);

  const { draft, opened, at } = build;
  const back = backOf(params.get("back"));

  /** Changes the build: by the parts given, or by what a function of the build as it stands now gives. */
  const update = useCallback((change: BuildChange | ((current: BuilderBuild) => BuildChange)) => setBuild((current) => {
    const asked = typeof change === "function" ? change(current) : change;
    const next = { draft: asked.draft ?? current.draft, opened: asked.opened ?? current.opened, at: asked.at ?? current.at };
    return next.draft === current.draft && next.opened === current.opened && next.at === current.at ? current : next;
  }), []);

  const connection = useQuery({
    queryKey: ["explorer", "connection", active],
    queryFn: () => explorerApi.connection(active),
    staleTime: 60_000,
  });
  const reachable = canOperate && connection.data?.available === true;

  // The commonest keys of the key's path, which the examples are; read again only when what they are read by changes.
  const kind = draft.kind;
  const keyed = draft.path !== null && draft.path !== "";
  const keysRequest = kind !== "" && keyed ? { kind, query: draft.query, path: draft.path } : null;
  const keys = useQuery({
    queryKey: ["explorer", "dimension", "keys", active, keysRequest],
    queryFn: () => sampleDimension(active, keysRequest!),
    enabled: reachable && keysRequest !== null,
    staleTime: FRESH_MS,
    retry: false,
    refetchOnWindowFocus: false,
  });
  const keyList = useMemo(() => keys.data?.answer.keys ?? [], [keys.data]);
  const exampleKey = keyed && keyList.length > 0 ? keyList[Math.min(at, keyList.length - 1)].key : null;

  // The record shown and the records the example key's trails reach.
  const trails = useMemo(() => (exampleKey === null ? [] : trailsOf(draft, opened)), [draft, opened, exampleKey]);
  const sampleRequest = kind === "" ? null : { kind, query: draft.query, path: draft.path, key: exampleKey, at: keyed ? 0 : at, trails };
  const sample = useQuery({
    queryKey: ["explorer", "dimension", "sample", active, sampleRequest],
    queryFn: () => sampleDimension(active, sampleRequest!),
    enabled: reachable && sampleRequest !== null && !(keyed && keys.isPending),
    placeholderData: keepPreviousData,
    staleTime: FRESH_MS,
    retry: false,
    refetchOnWindowFocus: false,
  });

  // The YAML, its check and the example row, once the draft has settled.
  const settled = useSettled(draft, SETTLE_MS);
  const compose = useQuery({
    queryKey: ["explorer", "dimension", "compose", active, settled, exampleKey],
    queryFn: () => composeDimension(active, settled, exampleKey),
    enabled: reachable && settled.kind !== "",
    placeholderData: keepPreviousData,
    retry: false,
    refetchOnWindowFocus: false,
  });

  const [pending, setPending] = useState<{ pick: PropertyPick; ambiguity: AmbiguousPick } | null>(null);
  const [pointed, setPointed] = useState<string | null>(null);
  const [showRow, setShowRow] = useState(false);
  const columns = [compose.data?.dimension?.keyColumn, compose.data?.dimension?.valueColumn].filter((name): name is string => typeof name === "string");
  const make = (pick: PropertyPick, filter: SegmentFilter | null, segment: number | null) => update((current) => {
    const result = applyPick(current.draft, current.opened, pick, filter, segment, columns);
    return result.draft === current.draft && result.opened === current.opened
      ? {}
      : { draft: result.draft, opened: result.opened, at: result.keyChanged ? 0 : undefined };
  });
  const onPick = (pick: PropertyPick) => {
    const ambiguity = ambiguityOf(pick);
    if (ambiguity === null) {
      make(pick, null, null);
    } else {
      setPending({ pick, ambiguity });
    }
  };

  const sampled = sample.data?.answer;
  const exampleCount = keyed ? keyList.length : Math.min(sampled?.total ?? 0, EXAMPLES);
  const exampleLabel = keyed
    ? (compose.data?.example?.key === exampleKey ? compose.data?.example?.value ?? exampleKey : exampleKey)
    : (sampled?.record === null || sampled?.record === undefined ? null : recordName(sampled.record));
  const notes = [
    ...(keys.data?.answer.notes ?? []),
    ...(sampled?.notes ?? []).filter((note) => !(keys.data?.answer.notes ?? []).includes(note)),
    ...(compose.data?.exampleProblem === null || compose.data?.exampleProblem === undefined ? [] : [`The example row could not be made: ${compose.data.exampleProblem}`]),
    ...(compose.data?.example?.notes ?? []),
    ...(keys.isError ? [`The keys could not be read: ${failureText(keys.error)}`] : []),
    ...(sample.isError ? [`The records could not be read: ${failureText(sample.error)}`] : []),
  ];

  const parts = kind === "" ? null : kindParts(kind);
  const header = (
    <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-2">
      <Button variant="ghost" size="sm" asChild>
        <Link to={back} data-testid="builder-back"><ArrowLeft />Explorer</Link>
      </Button>
      <h1 className="text-lg font-semibold leading-7">Build a dimension</h1>
      {kind !== "" && (
        <>
          <Input
            value={draft.name}
            onChange={(event) => {
              const name = event.target.value;
              update((current) => ({ draft: { ...current.draft, name } }));
            }}
            placeholder="Name the dimension"
            className="h-8 w-56 font-mono text-[13px]"
            aria-label="The dimension's name"
            data-testid="builder-name"
          />
          {parts !== null && (
            <RichTooltip title="Reads the records of" body={kind} mono>
              <span className="inline-flex items-center gap-1.5 rounded-full border px-2 py-0.5 text-[12px]" data-testid="builder-kind">
                {typeName(parts.entityType)}
                <span className="font-mono text-muted-foreground">{parts.version}</span>
              </span>
            </RichTooltip>
          )}
          <Button variant="ghost" size="sm" onClick={() => update({ draft: emptyDraft(""), opened: [], at: 0 })} data-testid="builder-restart">
            <RotateCcw />
            Start over
          </Button>
          <ExampleControl
            keyed={keyed}
            at={at}
            count={exampleCount}
            label={exampleLabel}
            title={exampleKey}
            onStep={(step) => update({ at: (at + step + exampleCount) % exampleCount })}
          />
        </>
      )}
    </div>
  );

  let content: ReactNode;
  if (!canOperate) {
    content = (
      <EmptyState
        icon={<Telescope />}
        title="The builder reads OSDU with a flow's credentials"
        description="Each read goes through a delivery flow's connection to the partition, which takes the operate scope. Ask an administrator for it."
        data-testid="builder-no-scope"
      />
    );
  } else if (connection.isError) {
    content = <ExplorerProblem error={connection.error} />;
  } else if (connection.data !== undefined && !connection.data.available) {
    content = (
      <EmptyState
        icon={<Unplug />}
        title={connection.data.partition === null ? "Pick a partition" : `No connection to ${connection.data.partition}`}
        description={connection.data.reason ?? undefined}
        data-testid="builder-no-connection"
      />
    );
  } else if (kind === "") {
    content = <KindPicker partition={active} reachable={reachable} onPick={(picked) => update({ draft: { ...emptyDraft(picked) }, opened: [], at: 0 })} />;
  } else {
    content = (
      <BuilderFrame>
        <Card className="min-h-0 flex-1 gap-0 overflow-hidden rounded-lg p-0" data-testid="builder-workspace">
          <ResizablePanelGroup orientation="horizontal" className="min-h-0 flex-1">
            <ResizablePanel defaultSize="64" minSize="40" className="flex min-h-0 flex-col">
              <ResizablePanelGroup orientation="vertical" className="min-h-0 flex-1">
                <ResizablePanel defaultSize="62" minSize="30" className="flex min-h-0 flex-col">
                  <BuilderLanes
                    draft={draft}
                    sample={sampled}
                    sampling={sample.isFetching || keys.isFetching}
                    trails={trails}
                    opened={opened}
                    onPick={onPick}
                    onClose={(trail) => update((current) => ({ opened: current.opened.filter((o) => trailKey(o) !== trailKey(trail)) }))}
                  />
                </ResizablePanel>
                <ResizableHandle />
                <ResizablePanel defaultSize="38" minSize="15" className="flex min-h-0 flex-col">
                  <BuilderColumns
                    draft={draft}
                    compose={compose.data}
                    issues={compose.data?.issues ?? []}
                    recordKind={sampled?.record?.kind ?? null}
                    onChange={(next) => update({ draft: next })}
                    onPoint={setPointed}
                  />
                </ResizablePanel>
              </ResizablePanelGroup>
            </ResizablePanel>
            <ResizableHandle />
            <ResizablePanel defaultSize="36" minSize="24" className="flex min-h-0 flex-col border-l">
              <BuilderExpression
                compose={compose.data}
                composing={compose.isFetching}
                failure={compose.isError ? failureText(compose.error) : null}
                notes={notes}
                pointed={pointed}
                onPoint={setPointed}
                onShowRow={() => setShowRow(true)}
              />
            </ResizablePanel>
          </ResizablePanelGroup>
        </Card>
      </BuilderFrame>
    );
  }

  return (
    <Page data-testid="page-dimension-builder">
      {header}
      {content}
      <Dialog open={showRow} onOpenChange={setShowRow}>
        <DialogContent className="max-h-[90vh] overflow-auto sm:max-w-[min(1400px,95vw)]" data-testid="builder-row-dialog">
          <DialogHeader>
            <DialogTitle>How a row is built</DialogTitle>
            <DialogDescription>
              The records searched, each record found by id, and the table, as a build reads them
              {exampleLabel === null ? "" : `, filled with what the example key ${exampleLabel} reads`}.
            </DialogDescription>
          </DialogHeader>
          <RowPreview compose={compose.data} />
        </DialogContent>
      </Dialog>
      <BuilderFilterDialog
        pick={pending?.ambiguity ?? null}
        onRead={(filter) => {
          if (pending !== null) {
            make(pending.pick, filter, pending.ambiguity.segment);
          }

          setPending(null);
        }}
        onCancel={() => setPending(null)}
      />
    </Page>
  );
}

/** A frame as tall as the window leaves under it, so the workspace scrolls inside itself and the page does not. */
function BuilderFrame({ children }: { children: ReactNode }) {
  const frame = useRef<HTMLDivElement>(null);
  useWindowFit(frame, BELOW, MIN_HEIGHT, undefined, "height");
  return <div ref={frame} className="flex min-h-0 flex-col" data-testid="builder-frame">{children}</div>;
}

/** The example the builder fills itself with: one of the commonest keys, or before a key one of the kind's first records. */
function ExampleControl({ keyed, at, count, label, title, onStep }: {
  keyed: boolean;
  at: number;
  count: number;
  label: string | null;
  title: string | null;
  onStep: (step: number) => void;
}) {
  return (
    <div className="ml-auto flex shrink-0 items-center gap-1 text-[12px]" data-testid="builder-example">
      <RichTooltip body={keyed ? "Every part is filled with what one key reads: one of the keys held by the most records." : "One of the first records of the kind, to pick the key from."}>
        <span className="text-muted-foreground">{keyed ? "Example key" : "Record"}</span>
      </RichTooltip>
      <Button variant="ghost" size="icon-xs" aria-label="The previous example" onClick={() => onStep(-1)} disabled={count < 2} data-testid="builder-example-previous">
        <ChevronLeft />
      </Button>
      <RichTooltip title={keyed ? "Key" : "Record"} body={title ?? label ?? ""} mono>
        <span className="max-w-56 truncate font-mono" data-testid="builder-example-label">{label ?? "none"}</span>
      </RichTooltip>
      <Button variant="ghost" size="icon-xs" aria-label="The next example" onClick={() => onStep(1)} disabled={count < 2} data-testid="builder-example-next">
        <ChevronRight />
      </Button>
      <span className="font-mono tabular-nums text-muted-foreground">{count === 0 ? "0/0" : `${Math.min(at, count - 1) + 1}/${count}`}</span>
    </div>
  );
}

/** How a build makes each column of the drafted dimension, drawn as a flow's own dimension is, filled with the example key's row. */
function RowPreview({ compose }: { compose: DimensionCompose | undefined }) {
  const layout = useMemo(() => (compose?.blueprint == null ? null : layoutOf(compose.blueprint)), [compose]);
  const examples = useMemo(
    () => (layout === null || compose?.example == null ? null : examplesOf(layout, keyOf(compose.example))),
    [layout, compose],
  );
  if (layout === null || compose?.dimension == null) {
    return <p className="p-4 text-[12.5px] text-muted-foreground">The row is drawn once the YAML reads.</p>;
  }

  return (
    <div className="min-w-0 overflow-x-auto rounded-lg border border-border" data-testid="builder-row-preview">
      <BlueprintDiagram
        layout={layout}
        dimension={compose.dimension}
        table={compose.table ?? ""}
        coverage={new Map()}
        light={null}
        picked={null}
        examples={examples}
        onPoint={() => undefined}
        onPick={() => undefined}
      />
    </div>
  );
}

/** The kinds of the partition, to pick the one whose records the dimension reads, with how many records each holds. */
function KindPicker({ partition, reachable, onPick }: { partition: string | null; reachable: boolean; onPick: (kind: string) => void }) {
  const [find, setFind] = useState("");
  const types = useExplorerRead<ExplorerTypes>(["types", partition, {}], reachable ? () => explorerApi.types(partition, {}) : null, 10 * 60_000);
  const kinds = (types.data?.answer.kinds ?? [])
    .filter((k) => k.kind.toLowerCase().includes(find.trim().toLowerCase()))
    .slice(0, 400);
  return (
    <Card className="mx-auto flex w-full max-w-2xl flex-col gap-3 rounded-lg p-4" data-testid="builder-kind-picker">
      <div>
        <h2 className="text-[14px] font-semibold">Which records does the dimension read?</h2>
        <p className="text-[12.5px] text-muted-foreground">Pick a kind; its records are where the dimension's keys come from, one of each distinct value.</p>
      </div>
      <Input value={find} onChange={(event) => setFind(event.target.value)} placeholder="Find a kind" className="h-8" data-testid="builder-kind-find" />
      {types.isPending && <p className="text-[12.5px] text-muted-foreground">Reading the kinds of the partition.</p>}
      {types.isError && <ExplorerProblem error={types.error} />}
      <ul className="flex max-h-[60vh] flex-col overflow-auto rounded-md border border-border" data-testid="builder-kind-list">
        {kinds.map((k) => (
          <li key={k.kind}>
            <button
              type="button"
              className="flex w-full min-w-0 items-center gap-2 border-b border-border/60 px-3 py-1.5 text-left text-[12.5px] last:border-b-0 hover:bg-muted/60"
              onClick={() => onPick(k.kind)}
              data-testid="builder-kind-item"
            >
              <span className="min-w-0 flex-1 truncate">{typeName(kindParts(k.kind).entityType)} <span className="font-mono text-muted-foreground">{k.kind}</span></span>
              <span className="shrink-0 tabular-nums text-muted-foreground">{k.count.toLocaleString("en-US")}</span>
            </button>
          </li>
        ))}
        {types.data !== undefined && kinds.length === 0 && <li className="px-3 py-2 text-[12.5px] text-muted-foreground">No kind matches.</li>}
      </ul>
      <p className="text-[11.5px] text-muted-foreground">Opened from a type or a kind in the explorer, the builder starts from it.</p>
    </Card>
  );
}
