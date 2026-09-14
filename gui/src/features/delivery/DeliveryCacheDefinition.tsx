import { Link as RouterLink } from "react-router-dom";
import { ShieldCheck } from "lucide-react";
import { Card } from "@/components/ui/card";
import type { DeliveryCache } from "../../api/delivery";
import { ConnectionRef } from "../../components/ConnectionRef";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailPair } from "../../components/DetailPair";
import { StatePill } from "../../components/StatusBadge";
import { KindText } from "./KindText";
import { scheduleCadence, summarizeTypes, type CachedTypeSummary } from "./cacheFormat";

/** What a changed value of a type does: nothing to show when it goes out on its own, a state pill when it waits. */
function ChangeRule({ onChange }: { onChange: string }) {
  return onChange === "approve"
    ? <StatePill tone="warning" label="needs approval" icon={ShieldCheck} testId="delivery-cache-rule-approve" />
    : <span className="text-[12px] text-muted-foreground">next run</span>;
}

const columns: Column<CachedTypeSummary>[] = [
  {
    id: "type",
    header: "Type",
    render: (row) => (
      <span className="flex flex-col">
        <span className="font-mono text-[12.5px] font-medium">{row.name}</span>
        <span className="font-mono text-[11px] text-muted-foreground">{row.entityType}</span>
      </span>
    ),
  },
  {
    id: "kind",
    header: "Searched as",
    fill: true,
    floor: 160,
    render: (row) => (
      <span className="flex min-w-0 flex-col">
        <KindText kind={row.kind} />
        {row.query !== null && row.query !== "*" && (
          <span className="truncate font-mono text-[11px] text-muted-foreground" title={row.query}>{row.query}</span>
        )}
      </span>
    ),
  },
  {
    id: "fields",
    header: "Keeps",
    fill: true,
    floor: 200,
    render: (row) => (
      <span className="flex min-w-0 flex-wrap gap-1 py-0.5">
        {row.fields.map((field) => (
          <span
            key={field.as}
            className="inline-flex min-w-0 max-w-full items-baseline gap-1 rounded-sm border border-border/60 bg-muted/40 px-1.5 font-mono text-[11px]"
            title={field.path}
          >
            <span className="shrink-0">{field.as}</span>
            {!field.path.endsWith(`.${field.as}`) && <span className="min-w-0 truncate text-muted-foreground">{field.path}</span>}
          </span>
        ))}
      </span>
    ),
  },
  { id: "onChange", header: "When a value changes", render: (row) => <ChangeRule onChange={row.onChange} /> },
];

/**
 * What the cache flow file declares, read back from the last sync: where the file is, the platform the types are
 * searched on, what refreshes it and whether a refresh becomes current, then each type with its search, the paths it
 * keeps and what a changed value does. Read only: what is cached is changed in the file.
 */
export function DeliveryCacheDefinition({ cache }: { cache: DeliveryCache }) {
  const types = summarizeTypes(cache);
  const approving = types.filter((type) => type.onChange === "approve").length;

  return (
    <div className="flex flex-col gap-3" data-testid="delivery-cache-definition">
      <Card className="grid gap-4 rounded-lg p-4 [grid-template-columns:repeat(auto-fill,minmax(200px,1fr))]">
        <DetailPair label="Cache flow file">
          <span className="flex flex-col">
            <span className="break-all font-mono text-[12px]">{cache.relativePath}</span>
            {cache.pipelineId !== null && (
              <RouterLink to={`/pipelines/${cache.pipelineId}?tab=yaml`} className="text-[12px] text-primary hover:underline">
                View the YAML
              </RouterLink>
            )}
          </span>
        </DetailPair>
        <DetailPair label="Captured from">
          <ConnectionRef value={cache.endpoint} copyTestId="delivery-cache-endpoint-copy" />
        </DetailPair>
        <DetailPair label="Refreshed by">
          <span className="flex flex-col font-mono text-[12px]">
            {cache.schedules.length === 0
              ? <span className="font-sans text-muted-foreground">no schedule; only Refresh now</span>
              : cache.schedules.map((schedule) => <span key={schedule.id}>{scheduleCadence(schedule)}</span>)}
          </span>
        </DetailPair>
        <DetailPair label="When a value changes">
          {approving === 0
            ? "goes out on the next run"
            : `${approving} of ${types.length} type${types.length === 1 ? "" : "s"} wait for approval; the rest go out on the next run`}
        </DetailPair>
        <DetailPair label="A new version">
          {cache.makeCurrent ? "becomes current when a refresh finds a change" : "is kept beside the current one"}
        </DetailPair>
      </Card>

      <DataTable
        columns={columns}
        rows={types}
        rowKey={(row) => row.name}
        emptyMessage="The cache flow declares no types."
        data-testid="delivery-cache-definition-types"
      />
    </div>
  );
}
