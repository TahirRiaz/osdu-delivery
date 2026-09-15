import { useNavigate } from "react-router-dom";
import { Play, ScrollText, ShieldCheck } from "lucide-react";
import { Card } from "@/components/ui/card";
import type { DeliveryCache, DeliveryCacheFlow } from "../../api/delivery";
import { ConnectionRef } from "../../components/ConnectionRef";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailPair } from "../../components/DetailPair";
import { IconAction } from "../../components/IconAction";
import { StatePill } from "../../components/StatusBadge";
import { CacheMappingGuide } from "./CacheMappingReference";
import { KindText } from "./KindText";
import { cacheReference, scheduleCadence, summarizeTypes, type CachedTypeSummary } from "./cacheFormat";

/** What a changed value of a type does: nothing to show when it goes out on its own, a state pill when it waits. */
function ChangeRule({ onChange }: { onChange: string }) {
  return onChange === "approve"
    ? <StatePill tone="warning" label="needs approval" icon={ShieldCheck} testId="delivery-cache-rule-approve" />
    : <span className="text-[12px] text-muted-foreground">next run</span>;
}

/**
 * What the partition's cache is filled with, read back from the last sync: the cache flow files that fill it (each with its
 * repository, the endpoint it searches, what refreshes it and the types it declares), then every type the cache holds as
 * those flows together declare it: which flow searches it with which kind and query, every path kept (with the flows asking
 * for it), and what a changed value does. Read only: what is cached is changed in the files.
 */
export function DeliveryCacheDefinition({ cache, onRefresh }: {
  cache: DeliveryCache;
  /** Opens the trigger dialog on one cache flow with the refresh operation. */
  onRefresh: (flow: DeliveryCacheFlow) => void;
}) {
  const navigate = useNavigate();
  const types = summarizeTypes(cache);
  const approving = types.filter((type) => type.onChange === "approve").length;
  const several = cache.flows.length > 1;

  const flowColumns: Column<DeliveryCacheFlow>[] = [
    {
      id: "flow",
      header: "Cache flow",
      render: (flow) => (
        <span className="flex flex-col">
          <span className="font-mono text-[12.5px] font-medium">{flow.name}</span>
          <span className="text-[11px] text-muted-foreground">{flow.repoName}</span>
        </span>
      ),
    },
    {
      id: "file",
      header: "File",
      fill: true,
      floor: 160,
      render: (flow) => <span className="block truncate font-mono text-[12px]" title={flow.relativePath}>{flow.relativePath}</span>,
    },
    { id: "endpoint", header: "Captured from", render: (flow) => <ConnectionRef value={flow.endpoint} maxWidth={160} /> },
    {
      id: "schedules",
      header: "Refreshed by",
      render: (flow) => (flow.schedules.length === 0
        ? <span className="text-[12px] text-muted-foreground">no schedule</span>
        : (
          <span className="flex flex-col font-mono text-[12px]">
            {flow.schedules.map((schedule) => <span key={schedule.id}>{scheduleCadence(schedule)}</span>)}
          </span>
        )),
    },
    {
      id: "types",
      header: "Types",
      fill: true,
      floor: 140,
      render: (flow) => <span className="block truncate font-mono text-[12px]" title={flow.types.join(", ")}>{flow.types.join(", ")}</span>,
    },
    {
      id: "actions",
      header: "",
      align: "right",
      render: (flow) => (flow.pipelineId === null ? null : (
        <span className="inline-flex items-center gap-0.5">
          <IconAction
            label="View the YAML"
            icon={<ScrollText />}
            onClick={() => navigate(`/pipelines/${flow.pipelineId}?tab=yaml`)}
            data-testid="delivery-cache-flow-yaml"
          />
          <IconAction
            label={`Refresh ${flow.name}`}
            icon={<Play />}
            onClick={() => onRefresh(flow)}
            data-testid="delivery-cache-flow-refresh"
          />
        </span>
      )),
    },
  ];

  const typeColumns: Column<CachedTypeSummary>[] = [
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
        <span className="flex min-w-0 flex-col gap-0.5">
          {row.sources.map((source) => (
            <span key={source.flow} className="flex min-w-0 flex-col">
              <KindText kind={source.kind} />
              {(several || (source.query !== null && source.query !== "*")) && (
                <span className="truncate font-mono text-[11px] text-muted-foreground" title={source.query ?? undefined}>
                  {several && <span>{source.flow}</span>}
                  {several && source.query !== null && source.query !== "*" && <span> · </span>}
                  {source.query !== null && source.query !== "*" && <span>{source.query}</span>}
                </span>
              )}
            </span>
          ))}
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
              title={`${cacheReference(row.name, field.as)} reads ${field.path}${several ? `, declared by ${field.flows.join(", ")}` : ""}`}
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

  return (
    <div className="flex flex-col gap-3" data-testid="delivery-cache-definition">
      <Card className="grid gap-4 rounded-lg p-4 [grid-template-columns:repeat(auto-fill,minmax(200px,1fr))]">
        <DetailPair label="Partition">
          <span className="font-mono text-[12.5px]">{cache.scope}</span>
        </DetailPair>
        <DetailPair label="Filled by">
          {cache.flows.length === 1 ? "one cache flow" : `${cache.flows.length} cache flows, whose declarations of a type are merged`}
        </DetailPair>
        <DetailPair label="When a value changes">
          {approving === 0
            ? "goes out on the next run"
            : `${approving} of ${types.length} type${types.length === 1 ? "" : "s"} wait for approval; the rest go out on the next run`}
        </DetailPair>
        <DetailPair label="A new version">
          becomes current when a refresh of any of its flows changes what the cache holds
        </DetailPair>
      </Card>

      <CacheMappingGuide types={types} scope={cache.scope} />

      <section className="flex flex-col gap-1.5">
        <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Cache flows filling it</h3>
        <DataTable
          columns={flowColumns}
          rows={cache.flows}
          rowKey={(flow) => `${flow.repoId}:${flow.name}`}
          emptyMessage="No synced cache flow fills this partition."
          data-testid="delivery-cache-definition-flows"
        />
      </section>

      <section className="flex flex-col gap-1.5">
        <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">What the cache holds</h3>
        <DataTable
          columns={typeColumns}
          rows={types}
          rowKey={(row) => row.name}
          emptyMessage="The cache flows declare no types."
          data-testid="delivery-cache-definition-types"
        />
      </section>
    </div>
  );
}
