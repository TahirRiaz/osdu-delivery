import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { ChevronsUpDown, CircleX, Info, Loader2, PencilLine, Play, Shapes } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { EmptyState } from "@/components/EmptyState";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import { KindText } from "../KindText";
import { counted } from "../assertions/assertionFormat";
import { DimensionExportMenu, DimensionsCrumb, StandingGlyph } from "./DimensionBadges";
import type { DimensionLaunch } from "./DimensionBuildDialog";
import { DimensionDefinition } from "./DimensionDefinition";
import { DimensionBuilds, DimensionChanges } from "./DimensionHistory";
import { DimensionKeys } from "./DimensionKeys";
import { DimensionRemoveButton } from "./DimensionRemoveButton";
import { DimensionTableGrid } from "./DimensionTableGrid";
import { DimensionValueSheet } from "./DimensionValueSheet";
import { DimensionValues } from "./DimensionValues";
import { DIMENSION_VIEWS, STANDING_VISUALS, coverage, percent, type DimensionEntry, type DimensionView } from "./dimensionFormat";

/** What each tab is for, as hovering its name says it. */
const VIEW_PURPOSE: Record<DimensionView, string> = {
  table: "The dimension as one table, as the database holds it: a row per key and value it collects, with the key's value, a column per attribute, and the records of the row. What a query, a report or a cascade of selects reads.",
  values: "The human-friendly values a person picks, each with the records holding it, the keys it stands for, its attributes and the search finding its records. Pick values to write the search that finds their records.",
  keys: "Every key exactly as the index holds it (an id, for a reference), with the value it belongs to, its attributes, and the search finding exactly its records.",
  changes: "What each build changed: the keys that arrived, left, came back, or moved to another value.",
  builds: "Every build: what it found, how it read the index and the labels, how complete the keys are, and what it changed.",
  definition: "How the flow's YAML builds the dimension: the records it searches, the records it reads by id through each key, every path against the template of the records it is read from, and the column of the table each one writes. Point at a line of the YAML to see what it does.",
};

const VIEW_LABEL: Record<DimensionView, string> = {
  table: "Table",
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
function Attention({ entry, onBuild, onRemoved }: { entry: DimensionEntry; onBuild: () => void; onRemoved?: () => void }) {
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
              Run pipeline
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
          <span>The kind, query, path, label or clean steps are not the ones the last build read with. Run the pipeline to read with them.</span>
          <Button size="xs" variant="outline" onClick={onBuild} data-testid="dimension-rebuild">
            <Play />
            Run pipeline
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
        <AlertDescription className="flex flex-wrap items-center gap-x-3 gap-y-1">
          <span>{STANDING_VISUALS.undeclared.hint}</span>
          <DimensionRemoveButton dimension={dimension} flowName={entry.flow.name} onRemoved={onRemoved} />
        </AlertDescription>
      </Alert>
    );
  }

  return null;
}

/** One fact of the line under a dimension's name: a number and what it counts, which opens the tab that explains it. */
function Fact({ children, hint, tone, onClick, testId }: {
  children: ReactNode;
  hint: string;
  tone?: "warning";
  onClick: () => void;
  testId: string;
}) {
  return (
    <RichTooltip body={hint}>
      <button
        type="button"
        onClick={onClick}
        className={cn(
          "rounded-sm outline-none hover:text-foreground hover:underline focus-visible:underline",
          tone === "warning" ? "text-warning" : undefined,
        )}
        data-testid={testId}
      >
        {children}
      </button>
    </RichTooltip>
  );
}

/** The other dimensions of the partition, one click away from the one in view. */
function Switcher({ entry, siblings, onOpen }: { entry: DimensionEntry; siblings: DimensionEntry[]; onOpen: (ref: string) => void }) {
  if (siblings.filter((sibling) => sibling.ref !== entry.ref).length === 0) {
    return null;
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon" className="size-6 text-muted-foreground" aria-label="Open another dimension" data-testid="dimension-switcher">
          <ChevronsUpDown />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="max-h-80 min-w-64 overflow-y-auto">
        {siblings.map((sibling) => (
          <DropdownMenuItem
            key={sibling.ref}
            onSelect={() => onOpen(sibling.ref)}
            className={cn("gap-2", sibling.ref === entry.ref && "bg-accent/60")}
            data-testid="dimension-switcher-item"
          >
            <StandingGlyph standing={sibling.standing} />
            <span className="min-w-0 flex-1 truncate">{sibling.dimension.name}</span>
            <span className="shrink-0 font-mono text-[11px] tabular-nums text-muted-foreground">{sibling.dimension.values.toLocaleString("en-US")}</span>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/**
 * One dimension of the partition, on the whole width of the page. Its heading is one block: the way back to every
 * dimension, its name and where it stands, the other dimensions a click away, Export and Build; under it, in a line, the
 * facts it is read against (values and keys, keys without a label, keys of no value, how many records hold a key, when it
 * was built), each opening the tab that explains it, and what it reads. Then what its newest build or its declaration
 * asks of the reader, and its tabs, whose grids scroll inside the page: the dimension as one table first, then its values,
 * its keys, what builds changed, the builds and the definition. The tab and the value open in a sheet live in the URL, so
 * a link lands on the same view.
 */
export function DimensionWorkspace({ entry, siblings, view, onView, value, onValue, onLaunch, onOpen, onRemoved }: {
  entry: DimensionEntry;
  /** The dimensions the switcher lists, the one in view among them. */
  siblings: DimensionEntry[];
  view: DimensionView;
  onView: (view: DimensionView) => void;
  /** The value open in its sheet, by id; null for none. */
  value: number | null;
  onValue: (valueId: number | null) => void;
  onLaunch: (launch: DimensionLaunch) => void;
  /** Opens another dimension by its link name, or every dimension for null. */
  onOpen: (ref: string | null) => void;
  /** Called once an admin removed the dimension, which its flow no longer declares. */
  onRemoved?: () => void;
}) {
  const { dimension, flow } = entry;
  const current = dimension.current;
  const covered = current === null ? null : coverage(current);
  const labelled = dimension.label.length > 0;
  const build = () => onLaunch({ pipelineId: flow.pipelineId, repoId: flow.repoId, flowName: flow.name, dimensions: [dimension.name] });
  const dot = <span className="text-muted-foreground/50" aria-hidden>·</span>;
  const changes = current === null ? 0 : current.changes.keysAdded + current.changes.keysRemoved + current.changes.keysMoved + current.changes.keysRestored;

  return (
    <div className="flex min-w-0 flex-col gap-3" data-testid="dimension-workspace" data-dimension={dimension.name}>
      <PageHeader
        title={(
          <span className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-1">
            <DimensionsCrumb onBack={() => onOpen(null)} />
            <StandingGlyph standing={entry.standing} testId="dimension-standing" />
            <span className="min-w-0 truncate" data-testid="dimension-name">{dimension.name}</span>
            <Switcher entry={entry} siblings={siblings} onOpen={onOpen} />
            {entry.standing !== "built" && <span className="text-[12px] font-normal text-muted-foreground">{STANDING_VISUALS[entry.standing].label}</span>}
            {dimension.description !== null && (
              <RichTooltip title="What it is for" body={dimension.description}>
                <Info className="size-4 shrink-0 text-muted-foreground" aria-label="What the dimension is for" data-testid="dimension-description" />
              </RichTooltip>
            )}
          </span>
        )}
        subtitle={(
          <div className="flex flex-col gap-0.5">
            {dimension.dimensionId !== null && (
              <div className="flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[13px]" data-testid="dimension-summary">
                <Fact hint="The human-friendly values the dimension holds now." onClick={() => onView("values")} testId="dimension-summary-values">
                  <span className="font-mono font-medium tabular-nums text-foreground">{dimension.values.toLocaleString("en-US")}</span> {dimension.values === 1 ? "value" : "values"}
                </Fact>
                <span>from</span>
                <Fact hint="The keys the values stand for, each exactly as the index holds it." onClick={() => onView("keys")} testId="dimension-summary-keys">
                  <span className="font-mono font-medium tabular-nums text-foreground">{dimension.keys.toLocaleString("en-US")}</span> {dimension.keys === 1 ? "key" : "keys"}
                </Fact>
                {labelled && current !== null && current.unlabelled > 0 && (
                  <>
                    {dot}
                    <Fact
                      hint={`No label was read for these keys: the record a key names is not in the search, or holds nothing where the label is read. ${dimension.unlabelled === null ? "Each is valued by the code its id ends with." : `They are valued ${dimension.unlabelled}.`} The build's notes say why.`}
                      tone={current.unlabelled >= current.labelled ? "warning" : undefined}
                      onClick={() => onView("builds")}
                      testId="dimension-summary-unlabelled"
                    >
                      <span className="font-mono tabular-nums">{current.unlabelled.toLocaleString("en-US")}</span> without a label
                    </Fact>
                  </>
                )}
                {current !== null && current.leftOut > 0 && (
                  <>
                    {dot}
                    <Fact hint="Keys cleaning left out of every value. The Keys tab lists them under Of no value." tone="warning" onClick={() => onView("keys")} testId="dimension-summary-left-out">
                      <span className="font-mono tabular-nums">{current.leftOut.toLocaleString("en-US")}</span> of no value
                    </Fact>
                  </>
                )}
                {covered !== null && (
                  <>
                    {dot}
                    <Fact hint={`${covered.text}.`} tone={covered.share < 0.5 ? "warning" : undefined} onClick={() => onView("builds")} testId="dimension-summary-coverage">
                      <span className="font-mono tabular-nums">{percent(covered.share)}</span> of records hold a key
                    </Fact>
                  </>
                )}
                {dot}
                <Fact
                  hint={current === null ? "No build has completed yet." : `Built by ${current.actor}; it changed ${counted(changes, "key")}.`}
                  onClick={() => onView("builds")}
                  testId="dimension-summary-built"
                >
                  {dimension.lastBuiltUtc === null ? "never built" : <>built <RelativeTime value={dimension.lastBuiltUtc} absolute={false} /></>}
                </Fact>
              </div>
            )}
            <div className="flex min-w-0 flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[12px]">
              <span className="min-w-0 max-w-[26rem]"><KindText kind={dimension.kind} /></span>
              {dot}
              <span className="font-mono text-foreground">{dimension.path}</span>
              {dot}
              <span>declared by</span>
              <Link to={`/pipelines/${flow.pipelineId}?tab=dimensions`} className="font-mono text-foreground hover:underline">{flow.name}</Link>
            </div>
          </div>
        )}
        actions={(
          <>
            {dimension.dimensionId !== null && (
              <DimensionExportMenu dimensionId={dimension.dimensionId} flowName={flow.name} partition={flow.partition} name={dimension.name} />
            )}
            {dimension.declared && (
              <RichTooltip body={`Runs ${flow.name} with this dimension picked: it reads the dimension's keys, labels and attributes again and rewrites what changed. The dialog can pick the flow's other dimensions too.`}>
                <Button size="sm" onClick={build} disabled={dimension.latest?.status === "running"} data-testid="dimension-build">
                  <Play />
                  Run pipeline
                </Button>
              </RichTooltip>
            )}
          </>
        )}
      />

      <Attention entry={entry} onBuild={build} onRemoved={onRemoved} />

      {dimension.dimensionId === null
        ? (
          <>
            <Card className="gap-0 rounded-lg p-0">
              <EmptyState
                icon={<Shapes />}
                title={`${dimension.name} has not been built in this partition`}
                description="A build reads every distinct key of the path from the OSDU search, paging past the search's limit on distinct values, reads each key's label from the record it names when the dimension asks for one, and cleans it into its value, keeping every key beside its value. Below is how its YAML builds it. Run the flow's pipeline to see its values."
                action={dimension.declared
                  ? <Button size="sm" onClick={build} data-testid="dimension-build-first"><Play />Run pipeline</Button>
                  : undefined}
                data-testid="dimension-not-built"
              />
            </Card>
            {dimension.declared && <DimensionDefinition entry={entry} />}
          </>
        )
        : (
          <>
            <Tabs value={view} onValueChange={(next) => onView(next as DimensionView)} className="min-w-0 gap-2">
              <div className="border-b border-border">
                <TabsList variant="line" data-testid="dimension-tabs">
                  {DIMENSION_VIEWS.map((name) => (
                    <TabsTrigger key={name} value={name} data-testid={`dimension-tab-${name}`}>
                      <ViewName view={name}>{VIEW_LABEL[name]}</ViewName>
                    </TabsTrigger>
                  ))}
                </TabsList>
              </div>
              <TabsContent value="table">
                <DimensionTableGrid
                  dimensionId={dimension.dimensionId}
                  table={dimension.table}
                  keyColumn={dimension.keyColumn}
                  valueColumn={dimension.valueColumn}
                  attributes={dimension.attributes}
                  unlabelled={dimension.unlabelled}
                />
              </TabsContent>
              <TabsContent value="values">
                <DimensionValues entry={entry} dimensionId={dimension.dimensionId} onValue={onValue} />
              </TabsContent>
              <TabsContent value="keys">
                <DimensionKeys
                  dimensionId={dimension.dimensionId}
                  keyColumn={dimension.keyColumn}
                  valueColumn={dimension.valueColumn}
                  labelled={labelled}
                  unlabelled={dimension.unlabelled}
                  attributes={dimension.attributes}
                  total={dimension.keys}
                  onValue={onValue}
                />
              </TabsContent>
              <TabsContent value="changes">
                <DimensionChanges dimensionId={dimension.dimensionId} keyColumn={dimension.keyColumn} valueColumn={dimension.valueColumn} onValue={onValue} />
              </TabsContent>
              <TabsContent value="builds">
                <DimensionBuilds dimensionId={dimension.dimensionId} />
              </TabsContent>
              <TabsContent value="definition">
                <DimensionDefinition entry={entry} />
              </TabsContent>
            </Tabs>
            <DimensionValueSheet
              dimensionId={dimension.dimensionId}
              keyColumn={dimension.keyColumn}
              labelled={labelled}
              attributes={dimension.attributes}
              valueId={value}
              onClose={() => onValue(null)}
            />
          </>
        )}
    </div>
  );
}
