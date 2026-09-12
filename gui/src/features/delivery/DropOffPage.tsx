import { useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CircleAlert, Copy, Loader2, Trash2, Upload } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { isApiError } from "../../api/client";
import { deliveryApi, type DeliveryDropOff } from "../../api/delivery";
import { CorrelationError } from "../../components/CorrelationError";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { SearchInput } from "../../components/SearchInput";
import { TruncatedText } from "../../components/TruncatedText";

/** A size a person reads, from the bytes the API reports. */
function size(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }

  return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[unit]}`;
}

const statusTone: Record<string, "default" | "secondary" | "destructive" | "outline"> = {
  complete: "default",
  uploading: "secondary",
  failed: "destructive",
  deleted: "outline",
};

/**
 * The drop-off area: the pre-step to a submission. Files are uploaded here into the place the compute nodes can read,
 * and a submission afterwards points at where they landed, so nothing large ever travels inside a submission request.
 * What is uploaded stays until somebody removes it, because re-processing a submission (a redelivery, a verify) reads
 * its files again.
 */
export default function DropOffPage() {
  const queryClient = useQueryClient();
  const fileInput = useRef<HTMLInputElement>(null);
  const [search, setSearch] = useState("");
  const [label, setLabel] = useState("");
  const [chosen, setChosen] = useState<File[]>([]);

  const area = useQuery({ queryKey: ["delivery", "dropoff-area"], queryFn: deliveryApi.dropOffArea });
  const dropOffs = useQuery({ queryKey: ["delivery", "dropoffs"], queryFn: () => deliveryApi.dropOffs() });

  const reset = () => {
    setChosen([]);
    setLabel("");
    if (fileInput.current !== null) {
      fileInput.current.value = "";
    }
  };

  const upload = useMutation({
    mutationFn: () => deliveryApi.uploadDropOff(chosen, label.trim() === "" ? undefined : label.trim()),
    onSuccess: async (dropOff) => {
      reset();
      toast.success(`${dropOff.fileCount} file${dropOff.fileCount === 1 ? "" : "s"} dropped off. Point a submission at the location.`);
      await queryClient.invalidateQueries({ queryKey: ["delivery", "dropoffs"] });
    },
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  const remove = useMutation({
    mutationFn: (dropOffId: string) => deliveryApi.deleteDropOff(dropOffId),
    onSuccess: async () => {
      toast.success("The drop-off's files were removed.");
      await queryClient.invalidateQueries({ queryKey: ["delivery", "dropoffs"] });
    },
    onError: (error) => toast.error(isApiError(error) ? error.detail ?? error.title : String(error)),
  });

  const copy = async (location: string) => {
    try {
      await navigator.clipboard.writeText(location);
      toast.success("Location copied. Paste it into a submission's files.");
    } catch {
      toast.error("The clipboard is not available here; select the location and copy it by hand.");
    }
  };

  const term = search.trim().toLowerCase();
  const rows = (dropOffs.data ?? []).filter((d) => term === ""
    || (d.label ?? "").toLowerCase().includes(term)
    || d.location.toLowerCase().includes(term)
    || d.uploadedBy.toLowerCase().includes(term));

  const enabled = area.data?.enabled === true;
  const maxFiles = area.data?.maxFilesPerUpload ?? 0;
  const maxMegabytes = area.data?.maxFileMegabytes ?? 0;
  const tooMany = chosen.length > maxFiles;
  const tooLarge = chosen.find((file) => file.size > maxMegabytes * 1024 * 1024);
  const problem = tooMany
    ? `One upload carries at most ${maxFiles} files; ${chosen.length} are selected.`
    : tooLarge !== undefined
      ? `'${tooLarge.name}' is ${size(tooLarge.size)}; one file is at most ${maxMegabytes} MB.`
      : null;

  const columns: Column<DeliveryDropOff>[] = [
    {
      id: "dropoff",
      header: "Drop-off",
      render: (row) => (
        <div className="flex min-w-0 flex-col">
          <span className="truncate text-[13px] font-medium">{row.label ?? row.dropOffId.slice(0, 8)}</span>
          <TruncatedText text={row.location} maxWidth={420} />
        </div>
      ),
    },
    {
      id: "status",
      header: "Status",
      render: (row) => (
        <div className="flex flex-col gap-1">
          <Badge variant={statusTone[row.status] ?? "outline"} className="w-fit">{row.status}</Badge>
          {row.error !== null && <TruncatedText text={row.error} maxWidth={320} />}
        </div>
      ),
    },
    { id: "files", header: "Files", render: (row) => <span className="tabular-nums">{row.fileCount}</span> },
    { id: "size", header: "Size", render: (row) => <span className="tabular-nums">{size(row.totalBytes)}</span> },
    {
      id: "uploaded",
      header: "Uploaded",
      render: (row) => (
        <div className="flex flex-col">
          <span className="text-[13px]">{new Date(row.uploadedUtc + "Z").toLocaleString()}</span>
          <span className="text-[11px] text-muted-foreground">{row.uploadedBy}</span>
        </div>
      ),
    },
    {
      id: "actions",
      header: "",
      render: (row) => (
        <div className="flex justify-end gap-1">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => void copy(row.location)}
            disabled={row.status === "deleted"}
            data-testid={`dropoff-copy-${row.dropOffId}`}
          >
            <Copy />
            Copy location
          </Button>
          <Button
            variant="ghost"
            size="sm"
            onClick={() => remove.mutate(row.dropOffId)}
            disabled={row.status === "deleted" || remove.isPending}
            data-testid={`dropoff-delete-${row.dropOffId}`}
          >
            <Trash2 />
          </Button>
        </div>
      ),
    },
  ];

  return (
    <Page data-testid="page-dropoff">
      <PageHeader
        title="Drop-off"
        subtitle="Upload files to the area the compute nodes read, then point a submission at where they landed. Nothing is uploaded again when the records are sent."
      />
      {area.isError && (isApiError(area.error)
        ? <CorrelationError error={area.error} />
        : <p className="text-[13px] text-destructive">{String(area.error)}</p>)}
      {dropOffs.isError && isApiError(dropOffs.error) && <CorrelationError error={dropOffs.error} />}
      {area.data !== undefined && !enabled && (
        <Alert variant="destructive" data-testid="dropoff-disabled">
          <CircleAlert />
          <AlertDescription>
            This deployment configures no drop-off area, so there is nowhere to upload to. Set SQLFLOW_DROPOFF_ROOT on the
            control plane and on every node, to a location the control plane can write and the nodes can read.
          </AlertDescription>
        </Alert>
      )}
      {enabled && (
        <div className="flex flex-col gap-3 rounded-lg border border-border p-4" data-testid="dropoff-upload">
          <div className="flex flex-col gap-1">
            <h2 className="text-[13px] font-medium">Upload files</h2>
            <p className="text-xs text-muted-foreground">
              Up to {maxFiles} files, each at most {maxMegabytes} MB. They land under{" "}
              <span className="font-mono">{area.data?.location}</span>, and the run reads them from there when it delivers.
              {area.data !== undefined && area.data.retentionDays > 0
                ? ` A completed drop-off is removed after ${area.data.retentionDays} days.`
                : " Nothing is removed automatically; delete a drop-off when it is no longer needed."}
            </p>
          </div>
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div className="flex flex-col gap-1">
              <Label htmlFor="dropoff-files">Files</Label>
              <Input
                id="dropoff-files"
                ref={fileInput}
                type="file"
                multiple
                className="h-9"
                onChange={(event) => setChosen(Array.from(event.target.files ?? []))}
                data-testid="dropoff-files"
              />
            </div>
            <div className="flex flex-col gap-1">
              <Label htmlFor="dropoff-label">Label (optional)</Label>
              <Input
                id="dropoff-label"
                className="h-9"
                placeholder="what this drop-off is"
                value={label}
                onChange={(event) => setLabel(event.target.value)}
                data-testid="dropoff-label"
              />
            </div>
          </div>
          {chosen.length > 0 && (
            <p className="text-xs text-muted-foreground">
              {chosen.length} file{chosen.length === 1 ? "" : "s"} selected, {size(chosen.reduce((total, file) => total + file.size, 0))} in total.
            </p>
          )}
          {problem !== null && <p className="text-xs font-medium text-destructive" data-testid="dropoff-error">{problem}</p>}
          {upload.isError && isApiError(upload.error) && <CorrelationError error={upload.error} />}
          <div>
            <Button
              size="sm"
              disabled={chosen.length === 0 || problem !== null || upload.isPending}
              onClick={() => upload.mutate()}
              data-testid="dropoff-upload-submit"
            >
              {upload.isPending ? <Loader2 className="animate-spin" /> : <Upload />}
              Upload
            </Button>
          </div>
        </div>
      )}
      <SearchInput
        value={search}
        onChange={setSearch}
        placeholder="Filter by label, location or who uploaded"
        label="Filter the drop-offs"
        testId="dropoff-search"
      />
      {dropOffs.data !== undefined && dropOffs.data.length === 0 ? (
        <EmptyState
          icon={<Upload />}
          title="Nothing dropped off yet"
          description="Upload the files a submission needs, then point the submission at the location this page gives you."
          data-testid="dropoff-empty"
        />
      ) : (
        <DataTable
          columns={columns}
          rows={rows}
          rowKey={(row) => row.dropOffId}
          emptyMessage="No drop-off matches the filter."
          data-testid="dropoff-table"
        />
      )}
    </Page>
  );
}
