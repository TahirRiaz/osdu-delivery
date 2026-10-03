import { useState } from "react";
import { ArrowLeft, ListTree, Loader2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from "@/components/ui/command";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { ExplorerTaskErrorText } from "./ExplorerProblem";
import {
  explorerApi, type ExplorerFieldInfo, type ExplorerFields, type ExplorerFilter, type ExplorerPage, type ExplorerSearchRequest,
} from "../../../api/explorer";
import { isRecordReference } from "../osduDocument";
import { RecordName } from "../RecordName";
import { fieldLabel, useExplorerRead } from "./explorerModel";

/**
 * Group by: the records in view grouped by one property's distinct values, each with how many of the records hold it, and a
 * value picked narrows the records to it. The properties are what OSDU holds: the record's own (its kind, who wrote it, its
 * access and legal) for every type, and for a type the properties of its content, read from one of its records. Everything
 * is read when the panel opens, not before.
 */
export function ExplorerGroupBy({ partition, base, onFilter }: {
  partition: string | null;
  /** The search in view: its text, kind and values, which the groups count within. */
  base: ExplorerSearchRequest;
  onFilter: (filter: ExplorerFilter) => void;
}) {
  const [open, setOpen] = useState(false);
  const [field, setField] = useState<ExplorerFieldInfo | null>(null);
  const kind = base.kind ?? "*:*:*:*";
  const fields = useExplorerRead<ExplorerFields>(["fields", partition, kind], open ? () => explorerApi.fields(partition, kind) : null);
  const groups = useExplorerRead<ExplorerPage>(
    ["groups", partition, base, field?.path ?? null],
    open && field !== null ? () => explorerApi.search(partition, { ...base, offset: 0, limit: 1, facet: { path: field.path, index: field.index } }) : null,
  );

  // A type's content is offered for a type; across every type only the properties every record has mean the same thing.
  const offered = (fields.data?.answer.fields ?? []).filter((info) => base.kind !== undefined || !info.path.startsWith("data."));
  const envelope = offered.filter((info) => !info.path.startsWith("data."));
  const content = offered.filter((info) => info.path.startsWith("data."));
  const buckets = groups.data?.answer.facet ?? null;

  return (
    <Popover open={open} onOpenChange={(next) => { setOpen(next); if (!next) { setField(null); } }}>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm" className="h-8 gap-1.5 px-2.5 text-[13px]" title="Group the records by one property's values, and narrow to one of them" data-testid="explorer-group-by">
          <ListTree />
          Group by
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-[380px] p-0" data-testid="explorer-group-by-panel">
        {field === null
          ? (
            <Command>
              <CommandInput placeholder="Find a property" data-testid="explorer-group-by-find" />
              <CommandList className="max-h-80">
                {fields.isPending && (
                  <div className="flex items-center gap-2 px-3 py-3 text-[12px] text-muted-foreground">
                    <Loader2 className="size-3.5 animate-spin" />
                    Reading the properties of a record
                  </div>
                )}
                {fields.isError && <ExplorerTaskErrorText error={fields.error} className="px-3 py-3" />}
                {fields.data !== undefined && <CommandEmpty>No property matches.</CommandEmpty>}
                {envelope.length > 0 && (
                  <CommandGroup heading="Record">
                    {envelope.map((info) => <FieldItem key={info.path} info={info} onPick={setField} />)}
                  </CommandGroup>
                )}
                {content.length > 0 && (
                  <CommandGroup heading="Content">
                    {content.map((info) => <FieldItem key={info.path} info={info} onPick={setField} />)}
                  </CommandGroup>
                )}
              </CommandList>
            </Command>
          )
          : (
            <div className="flex max-h-96 flex-col">
              <div className="flex items-center gap-2 border-b px-2 py-1.5">
                <Button variant="ghost" size="icon" className="size-7" onClick={() => setField(null)} aria-label="Pick another property" data-testid="explorer-group-by-back">
                  <ArrowLeft />
                </Button>
                <span className="min-w-0 flex-1 truncate font-mono text-[12px]" title={field.path}>{fieldLabel(field.path)}</span>
                <span className="shrink-0 text-[11px] text-muted-foreground">{field.index}</span>
              </div>
              <div className="min-h-0 flex-1 overflow-y-auto p-1" data-testid="explorer-group-by-values">
                {groups.isPending && (
                  <div className="flex items-center gap-2 px-2 py-2 text-[12px] text-muted-foreground">
                    <Loader2 className="size-3.5 animate-spin" />
                    Counting the values
                  </div>
                )}
                {groups.isError && <ExplorerTaskErrorText error={groups.error} className="px-2 py-2" />}
                {groups.data?.answer.refusal && <p className="px-2 py-2 text-[12px] text-destructive">{`The search service would not group by this property: ${groups.data.answer.refusal}`}</p>}
                {buckets !== null && buckets.length === 0 && <p className="px-2 py-2 text-[12px] text-muted-foreground">None of these records holds a value of it.</p>}
                {buckets?.map((bucket, index) => (
                  <button
                    key={`${bucket.value ?? ""}:${index}`}
                    type="button"
                    disabled={bucket.value === null}
                    onClick={() => {
                      if (bucket.value !== null) {
                        onFilter({ path: field.path, index: field.index, value: bucket.value });
                        setOpen(false);
                        setField(null);
                      }
                    }}
                    className="flex h-7 w-full min-w-0 items-center gap-2 rounded-md px-2 text-left text-[12px] hover:bg-accent/60 disabled:cursor-default disabled:opacity-60"
                    title={bucket.value ?? "The service named no value for these records"}
                    data-testid="explorer-group-by-value"
                  >
                    <span className="min-w-0 flex-1 truncate">
                      {bucket.value !== null && isRecordReference(bucket.value) ? <RecordName id={bucket.value} /> : bucket.value ?? "(no value named)"}
                    </span>
                    <span className="shrink-0 font-mono text-[11px] tabular-nums text-muted-foreground">{bucket.count.toLocaleString("en-US")}</span>
                  </button>
                ))}
              </div>
            </div>
          )}
      </PopoverContent>
    </Popover>
  );
}

function FieldItem({ info, onPick }: { info: ExplorerFieldInfo; onPick: (info: ExplorerFieldInfo) => void }) {
  return (
    <CommandItem value={info.path} onSelect={() => onPick(info)} className="gap-2" data-testid="explorer-group-by-field">
      <span className="min-w-0 flex-1 truncate font-mono text-[12px]">{fieldLabel(info.path)}</span>
      <span className="shrink-0 text-[11px] text-muted-foreground">{info.index}</span>
    </CommandItem>
  );
}
