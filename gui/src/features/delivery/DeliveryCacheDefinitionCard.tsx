import { Link as RouterLink } from "react-router-dom";
import { CalendarClock, FileCode, Play, ScrollText } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import type { DeliveryCache } from "../../api/delivery";
import { ConnectionRef } from "../../components/ConnectionRef";
import { DataTable, type Column } from "../../components/DataTable";
import { DetailPair } from "../../components/DetailPair";
import { RelativeTime } from "../../components/RelativeTime";
import { scheduleCadence, summarizeTypes, type CachedTypeSummary } from "./cacheFormat";

/**
 * What a cache is, before what it holds: the file that defines it (the one place what is cached is changed), where it is
 * captured from, what refreshes it, the version deliveries read with the run that captured it, and every type it declares
 * with the paths it keeps. Picking a type scopes the records below to it.
 */
export function DeliveryCacheDefinitionCard({ cache, scopedType, onPickType, onRefresh }: {
  cache: DeliveryCache;
  /** The type the records are scoped to, highlighted in the list; null for every type. */
  scopedType: string | null;
  onPickType: (type: string) => void;
  /** Opens the trigger dialog on the cache flow with the refresh operation. */
  onRefresh: () => void;
}) {
  const types = summarizeTypes(cache);
  const current = cache.current;

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
      render: (row) => (
        <span className="flex flex-col">
          <span className="font-mono text-[12px]">{row.kind}</span>
          {row.query !== null && row.query !== "*" && (
            <span className="font-mono text-[11px] text-muted-foreground">query {row.query}</span>
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
        <span className="flex flex-wrap gap-1">
          {row.fields.map((field) => (
            <span key={field.as} className="inline-flex items-baseline gap-1 rounded-sm border border-border/60 bg-muted/40 px-1.5 font-mono text-[11px]">
              <span className="text-foreground">{field.as}</span>
              <span className="text-muted-foreground">{field.path}</span>
            </span>
          ))}
        </span>
      ),
    },
    {
      id: "onChange",
      header: "When a value changes",
      render: (row) => (
        <Badge variant="secondary" className={row.onChange === "auto" ? "bg-info/15 text-info" : "bg-warning/15 text-warning"}>
          {row.onChange === "auto" ? "goes out on the next run" : "waits for approval"}
        </Badge>
      ),
    },
    {
      id: "items",
      header: "Records now",
      align: "right",
      render: (row) => <span className="font-mono tabular-nums">{row.items.toLocaleString()}</span>,
    },
  ];

  return (
    <Card className="gap-4 rounded-lg p-4" data-testid="delivery-cache-definition">
      <div className="flex flex-wrap items-start gap-3">
        <div className="flex min-w-0 flex-col gap-1">
          <h2 className="flex flex-wrap items-baseline gap-2 text-[15px] font-semibold">
            <span className="font-mono" data-testid="delivery-cache-name">{cache.name}</span>
            <span className="text-[12px] font-normal text-muted-foreground">in repository {cache.repoName}</span>
          </h2>
          <p className="flex flex-wrap items-center gap-1.5 text-[12.5px] text-muted-foreground" data-testid="delivery-cache-defined-in">
            <FileCode className="size-3.5 shrink-0" />
            What this cache holds is defined in
            <span className="font-mono text-foreground">{cache.relativePath}</span>.
            Change the file in its repository and sync it; the next refresh captures what it declares.
          </p>
        </div>
        <div className="grow" />
        {cache.pipelineId !== null && (
          <div className="flex items-center gap-2">
            <Button asChild size="sm" variant="outline" data-testid="delivery-cache-view-yaml">
              <RouterLink to={`/pipelines/${cache.pipelineId}?tab=yaml`}>
                <ScrollText />
                View YAML
              </RouterLink>
            </Button>
            <Button size="sm" onClick={onRefresh} data-testid="delivery-cache-refresh">
              <Play />
              Refresh now
            </Button>
          </div>
        )}
      </div>

      <div className="grid gap-4 [grid-template-columns:repeat(auto-fill,minmax(220px,1fr))]">
        <DetailPair label="Captured from">
          <ConnectionRef value={cache.endpoint} copyTestId="delivery-cache-endpoint-copy" />
        </DetailPair>
        <DetailPair label="Refreshed by">
          <span className="flex flex-col gap-0.5" data-testid="delivery-cache-schedules">
            {cache.schedules.length === 0
              ? <span className="text-muted-foreground">no schedule; a refresh runs only when triggered</span>
              : cache.schedules.map((schedule) => (
                <span key={schedule.id} className="inline-flex items-center gap-1.5">
                  <CalendarClock className="size-3.5 shrink-0 text-muted-foreground" />
                  <span className="font-mono text-[12px]">{scheduleCadence(schedule)}</span>
                </span>
              ))}
          </span>
        </DetailPair>
        <DetailPair label="Current version">
          {current === null
            ? <span className="text-muted-foreground" data-testid="delivery-cache-current">none captured yet</span>
            : (
              <span className="flex flex-col" data-testid="delivery-cache-current">
                <span className="font-mono text-[12.5px]">{current.version}</span>
                <span className="text-[12px] text-muted-foreground">
                  captured <RelativeTime value={current.capturedUtc} absolute={false} /> by {current.capturedBy}
                  {current.runId !== null && (
                    <>
                      {" in "}
                      <RouterLink to={`/runs/${current.runId}`} className="text-primary hover:underline" data-testid="delivery-cache-current-run">
                        run {current.runId.slice(0, 8)}
                      </RouterLink>
                    </>
                  )}
                </span>
              </span>
            )}
        </DetailPair>
        <DetailPair label="Versions">
          <span className="flex flex-col">
            <span className="font-mono tabular-nums" data-testid="delivery-cache-version-count">{cache.versions.toLocaleString()}</span>
            <span className="text-[12px] text-muted-foreground">
              {cache.makeCurrent ? "a refresh that finds a change becomes the current version" : "a refresh is kept beside the current version"}
            </span>
          </span>
        </DetailPair>
      </div>

      <div className="flex flex-col gap-1.5">
        <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
          What is cached · pick a type to see its records
        </h3>
        <DataTable
          columns={columns}
          rows={types}
          rowKey={(row) => row.name}
          onRowClick={(row) => onPickType(row.name)}
          rowSx={(row) => (row.name === scopedType ? { backgroundColor: "var(--accent)" } : undefined)}
          emptyMessage="The cache flow declares no types."
          data-testid="delivery-cache-types"
        />
      </div>
    </Card>
  );
}
