import { useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Shapes } from "lucide-react";
import { Card } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { EmptyState } from "@/components/EmptyState";
import { FilterCombobox, type FilterOption } from "@/components/FilterCombobox";
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
import { DimensionRail } from "./DimensionRail";
import { DimensionWorkspace } from "./DimensionWorkspace";
import { DIMENSION_VIEWS, STANDING_VISUALS, dimensionEntries, type DimensionEntry, type DimensionView } from "./dimensionFormat";

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

/** The facts the page is read against: how many dimensions are built, what they hold, what needs a look, and when they were built. */
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
      label: "Members",
      value: totals.members.toLocaleString("en-US"),
      caption: `from ${counted(totals.originals, "original")}`,
      testId: "dimensions-summary-members",
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

/** The tab a link names, the members tab when it names none or one that is not a tab. */
function viewOf(value: string | null): DimensionView {
  return DIMENSION_VIEWS.find((view) => view === value) ?? "members";
}

/**
 * The dimensions of the partition picked in the title bar (docs/dimension-plan.md): the distinct values of any part of an
 * OSDU document, read past the search's limit on distinct values, each original kept beside the clean member it belongs
 * to, and each member able to write the search that finds its records. The dimensions are listed beside the page by the
 * flow that declares them; with none picked, every dimension shows as a card with its commonest members; a dimension opens
 * with its members, originals, change log, builds and definition. The dimension, the tab and the member open in a sheet
 * live in the URL, so a link lands on the same view.
 */
export default function DeliveryDimensionsPage() {
  const [params, setParams] = useSearchParams();
  const [active] = useActivePartition();
  const [launch, setLaunch] = useState<DimensionLaunch | null>(null);
  const board = useQuery({
    queryKey: ["delivery", "dimensions", "board", active],
    queryFn: () => deliveryApi.dimensionBoard(),
    refetchInterval: REFRESH_MS,
  });
  const entries = useMemo(() => dimensionEntries(board.data), [board.data]);
  const selectedRef = params.get("d");
  const selected = selectedRef === null ? null : entries.find((entry) => entry.ref === selectedRef) ?? null;
  const memberParam = Number.parseInt(params.get("member") ?? "", 10);
  const member = Number.isSafeInteger(memberParam) && memberParam > 0 ? memberParam : null;

  const update = (changes: Record<string, string | null>) => setParams((current) => {
    const next = new URLSearchParams(current);
    for (const [key, value] of Object.entries(changes)) {
      if (value === null) {
        next.delete(key);
      } else {
        next.set(key, value);
      }
    }

    return next;
  }, { replace: true });

  const open = (ref: string | null) => update({ d: ref, view: null, member: null });
  const firstNeedingLook = entries.find((entry) => entry.standing === "failed" || entry.standing === "changed") ?? null;
  const options: FilterOption[] = entries.map((entry) => ({
    value: entry.ref,
    label: entry.dimension.name,
    hint: `${entry.flow.name} · ${STANDING_VISUALS[entry.standing].label.toLowerCase()} · ${counted(entry.dimension.members, "member")}`,
  }));

  return (
    <Page data-testid="page-delivery-dimensions">
      <PageHeader
        title="Dimensions"
        subtitle={board.data === undefined
          ? "The distinct values of any part of an OSDU document, cleaned into members, each able to filter the OSDU search."
          : `${counted(board.data.totals.dimensions, "dimension")} from ${counted(board.data.totals.flows, "dimension flow")}${board.data.partition === null ? "" : ` in ${board.data.partition}`}.`}
      />

      {board.isError
        ? <ProblemView error={board.error} testId="dimensions-error" />
        : board.data === undefined
          ? (
            <>
              <Skeleton className="h-16 w-full rounded-lg" />
              <Skeleton className="h-96 w-full rounded-lg" />
            </>
          )
          : board.data.flows.length === 0
            ? (
              <Card className="gap-0 rounded-lg p-0">
                <EmptyState
                  icon={<Shapes />}
                  title="No dimension flow is synced yet"
                  description="A dimension flow is a YAML file in a repository with flowType: dimension. Each dimension names a kind, the path of the value to gather (any property of the record or of its data), and the steps that clean each original into its member. Sync the repository and build the flow; its dimensions appear here."
                  data-testid="dimensions-none"
                />
              </Card>
            )
            : (
              <>
                <SummaryStrip
                  cells={summaryCells(board.data, entries, () => { if (firstNeedingLook !== null) { open(firstNeedingLook.ref); } })}
                  data-testid="dimensions-summary"
                />
                <div className="grid items-start gap-4 lg:grid-cols-[16rem_minmax(0,1fr)]">
                  <DimensionRail
                    entries={entries}
                    selected={selected?.ref ?? null}
                    onSelect={open}
                    className="hidden lg:sticky lg:top-0 lg:flex lg:max-h-[calc(100dvh-14rem)]"
                  />
                  <div className="flex min-w-0 flex-col gap-3">
                    <FilterCombobox
                      options={options}
                      value={selected?.ref ?? ""}
                      onChange={(value) => open(value === "" ? null : value)}
                      placeholder="Every dimension"
                      searchPlaceholder="Dimension, flow or standing"
                      emptyText="No dimension matches."
                      ariaLabel="Dimension"
                      testId="dimensions-picker"
                      className="w-full sm:w-72 lg:hidden"
                    />
                    {selectedRef !== null && selected === null && (
                      <p className="text-[13px] text-muted-foreground" data-testid="dimensions-not-here">
                        The dimension the link names is not one of this partition's; pick one from the list.
                      </p>
                    )}
                    {selected === null
                      ? entries.length === 0 && board.data.flows.every((flow) => !flow.buildsPartition)
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
                        : <DimensionOverview entries={entries} flows={board.data.flows} onOpen={open} onLaunch={setLaunch} />
                      : (
                        <DimensionWorkspace
                          key={selected.ref}
                          entry={selected}
                          view={viewOf(params.get("view"))}
                          onView={(view) => update({ view: view === "members" ? null : view })}
                          member={member}
                          onMember={(memberId) => update({ member: memberId === null ? null : String(memberId) })}
                          onLaunch={setLaunch}
                        />
                      )}
                  </div>
                </div>
              </>
            )}

      <DimensionBuildDialog launch={launch} onClose={() => setLaunch(null)} />
    </Page>
  );
}
