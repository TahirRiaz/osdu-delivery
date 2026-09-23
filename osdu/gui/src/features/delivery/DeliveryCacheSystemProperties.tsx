import { CircleCheck, CircleHelp, CircleOff } from "lucide-react";
import type { DeliveryCacheSystemProperty, DeliveryCacheVersion } from "../../api/delivery";
import { DataTable, type Column } from "@/components/DataTable";
import { StatePill } from "@/components/StatusBadge";

/** What the engine does with the system properties it reads, by service and name. */
const READ_BY_THE_ENGINE: Record<string, string> = {
  "indexer:featureFlag.keywordLower.enabled":
    "When on, a search that finds nothing exactly asks again regardless of case, and takes the answer only when one record matches.",
};

function State({ property }: { property: DeliveryCacheSystemProperty }) {
  switch (property.state) {
    case "Enabled":
      return <StatePill tone="success" label="enabled" icon={CircleCheck} testId="delivery-cache-system-state" />;
    case "Disabled":
      return <StatePill tone="muted" label="disabled" icon={CircleOff} testId="delivery-cache-system-state" />;
    default:
      return <StatePill tone="warning" label="unknown" icon={CircleHelp} testId="delivery-cache-system-state" />;
  }
}

/**
 * The partition's system properties as the current version of its cache holds them: the settings the platform's indexer and
 * search service report for the partition, read by every capture. They are neither reference nor master data and have no
 * record behind them: they say how the partition's records are indexed and searched, and no mapping reads them. A property
 * the engine relies on says what it changes; one no service could report is unknown, and nothing relies on it being on.
 */
export function DeliveryCacheSystemProperties({ version }: { version: DeliveryCacheVersion | null }) {
  const columns: Column<DeliveryCacheSystemProperty>[] = [
    {
      id: "property",
      header: "Property",
      fill: true,
      floor: 220,
      render: (property) => {
        const use = READ_BY_THE_ENGINE[`${property.service}:${property.name}`];
        return (
          <span className="flex min-w-0 flex-col">
            <span className="truncate font-mono text-[12.5px] font-medium" title={property.name}>{property.name}</span>
            {use !== undefined && <span className="text-[11px] text-muted-foreground">{use}</span>}
          </span>
        );
      },
    },
    { id: "service", header: "Reported by", render: (property) => <span className="font-mono text-[12px]">{property.service}</span> },
    { id: "state", header: "State", render: (property) => <State property={property} /> },
    {
      id: "source",
      header: "Taken from",
      render: (property) => (property.source === null
        ? <span className="text-[12px] text-muted-foreground">not said</span>
        : <span className="font-mono text-[12px]">{property.source}</span>),
    },
    {
      id: "detail",
      header: "Detail",
      fill: true,
      floor: 200,
      render: (property) => (
        <span className="block truncate text-[12px] text-muted-foreground" title={property.detail ?? undefined}>{property.detail ?? ""}</span>
      ),
    },
  ];

  return (
    <section className="flex flex-col gap-1.5" data-testid="delivery-cache-system">
      <p className="text-[12px] text-muted-foreground">
        Settings of the platform for this partition, as its indexer and search service report them at every capture. They are
        not reference or master data: they say how the partition&apos;s records are indexed and searched, and no mapping reads them.
      </p>
      <DataTable
        columns={columns}
        rows={version?.systemProperties ?? []}
        rowKey={(property) => `${property.service}:${property.name}`}
        emptyMessage={version === null
          ? "The partition's cache holds no version yet."
          : "No capture has read this partition's settings yet. Refresh a cache flow of the partition to read them."}
        data-testid="delivery-cache-system-properties"
      />
    </section>
  );
}
