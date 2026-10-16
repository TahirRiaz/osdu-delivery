import { CircleHelp, Eye, Info, Layers, Power, PowerOff, Search, type LucideIcon } from "lucide-react";
import { Card } from "@/components/ui/card";
import type { DeliveryCacheSystemProperty, DeliveryCacheVersion } from "../../api/delivery";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { StatePill } from "@/components/StatusBadge";
import { splitFlagName } from "./cacheFormat";

/** The platform services every refresh asks for the partition's feature flags, and the info endpoint each is asked on. */
const SERVICES: readonly { id: string; label: string; path: string | null; icon: LucideIcon }[] = [
  { id: "indexer", label: "Indexer", path: "/api/indexer/v2/info", icon: Layers },
  { id: "search", label: "Search service", path: "/api/search/v2/info", icon: Search },
];

/** What OSDU Delivery does differently by the state of a flag it reads, by service and name. */
const READ_BY_OSDU_DELIVERY: Record<string, { on: string; otherwise: string }> = {
  "indexer:featureFlag.keywordLower.enabled": {
    on: "On: a lookup whose value no record holds exactly asks once more regardless of case, and takes that answer only when it is one record.",
    otherwise: "Not on: lookups ask for the exact value only. Nothing relies on the flag unless the indexer reports it on.",
  },
};

const CHANGE_NOTE = "A mapping that searches is pinned to the state of this flag, so a change of it renders every record built by such a mapping again.";

const WHERE_FROM = [
  "Every refresh of the partition's cache asks the indexer (GET /api/indexer/v2/info) and the search service (GET /api/search/v2/info) for the feature flags they report for the partition (featureFlagStates), and keeps them with the version it writes.",
  "They are settings of the OSDU platform: not reference or master data, not read by any mapping, and never changed by OSDU Delivery.",
  "A service that cannot be asked changes nothing: the version keeps what the cache knew of its flags.",
].join("\n\n");

/** On, off or unknown, as the outlined state chip every configuration state wears (DESIGN.md 7.3). */
function FlagState({ flag }: { flag: DeliveryCacheSystemProperty }) {
  switch (flag.state) {
    case "Enabled":
      return <StatePill tone="success" label="on" icon={Power} testId="delivery-cache-flag-state" />;
    case "Disabled":
      return <StatePill tone="muted" label="off" icon={PowerOff} testId="delivery-cache-flag-state" />;
    default:
      return <StatePill tone="warning" label="unknown" icon={CircleHelp} testId="delivery-cache-flag-state" />;
  }
}

/** The flag's name as the service spells it, with the affixes most names repeat stepped back. */
function FlagName({ name }: { name: string }) {
  const { head, core, tail } = splitFlagName(name);
  return (
    <span className="min-w-0 truncate font-mono text-[12.5px]" title={name} data-testid="delivery-cache-flag-name">
      <span className="text-muted-foreground">{head}</span>
      <span className="font-medium text-foreground">{core}</span>
      <span className="text-muted-foreground">{tail}</span>
    </span>
  );
}

/**
 * The version whose refresh found the flag in its current state, walking back from `current` through the versions
 * (newest first): the newest version after which an older one reports it otherwise. "never" when every older version that
 * read the flag at all reports the same state; null when the version is not among those listed.
 */
function lastChange(
  versions: DeliveryCacheVersion[], current: DeliveryCacheVersion, flag: DeliveryCacheSystemProperty,
): DeliveryCacheVersion | "never" | null {
  const start = versions.findIndex((candidate) => candidate.version === current.version);
  if (start < 0) {
    return null;
  }

  for (let at = start + 1; at < versions.length; at++) {
    const earlier = versions[at].systemProperties.find((p) => p.service === flag.service && p.name === flag.name);
    if (earlier === undefined) {
      return "never";
    }

    if (earlier.state !== flag.state) {
      return versions[at - 1];
    }
  }

  return "never";
}

/** Flags OSDU Delivery reads come first, then the rest by the part of the name that says what they are. */
function compareFlags(a: DeliveryCacheSystemProperty, b: DeliveryCacheSystemProperty): number {
  const readA = READ_BY_OSDU_DELIVERY[`${a.service}:${a.name}`] !== undefined;
  const readB = READ_BY_OSDU_DELIVERY[`${b.service}:${b.name}`] !== undefined;
  if (readA !== readB) {
    return readA ? -1 : 1;
  }

  return splitFlagName(a.name).core.localeCompare(splitFlagName(b.name).core, undefined, { sensitivity: "base" });
}

/** How many of a service's flags are on, off and unknown, each with its state glyph. */
function StateCounts({ flags }: { flags: DeliveryCacheSystemProperty[] }) {
  const tally = [
    { count: flags.filter((f) => f.state === "Enabled").length, label: "on", icon: Power, tone: "text-success" },
    { count: flags.filter((f) => f.state === "Disabled").length, label: "off", icon: PowerOff, tone: "text-muted-foreground" },
    { count: flags.filter((f) => f.state === "Unknown").length, label: "unknown", icon: CircleHelp, tone: "text-warning" },
  ].filter((entry) => entry.count > 0);

  return (
    <span className="flex shrink-0 items-center gap-3 text-[12px] text-muted-foreground">
      {tally.map(({ count, label, icon: Icon, tone }) => (
        <span key={label} className="inline-flex items-center gap-1">
          <Icon className={`size-3.5 ${tone}`} />
          <span className="font-mono tabular-nums text-foreground">{count}</span>
          {label}
        </span>
      ))}
    </span>
  );
}

/**
 * The flags one platform service reported for the partition, under a head naming the service and the endpoint they were
 * read from: each flag's name, its state, the configuration the service says set it, and the version whose refresh
 * found it in that state. A service that gave the version no flags says so in a line rather than an empty table.
 */
function ServiceFlags({ service, flags, versions, version }: {
  service: (typeof SERVICES)[number];
  flags: DeliveryCacheSystemProperty[];
  versions: DeliveryCacheVersion[];
  version: DeliveryCacheVersion;
}) {
  const Icon = service.icon;
  const header = (
    <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 border-b border-border px-3 py-2">
      <Icon className="size-4 shrink-0 text-muted-foreground" />
      <span className="text-[13px] font-medium">{service.label}</span>
      {service.path !== null && <span className="font-mono text-[11px] text-muted-foreground">GET {service.path}</span>}
      <span className="ml-auto">
        {flags.length === 0
          ? <span className="text-[12px] text-muted-foreground">no flags</span>
          : <StateCounts flags={flags} />}
      </span>
    </div>
  );

  if (flags.length === 0) {
    return (
      <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid={`delivery-cache-flags-${service.id}`}>
        {header}
        <p className="px-3 py-2.5 text-[12px] text-muted-foreground">
          This version holds no flag from the {service.label.toLowerCase()}: it reported none for the partition, or could not
          be asked, and the refresh&apos;s log says which.
        </p>
      </Card>
    );
  }

  // Which version found each flag in its state. The column is left out while no flag of the service has changed, which
  // the line over the blocks says once rather than every row saying never.
  const changes = new Map(flags.map((flag) => [`${flag.service}:${flag.name}`, lastChange(versions, version, flag)]));
  const anyChanged = [...changes.values()].some((change) => change !== null && change !== "never");

  const columns: Column<DeliveryCacheSystemProperty>[] = [
    {
      id: "flag",
      header: "Flag",
      render: (flag) => {
        const read = READ_BY_OSDU_DELIVERY[`${flag.service}:${flag.name}`];
        return (
          <span className="flex min-w-0 items-center gap-2">
            <FlagName name={flag.name} />
            {read !== undefined && (
              <RichTooltip
                title="What OSDU Delivery does with it"
                body={`${flag.state === "Enabled" ? read.on : read.otherwise}\n\n${CHANGE_NOTE}`}
              >
                <span
                  className="inline-flex shrink-0 items-center gap-1 rounded-sm bg-primary/12 px-1.5 py-px text-[11px] font-medium text-primary"
                  data-testid="delivery-cache-flag-read"
                >
                  <Eye className="size-3.5" />
                  read by OSDU Delivery
                </span>
              </RichTooltip>
            )}
          </span>
        );
      },
    },
    {
      id: "state",
      header: "State",
      render: (flag) => (
        <span className="inline-flex items-center gap-1.5">
          <FlagState flag={flag} />
          {flag.detail !== null && (
            <RichTooltip body={flag.detail} title="Why">
              <Info className="size-4 text-muted-foreground" aria-label="Why" data-testid="delivery-cache-flag-detail" />
            </RichTooltip>
          )}
        </span>
      ),
    },
    {
      id: "source",
      header: "Set in",
      fill: !anyChanged,
      // The service names where it took the state from, sometimes several places joined by a plus.
      render: (flag) => (flag.source === null
        ? <span className="text-[12px] text-muted-foreground">not said</span>
        : (
          <span className="inline-flex items-center gap-1">
            {flag.source.split("+").map((part) => part.trim()).filter((part) => part !== "").map((part) => (
              <span key={part} className="rounded-sm border border-border px-1.5 font-mono text-[11px] leading-5 text-muted-foreground">
                {part}
              </span>
            ))}
          </span>
        )),
    },
  ];

  if (anyChanged) {
    columns.push({
      id: "changed",
      header: "Last changed",
      fill: true,
      render: (flag) => {
        const change = changes.get(`${flag.service}:${flag.name}`) ?? null;
        if (change === null) {
          return <span className="text-[12px] text-muted-foreground">-</span>;
        }

        return change === "never"
          ? <span className="text-[12px] text-muted-foreground" data-testid="delivery-cache-flag-changed">never</span>
          : <span className="font-mono text-[12px]" data-testid="delivery-cache-flag-changed">{change.version}</span>;
      },
    });
  }

  return (
    <div data-testid={`delivery-cache-flags-${service.id}`}>
      <DataTable
        columns={columns}
        rows={[...flags].sort(compareFlags)}
        rowKey={(flag) => `${flag.service}:${flag.name}`}
        emptyMessage="No flags."
        toolbar={header}
        data-testid="delivery-cache-flags-table"
      />
    </div>
  );
}

/**
 * The partition's OSDU feature flags, as the current version of its cache holds them: settings of the platform, not of
 * OSDU Delivery, which the indexer and the search service report for the partition and every refresh reads again. One
 * block per service names the endpoint the flags were read from; each flag gives its state, where the service says it
 * was set, and the version whose refresh found it so. A flag OSDU Delivery reads says so, and what it changes.
 */
export function DeliveryCacheFlags({ scope, version, versions }: {
  /** The partition the flags are set for. */
  scope: string;
  /** The version whose flags are shown: the current one. */
  version: DeliveryCacheVersion | null;
  /** Every version of the cache, newest first, to tell when each flag last changed. */
  versions: DeliveryCacheVersion[];
}) {
  if (version === null) {
    return (
      <Card className="gap-0 rounded-lg p-0" data-testid="delivery-cache-flags">
        <EmptyState
          icon={<Power />}
          title="The partition's cache holds no version yet"
          description="Every refresh of a cache flow of the partition reads the partition's OSDU feature flags; refresh one to read them."
          data-testid="delivery-cache-flags-none"
        />
      </Card>
    );
  }

  const flags = version.systemProperties;
  // The versions that read any flag at all: those written before the flags were captured hold none.
  const reading = versions.filter((candidate) => candidate.systemProperties.length > 0).length;
  const unchanged = flags.length > 0 && reading > 1
    && flags.every((flag) => lastChange(versions, version, flag) === "never");
  const services = [
    ...SERVICES,
    ...[...new Set(flags.map((flag) => flag.service))]
      .filter((id) => !SERVICES.some((known) => known.id === id))
      .map((id) => ({ id, label: id, path: null, icon: Layers })),
  ];

  return (
    <section className="flex flex-col gap-3" data-testid="delivery-cache-flags">
      <div className="flex flex-wrap items-center gap-x-1.5 gap-y-1 text-[12px] text-muted-foreground" data-testid="delivery-cache-flags-read">
        <span>Set on the OSDU platform for</span>
        <span className="font-mono text-foreground">{scope}</span>
        <span>and never changed by OSDU Delivery. As read with version</span>
        <span><span className="font-mono text-foreground">{version.version}</span>,</span>
        <RelativeTime value={version.capturedUtc} absolute={false} />
        {unchanged && <span data-testid="delivery-cache-flags-unchanged">· none has changed since it was first read</span>}
        <RichTooltip body={WHERE_FROM} title="Where these come from">
          <Info className="size-4 cursor-help text-muted-foreground" aria-label="Where these come from" data-testid="delivery-cache-flags-about" />
        </RichTooltip>
      </div>

      {services.map((service) => (
        <ServiceFlags
          key={service.id}
          service={service}
          flags={flags.filter((flag) => flag.service === service.id)}
          versions={versions}
          version={version}
        />
      ))}
    </section>
  );
}
