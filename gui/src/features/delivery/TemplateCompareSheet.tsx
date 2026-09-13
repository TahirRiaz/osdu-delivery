import { useMemo, useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import {
  ArrowLeftRight, CircleCheck, CirclePlus, Minus, PencilLine, Plus, TriangleAlert, Type, type LucideIcon,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  deliveryApi,
  type DeliveryOsduComparison,
  type DeliveryOsduReferencedFile,
  type DeliveryOsduRelease,
  type DeliveryOsduSchema,
  type DeliveryTemplateChangeImpact,
  type DeliveryTemplateFieldChange,
  type DeliveryTemplateVariableChange,
} from "../../api/delivery";
import { CodeDiffView } from "../../components/CodeDiffView";
import { DataTable, type Column } from "../../components/DataTable";
import { FilterBar } from "../../components/FilterBar";
import { LinkRef } from "../../components/LinkRef";
import { RichTooltip } from "../../components/RichTooltip";
import { SearchInput } from "../../components/SearchInput";
import { StatePill } from "../../components/StatusBadge";
import { HeadClippedText } from "./HeadClippedText";
import { entityName, pathDepth, splitPath } from "./templateFormat";
import { ProblemView, TaskProgress } from "./TemplateSheet";

/** One side of a comparison: a release of the OSDU data definitions, and a version of the kind in it. */
export interface CompareSide {
  release: string;
  kind: string;
}

/** The two versions a comparison opens with. */
export interface CompareStart {
  from: CompareSide;
  to: CompareSide;
}

/** authority:source:entityType, which every version of a kind shares. */
export function kindStem(kind: string): string {
  return kind.split(":").slice(0, 3).join(":");
}

const CHANGE_VISUAL: Record<DeliveryTemplateVariableChange["change"], { tone: "success" | "destructive" | "info"; icon: LucideIcon }> = {
  Added: { tone: "success", icon: Plus },
  Removed: { tone: "destructive", icon: Minus },
  Changed: { tone: "info", icon: PencilLine },
};

const IMPACT_VISUAL: Record<DeliveryTemplateChangeImpact, { tone: "destructive" | "info" | "muted"; icon: LucideIcon; hint: string }> = {
  Breaking: {
    tone: "destructive",
    icon: TriangleAlert,
    hint: "A mapping written for the older version can stop rendering, or render a record the newer version does not accept.",
  },
  Additive: { tone: "info", icon: CirclePlus, hint: "Something a mapping may now use; nothing a mapping of the older version relies on changed." },
  Wording: { tone: "muted", icon: Type, hint: "Only a title or a description reads differently." },
};

const IMPACTS: readonly DeliveryTemplateChangeImpact[] = ["Breaking", "Additive", "Wording"];

/** The file picker's value for the kind's own schema file; a shared schema is picked by its name. */
const KIND_FILE = "kind";

/** The three-part version at the end of a schema file's path (abstract/AbstractFacility.1.1.0.json), or null. */
function fileVersion(path: string | null): string | null {
  const match = path === null ? null : /\.(\d+\.\d+\.\d+)\.json$/.exec(path);
  return match === null ? null : match[1];
}

/** What happened to a shared schema between the versions, as the file picker words it. */
function referencedState(file: DeliveryOsduReferencedFile): string {
  if (file.fromPath === null) {
    return "only in To";
  }

  if (file.toPath === null) {
    return "only in From";
  }

  const before = fileVersion(file.fromPath);
  const after = fileVersion(file.toPath);
  return before !== null && after !== null && before !== after ? `${before} → ${after}` : "changed";
}

const FIELD_LABELS: Record<string, string> = {
  type: "type",
  format: "format",
  pattern: "pattern",
  keyValueType: "free key type",
  unitContext: "unit context",
  role: "written by",
  nested: "nested list",
  required: "required",
  relationships: "points to",
  title: "title",
  description: "description",
};

/** The versions of the kind a release publishes, newest first, and the one a side resolves to: its own, or the newest there. */
function useSideVersions(side: CompareSide, stem: string) {
  const index = useQuery({
    queryKey: ["delivery", "osdu-definitions", "schemas", side.release],
    queryFn: () => deliveryApi.osduSchemas(side.release),
    staleTime: Infinity,
  });
  const versions = useMemo(
    (): DeliveryOsduSchema[] => (index.data?.schemas ?? []).filter((schema) => kindStem(schema.kind) === stem),
    [index.data, stem],
  );
  const kind = versions.some((schema) => schema.kind === side.kind) ? side.kind : versions[0]?.kind ?? null;
  return { index, versions, kind };
}

interface SidePickerProps {
  caption: string;
  side: CompareSide;
  kind: string | null;
  releases: DeliveryOsduRelease[];
  versions: DeliveryOsduSchema[];
  loading: boolean;
  onChange: (side: CompareSide) => void;
  testId: string;
}

function SidePicker({ caption, side, kind, releases, versions, loading, onChange, testId }: SidePickerProps) {
  return (
    <div className="flex min-w-0 flex-col gap-1.5">
      <Label className="text-xs font-normal text-muted-foreground">{caption}</Label>
      <div className="flex flex-wrap items-center gap-2">
        <Select value={side.release} onValueChange={(release) => onChange({ release, kind: side.kind })}>
          <SelectTrigger size="sm" className="h-8 w-32" data-testid={`${testId}-release`} aria-label={`${caption} release`}>
            <SelectValue placeholder={side.release} />
          </SelectTrigger>
          <SelectContent>
            {releases.map((release) => (
              <SelectItem key={release.name} value={release.name}><span className="font-mono">{release.name}</span></SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select value={kind ?? ""} onValueChange={(next) => onChange({ release: side.release, kind: next })} disabled={versions.length === 0}>
          <SelectTrigger size="sm" className="h-8 w-44" data-testid={`${testId}-version`} aria-label={`${caption} version`}>
            <SelectValue placeholder={loading ? "Loading versions" : "Not in this release"} />
          </SelectTrigger>
          <SelectContent>
            {versions.map((schema) => (
              <SelectItem key={schema.kind} value={schema.kind}>
                <span className="font-mono">{schema.version}</span>
                {schema.status !== null && schema.status !== "PUBLISHED" && (
                  <span className="text-[11px] text-muted-foreground">{schema.status.toLowerCase()}</span>
                )}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
    </div>
  );
}

/** What one field went from and to; a rewritten text is summed up, with both wordings on hover. */
function FieldLine({ path, field }: { path: string; field: DeliveryTemplateFieldChange }) {
  const label = FIELD_LABELS[field.field] ?? field.field;
  const wording = field.field === "title" || field.field === "description";
  let value: ReactNode;
  if (wording) {
    value = (
      <RichTooltip title={label} body={`Before: ${field.before ?? "none"}\n\nAfter: ${field.after ?? "none"}`}>
        <span className="underline decoration-dotted underline-offset-2">
          {field.before === null ? "added" : field.after === null ? "removed" : "reworded"}
        </span>
      </RichTooltip>
    );
  } else if (field.before === null || field.after === null) {
    value = <span className="font-mono">{field.after ?? field.before}</span>;
  } else {
    value = (
      <span className="min-w-0 truncate font-mono">
        <span className="text-muted-foreground line-through decoration-muted-foreground/60">{field.before}</span>
        {" → "}
        <span>{field.after}</span>
      </span>
    );
  }

  return (
    <span className="flex min-w-0 items-baseline gap-1.5 text-[12px]" data-testid={`templates-compare-field-${path}-${field.field}`}>
      <span className="shrink-0 text-muted-foreground">{label}</span>
      {value}
    </span>
  );
}

/** The comparison summed up: no change, or how many changes of each impact, and what that means for existing mappings. */
function Verdict({ data }: { data: DeliveryOsduComparison }) {
  if (data.changes.length === 0) {
    const text = data.sameFile
      ? "The two versions are the same schema, file for file."
      : data.onlyIdentifiersDiffer
        ? "The files differ only in the version identifiers each one carries; the schema is the same."
        : "No template variable differs. The files differ in what a template does not hold, such as annotations.";
    return (
      <div className="flex flex-wrap items-center gap-2 text-[13px]" data-testid="templates-compare-verdict">
        <StatePill tone="success" label="No change" icon={CircleCheck} testId="templates-compare-no-change" />
        <span className="text-muted-foreground">{text}</span>
      </div>
    );
  }

  return (
    <div className="flex flex-wrap items-center gap-2 text-[13px]" data-testid="templates-compare-verdict">
      {IMPACTS.map((impact) => {
        const count = impact === "Breaking" ? data.breaking : impact === "Additive" ? data.additive : data.wording;
        return count === 0 ? null : (
          <RichTooltip key={impact} title={impact} body={IMPACT_VISUAL[impact].hint}>
            <span>
              <StatePill
                tone={IMPACT_VISUAL[impact].tone}
                label={`${count} ${impact.toLowerCase()}`}
                icon={IMPACT_VISUAL[impact].icon}
                testId={`templates-compare-count-${impact.toLowerCase()}`}
              />
            </span>
          </RichTooltip>
        );
      })}
      <span className="text-muted-foreground">
        {`${data.unchanged.toLocaleString()} variable${data.unchanged === 1 ? "" : "s"} unchanged. `}
        {data.breaking > 0
          ? "A mapping written for the older version needs a look at the breaking changes before it moves."
          : "Nothing a mapping written for the older version relies on changed."}
        {data.sameFile && data.referencedFiles.length > 0 && (
          <span data-testid="templates-compare-from-shared">
            {` The kind's own file is the same in both; these changes come from the ${data.referencedFiles.length} shared schema${data.referencedFiles.length === 1 ? "" : "s"} it refers to that changed, listed under Published files.`}
          </span>
        )}
      </span>
    </div>
  );
}

interface TemplateCompareSheetProps {
  start: CompareStart;
  onClose: () => void;
}

/**
 * Two versions of an OSDU kind compared, each picked from any release of the OSDU data definitions: a verdict first (no
 * change, or how many breaking, additive and wording changes), then every variable that differs with what changed about
 * it, and the two schema files side by side exactly as the repository publishes them.
 */
export function TemplateCompareSheet({ start, onClose }: TemplateCompareSheetProps) {
  const stem = kindStem(start.to.kind);
  const [from, setFrom] = useState<CompareSide>(start.from);
  const [to, setTo] = useState<CompareSide>(start.to);
  const [filter, setFilter] = useState("");
  const [showWording, setShowWording] = useState(true);
  const [pickedFile, setPickedFile] = useState(KIND_FILE);

  const releases = useQuery({
    queryKey: ["delivery", "osdu-definitions", "releases"],
    queryFn: deliveryApi.osduReleases,
    staleTime: 10 * 60_000,
  });
  const left = useSideVersions(from, stem);
  const right = useSideVersions(to, stem);

  const comparison = useQuery({
    queryKey: ["delivery", "osdu-definitions", "compare", from.release, left.kind, to.release, right.kind],
    queryFn: () => deliveryApi.osduCompare({ release: from.release, kind: left.kind! }, { release: to.release, kind: right.kind! }),
    enabled: left.kind !== null && right.kind !== null,
    staleTime: Infinity,
  });

  const rows = useMemo(() => {
    if (comparison.data === undefined) {
      return undefined;
    }

    const term = filter.trim().toLowerCase();
    return comparison.data.changes.filter((change) => (showWording || change.impact !== "Wording")
      && (term === "" || change.path.toLowerCase().includes(term)));
  }, [comparison.data, filter, showWording]);

  const columns: Column<DeliveryTemplateVariableChange>[] = [
    {
      id: "path",
      header: "Variable",
      fill: true,
      floor: 240,
      render: (change) => {
        const { parent, leaf } = splitPath(change.path);
        return (
          <span className="flex min-w-0 font-mono text-[12px]" style={{ paddingLeft: pathDepth(change.path) * 14 }}>
            <HeadClippedText head={parent} body={leaf} title="Variable" />
          </span>
        );
      },
    },
    {
      id: "change",
      header: "Change",
      render: (change) => (
        <StatePill
          tone={CHANGE_VISUAL[change.change].tone}
          label={change.change}
          icon={CHANGE_VISUAL[change.change].icon}
          testId={`templates-compare-change-${change.path}`}
        />
      ),
    },
    {
      id: "impact",
      header: "Impact",
      render: (change) => (
        <RichTooltip title={change.impact} body={IMPACT_VISUAL[change.impact].hint}>
          <span>
            <StatePill
              tone={IMPACT_VISUAL[change.impact].tone}
              label={change.impact}
              icon={IMPACT_VISUAL[change.impact].icon}
              testId={`templates-compare-impact-${change.path}`}
            />
          </span>
        </RichTooltip>
      ),
    },
    {
      id: "what",
      header: "What changed",
      fill: true,
      floor: 220,
      render: (change) => (
        <span className="flex min-w-0 flex-col gap-0.5 py-0.5">
          {change.fields.map((field) => <FieldLine key={field.field} path={change.path} field={field} />)}
        </span>
      ),
    },
  ];

  const releaseList = releases.data?.releases ?? [];
  const data = comparison.data;
  // A shared schema picked for another pair of versions falls back to the kind's own file when this pair has no such difference.
  const shownShared = data?.referencedFiles.find((file) => file.name === pickedFile) ?? null;
  const fileKey = shownShared === null ? KIND_FILE : shownShared.name;
  const missing = (!left.index.isPending && left.kind === null) || (!right.index.isPending && right.kind === null);

  return (
    <Sheet open onOpenChange={(next) => { if (!next) { onClose(); } }}>
      <SheetContent className="w-full gap-0 sm:max-w-6xl" data-testid="templates-compare-sheet">
        <SheetHeader>
          <SheetTitle className="pr-6 text-[15px]">{`Compare ${entityName(start.to.kind)} versions`}</SheetTitle>
          <SheetDescription>
            Two versions from the OSDU data definitions, each from any release, compared variable by variable and as the files
            are published.
          </SheetDescription>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-3 overflow-y-auto px-4 pb-4">
          <div className="flex flex-wrap items-end gap-3">
            <SidePicker
              caption="From"
              side={{ release: from.release, kind: left.kind ?? from.kind }}
              kind={left.kind}
              releases={releaseList}
              versions={left.versions}
              loading={left.index.isPending}
              onChange={setFrom}
              testId="templates-compare-from"
            />
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label="Swap the two versions"
              onClick={() => { setFrom({ release: to.release, kind: right.kind ?? to.kind }); setTo({ release: from.release, kind: left.kind ?? from.kind }); }}
              data-testid="templates-compare-swap"
            >
              <ArrowLeftRight />
            </Button>
            <SidePicker
              caption="To"
              side={{ release: to.release, kind: right.kind ?? to.kind }}
              kind={right.kind}
              releases={releaseList}
              versions={right.versions}
              loading={right.index.isPending}
              onChange={setTo}
              testId="templates-compare-to"
            />
          </div>

          {releases.isError && <ProblemView error={releases.error} testId="templates-compare-error" />}
          {left.index.isError && <ProblemView error={left.index.error} testId="templates-compare-error" />}
          {right.index.isError && <ProblemView error={right.index.error} testId="templates-compare-error" />}
          {missing && (
            <p className="text-[13px] text-muted-foreground" data-testid="templates-compare-missing">
              {`${entityName(start.to.kind)} is not in ${left.kind === null ? from.release : to.release}. Pick another release.`}
            </p>
          )}
          {comparison.isError && <ProblemView error={comparison.error} testId="templates-compare-error" />}
          {comparison.fetchStatus === "fetching" && data === undefined && (
            <TaskProgress label="Reading both versions from the OSDU data definitions and comparing them" testId="templates-compare-progress" />
          )}

          {data !== undefined && (
            <>
              <Verdict data={data} />
              <Tabs defaultValue="changes">
                <TabsList data-testid="templates-compare-tabs">
                  <TabsTrigger value="changes" data-testid="templates-compare-tab-changes">
                    {`Changes (${data.changes.length.toLocaleString()})`}
                  </TabsTrigger>
                  <TabsTrigger value="files" data-testid="templates-compare-tab-files">Published files</TabsTrigger>
                </TabsList>
                <TabsContent value="changes" className="flex flex-col gap-3 pt-1">
                  <FilterBar>
                    <SearchInput
                      value={filter}
                      onChange={setFilter}
                      placeholder="Variable path"
                      label="Filter the changes"
                      testId="templates-compare-filter"
                    />
                    <Label className="flex items-center gap-2 text-[13px] font-normal">
                      <Switch checked={showWording} onCheckedChange={setShowWording} data-testid="templates-compare-show-wording" />
                      Wording changes
                    </Label>
                  </FilterBar>
                  <DataTable
                    columns={columns}
                    rows={rows}
                    rowKey={(change) => change.path}
                    emptyMessage={data.changes.length === 0 ? "No variable differs between the two versions." : "No change matches the filter."}
                    data-testid="templates-compare-changes-table"
                  />
                </TabsContent>
                <TabsContent value="files" className="flex flex-col gap-2 pt-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <Select value={fileKey} onValueChange={setPickedFile}>
                      <SelectTrigger size="sm" className="h-8 w-96 max-w-full" data-testid="templates-compare-file-picker" aria-label="The file to compare">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value={KIND_FILE}>
                          <span className="font-mono">{data.to.path.slice(data.to.path.lastIndexOf("/") + 1)}</span>
                          <span className="text-[11px] text-muted-foreground">{data.sameFile ? "the kind, the same" : "the kind"}</span>
                        </SelectItem>
                        {data.referencedFiles.map((file) => (
                          <SelectItem key={file.name} value={file.name}>
                            <span className="font-mono">{file.name}</span>
                            <span className="text-[11px] text-muted-foreground">{referencedState(file)}</span>
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                    <span className="text-xs text-muted-foreground" data-testid="templates-compare-referenced-summary">
                      {data.referencedFiles.length === 0
                        ? `Every shared schema it refers to is the same in both (${data.sameReferencedFiles}).`
                        : `${data.referencedFiles.length} shared schema${data.referencedFiles.length === 1 ? "" : "s"} it refers to ${data.referencedFiles.length === 1 ? "differs" : "differ"}; ${data.sameReferencedFiles} ${data.sameReferencedFiles === 1 ? "is" : "are"} the same.`}
                    </span>
                  </div>
                  {shownShared === null ? (
                    <>
                      <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
                        <span className="inline-flex items-center gap-1">
                          <span className="font-mono">{`${data.from.release.name} ${data.from.path}`}</span>
                          <LinkRef url={data.from.webUrl} title="From, in the OSDU data definitions" testId="templates-compare-from-link" copyTestId="templates-compare-from-copy" />
                        </span>
                        <span data-testid="templates-compare-file-state">
                          {data.sameFile ? "The files are identical." : data.onlyIdentifiersDiffer ? "The files differ only in their version identifiers." : null}
                        </span>
                        <span className="inline-flex items-center gap-1">
                          <span className="font-mono">{`${data.to.release.name} ${data.to.path}`}</span>
                          <LinkRef url={data.to.webUrl} title="To, in the OSDU data definitions" testId="templates-compare-to-link" copyTestId="templates-compare-to-copy" />
                        </span>
                      </div>
                      <CodeDiffView
                        key={KIND_FILE}
                        original={data.from.fileText}
                        modified={data.to.fileText}
                        originalLabel={`${data.from.kind}  (${data.from.release.name}, template ${data.from.templateVersion})`}
                        modifiedLabel={`${data.to.kind}  (${data.to.release.name}, template ${data.to.templateVersion})`}
                        language="json"
                        height={620}
                        data-testid="templates-compare-files"
                      />
                    </>
                  ) : (
                    <>
                      <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
                        <span className="inline-flex items-center gap-1">
                          <span className="font-mono">{`${data.from.release.name} ${shownShared.fromPath ?? "does not refer to it"}`}</span>
                          <LinkRef url={shownShared.fromWebUrl} title="From, in the OSDU data definitions" testId="templates-compare-from-link" copyTestId="templates-compare-from-copy" />
                        </span>
                        <span className="inline-flex items-center gap-1">
                          <span className="font-mono">{`${data.to.release.name} ${shownShared.toPath ?? "does not refer to it"}`}</span>
                          <LinkRef url={shownShared.toWebUrl} title="To, in the OSDU data definitions" testId="templates-compare-to-link" copyTestId="templates-compare-to-copy" />
                        </span>
                      </div>
                      <CodeDiffView
                        key={shownShared.name}
                        original={shownShared.fromText ?? ""}
                        modified={shownShared.toText ?? ""}
                        originalLabel={`${data.from.release.name}  ${shownShared.fromPath ?? "not referred to"}`}
                        modifiedLabel={`${data.to.release.name}  ${shownShared.toPath ?? "not referred to"}`}
                        language="json"
                        height={620}
                        data-testid="templates-compare-files"
                      />
                    </>
                  )}
                </TabsContent>
              </Tabs>
            </>
          )}
        </div>
      </SheetContent>
    </Sheet>
  );
}
