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
import { DimensionKeys } from "./DimensionKeys";
import { DimensionValueSheet } from "./DimensionValueSheet";
import { DimensionValues } from "./DimensionValues";
import { DIMENSION_VIEWS, STANDING_VISUALS, coverage, fieldText, percent, type DimensionEntry, type DimensionView } from "./dimensionFormat";

/** What each tab is for, as hovering its name says it. */
const VIEW_PURPOSE: Record<DimensionView, string> = {
  values: "The human-friendly values a person picks, each with the records holding it, the keys it stands for and the search filter finding its records. Pick values to write the search that finds their records.",
  keys: "Every key exactly as the index holds it (an id, for a reference), with the label read for it, the value it belongs to, and the search filter finding exactly its records.",
  changes: "What each build changed: the keys that arrived, left, came back, or moved to another value.",
  builds: "Every build: what it found, how it read the index and the labels, how complete the keys are, and what it changed.",
  definition: "How the flow declares the dimension: the kind, query and path it reads, where a key's label and attributes are read, how the index stores the field, and the steps that clean each value.",
};

const VIEW_LABEL: Record<DimensionView, string> = {
  values: "Values",
  keys: "Keys",
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
          <span>The kind, query, path, label or clean steps are not the ones the last build read with. Build it again to read with them.</span>
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
 * One dimension of the partition: its name, what it reads (and where a key's label is read) and how the index stores it,
 * with Build and Export; what its newest build or its declaration asks of the reader; the facts it is read against (values,
 * how complete the keys are, what cleaning left out, when it was built); and its five tabs. The tab and the value open in a
 * sheet live in the URL, so a link lands on the same view.
 */
export function DimensionWorkspace({ entry, view, onView, value, onValue, onLaunch }: {
  entry: DimensionEntry;
  view: DimensionView;
  onView: (view: DimensionView) => void;
  /** The value open in its sheet, by id; null for none. */
  value: number | null;
  onValue: (valueId: number | null) => void;
  onLaunch: (launch: DimensionLaunch) => void;
}) {
  const { dimension, flow } = entry;
  const current = dimension.current;
  const covered = current === null ? null : coverage(current);
  const labelled = dimension.label.length > 0;
  const build = () => onLaunch({ pipelineId: flow.pipelineId, repoId: flow.repoId, flowName: flow.name, dimensions: [dimension.name] });

  const cells: SummaryCell[] = [
    {
      label: "Values",
      value: dimension.values.toLocaleString("en-US"),
      caption: `from ${counted(dimension.keys, "key")}${labelled && current !== null && current.unlabelled > 0 ? `, ${current.unlabelled.toLocaleString("en-US")} unlabelled` : ""}`,
      tone: labelled && current !== null && current.unlabelled > 0 && current.unlabelled >= current.labelled ? "warning" : undefined,
      onClick: () => onView("values"),
      testId: "dimension-summary-values",
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
      label: "Of no value",
      value: (current?.leftOut ?? 0).toLocaleString("en-US"),
      caption: current === null || current.leftOut === 0 ? "every key has a value" : "keys cleaning left out",
      onClick: () => onView("keys"),
      testId: "dimension-summary-left-out",
    },
    {
      label: "Last built",
      value: dimension.lastBuiltUtc === null ? "never" : <RelativeTime value={dimension.lastBuiltUtc} absolute={false} />,
      caption: current === null
        ? "build it to read its values"
        : `by ${current.actor}, ${counted(current.changes.keysAdded + current.changes.keysRemoved + current.changes.keysMoved + current.changes.keysRestored, "change")}`,
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
            {labelled && (
              <>
                <span className="text-muted-foreground/60">·</span>
                <RichTooltip
                  title="Labelled by"
                  body={`Each key names a record; its value is read there, through ${dimension.label.join(", then ")}. The key stays the id, so every filter still compares it.`}
                >
                  <span className="underline decoration-dotted underline-offset-2" data-testid="dimension-label">
                    labelled by <span className="font-mono text-foreground">{dimension.label.at(-1)}</span>
                  </span>
                </RichTooltip>
              </>
            )}
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
              description="A build reads every distinct key of the path from the OSDU search, paging past the search's limit on distinct values, reads each key's label from the record it names when the dimension asks for one, and cleans it into its value, keeping every key beside its value. Build it to see its values."
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
              <TabsContent value="values">
                <DimensionValues entry={entry} dimensionId={dimension.dimensionId} onValue={onValue} />
              </TabsContent>
              <TabsContent value="keys">
                <DimensionKeys dimensionId={dimension.dimensionId} labelled={labelled} attributes={dimension.attributes} onValue={onValue} />
              </TabsContent>
              <TabsContent value="changes">
                <DimensionChanges dimensionId={dimension.dimensionId} onValue={onValue} />
              </TabsContent>
              <TabsContent value="builds">
                <DimensionBuilds dimensionId={dimension.dimensionId} />
              </TabsContent>
              <TabsContent value="definition">
                <DimensionDefinition entry={entry} />
              </TabsContent>
            </Tabs>
            <DimensionValueSheet dimensionId={dimension.dimensionId} labelled={labelled} attributes={dimension.attributes} valueId={value} onClose={() => onValue(null)} />
          </>
        )}
    </div>
  );
}
