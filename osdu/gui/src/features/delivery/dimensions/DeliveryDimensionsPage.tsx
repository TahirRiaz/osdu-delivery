import { useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Search, Shapes } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { SummaryStrip, type SummaryCell } from "@/components/SummaryStrip";
import { deliveryApi, type DeliveryDimensionBoard } from "../../../api/delivery";
import { useActivePartition } from "../activePartition";
import { ProblemView } from "../TemplateSheet";
import { counted } from "../assertions/assertionFormat";
import { DimensionBuildDialog, type DimensionLaunch } from "./DimensionBuildDialog";
import { DimensionOverview } from "./DimensionOverview";
import { DimensionSearchBuilder } from "./DimensionSearchBuilder";
import { DimensionViewWorkspace } from "./DimensionViewWorkspace";
import { DimensionWorkspace } from "./DimensionWorkspace";
import {
  DIMENSION_VIEWS, SEARCH_REF, attributePicksOf, attributePicksText, dimensionEntries, picksOf, picksText, type DimensionEntry, type DimensionView,
} from "./dimensionFormat";
import { VIEW_PARAM, VIEW_TABS, VIEW_TAB_PARAM, viewTabOf } from "./dimensionViewFormat";

/** How often the board is read again, so a build under way shows its outcome as it lands. */
const REFRESH_MS = 15000;

/** The newest time any dimension of the board was built, for the summary. */
function newestBuild(entries: DimensionEntry[]): string | null {
  let newest: string | null = null;
  for (const entry of entries) {
    const built = entry.dimension.lastBuiltUtc;
    if (built !== null && (newest === null || built > newest)) {
      newest = built;
    }
  }

  return newest;
}

/** The facts the overview is read against: how many dimensions are built, what they hold, what needs a look, and when they were built. */
function summaryCells(board: DeliveryDimensionBoard, entries: DimensionEntry[], onAttention: () => void): SummaryCell[] {
  const totals = board.totals;
  const attention = totals.failing + totals.changed;
  const newest = newestBuild(entries);
  return [
    {
      label: "Dimensions",
      value: `${totals.built.toLocaleString("en-US")} of ${totals.dimensions.toLocaleString("en-US")}`,
      caption: totals.notBuilt > 0 ? `built; ${totals.notBuilt} not built yet` : `built, from ${counted(totals.flows, "dimension flow")}`,
      testId: "dimensions-summary-built",
    },
    {
      label: "Values",
      value: totals.values.toLocaleString("en-US"),
      caption: `from ${counted(totals.keys, "key")}`,
      testId: "dimensions-summary-values",
    },
    {
      label: "Needs a look",
      value: attention.toLocaleString("en-US"),
      tone: totals.failing > 0 ? "destructive" : attention > 0 ? "warning" : undefined,
      caption: attention === 0
        ? totals.running > 0 ? `${totals.running} building now` : "every dimension holds what it declares"
        : `${totals.failing} failed, ${totals.changed} changed since built`,
      onClick: attention > 0 ? onAttention : undefined,
      testId: "dimensions-summary-attention",
    },
    {
      label: "Last built",
      value: newest === null ? "never" : <RelativeTime value={newest} absolute={false} />,
      caption: totals.running > 0 ? `${totals.running} building now` : "the newest build of any dimension",
      testId: "dimensions-summary-last-built",
    },
  ];
}

/** The tab a link names, the values tab when it names none or one that is not a tab. */
function viewOf(value: string | null): DimensionView {
  return DIMENSION_VIEWS.find((view) => view === value) ?? DIMENSION_VIEWS[0];
}

/**
 * The dimensions of the partition picked in the title bar (docs/dimension-plan.md): the distinct keys of any part of an
 * OSDU document, exactly as the index holds them (an id, for a reference), read past the search's limit on distinct
 * values, each with the human-friendly value it stands for (the label read from the record it names, cleaned), and every
 * key and value with the search that finds its records. The page is three views of one address: every dimension, flow by
 * flow, as cards under the facts they add up to; one dimension, whose grids take the page's whole width and scroll inside
 * it; and the search builder, which picks values across a kind's dimensions to compose the OSDU search that finds their
 * records. The dimension, the tab, the value open in a sheet, and the builder's kind and picks live in the URL, so a link
 * lands on the same view. Dimensions their flows no longer declare are left out of the lists until asked for. A view a
 * dimension flow declares over its tables opens here too (`v`, its tab `vt`), on the page's whole width.
 */
export default function DeliveryDimensionsPage() {
  const [params, setParams] = useSearchParams();
  const [active] = useActivePartition();
  const [launch, setLaunch] = useState<DimensionLaunch | null>(null);
  const [showRetired, setShowRetired] = useState(false);
  const board = useQuery({
    queryKey: ["delivery", "dimensions", "board", active],
    queryFn: () => deliveryApi.dimensionBoard(),
    refetchInterval: REFRESH_MS,
  });
  const entries = useMemo(() => dimensionEntries(board.data), [board.data]);
  const declared = useMemo(() => entries.filter((entry) => entry.standing !== "undeclared"), [entries]);
  const retired = entries.length - declared.length;
  const shown = showRetired ? entries : declared;
  const selectedRef = params.get("d");
  const building = selectedRef === SEARCH_REF;
  const selected = selectedRef === null || building ? null : entries.find((entry) => entry.ref === selectedRef) ?? null;
  const valueParam = Number.parseInt(params.get("value") ?? "", 10);
  const value = Number.isSafeInteger(valueParam) && valueParam > 0 ? valueParam : null;
  const picks = useMemo(() => picksOf(params.get("p")), [params]);
  const attributePicks = useMemo(() => attributePicksOf(params.get("a")), [params]);

  const update = (changes: Record<string, string | null>) => setParams((current) => {
    const next = new URLSearchParams(current);
    for (const [key, changed] of Object.entries(changes)) {
      if (changed === null) {
        next.delete(key);
      } else {
        next.set(key, changed);
      }
    }

    return next;
  }, { replace: true });

  const open = (ref: string | null) => update({ d: ref, view: null, value: null, kind: null, p: null, a: null, [VIEW_PARAM]: null, [VIEW_TAB_PARAM]: null });
  const openView = (name: string) => update({ [VIEW_PARAM]: name, [VIEW_TAB_PARAM]: null, d: null, view: null, value: null, kind: null, p: null, a: null });
  const viewName = params.get(VIEW_PARAM);
  const firstNeedingLook = declared.find((entry) => entry.standing === "failed" || entry.standing === "changed") ?? null;
  const searchButton = (
    <Button variant="outline" size="sm" onClick={() => open(SEARCH_REF)} disabled={declared.every((entry) => entry.dimension.dimensionId === null)} data-testid="dimensions-build-search">
      <Search />
      Build a search
    </Button>
  );

  if (viewName !== null && viewName.trim() !== "") {
    return (
      <Page data-testid="page-delivery-dimensions">
        <DimensionViewWorkspace
          key={viewName}
          name={viewName}
          tab={viewTabOf(params.get(VIEW_TAB_PARAM))}
          onTab={(tab) => update({ [VIEW_TAB_PARAM]: tab === VIEW_TABS[0] ? null : tab })}
          onOpen={openView}
          onBack={() => open(null)}
          onOpenDimension={open}
          flows={board.data?.flows ?? []}
          onLaunch={setLaunch}
        />
        <DimensionBuildDialog launch={launch} onClose={() => setLaunch(null)} />
      </Page>
    );
  }

  if (board.isError) {
    return (
      <Page data-testid="page-delivery-dimensions">
        <PageHeader title="Dimensions" />
        <ProblemView error={board.error} testId="dimensions-error" />
      </Page>
    );
  }

  if (board.data === undefined) {
    return (
      <Page data-testid="page-delivery-dimensions">
        <PageHeader title="Dimensions" subtitle="The distinct keys of any part of an OSDU document, each with a human-friendly value and the OSDU search that finds its records." />
        <Skeleton className="h-16 w-full rounded-lg" />
        <Skeleton className="h-96 w-full rounded-lg" />
      </Page>
    );
  }

  if (selected !== null) {
    return (
      <Page data-testid="page-delivery-dimensions">
        <DimensionWorkspace
          key={selected.ref}
          entry={selected}
          siblings={selected.standing === "undeclared" ? entries : shown}
          view={viewOf(params.get("view"))}
          onView={(view) => update({ view: view === DIMENSION_VIEWS[0] ? null : view })}
          value={value}
          onValue={(valueId) => update({ value: valueId === null ? null : String(valueId) })}
          onLaunch={setLaunch}
          onOpen={open}
          onRemoved={() => open(null)}
        />
        <DimensionBuildDialog launch={launch} onClose={() => setLaunch(null)} />
      </Page>
    );
  }

  if (building) {
    return (
      <Page data-testid="page-delivery-dimensions">
        <DimensionSearchBuilder
          entries={declared}
          onBack={() => open(null)}
          kind={params.get("kind")}
          onKind={(kind) => update({ kind, p: null, a: null })}
          picks={picks}
          onPicks={(next) => update({ p: picksText(next) === "" ? null : picksText(next) })}
          attributes={attributePicks}
          onAttributes={(next) => update({ a: attributePicksText(next) === "" ? null : attributePicksText(next) })}
        />
      </Page>
    );
  }

  const totals = board.data.totals;
  return (
    <Page data-testid="page-delivery-dimensions">
      <PageHeader
        title="Dimensions"
        subtitle={`${counted(totals.dimensions, "dimension")} from ${counted(totals.flows, "dimension flow")}${board.data.partition === null ? "" : ` in ${board.data.partition}`}.`}
        actions={board.data.flows.length > 0 ? searchButton : undefined}
      />
      {board.data.flows.length === 0
        ? (
          <Card className="gap-0 rounded-lg p-0">
            <EmptyState
              icon={<Shapes />}
              title="No dimension flow is synced yet"
              description="A dimension flow is a YAML file in a repository with flowType: dimension. Each dimension names a kind, the path of the key to gather (any property of the record or of its data), where a key's label is read when it names a record, and the steps that clean it into its value. Sync the repository and build the flow; its dimensions appear here."
              data-testid="dimensions-none"
            />
          </Card>
        )
        : (
          <>
            {selectedRef !== null && (
              <p className="text-[13px] text-muted-foreground" data-testid="dimensions-not-here">
                The dimension the link names is not one of this partition's; pick one below.
              </p>
            )}
            <SummaryStrip
              cells={summaryCells(board.data, declared, () => { if (firstNeedingLook !== null) { open(firstNeedingLook.ref); } })}
              data-testid="dimensions-summary"
            />
            {entries.length === 0 && board.data.flows.every((flow) => !flow.buildsPartition)
              ? (
                <Card className="gap-0 rounded-lg p-0">
                  <EmptyState
                    icon={<Shapes />}
                    title={`No dimension flow builds in ${board.data.partition ?? "this partition"}`}
                    description="Every dimension flow builds in other partitions. Pick one of them in the title bar, or add this partition to a flow's partitions and sync its repository."
                    data-testid="dimensions-none-here"
                  />
                </Card>
              )
              : <DimensionOverview entries={shown} flows={board.data.flows} onOpen={open} onLaunch={setLaunch} />}
            {retired > 0 && (
              <button
                type="button"
                onClick={() => setShowRetired((current) => !current)}
                className="self-start rounded-md px-1 text-[12px] text-muted-foreground outline-none hover:text-foreground hover:underline focus-visible:underline"
                data-testid="dimensions-retired-toggle"
              >
                {showRetired ? "Hide the dimensions no longer declared" : `Show ${counted(retired, "dimension")} no longer declared`}
              </button>
            )}
          </>
        )}
      <DimensionBuildDialog launch={launch} onClose={() => setLaunch(null)} />
    </Page>
  );
}
