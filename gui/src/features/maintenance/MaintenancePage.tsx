import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Database, Info, ListTree, Loader2, Trash2 } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { isApiError } from "../../api/client";
import { maintenanceApi } from "../../api/endpoints";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";

const STORAGE_KEY = ["maintenance", "trace-storage"] as const;

/** The API's detail line wins over a generic title for any toast on this page. */
function errorText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : "Something went wrong.";
}

/** One labelled count. */
function Stat({ icon: Icon, label, value, hint }: {
  icon: typeof Database;
  label: string;
  value: number;
  hint?: string;
}) {
  return (
    <div className="flex flex-col gap-1 rounded-lg border border-border p-3">
      <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <Icon className="size-3.5 shrink-0" />
        {label}
      </div>
      <div className="text-2xl font-semibold tabular-nums leading-7">{value.toLocaleString()}</div>
      {hint ? <div className="text-xs text-muted-foreground">{hint}</div> : null}
    </div>
  );
}

/**
 * Settings, Maintenance: the operator's view of the per-run trace. The run events are the execution log of every
 * run (what each step did, the counts, the timings). Old traces of finished runs can be pruned on a retention
 * schedule, pruned now, or purged outright; the run header (status, counts, error) and the delivery ledger are
 * always kept regardless, so the record-level history never depends on this setting.
 */
export default function MaintenancePage() {
  const queryClient = useQueryClient();
  const [confirmPurge, setConfirmPurge] = useState(false);

  const storageQuery = useQuery({
    queryKey: STORAGE_KEY,
    queryFn: () => maintenanceApi.traceStorage(),
  });

  const storage = storageQuery.data;

  // The retention editor mirrors the stored value until the operator edits it. Initialised once the summary loads.
  const [keepForever, setKeepForever] = useState(false);
  const [daysInput, setDaysInput] = useState("");
  const [retentionInit, setRetentionInit] = useState(false);

  if (storage && !retentionInit) {
    if (storage.retentionDays === null) {
      setKeepForever(true);
      setDaysInput("");
    } else {
      setKeepForever(false);
      setDaysInput(String(storage.retentionDays));
    }

    setRetentionInit(true);
  }

  const parsedDays = Number.parseInt(daysInput, 10);
  const retentionValue = keepForever ? null : parsedDays;
  const retentionValid = keepForever || (Number.isInteger(parsedDays) && parsedDays >= 0 && String(parsedDays) === daysInput.trim());
  const retentionChanged = storage !== undefined && retentionValue !== storage.retentionDays;

  const pruneMutation = useMutation({
    mutationFn: () => maintenanceApi.pruneEvents(),
    onSuccess: (result) => {
      void queryClient.invalidateQueries({ queryKey: STORAGE_KEY });
      toast.success(
        result.eventsDeleted === 0
          ? "No run events are past the retention window."
          : `Removed ${result.eventsDeleted.toLocaleString()} run event row(s).`,
      );
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const purgeMutation = useMutation({
    mutationFn: () => maintenanceApi.purgeEvents(),
    onSuccess: (result) => {
      setConfirmPurge(false);
      void queryClient.invalidateQueries({ queryKey: STORAGE_KEY });
      toast.success(
        result.eventsDeleted === 0
          ? "No run events to remove."
          : `Removed ${result.eventsDeleted.toLocaleString()} run event row(s).`,
      );
    },
    onError: (error) => {
      setConfirmPurge(false);
      toast.error(errorText(error));
    },
  });

  const retentionMutation = useMutation({
    mutationFn: () => maintenanceApi.setTraceRetention(retentionValue),
    onSuccess: (result) => {
      setKeepForever(result.retentionDays === null);
      setDaysInput(result.retentionDays === null ? "" : String(result.retentionDays));
      void queryClient.invalidateQueries({ queryKey: STORAGE_KEY });
      toast.success(
        result.retentionDays === null
          ? "Run events will be kept forever."
          : `Run events of finished runs older than ${result.retentionDays} day${result.retentionDays === 1 ? "" : "s"} will be removed.`,
      );
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const retentionText = useMemo(() => {
    if (!storage) {
      return "";
    }

    const days = storage.retentionDays;
    if (days === null) {
      return "Run events are kept forever; no automatic cleanup runs. Set a retention period to drop the traces of old finished runs.";
    }

    const window = days === 0 ? "as soon as the run has finished" : `once the run is older than ${days} day${days === 1 ? "" : "s"}`;
    return `Automatic cleanup removes the trace of a finished run ${window}; the run itself, its counts and its error stay. Reclaimable now: ${storage.prunableEvents.toLocaleString()} event row(s) across ${storage.prunableRuns.toLocaleString()} run${storage.prunableRuns === 1 ? "" : "s"}.`;
  }, [storage]);

  return (
    <Page data-testid="maintenance-page">
      <PageHeader
        title="Maintenance"
        subtitle="Reclaim storage held by the traces of old runs. Run history, counts, errors, and the delivery ledger are kept."
      />

      {storageQuery.isError ? (
        <Alert variant="destructive" data-testid="trace-storage-error">
          <Info className="size-4" />
          <AlertDescription className="flex items-center justify-between gap-3">
            <span>{errorText(storageQuery.error)}</span>
            <Button variant="outline" size="sm" onClick={() => void storageQuery.refetch()}>
              Retry
            </Button>
          </AlertDescription>
        </Alert>
      ) : storageQuery.isLoading || !storage ? (
        <Skeleton className="h-64 rounded-lg" />
      ) : (
        <Card className="flex flex-col gap-4 p-4" data-testid="run-events-card">
          <div className="flex flex-col gap-1">
            <h2 className="text-sm font-semibold">Run events</h2>
            <p className="text-xs text-muted-foreground">
              The per-run execution log: what each step did, the record counts, and the timings, streamed live
              while a run executes and kept afterwards as its trace. This is the heaviest per-run data.
            </p>
          </div>

          <div className="grid grid-cols-2 gap-3 sm:max-w-md">
            <Stat icon={ListTree} label="Events stored" value={storage.totalEvents} />
            <Stat icon={Trash2} label="Reclaimable now" value={storage.prunableEvents} />
          </div>

          <div className="flex flex-col gap-2 border-t border-border pt-4">
            <h3 className="text-sm font-medium">Automatic cleanup</h3>
            <Alert>
              <Info className="size-4" />
              <AlertDescription>{retentionText}</AlertDescription>
            </Alert>
            <div className="flex flex-wrap items-center gap-4">
              <div className="flex items-center gap-2">
                <Input
                  type="number"
                  min={0}
                  inputMode="numeric"
                  value={daysInput}
                  disabled={keepForever}
                  onChange={(e) => setDaysInput(e.target.value)}
                  className="w-24"
                  data-testid="retention-days"
                />
                <span className="text-sm text-muted-foreground">days</span>
              </div>
              <label className="flex items-center gap-2 text-sm">
                <Switch checked={keepForever} onCheckedChange={setKeepForever} data-testid="retention-keep-forever" />
                Keep forever
              </label>
              <Button
                size="sm"
                variant="outline"
                onClick={() => retentionMutation.mutate()}
                disabled={!retentionValid || !retentionChanged || retentionMutation.isPending}
                data-testid="retention-save"
              >
                {retentionMutation.isPending ? <Loader2 className="size-4 animate-spin" /> : null}
                Save
              </Button>
              <Button
                size="sm"
                variant="outline"
                onClick={() => pruneMutation.mutate()}
                disabled={storage.retentionDays === null || storage.prunableEvents === 0 || pruneMutation.isPending}
                data-testid="events-prune"
              >
                {pruneMutation.isPending ? <Loader2 className="size-4 animate-spin" /> : <Database className="size-4" />}
                Prune now
              </Button>
            </div>
          </div>

          <div className="flex items-center gap-3 border-t border-border pt-4">
            <Button
              variant="destructive"
              onClick={() => setConfirmPurge(true)}
              disabled={storage.totalEvents === 0 || purgeMutation.isPending}
              data-testid="events-purge"
            >
              {purgeMutation.isPending ? <Loader2 className="size-4 animate-spin" /> : <Trash2 className="size-4" />}
              Purge all run events
            </Button>
            <span className="text-xs text-muted-foreground">
              {storage.totalEvents === 0
                ? "Nothing stored."
                : "Deletes every stored run event now, the traces of running runs included."}
            </span>
          </div>
        </Card>
      )}

      <ConfirmDialog
        open={confirmPurge}
        title="Purge all run events?"
        message={
          storage
            ? `This permanently deletes all ${storage.totalEvents.toLocaleString()} stored run event row(s) for every run. Run history, counts and errors on the run header are kept, but every trace is lost. This cannot be undone.`
            : "This permanently deletes all stored run events. This cannot be undone."
        }
        confirmLabel="Purge run events"
        danger
        busy={purgeMutation.isPending}
        onConfirm={() => purgeMutation.mutate()}
        onClose={() => setConfirmPurge(false)}
      />
    </Page>
  );
}
