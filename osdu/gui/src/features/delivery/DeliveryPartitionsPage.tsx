import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Layers, Loader2, Plus } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
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
import { isPartitionId, useActivePartition } from "./activePartition";
import { problemText } from "./problemText";
import { PARTITIONS_KEY, usePartitions } from "./usePartitions";

/** The widest description a partition keeps (DeliveryPartition.MaxDescriptionLength). */
const MAX_DESCRIPTION = 400;

const HEADERS = ["Partition", "Description", "Cache", "Flows", "Registered", "Actions"];

/** Register a partition, or describe one already registered, in a side sheet. */
function PartitionSheet({ editing, initialName, first, onClose }: {
  editing: DeliveryPartition | null;
  initialName: string;
  first: boolean;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(editing?.name ?? initialName);
  const [description, setDescription] = useState(editing?.description ?? "");
  const [makeDefault, setMakeDefault] = useState(first);
  const trimmed = name.trim();
  const nameError = editing !== null || trimmed === "" || isPartitionId(trimmed)
    ? null
    : "A partition id is letters, digits, underscore, hyphen and dot, at most 200 characters.";

  const save = useMutation({
    mutationFn: () => (editing === null
      ? deliveryApi.addPartition({ name: trimmed, description: description.trim() === "" ? null : description.trim(), isDefault: makeDefault })
      : deliveryApi.describePartition(editing.name, description.trim() === "" ? null : description.trim())),
    onSuccess: (saved) => {
      toast.success(editing === null
        ? `${saved.name} registered${saved.isDefault ? " as the default" : ""}.`
        : `${saved.name} described.`);
      void queryClient.invalidateQueries({ queryKey: ["delivery"] });
      onClose();
    },
    onError: (error) => toast.error(problemText(error)),
  });

  const canSave = !save.isPending && trimmed !== "" && nameError === null && description.length <= MAX_DESCRIPTION;
  return (
    <Sheet open onOpenChange={(next) => { if (!next && !save.isPending) { onClose(); } }}>
      <SheetContent className="w-full sm:max-w-md" data-testid="partition-sheet">
        <SheetHeader>
          <SheetTitle>{editing === null ? "Register a partition" : `Describe ${editing.name}`}</SheetTitle>
          <SheetDescription>
            {editing === null
              ? "Flows that name no partitions serve every registered one; a run that names none runs in the default."
              : "What the partition is for, as the switcher and the trigger dialog show it."}
          </SheetDescription>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="partition-name">Data partition id</Label>
            <Input
              id="partition-name"
              className="h-8 font-mono"
              maxLength={200}
              placeholder="e.g. opendes"
              value={name}
              disabled={editing !== null}
              onChange={(event) => setName(event.target.value)}
              data-testid="partition-name"
            />
            {nameError !== null && <p className="text-xs text-destructive">{nameError}</p>}
          </div>
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
          {editing === null && (
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Checkbox checked={makeDefault} disabled={first} onCheckedChange={(checked) => setMakeDefault(checked === true)} data-testid="partition-default" />
              Make it the default
              {first && <span className="text-muted-foreground">(the first partition registered always is)</span>}
            </Label>
          )}
        </div>
        <SheetFooter className="flex-row justify-end">
          <Button variant="ghost" size="sm" onClick={onClose} disabled={save.isPending}>Cancel</Button>
          <Button size="sm" onClick={() => save.mutate()} disabled={!canSave} data-testid="partition-save">
            {save.isPending && <Loader2 className="animate-spin" />}
            {editing === null ? "Register" : "Save"}
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
      <span className="whitespace-nowrap">
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
 * The partition registry (docs/partitions-design.md section 2.1): the OSDU partitions a flow that names none serves, and
 * the default one a run that names none runs in, with what each holds. Partitions a flow hard-codes, or that something
 * is still kept under after they were removed, are listed too, marked unregistered. Registering, describing, changing
 * the default and removing are admin work; removing deletes nothing kept under a partition.
 */
export default function DeliveryPartitionsPage() {
  const queryClient = useQueryClient();
  const { hasScope } = useAuth();
  const admin = hasScope("admin");
  const [active, setActive] = useActivePartition();
  const { partitions, list } = usePartitions();
  const [sheet, setSheet] = useState<{ editing: DeliveryPartition | null; name: string } | null>(null);
  const [removing, setRemoving] = useState<DeliveryPartition | null>(null);
  const registered = list.filter((partition) => partition.registered);

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
        subtitle="The partitions flows run in. A flow that names none serves every registered partition; a run that names none runs in the default."
        actions={admin && (
          <Button size="sm" onClick={() => setSheet({ editing: null, name: "" })} data-testid="partition-register">
            <Plus />
            Register partition
          </Button>
        )}
      />

      {partitions.isError && <p className="text-[13px] text-destructive">{problemText(partitions.error)}</p>}

      <Card className="gap-0 overflow-hidden rounded-lg p-0">
        <Table>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              {HEADERS.map((header, i) => (
                <TableHead
                  key={header}
                  className={`h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground${i === HEADERS.length - 1 ? " text-right" : ""}`}
                >
                  {header}
                </TableHead>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {partitions.data === undefined && !partitions.isError && Array.from({ length: 3 }, (_, i) => (
              <TableRow key={`skeleton-${i}`}>
                {HEADERS.map((header) => (
                  <TableCell key={header} className="px-3 py-2"><Skeleton className="h-4 w-full" /></TableCell>
                ))}
              </TableRow>
            ))}
            {partitions.data !== undefined && list.length === 0 && (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={HEADERS.length} className="border-0 p-0">
                  <EmptyState
                    icon={<Layers />}
                    title="No partition is registered"
                    description="A flow that names no partitions has none to run in until one is registered."
                    action={admin
                      ? <Button size="sm" variant="outline" onClick={() => setSheet({ editing: null, name: "" })}>Register partition</Button>
                      : undefined}
                  />
                </TableCell>
              </TableRow>
            )}
            {list.map((partition) => (
              <TableRow key={partition.name} data-testid={`partition-row-${partition.name}`}>
                <TableCell className="whitespace-nowrap px-3 py-1.5">
                  <span className="flex items-center gap-2">
                    <span className="font-mono text-[13px] font-medium">{partition.name}</span>
                    {partition.isDefault && <Badge variant="secondary" data-testid="partition-default-badge">default</Badge>}
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
                <TableCell className="min-w-40 max-w-72 whitespace-normal px-3 py-1.5 text-[13px]">
                  {partition.description ?? <span className="text-muted-foreground">none</span>}
                </TableCell>
                <TableCell className="whitespace-normal px-3 py-1.5 text-[13px]"><CacheCell partition={partition} /></TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]"><FlowsCell partition={partition} /></TableCell>
                <TableCell className="whitespace-normal px-3 py-1.5 text-[13px]">
                  {partition.createdUtc === null
                    ? <span className="text-muted-foreground">no</span>
                    : (
                      <span className="flex flex-col" title={partition.updatedBy !== null ? `last changed by ${partition.updatedBy}` : undefined}>
                        <RelativeTime value={partition.createdUtc} absolute={false} />
                        <span className="truncate text-[12px] text-muted-foreground">by {partition.createdBy}</span>
                      </span>
                    )}
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-right">
                  {admin && (
                    <span className="inline-flex gap-1">
                      {partition.registered ? (
                        <>
                          {!partition.isDefault && (
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
                          <Button variant="ghost" size="xs" onClick={() => setSheet({ editing: partition, name: partition.name })} data-testid={`partition-describe-${partition.name}`}>
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
                        </>
                      ) : (
                        <Button
                          variant="ghost"
                          size="xs"
                          onClick={() => setSheet({ editing: null, name: partition.name })}
                          data-testid={`partition-register-${partition.name}`}
                        >
                          Register
                        </Button>
                      )}
                    </span>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Card>

      {sheet !== null && (
        <PartitionSheet editing={sheet.editing} initialName={sheet.name} first={registered.length === 0} onClose={() => setSheet(null)} />
      )}
      <ConfirmDialog
        open={removing !== null}
        title={`Remove ${removing?.name ?? ""} from the registry?`}
        message="Nothing kept under it is deleted: its caches, ledgers and runs stay. Flows that name no partitions stop running in it until it is registered again."
        confirmLabel="Remove"
        danger
        busy={remove.isPending}
        onConfirm={() => { if (removing !== null) { remove.mutate(removing.name); } }}
        onClose={() => setRemoving(null)}
      />
    </Page>
  );
}
