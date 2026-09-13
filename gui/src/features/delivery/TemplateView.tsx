import { useMemo, useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { Cloud, Cog, Save, TriangleAlert, type LucideIcon } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import {
  deliveryApi,
  type DeliveryTemplateDetail,
  type DeliveryTemplateRole,
  type DeliveryTemplateVariable,
} from "../../api/delivery";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { FilterBar } from "../../components/FilterBar";
import { RelativeTime } from "../../components/RelativeTime";
import { SearchInput } from "../../components/SearchInput";
import { StatePill } from "../../components/StatusBadge";
import { TruncatedText } from "../../components/TruncatedText";
import { pathDepth, roleLabel, shapeText, splitPath } from "./templateFormat";

interface TemplateViewProps {
  detail: DeliveryTemplateDetail;
  /** The schema being looked at before it is saved, for the Schema JSON tab. Without it, the saved template's schema is loaded. */
  previewSchema?: Record<string, unknown>;
  /** The header's actions: save or delete. */
  actions?: ReactNode;
}

/** Whether a variable matches the filter text: its path, title, description or the entity types it points to. */
function matches(variable: DeliveryTemplateVariable, term: string): boolean {
  return term === ""
    || variable.path.toLowerCase().includes(term)
    || (variable.title ?? "").toLowerCase().includes(term)
    || (variable.description ?? "").toLowerCase().includes(term)
    || variable.relationships.some((relationship) => relationship.toLowerCase().includes(term));
}

/**
 * How a variable no mapping may fill stands out from the ones a mapping fills: a glyph and a tone per writer, carried
 * by both the path and its label, so a row OSDU sets reads differently from a row OSDU Delivery writes at a glance.
 * Null for a variable a mapping fills, which keeps the plain look.
 */
function roleVisual(role: DeliveryTemplateRole): { label: string; tone: "info" | "warning"; icon: LucideIcon; textClass: string } | null {
  const label = roleLabel(role);
  if (label === null) {
    return null;
  }

  return role === "Osdu"
    ? { label, tone: "info", icon: Cloud, textClass: "text-info" }
    : { label, tone: "warning", icon: Cog, textClass: "text-warning" };
}

/** The schema as JSON: the one being previewed, or the saved template's, loaded when the tab opens. */
function SchemaJson({ detail, previewSchema }: { detail: DeliveryTemplateDetail; previewSchema?: Record<string, unknown> }) {
  const saved = useQuery({
    queryKey: ["delivery", "templates", "schema", detail.kind, detail.version],
    queryFn: () => deliveryApi.templateSchema(detail.kind, detail.version),
    enabled: previewSchema === undefined,
  });
  const previewText = useMemo(
    () => (previewSchema === undefined ? null : JSON.stringify(previewSchema, null, 2)),
    [previewSchema],
  );

  if (previewText !== null) {
    return <CodeView value={previewText} language="json" height={560} data-testid="templates-view-schema-json" />;
  }

  if (saved.isError) {
    return isApiError(saved.error)
      ? <CorrelationError error={saved.error} />
      : <p className="text-[13px] text-destructive">{String(saved.error)}</p>;
  }

  if (saved.data === undefined) {
    return <Skeleton className="h-[560px] w-full rounded-lg" />;
  }

  return <CodeView value={saved.data} language="json" height={560} data-testid="templates-view-schema-json" />;
}

/**
 * A template laid out for reading: the kind and version, whether and where from it was saved, every variable with its
 * shape, requiredness, relationships, unit context and OSDU's description, and the schema itself. The Templates page shows
 * a saved version, an OSDU schema fetched for a look, and an imported file through this one view.
 */
export function TemplateView({ detail, previewSchema, actions }: TemplateViewProps) {
  const [filter, setFilter] = useState("");
  const [showWritten, setShowWritten] = useState(false);
  const [showNested, setShowNested] = useState(false);

  const hasCacheTypes = detail.variables.some((variable) => variable.cacheTypes.length > 0);
  const rows = useMemo(() => {
    const term = filter.trim().toLowerCase();
    return detail.variables.filter((variable) => (showWritten || variable.role === "Mapping")
      && (showNested || !variable.nested)
      && matches(variable, term));
  }, [detail.variables, filter, showWritten, showNested]);

  const columns = useMemo<Column<DeliveryTemplateVariable>[]>(() => {
    const list: Column<DeliveryTemplateVariable>[] = [
      {
        id: "path",
        header: "Variable",
        render: (variable) => {
          const { parent, leaf } = splitPath(variable.path);
          const role = roleVisual(variable.role);
          return (
            <span className="inline-flex items-center gap-1.5" style={{ paddingLeft: pathDepth(variable.path) * 14 }}>
              <span
                className={cn("font-mono text-[12px]", role?.textClass)}
                data-testid={`templates-view-variable-${variable.path}`}
                data-role={variable.role}
              >
                <span className={role === null ? "text-muted-foreground" : "opacity-70"}>{parent}</span>
                <span className="font-medium">{leaf}</span>
              </span>
              {role !== null && (
                <StatePill tone={role.tone} label={role.label} icon={role.icon} testId={`templates-view-role-${variable.path}`} />
              )}
              {variable.nested && <Badge variant="outline" className="text-[10px]">nested list</Badge>}
              {variable.keyValueType !== null && <Badge variant="outline" className="text-[10px]">free keys of {variable.keyValueType}</Badge>}
            </span>
          );
        },
      },
      {
        id: "shape",
        header: "Shape",
        render: (variable) => (
          <span className="font-mono text-[12px]">
            {shapeText(variable)}
            {variable.format !== null && <span className="text-muted-foreground"> {variable.format}</span>}
          </span>
        ),
      },
      {
        id: "required",
        header: "",
        render: (variable) => (variable.required
          ? <Badge variant="secondary" className="text-[10px]" data-testid="templates-view-required">Required</Badge>
          : null),
      },
      { id: "relationships", header: "Points to", render: (variable) => <TruncatedText text={variable.relationships.join(", ")} mono maxWidth={260} /> },
      { id: "unit", header: "Unit context", render: (variable) => <TruncatedText text={variable.unitContext} mono maxWidth={160} /> },
    ];

    if (hasCacheTypes) {
      list.push({
        id: "cache",
        header: "Cached types",
        render: (variable) => <TruncatedText text={variable.cacheTypes.join(", ")} mono maxWidth={200} />,
      });
    }

    list.push({
      id: "description",
      header: "Description",
      render: (variable) => (
        <TruncatedText text={variable.description ?? variable.title} maxWidth={380} title={variable.title ?? "Description"} />
      ),
    });
    return list;
  }, [hasCacheTypes]);

  return (
    <div className="flex flex-col gap-3" data-testid="templates-view">
      <div className="flex flex-col gap-1.5" data-testid="templates-view-header">
        <div className="flex flex-wrap items-center gap-2">
          <span className="break-all font-mono text-[13px] font-medium" data-testid="templates-view-kind">{detail.kind}</span>
          <Badge variant="outline" className="font-mono" data-testid="templates-view-version">{detail.version}</Badge>
          {detail.saved !== null
            ? <StatePill tone="success" label="saved" icon={Save} testId="templates-view-state" />
            : <StatePill tone="warning" label="not saved" icon={TriangleAlert} testId="templates-view-state" />}
          {actions !== undefined && <div className="ml-auto flex flex-wrap items-center gap-2">{actions}</div>}
        </div>
        {detail.title !== null && <div className="text-[13px] font-medium">{detail.title}</div>}
        {detail.description !== null && <p className="text-[13px] text-muted-foreground">{detail.description}</p>}
        {detail.saved !== null ? (
          <p className="text-xs text-muted-foreground" data-testid="templates-view-saved">
            Saved <RelativeTime value={detail.saved.capturedUtc} /> by {detail.saved.capturedBy}, from {detail.saved.origin}.
            {" "}
            {detail.saved.pinnedBy === 0
              ? "No synced mapping pins it."
              : `Pinned by ${detail.saved.pinnedBy} synced mapping${detail.saved.pinnedBy === 1 ? "" : "s"}.`}
          </p>
        ) : (
          <p className="text-xs text-muted-foreground" data-testid="templates-view-saved">
            This version is not saved, so no mapping can pin it yet.
          </p>
        )}
      </div>

      <Tabs defaultValue="variables">
        <TabsList data-testid="templates-view-tabs">
          <TabsTrigger value="variables" data-testid="templates-view-tab-variables">Variables</TabsTrigger>
          <TabsTrigger value="schema" data-testid="templates-view-tab-schema">Schema JSON</TabsTrigger>
        </TabsList>
        <TabsContent value="variables" className="flex flex-col gap-3 pt-1">
          <FilterBar>
            <SearchInput
              value={filter}
              onChange={setFilter}
              placeholder="Path, description or entity type"
              label="Filter the variables"
              testId="templates-view-variables-filter"
            />
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch checked={showWritten} onCheckedChange={setShowWritten} data-testid="templates-view-show-written" />
              Show what OSDU Delivery and OSDU write
            </Label>
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Switch checked={showNested} onCheckedChange={setShowNested} data-testid="templates-view-show-nested" />
              Show nested lists
            </Label>
            <span className="text-xs text-muted-foreground sm:ml-auto" data-testid="templates-view-variables-count">
              {rows.length} of {detail.variables.length} variables
            </span>
          </FilterBar>
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(variable) => variable.path}
            emptyMessage="No variable matches the filter."
            minWidth={980}
            skeletonRows={8}
            data-testid="templates-view-variables-table"
          />
        </TabsContent>
        <TabsContent value="schema" className="pt-1">
          <SchemaJson detail={detail} previewSchema={previewSchema} />
        </TabsContent>
      </Tabs>
    </div>
  );
}
