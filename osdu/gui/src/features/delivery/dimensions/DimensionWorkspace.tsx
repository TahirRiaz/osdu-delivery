import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { CircleX, Info, Loader2, PencilLine, Play, Shapes } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { EmptyState } from "@/components/EmptyState";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { SummaryStrip, type SummaryCell } from "@/components/SummaryStrip";
import { KindText } from "../KindText";
import { counted } from "../assertions/assertionFormat";
import { DimensionExportMenu, StandingGlyph } from "./DimensionBadges";
import type { DimensionLaunch } from "./DimensionBuildDialog";
import { DimensionDefinition } from "./DimensionDefinition";
import { DimensionBuilds, DimensionChanges } from "./DimensionHistory";
import { DimensionMemberSheet } from "./DimensionMemberSheet";
import { DimensionMembers } from "./DimensionMembers";
import { DimensionOriginals } from "./DimensionOriginals";
import { DIMENSION_VIEWS, STANDING_VISUALS, coverage, fieldText, percent, type DimensionEntry, type DimensionView } from "./dimensionFormat";

/** What each tab is for, as hovering its name says it. */
const VIEW_PURPOSE: Record<DimensionView, string> = {
  members: "The clean values, each with the records holding it and the originals it gathers. Pick members to write the search that finds their records.",
  originals: "Every value exactly as the index holds it, with the member it was cleaned into, or why it belongs to none.",
  changes: "What each build changed: the originals that arrived, left, came back, or moved to another member.",
  builds: "Every build: what it found, how it read the index, how complete the values are, and what it changed.",
  definition: "How the flow declares the dimension: the kind, query and path it reads, how the index stores the field, and the steps that clean each original.",
};

const VIEW_LABEL: Record<DimensionView, string> = {
  members: "Members",
  originals: "Originals",
  changes: "Changes",
  builds: "Builds",
  definition: "Definition",
};

/** A tab's name with what the tab is for on hover; the hover sits on the name, so it never touches the tab's own state. */
function ViewName({ view, children }: { view: DimensionView; children: ReactNode }) {
  return (
    <RichTooltip body={VIEW_PURPOSE[view]}>
      <span>{children}</span>
    </RichTooltip>
  );
}

/** What the dimension's newest build or its declaration asks of the reader, when anything does. */
function Attention({ entry, onBuild }: { entry: DimensionEntry; onBuild: () => void }) {
  const { dimension } = entry;
  const latest = dimension.latest;
  if (latest?.status === "failed") {
    return (
      <Alert variant="destructive" data-testid="dimension-failed">
        <CircleX />
        <AlertTitle>The newest build failed <RelativeTime value={latest.completedUtc ?? latest.startedUtc} absolute={false} /></AlertTitle>
        <AlertDescription className="flex flex-col gap-1.5">
          <span className="break-words">{latest.error ?? "The build kept no reason."}</span>
          <span className="text-muted-foreground">It wrote nothing: what the dimension holds is what the build before it wrote.</span>
          {dimension.declared && (
            <Button size="xs" variant="outline" className="self-start" onClick={onBuild} data-testid="dimension-rebuild">
              <Play />
              Build again
            </Button>
          )}
        </AlertDescription>
      </Alert>
    );
  }

  if (latest?.status === "running") {
    return (
      <Alert className="border-info/40 bg-info/8" data-testid="dimension-running">
        <Loader2 className="animate-spin text-info" />
        <AlertTitle>Building since <RelativeTime value={latest.startedUtc} absolute={false} /></AlertTitle>
        <AlertDescription>The page shows what the build before it wrote until this one has written its values.</AlertDescription>
      </Alert>
    );
  }

  if (dimension.changed) {
    return (
      <Alert className="border-warning/40 bg-warning/8" data-testid="dimension-changed">
        <PencilLine className="text-warning" />
        <AlertTitle>The declaration changed since these values were read</AlertTitle>
        <AlertDescription className="flex flex-wrap items-center gap-x-3 gap-y-1">
          <span>The kind, query, path or clean steps are not the ones the last build read with. Build it again to read with them.</span>
          <Button size="xs" variant="outline" onClick={onBuild} data-testid="dimension-rebuild">
            <Play />
            Build again
          </Button>
        </AlertDescription>
      </Alert>
    );
  }

  if (!dimension.declared) {
    return (
      <Alert data-testid="dimension-undeclared">
        <Info />
        <AlertTitle>{STANDING_VISUALS.undeclared.label}</AlertTitle>
        <AlertDescription>{STANDING_VISUALS.undeclared.hint}</AlertDescription>
      </Alert>
    );
  }

  return null;
}

/**
 * One dimension of the partition: its name, what it reads and how the index stores it, with Build and Export; what its
 * newest build or its declaration asks of the reader; the facts it is read against (members, how complete the values are,
 * what cleaning left out, when it was built); and its five tabs. The tab and the member open in a sheet live in the URL, so
 * a link lands on the same view.
 */
export function DimensionWorkspace({ entry, view, onView, member, onMember, onLaunch }: {
  entry: DimensionEntry;
  view: DimensionView;
  onView: (view: DimensionView) => void;
  /** The member open in its sheet, by id; null for none. */
  member: number | null;
  onMember: (memberId: number | null) => void;
  onLaunch: (launch: DimensionLaunch) => void;
}) {
  const { dimension, flow } = entry;
  const current = dimension.current;
  const covered = current === null ? null : coverage(current);
  const build = () => onLaunch({ pipelineId: flow.pipelineId, repoId: flow.repoId, flowName: flow.name, dimensions: [dimension.name] });

  const cells: SummaryCell[] = [
    {
      label: "Members",
      value: dimension.members.toLocaleString("en-US"),
      caption: `from ${counted(dimension.originals, "original")}`,
      onClick: () => onView("members"),
      testId: "dimension-summary-members",
    },
    {
      label: "Coverage",
      value: covered === null ? "not counted" : percent(covered.share),
      caption: covered === null ? "the build could not count the records it read" : covered.text,
      tone: covered !== null && covered.share < 0.5 ? "warning" : undefined,
      onClick: () => onView("builds"),
      testId: "dimension-summary-coverage",
    },
    {
      label: "Under no member",
      value: (current?.leftOut ?? 0).toLocaleString("en-US"),
      caption: current === null || current.leftOut === 0 ? "every original has a member" : "originals cleaning left out",
      onClick: () => onView("originals"),
      testId: "dimension-summary-left-out",
    },
    {
      label: "Last built",
      value: dimension.lastBuiltUtc === null ? "never" : <RelativeTime value={dimension.lastBuiltUtc} absolute={false} />,
      caption: current === null
        ? "build it to read its values"
        : `by ${current.actor}, ${counted(current.changes.originalsAdded + current.changes.originalsRemoved + current.changes.originalsMoved + current.changes.originalsRestored, "change")}`,
      onClick: () => onView("builds"),
      testId: "dimension-summary-built",
    },
  ];

  return (
    <div className="flex min-w-0 flex-col gap-3" data-testid="dimension-workspace" data-dimension={dimension.name}>
      <div className="flex flex-wrap items-start gap-x-4 gap-y-2">
        <div className="flex min-w-0 flex-1 flex-col gap-1">
          <div className="flex min-w-0 items-center gap-2">
            <StandingGlyph standing={entry.standing} testId="dimension-standing" />
            <h2 className="truncate text-base font-medium" data-testid="dimension-name">{dimension.name}</h2>
            <span className="text-[12px] text-muted-foreground">{STANDING_VISUALS[entry.standing].label}</span>
          </div>
          {dimension.description !== null && <p className="text-[13px] text-muted-foreground">{dimension.description}</p>}
          <div className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[12px] text-muted-foreground">
            <span className="min-w-0 max-w-[26rem] text-foreground"><KindText kind={dimension.kind} /></span>
            <span className="text-muted-foreground/60">·</span>
            <span className="font-mono text-foreground">{dimension.path}</span>
            {dimension.field !== null && (
              <>
                <span className="text-muted-foreground/60">·</span>
                <RichTooltip title="Read as" body={`aggregateBy: ${dimension.field.aggregateBy}`} mono>
                  <span className="underline decoration-dotted underline-offset-2" data-testid="dimension-field">{fieldText(dimension.field)}</span>
                </RichTooltip>
              </>
            )}
            <span className="text-muted-foreground/60">·</span>
            <span>declared by</span>
            <Link to={`/pipelines/${flow.pipelineId}?tab=dimensions`} className="font-mono text-foreground hover:underline">{flow.name}</Link>
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {dimension.dimensionId !== null && (
            <DimensionExportMenu dimensionId={dimension.dimensionId} flowName={flow.name} partition={flow.partition} name={dimension.name} />
          )}
          {dimension.declared && (
            <Button size="sm" onClick={build} disabled={dimension.latest?.status === "running"} data-testid="dimension-build">
              <Play />
              Build
            </Button>
          )}
        </div>
      </div>

      <Attention entry={entry} onBuild={build} />

      {dimension.dimensionId === null
        ? (
          <Card className="gap-0 rounded-lg p-0">
            <EmptyState
              icon={<Shapes />}
              title={`${dimension.name} has not been built in this partition`}
              description="A build reads every distinct value of the path from the OSDU search, pages past the search's limit on distinct values, cleans each into its member, and keeps the originals beside them. Build it to see its members."
              action={dimension.declared
                ? <Button size="sm" onClick={build} data-testid="dimension-build-first"><Play />Build now</Button>
                : undefined}
              data-testid="dimension-not-built"
            />
          </Card>
        )
        : (
          <>
            <SummaryStrip cells={cells} data-testid="dimension-summary" />
            <Tabs value={view} onValueChange={(value) => onView(value as DimensionView)} className="min-w-0 gap-3">
              <div className="border-b border-border">
                <TabsList variant="line" data-testid="dimension-tabs">
                  {DIMENSION_VIEWS.map((name) => (
                    <TabsTrigger key={name} value={name} data-testid={`dimension-tab-${name}`}>
                      <ViewName view={name}>{VIEW_LABEL[name]}</ViewName>
                    </TabsTrigger>
                  ))}
                </TabsList>
              </div>
              <TabsContent value="members">
                <DimensionMembers entry={entry} dimensionId={dimension.dimensionId} onMember={onMember} />
              </TabsContent>
              <TabsContent value="originals">
                <DimensionOriginals dimensionId={dimension.dimensionId} onMember={onMember} />
              </TabsContent>
              <TabsContent value="changes">
                <DimensionChanges dimensionId={dimension.dimensionId} onMember={onMember} />
              </TabsContent>
              <TabsContent value="builds">
                <DimensionBuilds dimensionId={dimension.dimensionId} />
              </TabsContent>
              <TabsContent value="definition">
                <DimensionDefinition entry={entry} />
              </TabsContent>
            </Tabs>
            <DimensionMemberSheet dimensionId={dimension.dimensionId} memberId={member} onClose={() => onMember(null)} />
          </>
        )}
    </div>
  );
}
