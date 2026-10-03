import { useState, type FormEvent, type KeyboardEvent } from "react";
import { Braces, CornerDownLeft, History, Search, Trash2, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { RelativeTime } from "@/components/RelativeTime";
import { cn } from "@/lib/utils";
import { isRecordReference } from "../osduDocument";
import { idParts } from "../osduRecordModel";
import { forgetRecentRecords, kindParts, recentRecords, type RecentRecord } from "./explorerModel";

/**
 * The explorer's one search field. What is typed is read when Enter is pressed: a whole record id opens that record, and
 * anything else searches the records in the place picked, as an id, the start of one, or words found anywhere in a record.
 * The braces switch it to a Lucene query, sent as written. A hint at the end of the field says what Enter will do, so the
 * reader is never surprised; Escape clears it. The field starts from the search in the address, so going back restores it.
 */
export function ExplorerSearchBar({ text, lucene, placeholder, onSearch, onOpenId, className }: {
  text: string;
  lucene: boolean;
  placeholder: string;
  onSearch: (text: string, lucene: boolean) => void;
  onOpenId: (id: string) => void;
  className?: string;
}) {
  const [draft, setDraft] = useState(text);
  const [asLucene, setAsLucene] = useState(lucene);
  const typed = draft.trim();
  const opens = !asLucene && isRecordReference(typed);
  const hint = typed === "" ? null : opens ? "open" : asLucene ? "query" : "search";

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (opens) {
      onOpenId(typed);
    } else {
      onSearch(typed, asLucene);
    }
  };

  const keyed = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === "Escape" && draft !== "") {
      event.preventDefault();
      setDraft("");
    }
  };

  return (
    <form role="search" onSubmit={submit} className={cn("relative flex min-w-0 items-center", className)} data-testid="explorer-search">
      <Search className="pointer-events-none absolute left-3 size-4 text-muted-foreground" />
      <Input
        value={draft}
        onChange={(event) => setDraft(event.target.value)}
        onKeyDown={keyed}
        placeholder={asLucene ? "A Lucene query: data.FacilityName:\"NO 33*\" AND kind:*Wellbore*" : placeholder}
        aria-label="Search OSDU"
        spellCheck={false}
        autoComplete="off"
        className={cn("h-9 bg-secondary/70 pl-9 pr-36 text-[13px] dark:bg-input/60", asLucene && "font-mono text-[12px]")}
        data-testid="explorer-search-input"
      />
      <div className="absolute right-1.5 flex items-center gap-1">
        {hint !== null && (
          <span className="pointer-events-none flex items-center gap-1 rounded border px-1.5 py-0.5 text-[11px] text-muted-foreground" data-testid="explorer-search-hint">
            <CornerDownLeft className="size-3" />
            {hint}
          </span>
        )}
        {draft !== "" && (
          <button type="button" onClick={() => setDraft("")} aria-label="Clear the search" className="rounded-sm p-0.5 text-muted-foreground hover:text-foreground" data-testid="explorer-search-clear">
            <X className="size-4" />
          </button>
        )}
        <Button
          type="button"
          variant={asLucene ? "secondary" : "ghost"}
          size="icon"
          className={cn("size-7", asLucene && "text-primary")}
          aria-pressed={asLucene}
          aria-label="Write a Lucene query"
          title={asLucene ? "Searching with a Lucene query, sent as written. Click to search by id, name or text." : "Write a Lucene query: field:value, AND, OR, quotes and wildcards, sent as written."}
          onClick={() => setAsLucene((was) => !was)}
          data-testid="explorer-search-lucene"
        >
          <Braces />
        </Button>
      </div>
    </form>
  );
}

/**
 * The records opened last in this browser, newest first, one click from being opened again: the reader's own trail across
 * sessions, kept nowhere but here. A record of another partition opens in that partition.
 */
export function ExplorerRecent({ onOpen }: { onOpen: (record: RecentRecord) => void }) {
  const [open, setOpen] = useState(false);
  const [records, setRecords] = useState<RecentRecord[]>([]);
  const forget = () => {
    forgetRecentRecords();
    setRecords([]);
  };

  return (
    <Popover open={open} onOpenChange={(next) => { setOpen(next); if (next) { setRecords(recentRecords()); } }}>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm" className="h-9 gap-1.5 px-2.5 text-[13px]" title="The records opened last" data-testid="explorer-recent">
          <History />
          Recent
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-[420px] p-0" data-testid="explorer-recent-panel">
        <div className="flex items-center justify-between border-b px-3 py-2">
          <span className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Opened lately</span>
          {records.length > 0 && (
            <Button variant="ghost" size="sm" className="h-6 gap-1 px-1.5 text-[11px] text-muted-foreground" onClick={forget} data-testid="explorer-recent-forget">
              <Trash2 className="size-3" />
              Forget
            </Button>
          )}
        </div>
        {records.length === 0
          ? <p className="px-3 py-4 text-[12px] text-muted-foreground">Records opened in the explorer show here, in this browser only.</p>
          : (
            <div className="max-h-80 overflow-y-auto p-1">
              {records.map((record) => (
                <button
                  key={`${record.partition ?? ""}|${record.id}`}
                  type="button"
                  onClick={() => { setOpen(false); onOpen(record); }}
                  className="flex w-full min-w-0 items-center gap-2 rounded-md px-2 py-1.5 text-left hover:bg-accent/60"
                  title={record.id}
                  data-testid="explorer-recent-item"
                >
                  <span className="flex min-w-0 flex-1 flex-col">
                    <span className="truncate text-[13px]">{record.name ?? idParts(record.id).unique}</span>
                    <span className="truncate text-[11px] text-muted-foreground">
                      {[kindParts(record.kind ?? `*:*:${idParts(record.id).group}--${idParts(record.id).type}:*`).type, record.partition].filter(Boolean).join(" in ")}
                    </span>
                  </span>
                  <span className="shrink-0 text-[11px] text-muted-foreground"><RelativeTime value={record.openedUtc} absolute={false} /></span>
                </button>
              ))}
            </div>
          )}
      </PopoverContent>
    </Popover>
  );
}
