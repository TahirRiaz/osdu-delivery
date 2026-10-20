import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, Hammer, Network } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { EmptyState } from "@/components/EmptyState";
import { ExplainMark, ExplainTip } from "@/components/ExplainTip";
import { FilterCombobox } from "@/components/FilterCombobox";
import { RelativeTime } from "@/components/RelativeTime";
import { useOwnedPanel } from "@/layout/workbench/useOwnedPanel";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { cn } from "@/lib/utils";
import { deliveryApi } from "../../../api/delivery";
import { orderApi, type OrderDirection, type OrderEntity, type OrderGraph as OrderGraphData } from "../../../api/order";
import { problemText } from "../problemText";
import { ProblemView } from "../TemplateSheet";
import { useWindowFit } from "../useWindowFit";
import { OrderGraph, type OrderGraphLink, type OrderGraphNode } from "./OrderGraph";

const BELOW = 24;
const MIN_GRAPH_HEIGHT = 320;
const PANEL = "order-entity:";
const DEPTHS = [1, 2, 3, 4];

const DIRECTIONS: { value: OrderDirection; label: string }[] = [
  { value: "both", label: "Both ways" },
  { value: "out", label: "What it refers to" },
  { value: "in", label: "What refers to it" },
];

function shortType(entityType: string): string {
  const at = entityType.indexOf("--");
  return at < 0 ? `${entityType} (any)` : entityType.slice(at + 2);
}

/** Builds a release of OSDU's lineage from the OSDU data definitions: the release picked, or the newest. */
function BuildDialog({ open, onClose, onBuilt }: { open: boolean; onClose: () => void; onBuilt: (release: string) => void }) {
  const queryClient = useQueryClient();
  const releases = useQuery({ queryKey: ["delivery", "templates", "osdu", "releases"], queryFn: deliveryApi.osduReleases, enabled: open });
  const [release, setRelease] = useState<string | null>(null);
  const chosen = release ?? releases.data?.releases[0]?.name ?? null;
  const build = useMutation({
    mutationFn: (name: string | null) => orderApi.build(name),
    onSuccess: (built) => {
      toast.success(`Built OSDU's lineage of ${built.release}: ${built.kinds} kinds, ${built.relationships} relationships.`);
      void queryClient.invalidateQueries({ queryKey: ["delivery", "order"] });
      onBuilt(built.release);
      onClose();
    },
    onError: (error) => toast.error(problemText(error)),
  });

  return (
    <Dialog open={open} onOpenChange={(next) => { if (!next && !build.isPending) { onClose(); } }}>
      <DialogContent className="sm:max-w-md" data-testid="order-build-dialog">
        <DialogHeader>
          <DialogTitle>Build a release's lineage</DialogTitle>
          <DialogDescription>
            Reads every record schema the release of the OSDU data definitions publishes, downloading the release when the
            control plane does not have it, and keeps what each kind refers to. Building a release again replaces what it kept.
          </DialogDescription>
        </DialogHeader>
        {releases.isError && <ProblemView error={releases.error} testId="order-build-releases-error" />}
        {releases.data === undefined && !releases.isError && <Skeleton className="h-8 w-full" />}
        {releases.data !== undefined && (
          <Select value={chosen ?? undefined} onValueChange={setRelease}>
            <SelectTrigger aria-label="Release" data-testid="order-build-release">
              <SelectValue placeholder="Release" />
            </SelectTrigger>
            <SelectContent>
              {releases.data.releases.map((r) => (
                <SelectItem key={r.name} value={r.name}>{r.name}{r.local ? "" : " (download)"}</SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
        <DialogFooter>
          <Button variant="outline" size="sm" onClick={onClose} disabled={build.isPending}>Cancel</Button>
          <Button size="sm" onClick={() => build.mutate(chosen)} disabled={build.isPending || chosen === null} data-testid="order-build-confirm">
            <Hammer />
            {build.isPending ? "Building" : "Build"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

/** One entity type in the bottom panel: its kinds, and the references it makes and is the target of within the part drawn. */
function EntityDetail({ entity, graph, onDraw }: { entity: OrderEntity; graph: OrderGraphData; onDraw: (entityType: string) => void }) {
  const out = graph.edges.filter((e) => e.from === entity.entityType);
  const into = graph.edges.filter((e) => e.to === entity.entityType);
  return (
    <div className="flex flex-col gap-3 p-3 text-[12.5px]" data-testid="order-entity-detail">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1">
        <span className="font-medium">{entity.entityType}</span>
        <span className="text-muted-foreground">refers to {entity.refersTo} · referred to by {entity.referredBy}</span>
        {entity.entityType !== graph.entity && (
          <Button variant="outline" size="xs" onClick={() => onDraw(entity.entityType)} data-testid="order-entity-draw">
            <Network />
            Draw around it
          </Button>
        )}
      </div>
      {entity.kinds.length > 0 && (
        <div className="flex flex-wrap gap-x-4 gap-y-1">
          {entity.kinds.map((k) => (
            <span key={k.kind} className="font-mono text-[12px]">{k.kind}<span className="text-muted-foreground">{k.status === null ? "" : ` ${k.status.toLowerCase()}`}</span></span>
          ))}
        </div>
      )}
      {[["Refers to", out, (e: typeof out[number]) => e.to] as const, ["Referred to by", into, (e: typeof into[number]) => e.from] as const].map(([title, edges, other]) => edges.length > 0 && (
        <section key={title} className="flex flex-col gap-1">
          <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{title}</h3>
          <ul className="flex flex-col gap-0.5">
            {edges.map((e) => (
              <li key={`${e.from}>${e.to}`} className="flex min-w-0 gap-2">
                <span className="shrink-0">{other(e)}</span>
                <span className="truncate font-mono text-[11.5px] text-muted-foreground" title={e.properties.join("\n")}>{e.properties.join(", ")}</span>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}

/**
 * OSDU's lineage by release: a release of the OSDU data definitions built into what each kind refers to, drawn around one
 * entity type, as far out as the depth asks, the way its references point. It is what the order reads a kind from when
 * no template of it is saved, and a map of how OSDU's records connect.
 */
export function ReleaseLineageTab({ canBuild }: { canBuild: boolean }) {
  const releases = useQuery({ queryKey: ["delivery", "order", "releases"], queryFn: orderApi.releases });
  const [storedRelease, setRelease] = useLocalStorageState<string>("sqlflow.osdu.order.release", "");
  const [entity, setEntity] = useLocalStorageState<string>("sqlflow.osdu.order.entity", "master-data--Wellbore");
  const [depth, setDepth] = useState(1);
  const [direction, setDirection] = useState<OrderDirection>("both");
  const [building, setBuilding] = useState(false);
  const { ownedId, show, close } = useOwnedPanel(PANEL);
  const frame = useRef<HTMLDivElement>(null);
  useWindowFit(frame, BELOW, MIN_GRAPH_HEIGHT, undefined, "height");

  const built = releases.data ?? [];
  const release = built.some((r) => r.release === storedRelease) ? storedRelease : built[0]?.release ?? null;
  const info = built.find((r) => r.release === release) ?? null;
  const entities = useQuery({
    queryKey: ["delivery", "order", "entities", release],
    queryFn: () => orderApi.entities(release!),
    enabled: release !== null,
  });
  const known = entities.data?.entities.some((e) => e.entityType.toLowerCase() === entity.toLowerCase()) ?? false;
  const graph = useQuery({
    queryKey: ["delivery", "order", "graph", release, entity, depth, direction],
    queryFn: () => orderApi.graph(release!, entity, depth, direction),
    enabled: release !== null && known,
  });

  const options = useMemo(
    () => (entities.data?.entities ?? [])
      .filter((e) => e.kinds.length > 0)
      .map((e) => ({ value: e.entityType, label: e.entityType, hint: `${e.refersTo} out · ${e.referredBy} in` })),
    [entities.data],
  );

  const { nodes, links } = useMemo(() => {
    const data = graph.data;
    const graphNodes: OrderGraphNode[] = (data?.nodes ?? []).map((n) => ({
      id: n.entityType,
      title: shortType(n.entityType),
      subtitle: `${n.group} · ${n.refersTo} out · ${n.referredBy} in`,
    }));
    // The arrow goes from what goes first (the record referred to) to what refers to it.
    const graphLinks: OrderGraphLink[] = (data?.edges ?? []).map((e) => ({
      id: `${e.from}>${e.to}`,
      from: e.to,
      to: e.from,
      tone: "neutral",
      title: `${e.from} refers to ${e.to}: ${e.properties.join(", ")}`,
    }));
    return { nodes: graphNodes, links: graphLinks };
  }, [graph.data]);

  // The panel shows the type picked in the graph, read again whenever the part drawn is.
  useEffect(() => {
    if (ownedId === null || graph.data === undefined) {
      return;
    }

    const picked = graph.data.nodes.find((n) => n.entityType === ownedId);
    if (picked === undefined) {
      close();
      return;
    }

    show(ownedId, `Entity type · ${shortType(ownedId)}`, <EntityDetail entity={picked} graph={graph.data} onDraw={setEntity} />);
  }, [graph.data, ownedId, show, close, setEntity]);

  const pick = (id: string | null) => {
    const picked = id === null ? undefined : graph.data?.nodes.find((n) => n.entityType === id);
    if (picked === undefined || graph.data === undefined) {
      close();
      return;
    }

    show(picked.entityType, `Entity type · ${shortType(picked.entityType)}`, <EntityDetail entity={picked} graph={graph.data} onDraw={setEntity} />);
  };

  const buildButton = canBuild && (
    <Button variant="outline" size="sm" className="h-7" onClick={() => setBuilding(true)} data-testid="order-build">
      <Hammer />
      Build
    </Button>
  );

  if (releases.isError) {
    return <ProblemView error={releases.error} testId="order-releases-error" />;
  }

  return (
    <div className="flex flex-col gap-2">
      <BuildDialog open={building} onClose={() => setBuilding(false)} onBuilt={setRelease} />
      {releases.data !== undefined && built.length === 0 ? (
        <EmptyState
          icon={<Network />}
          title="No release of OSDU's lineage is built"
          description="Build one from the OSDU data definitions to see how OSDU's records refer to each other, and to order a kind whose template is not saved."
          action={buildButton || undefined}
          data-testid="order-releases-empty"
        />
      ) : (
        <>
          <div className="flex flex-wrap items-center gap-2 text-[12.5px]" data-testid="order-release-tools">
            {releases.data === undefined || release === null ? <Skeleton className="h-7 w-72" /> : (
              <>
                <Select value={release} onValueChange={setRelease}>
                  <SelectTrigger size="sm" className="h-7 w-28" aria-label="Release" data-testid="order-release-picker">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {built.map((r) => <SelectItem key={r.release} value={r.release}>{r.release}</SelectItem>)}
                  </SelectContent>
                </Select>
                <FilterCombobox
                  options={options}
                  value={entity}
                  onChange={setEntity}
                  placeholder="Entity type"
                  searchPlaceholder="Find an entity type"
                  emptyText="No entity type matches."
                  ariaLabel="Entity type drawn around"
                  testId="order-entity-picker"
                  className="w-56"
                />
                <Select value={String(depth)} onValueChange={(v) => setDepth(Number(v))}>
                  <SelectTrigger size="sm" className="h-7 w-24" aria-label="Depth" data-testid="order-depth">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {DEPTHS.map((d) => <SelectItem key={d} value={String(d)}>{d} step{d === 1 ? "" : "s"}</SelectItem>)}
                  </SelectContent>
                </Select>
                <Select value={direction} onValueChange={(v) => setDirection(v as OrderDirection)}>
                  <SelectTrigger size="sm" className="h-7 w-40" aria-label="Direction" data-testid="order-direction">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {DIRECTIONS.map((d) => <SelectItem key={d.value} value={d.value}>{d.label}</SelectItem>)}
                  </SelectContent>
                </Select>
                {info !== null && (
                  <ExplainTip
                    title={`Release ${info.release}`}
                    testId="order-release-info"
                    text={`Commit ${info.commit}. ${info.kinds} kinds, ${info.relationships} properties referring to other records. Built ${new Date(info.builtUtc).toLocaleString()} by ${info.builtBy}.`
                      + (info.failures.length === 0 ? "" : `\n\nNot read:\n${info.failures.map((f) => `${f.kind}: ${f.reason}`).join("\n")}`)}
                  >
                    <span className={cn("inline-flex cursor-default items-center gap-1 text-muted-foreground")}>
                      {info.failures.length > 0 && <AlertTriangle className="size-3.5 text-warning" aria-hidden />}
                      built <RelativeTime value={info.builtUtc} absolute={false} />
                    </span>
                  </ExplainTip>
                )}
                {graph.data?.truncated === true && (
                  <span className="inline-flex items-center gap-1" data-testid="order-graph-truncated">
                    <AlertTriangle className="size-3.5 text-warning" aria-hidden />
                    cut at {graph.data.nodes.length} types
                  </span>
                )}
                <ExplainMark
                  title="OSDU's lineage"
                  testId="order-release-about"
                  text={"Every record kind a release publishes and the properties of its schema that refer to other records (x-osdu-relationship). "
                    + "An arrow goes from the type referred to, which goes first, to the type referring to it. A click on a box shows its references; "
                    + "a double click draws around it. A group alone (a dataset of any type) stands as a box of its own."}
                />
                <span className="ml-auto">{buildButton}</span>
              </>
            )}
          </div>
          <div ref={frame} className="h-80 min-h-0">
            {graph.isError && <ProblemView error={graph.error} testId="order-graph-error" />}
            {entities.data !== undefined && !known && (
              <p className="py-6 text-center text-[12.5px] text-muted-foreground" data-testid="order-entity-unknown">
                {`Release ${release} holds no entity type '${entity}'. Pick one above.`}
              </p>
            )}
            {known && !graph.isError && (
              <OrderGraph nodes={nodes} links={links} selected={ownedId} onSelect={pick} onOpen={setEntity} testId="order-release-graph" />
            )}
          </div>
        </>
      )}
      {releases.isFetched && built.length > 0 && entities.isError && <ProblemView error={entities.error} testId="order-entities-error" />}
    </div>
  );
}
