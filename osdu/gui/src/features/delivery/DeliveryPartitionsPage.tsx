import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Layers, Loader2 } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Textarea } from "@/components/ui/textarea";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { useAuth } from "@/auth/AuthContext";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { RelativeTime } from "@/components/RelativeTime";
import { deliveryApi, type DeliveryPartition } from "../../api/delivery";
import { useActivePartition } from "./activePartition";
import { problemText } from "./problemText";
import { PARTITIONS_KEY, usePartitions } from "./usePartitions";

/** The widest description a partition keeps (DeliveryPartition.MaxDescriptionLength). */
const MAX_DESCRIPTION = 400;

const COLUMNS = ["Partition", "Description", "Cache", "Flows", "Registered"];

/** The setting a partition is named under in the control plane's configuration. */
const settingOf = (name: string) => `Osdu:Partitions:${name}`;

/** Describe a registered partition the configuration does not name, in a side sheet. */
function PartitionSheet({ editing, onClose }: { editing: DeliveryPartition; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [description, setDescription] = useState(editing.description ?? "");

  const save = useMutation({
    mutationFn: () => deliveryApi.describePartition(editing.name, description.trim() === "" ? null : description.trim()),
    onSuccess: (saved) => {
      toast.success(`${saved.name} described.`);
      void queryClient.invalidateQueries({ queryKey: ["delivery"] });
      onClose();
    },
    onError: (error) => toast.error(problemText(error)),
  });

  const canSave = !save.isPending && description.length <= MAX_DESCRIPTION;
  return (
    <Sheet open onOpenChange={(next) => { if (!next && !save.isPending) { onClose(); } }}>
      <SheetContent className="w-full sm:max-w-md" data-testid="partition-sheet">
        <SheetHeader>
          <SheetTitle>Describe {editing.name}</SheetTitle>
          <SheetDescription>What the partition is for, as the switcher and the trigger dialog show it.</SheetDescription>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="partition-description">Description</Label>
            <Textarea
              id="partition-description"
              className="min-h-16 text-[13px]"
              placeholder="e.g. Test environment on the partner platform"
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              data-testid="partition-description"
            />
            {description.length > MAX_DESCRIPTION && (
              <p className="text-xs text-destructive">At most {MAX_DESCRIPTION} characters; this is {description.length}.</p>
            )}
          </div>
        </div>
        <SheetFooter className="flex-row justify-end">
          <Button variant="ghost" size="sm" onClick={onClose} disabled={save.isPending}>Cancel</Button>
          <Button size="sm" onClick={() => save.mutate()} disabled={!canSave} data-testid="partition-save">
            {save.isPending && <Loader2 className="animate-spin" />}
            Save
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

/** What a partition's cache serves, in a few words. */
function CacheCell({ partition }: { partition: DeliveryPartition }) {
  if (partition.currentVersion === null) {
    return <span className="text-muted-foreground">none yet</span>;
  }

  return (
    <span className="flex flex-col">
      <span>
        {partition.types.toLocaleString()} {partition.types === 1 ? "type" : "types"}, {partition.items.toLocaleString()}{" "}
        {partition.items === 1 ? "entry" : "entries"}
      </span>
      {partition.capturedUtc !== null && (
        <span className="text-[12px] text-muted-foreground">captured <RelativeTime value={partition.capturedUtc} absolute={false} /></span>
      )}
    </span>
  );
}

/** The flows that fill a partition's cache and deliver to it, counted, with their names on hover. */
function FlowsCell({ partition }: { partition: DeliveryPartition }) {
  const names = [...partition.cacheFlows.map((flow) => `cache: ${flow}`), ...partition.deliveryFlows.map((flow) => `delivery: ${flow}`)];
  const text = `${partition.cacheFlows.length.toLocaleString()} cache, ${partition.deliveryFlows.length.toLocaleString()} delivery`;
  if (names.length === 0) {
    return <span className="text-muted-foreground">none</span>;
  }

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className="cursor-default underline decoration-dotted underline-offset-2">{text}</span>
      </TooltipTrigger>
      <TooltipContent className="max-w-sm">
        <div className="flex flex-col gap-0.5 font-mono text-[11px]">{names.map((line) => <span key={line}>{line}</span>)}</div>
      </TooltipContent>
    </Tooltip>
  );
}

/**
 * The partition registry (osdu/docs/reference/concepts/partitions.md, The partition registry): the OSDU partitions a flow
 * that names none serves, and the default one a run that names none runs in, with what each holds. The control plane's
 * configuration names them (Osdu:Partitions), and the control plane registers them when it starts, so this page shows
 * them and what they hold rather than registering any. Partitions a flow hard-codes, or that something is still kept
 * under after they were removed, are listed too, marked unregistered. A registered partition the configuration does not
 * name (one registered before, or one it stopped naming) is an admin's to describe, make the default and remove; removing
 * deletes nothing kept under it.
 */
export default function DeliveryPartitionsPage() {
  const queryClient = useQueryClient();
  const { hasScope } = useAuth();
  const admin = hasScope("admin");
  const [active, setActive] = useActivePartition();
  const { partitions, list } = usePartitions();
  const [describing, setDescribing] = useState<DeliveryPartition | null>(null);
  const [removing, setRemoving] = useState<DeliveryPartition | null>(null);
  const registered = list.filter((partition) => partition.registered);
  // The configuration names the default whenever it names any partition, so only it moves the default then.
  const defaultConfigured = list.some((partition) => partition.configured && partition.isDefault);
  // A registered partition the configuration does not name is the only one an admin acts on here; without one, the
  // column is left out and the table keeps its width for what the partitions hold.
  const actionable = (partition: DeliveryPartition) => admin && partition.registered && !partition.configured;
  const headers = list.some(actionable) ? [...COLUMNS, "Actions"] : COLUMNS;

  const makeDefault = useMutation({
    mutationFn: (name: string) => deliveryApi.makeDefaultPartition(name),
    onSuccess: (saved) => {
      toast.success(`${saved.name} is the default.`);
      void queryClient.invalidateQueries({ queryKey: PARTITIONS_KEY });
    },
    onError: (error) => toast.error(problemText(error)),
  });
  const remove = useMutation({
    mutationFn: (name: string) => deliveryApi.removePartition(name),
    onSuccess: (_result, name) => {
      toast.success(`${name} removed from the registry.`);
      if (active === name) {
        setActive(null);
      }

      void queryClient.invalidateQueries({ queryKey: ["delivery"] });
      setRemoving(null);
    },
    onError: (error) => {
      toast.error(problemText(error));
      setRemoving(null);
    },
  });

  return (
    <Page data-testid="page-delivery-partitions">
      <PageHeader
        title="Partitions"
        subtitle="The partitions flows run in, as the control plane's configuration names them. A flow that names none serves every registered partition; a run that names none runs in the default."
      />

      {partitions.isError && <p className="text-[13px] text-destructive">{problemText(partitions.error)}</p>}

      <Card className="gap-0 overflow-hidden rounded-lg p-0">
        <Table>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              {headers.map((header) => (
                <TableHead
                  key={header}
                  className={`h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground${header === "Actions" ? " text-right" : ""}`}
                >
                  {header}
                </TableHead>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {partitions.data === undefined && !partitions.isError && Array.from({ length: 3 }, (_, i) => (
              <TableRow key={`skeleton-${i}`}>
                {headers.map((header) => (
                  <TableCell key={header} className="px-3 py-2"><Skeleton className="h-4 w-full" /></TableCell>
                ))}
              </TableRow>
            ))}
            {partitions.data !== undefined && list.length === 0 && (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={headers.length} className="whitespace-normal border-0 p-0">
                  <EmptyState
                    icon={<Layers />}
                    title="No partition to work against"
                    description={(
                      <>
                        Every delivery runs in a partition. Name at least one in the control plane&apos;s configuration,{" "}
                        <code className="font-mono text-[12px]">Osdu:Partitions:&lt;data-partition-id&gt;</code>, with the values its runs
                        use under <code className="font-mono text-[12px]">Properties</code> (OSDU_URL, OSDU_CLIENT_ID and the rest), and
                        restart the control plane.
                      </>
                    )}
                  />
                </TableCell>
              </TableRow>
            )}
            {list.map((partition) => (
              <TableRow key={partition.name} data-testid={`partition-row-${partition.name}`}>
                <TableCell className="min-w-32 whitespace-normal px-3 py-1.5">
                  <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
                    <span className="font-mono text-[13px] font-medium">{partition.name}</span>
                    {partition.isDefault && <Badge variant="secondary" data-testid="partition-default-badge">default</Badge>}
                    {partition.configured && (
                      <Tooltip>
                        <TooltipTrigger asChild>
                          <Badge variant="outline" data-testid={`partition-configured-${partition.name}`}>configured</Badge>
                        </TooltipTrigger>
                        <TooltipContent className="max-w-xs">
                          Named in the control plane&apos;s configuration, <span className="font-mono">{settingOf(partition.name)}</span>,
                          with the values its runs use. Change it there; the control plane writes it when it next starts.
                        </TooltipContent>
                      </Tooltip>
                    )}
                    {!partition.registered && (
                      <Tooltip>
                        <TooltipTrigger asChild>
                          <Badge variant="outline" className="text-warning">not registered</Badge>
                        </TooltipTrigger>
                        <TooltipContent className="max-w-xs">
                          A flow hard-codes it, or something is kept under it from before it was removed. Flows that name no
                          partitions do not run in it until it is registered.
                        </TooltipContent>
                      </Tooltip>
                    )}
                    {partition.pendingChanges > 0 && (
                      <span className="text-[11px] text-warning">
                        {partition.pendingChanges.toLocaleString()} {partition.pendingChanges === 1 ? "change waits" : "changes wait"}
                      </span>
                    )}
                  </span>
                </TableCell>
                <TableCell className="min-w-32 max-w-52 whitespace-normal px-3 py-1.5 text-[13px]">
                  {partition.description ?? <span className="text-muted-foreground">none</span>}
                </TableCell>
                <TableCell className="min-w-32 max-w-44 whitespace-normal px-3 py-1.5 text-[13px]"><CacheCell partition={partition} /></TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]"><FlowsCell partition={partition} /></TableCell>
                <TableCell className="max-w-40 whitespace-normal px-3 py-1.5 text-[13px]">
                  {partition.createdUtc === null
                    ? <span className="text-muted-foreground">no</span>
                    : (
                      <span
                        className="flex min-w-0 flex-col"
                        title={`by ${partition.createdBy ?? "unknown"}${partition.updatedBy !== null ? `, last changed by ${partition.updatedBy}` : ""}`}
                      >
                        <RelativeTime value={partition.createdUtc} absolute={false} />
                        <span className="truncate text-[12px] text-muted-foreground">by {partition.createdBy}</span>
                      </span>
                    )}
                </TableCell>
                {headers.length > COLUMNS.length && (
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-right">
                  {actionable(partition) && (
                    <span className="inline-flex gap-1">
                      {!partition.isDefault && !defaultConfigured && (
                        <Button
                          variant="ghost"
                          size="xs"
                          disabled={makeDefault.isPending}
                          onClick={() => makeDefault.mutate(partition.name)}
                          data-testid={`partition-make-default-${partition.name}`}
                        >
                          Make default
                        </Button>
                      )}
                      <Button variant="ghost" size="xs" onClick={() => setDescribing(partition)} data-testid={`partition-describe-${partition.name}`}>
                        Describe
                      </Button>
                      <Button
                        variant="ghost"
                        size="xs"
                        className="text-destructive hover:text-destructive"
                        disabled={partition.isDefault && registered.length > 1}
                        title={partition.isDefault && registered.length > 1 ? "Make another partition the default first" : undefined}
                        onClick={() => setRemoving(partition)}
                        data-testid={`partition-remove-${partition.name}`}
                      >
                        Remove
                      </Button>
                    </span>
                  )}
                </TableCell>
                )}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Card>

      {describing !== null && <PartitionSheet editing={describing} onClose={() => setDescribing(null)} />}
      <ConfirmDialog
        open={removing !== null}
        title={`Remove ${removing?.name ?? ""} from the registry?`}
        message="Nothing kept under it is deleted: its caches, ledgers and runs stay. Flows that name no partitions stop running in it until the configuration names it."
        confirmLabel="Remove"
        danger
        busy={remove.isPending}
        onConfirm={() => { if (removing !== null) { remove.mutate(removing.name); } }}
        onClose={() => setRemoving(null)}
      />
    </Page>
  );
}
