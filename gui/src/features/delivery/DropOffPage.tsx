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

/**
 * The largest file this page writes straight to storage in one request. Azure Storage accepts a single blob write of up
 * to 5000 MiB and a larger blob only in blocks, which a browser has no business assembling: a file above this is a set
 * to prepare as a drop, or one for the producing system to upload through the API itself.
 */
const MaxDirectMegabytes = 5000;

/**
 * Writes one file to the URL a reservation handed out. The URL carries its own credential, so nothing else is sent with
 * it; the blob type header is what Azure Storage refuses a blob write without.
 */
async function writeToStorage(url: string, file: File): Promise<void> {
  const response = await fetch(url, {
    method: "PUT",
    headers: { "x-ms-blob-type": "BlockBlob", "Content-Type": file.type === "" ? "application/octet-stream" : file.type },
    body: file,
  });
  if (!response.ok) {
    throw new Error(`Storage refused '${file.name}' (${response.status} ${response.statusText}).`);
  }
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
  /** Which file is being written straight to storage, so a direct upload of several says where it has got to. */
  const [writing, setWriting] = useState<string | null>(null);

  const area = useQuery({ queryKey: ["delivery", "dropoff-area"], queryFn: deliveryApi.dropOffArea });
  const dropOffs = useQuery({ queryKey: ["delivery", "dropoffs"], queryFn: () => deliveryApi.dropOffs() });

  const reset = () => {
    setChosen([]);
    setLabel("");
    setWriting(null);
    if (fileInput.current !== null) {
      fileInput.current.value = "";
    }
  };

  const upload = useMutation({
    mutationFn: async () => {
      const named = label.trim() === "" ? undefined : label.trim();
      if (!direct) {
        return deliveryApi.uploadDropOff(chosen, named);
      }

      // Reserved first, so the drop-off exists before a single URL does and an upload nobody finishes is visible as
      // one. The bytes then go straight to storage, which is the whole point of this path.
      const reservation = await deliveryApi.reserveDropOff(chosen.map((file) => ({ name: file.name, bytes: file.size })), named);
      const byName = new Map(chosen.map((file) => [file.name, file]));
      for (const target of reservation.uploads) {
        const file = byName.get(target.name);
        if (file === undefined) {
          throw new Error(`The reservation named '${target.name}', which is not among the files selected.`);
        }

        setWriting(target.name);
        await writeToStorage(target.url, file);
      }

      setWriting(null);
      // No hash: the bytes never came past the control plane, and a browser cannot hash a file of this size without
      // reading it all into memory. The drop-off records that none was asserted rather than implying one was checked.
      return deliveryApi.completeDropOff(reservation.dropOffId, reservation.uploads.map((u) => ({ name: u.name })));
    },
    onSuccess: async (dropOff) => {
      reset();
      toast.success(`${dropOff.fileCount} file${dropOff.fileCount === 1 ? "" : "s"} dropped off. Point a submission at the location.`);
      await queryClient.invalidateQueries({ queryKey: ["delivery", "dropoffs"] });
    },
    onError: (error) => {
      setWriting(null);
      toast.error(isApiError(error) ? error.detail ?? error.title : String(error));
    },
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
  const signed = area.data?.signedUploads === true;
  const streamedCap = maxMegabytes * 1024 * 1024;
  // A file past what the control plane will carry goes straight to storage instead, when this deployment can hand out
  // a URL for it. One set takes one route: mixing the two would leave two drop-offs where the operator asked for one.
  const direct = signed && chosen.some((file) => file.size > streamedCap);
  const directCap = Math.min(area.data?.maxSignedFileGigabytes ?? 0, MaxDirectMegabytes / 1024) * 1024 * 1024 * 1024;
  const cap = direct ? directCap : streamedCap;
  const tooMany = chosen.length > maxFiles;
  const tooLarge = chosen.find((file) => file.size > cap);
  const problem = tooMany
    ? `One upload carries at most ${maxFiles} files; ${chosen.length} are selected.`
    : tooLarge === undefined
      ? null
      : direct
        ? `'${tooLarge.name}' is ${size(tooLarge.size)}, more than the ${size(cap)} this page writes straight to storage. Prepare a set that large as a drop instead.`
        : `'${tooLarge.name}' is ${size(tooLarge.size)}; one file uploaded through the control plane is at most ${maxMegabytes} MB.`
          + (signed ? "" : " This deployment cannot hand out upload URLs, so there is no direct route for a file this size.");

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
          <div className="flex items-center gap-1">
            <Badge variant={statusTone[row.status] ?? "outline"} className="w-fit">{row.status}</Badge>
            {row.uploadMode === "signed" && (
              <Badge variant="outline" className="w-fit" title="Written straight to storage; the control plane saw no bytes and computed no hash.">
                direct
              </Badge>
            )}
          </div>
          {row.error !== null && <TruncatedText text={row.error} maxWidth={320} />}
        </div>
      ),
    },
    {
      id: "files",
      header: "Files",
      render: (row) => {
        // A hash the control plane never computed is the uploader's word, and one nobody asserted is absent. Saying so
        // here is what keeps a reader from taking either for a check that happened.
        const asserted = row.files.filter((f) => f.hashSource === "client").length;
        const unhashed = row.files.filter((f) => f.hashSource === "none").length;
        return (
          <div className="flex flex-col">
            <span className="tabular-nums">{row.fileCount}</span>
            {asserted > 0 && <span className="text-[11px] text-muted-foreground">{asserted} hash asserted</span>}
            {unhashed > 0 && <span className="text-[11px] text-muted-foreground">{unhashed} without a hash</span>}
          </div>
        );
      },
    },
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
              Up to {maxFiles} files. Each at most {maxMegabytes} MB through the control plane
              {signed ? `, or up to ${size(directCap)} written straight to storage` : ""}. They land under{" "}
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
              {direct && " Too large to send through the control plane, so these go straight to storage. Nothing here computes"
                + " their content hash, so the drop-off will say it has none: a flow that decides payload changes by content"
                + " hash needs the hash from whatever produced the file."}
            </p>
          )}
          {writing !== null && (
            <p className="text-xs text-muted-foreground" data-testid="dropoff-writing">
              Writing <span className="font-mono">{writing}</span> to storage.
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
