import { useEffect, useId, useRef, useState, type FormEvent, type KeyboardEvent, type ReactNode } from "react";
import { Braces, Columns3, CornerDownLeft, History, ListFilter, Search, TextSearch, Trash2, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { RelativeTime } from "@/components/RelativeTime";
import { cn } from "@/lib/utils";
import type { ExplorerFieldInfo } from "../../../api/explorer";
import { isRecordReference } from "../osduDocument";
import { idParts } from "../osduRecordModel";
import { nestedLabel } from "./explorerFields";
import { CONDITION_LABELS, fieldLabel, forgetRecentRecords, kindParts, recentRecords, searchInCondition, type RecentRecord } from "./explorerModel";
import { termSearchInCondition, termTitle, type OfferedTerm } from "./explorerTerms";

/** What a search field offers beside searching every property: the text searched in one property, picked under it or in the editor. */
export interface SearchIn {
  /** The properties offered first, the likeliest first. */
  choices: ExplorerFieldInfo[];
  /**
   * The source columns (search terms, osdu/docs/search-terms.md) offered after the properties: those searched in lately for
   * the type. None where the place is of many types.
   */
  terms?: OfferedTerm[];
  /** Whether the type has source columns to search in, which the editor lists beside its properties. */
  hasTerms?: boolean;
  /** Whether the properties are still being read. */
  reading: boolean;
  /** Called as the reader types, so the properties are read only once they may be wanted. */
  onWanted: () => void;
  /** Searches the text in one property, as a condition of the list. */
  onSearchIn: (field: ExplorerFieldInfo, text: string) => void;
  /** Searches the text in one source column, as a condition of the list, the text as the source holds it. */
  onSearchInTerm?: (term: OfferedTerm, text: string) => void;
  /** Opens the condition editor with the text, to pick the property it is searched in. */
  onChoose: (text: string) => void;
}

/**
 * A search field of the explorer: the page's own, which searches every type, and the one over the records of a group, a
 * type or a kind picked, which searches inside it. What is typed is read when Enter is pressed: a whole record id opens
 * that record, and anything else searches the records, as an id, the start of one, or words found anywhere in a record.
 * The braces switch it to a Lucene query, sent as written. A hint at the end of the field says what Enter will do, so the
 * reader is never surprised. The cross and Escape clear the field and the search it shows, as Enter does on the field
 * emptied, so the records are whole again. The field shows the search in the address, so going back restores it, and a
 * search sent leaves the cursor where it was. Asked to (`focusRequest`), it takes the cursor to the end of what it shows,
 * so a query the page put there is edited at once.
 */
export function ExplorerSearchBar({ text, lucene, placeholder, label, onSearch, onOpenId, searchIn, shortcut = false, focusRequest = 0, className, testId }: {
  text: string;
  lucene: boolean;
  placeholder: string;
  /** The field's accessible name, which tells the page's field from the one over the records. */
  label: string;
  onSearch: (text: string, lucene: boolean) => void;
  onOpenId: (id: string) => void;
  /** Searching the text in one property, offered under the field as it is typed; none where the field searches every property alone. */
  searchIn?: SearchIn;
  /** Whether `/` is the page's way into this field, shown at its end while it is empty and the cursor elsewhere. */
  shortcut?: boolean;
  /** Raised by the page to put the cursor in the field, at the end of what it shows; the value it starts with does not. */
  focusRequest?: number;
  className?: string;
  /** Base testid; the input, the hint, the clear button and the braces add `-input`, `-hint`, `-clear` and `-lucene`. */
  testId: string;
}) {
  const [draft, setDraft] = useState(text);
  const [asLucene, setAsLucene] = useState(lucene);
  // A new search in the address (one sent, Back, a query to edit) replaces what the field holds; the field stays the same
  // element, so the cursor stays in it.
  const [shown, setShown] = useState({ text, lucene });
  if (shown.text !== text || shown.lucene !== lucene) {
    setShown({ text, lucene });
    setDraft(text);
    setAsLucene(lucene);
  }

  const input = useRef<HTMLInputElement>(null);
  const focused = useRef(focusRequest);
  useEffect(() => {
    if (focusRequest === focused.current) {
      return;
    }

    focused.current = focusRequest;
    const field = input.current;
    if (field !== null) {
      field.focus();
      field.setSelectionRange(field.value.length, field.value.length);
    }
  }, [focusRequest]);
  const typed = draft.trim();
  const opens = !asLucene && isRecordReference(typed);
  // Under the field as text is typed: every property (Enter), each property offered, and another property to pick.
  const [hasFocus, setHasFocus] = useState(false);
  const [dismissed, setDismissed] = useState(false);
  const [active, setActive] = useState(0);
  const listId = useId();
  const offering = searchIn !== undefined && hasFocus && !dismissed && typed !== "" && !asLucene && !opens;
  const termChoices = searchIn?.onSearchInTerm === undefined ? [] : searchIn.terms ?? [];
  const options = offering ? searchIn.choices.length + termChoices.length + 2 : 0;
  const highlighted = Math.min(active, Math.max(options - 1, 0));
  // The search the field shows is still the list's until it is cleared: an emptied field says Enter clears it.
  const searched = text !== "";
  const hint = typed === "" ? (searched ? "clear" : null) : opens ? "open" : asLucene ? "query" : "search";

  // Clearing clears the search as well as the field; a field holding no search has only its draft to drop. The cursor
  // stays in the field, for the next search.
  const clear = () => {
    setDraft("");
    input.current?.focus();
    if (searched) {
      onSearch("", false);
    }
  };

  // The options after the first: each property offered, each source column offered, then another property or column,
  // picked in the condition editor.
  const pickOption = (option: number) => {
    if (searchIn === undefined) {
      return;
    }

    setDismissed(true);
    // The text is the condition's from here (or the editor's, to pick its property in), not the field's to search with.
    setDraft("");
    const fields = searchIn.choices.length;
    if (option <= fields) {
      searchIn.onSearchIn(searchIn.choices[option - 1], typed);
    } else if (option <= fields + termChoices.length) {
      searchIn.onSearchInTerm?.(termChoices[option - fields - 1], typed);
    } else {
      searchIn.onChoose(typed);
    }
  };

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (offering && highlighted > 0) {
      pickOption(highlighted);
      return;
    }

    setDismissed(true);
    if (opens) {
      onOpenId(typed);
    } else if (typed !== "" || searched) {
      onSearch(typed, asLucene);
    }
  };

  const keyed = (event: KeyboardEvent<HTMLInputElement>) => {
    if (offering && (event.key === "ArrowDown" || event.key === "ArrowUp")) {
      event.preventDefault();
      setActive((highlighted + (event.key === "ArrowDown" ? 1 : options - 1)) % options);
      return;
    }

    if (event.key === "Escape" && offering) {
      event.preventDefault();
      setDismissed(true);
      return;
    }

    if (event.key === "Escape" && (draft !== "" || searched)) {
      event.preventDefault();
      clear();
    }
  };

  return (
    <form role="search" aria-label={label} onSubmit={submit} className={cn("relative flex min-w-0 items-center", className)} data-testid={testId}>
      <Search className="pointer-events-none absolute left-3 size-4 text-muted-foreground" />
      <Input
        ref={input}
        value={draft}
        onChange={(event) => {
          setDraft(event.target.value);
          setDismissed(false);
          setActive(0);
          searchIn?.onWanted();
        }}
        onKeyDown={keyed}
        onFocus={() => setHasFocus(true)}
        onBlur={() => setHasFocus(false)}
        role={searchIn === undefined ? undefined : "combobox"}
        aria-expanded={searchIn === undefined ? undefined : offering}
        aria-controls={offering ? listId : undefined}
        aria-activedescendant={offering ? `${listId}-${highlighted}` : undefined}
        placeholder={asLucene ? "A Lucene query: createTime:[2024-01-01 TO *] AND kind:*Wellbore*" : placeholder}
        aria-label={label}
        spellCheck={false}
        autoComplete="off"
        className={cn("peer h-8 bg-secondary/70 pl-9 pr-36 text-[13px] dark:bg-input/60", asLucene && "font-mono text-[12px]")}
        data-testid={`${testId}-input`}
      />
      {shortcut && draft === "" && (
        <kbd
          className="pointer-events-none absolute right-9 flex h-5 min-w-5 items-center justify-center rounded border bg-background/60 px-1 font-mono text-[11px] text-muted-foreground peer-focus:hidden"
          title="Press / to search"
          data-testid={`${testId}-shortcut`}
        >
          /
        </kbd>
      )}
      <div className="absolute right-1.5 flex items-center gap-1">
        {hint !== null && (
          <span className="pointer-events-none flex items-center gap-1 rounded border px-1.5 py-0.5 text-[11px] text-muted-foreground" data-testid={`${testId}-hint`}>
            <CornerDownLeft className="size-3" />
            {hint}
          </span>
        )}
        {draft !== "" && (
          <button type="button" onClick={clear} aria-label="Clear the search" className="rounded-sm p-0.5 text-muted-foreground hover:text-foreground" data-testid={`${testId}-clear`}>
            <X className="size-4" />
          </button>
        )}
        <Button
          type="button"
          variant={asLucene ? "secondary" : "ghost"}
          size="icon"
          className={cn("size-6", asLucene && "text-primary")}
          aria-pressed={asLucene}
          aria-label="Write a Lucene query"
          title={asLucene ? "Searching with a Lucene query, sent as written. Click to search by id, name or text." : "Write a Lucene query: field:value, AND, OR, quotes and wildcards, sent as written."}
          onClick={() => setAsLucene((was) => !was)}
          data-testid={`${testId}-lucene`}
        >
          <Braces />
        </Button>
      </div>
      {offering && (
        <SearchInOptions
          id={listId}
          typed={typed}
          searchIn={searchIn}
          terms={termChoices}
          highlighted={highlighted}
          onHighlight={setActive}
          onEverywhere={() => { setDismissed(true); onSearch(typed, false); }}
          onPick={pickOption}
          testId={`${testId}-in`}
        />
      )}
    </form>
  );
}

/**
 * The options under a search field as text is typed: the text searched in every property (what Enter does), in one of the
 * properties offered (those searched in lately for the type, then the record's name), in one of the source columns searched
 * in lately, or in another property or column, picked in the condition editor. Text searched in one property becomes a
 * condition of the list, said beside it: its words for text, its start for a keyword, its whole value otherwise; in a source
 * column, as the column's route allows. The arrow keys move among the options and Enter takes the one lit; the field keeps
 * the cursor, since a press on an option does not take it.
 */
function SearchInOptions({ id, typed, searchIn, terms, highlighted, onHighlight, onEverywhere, onPick, testId }: {
  id: string;
  typed: string;
  searchIn: SearchIn;
  terms: OfferedTerm[];
  highlighted: number;
  onHighlight: (option: number) => void;
  onEverywhere: () => void;
  onPick: (option: number) => void;
  testId: string;
}) {
  const option = (index: number, onChoose: () => void, children: ReactNode, suffix: string, title?: string) => (
    <div
      key={`${suffix}:${index}`}
      id={`${id}-${index}`}
      role="option"
      aria-selected={highlighted === index}
      title={title}
      className={cn("flex h-8 cursor-pointer items-center gap-2 rounded-md px-2 text-[13px]", highlighted === index && "bg-accent text-accent-foreground")}
      // The field keeps the cursor: a press on an option does not blur it.
      onMouseDown={(event) => event.preventDefault()}
      onMouseEnter={() => onHighlight(index)}
      onClick={onChoose}
      data-testid={`${testId}-${suffix}`}
    >
      {children}
    </div>
  );

  return (
    <div id={id} role="listbox" aria-label="Where to search" className="absolute top-full right-0 left-0 z-50 mt-1 rounded-md border bg-popover p-1 text-popover-foreground shadow-md" data-testid={testId}>
      {option(0, onEverywhere, (
        <>
          <Search className="size-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 flex-1 truncate">
            Search every property for <span className="font-medium">{typed}</span>
          </span>
          <CornerDownLeft className="size-3.5 shrink-0 text-muted-foreground" />
        </>
      ), "everywhere")}
      <div className="px-2 pt-1.5 pb-0.5 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Search in one property</div>
      {searchIn.choices.map((field, at) => option(at + 1, () => onPick(at + 1), (
        <>
          <TextSearch className="size-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 flex-1 truncate">
            <span className="font-mono text-[12px]">{fieldLabel(field.path)}</span>
            <span className="text-muted-foreground"> {CONDITION_LABELS[searchInCondition(field)]} </span>
            <span className="font-medium">{typed}</span>
          </span>
          {nestedLabel(field.nested) !== null && <span className="shrink-0 rounded-sm border px-1 text-[10px] text-muted-foreground">in {nestedLabel(field.nested)}</span>}
        </>
      ), "field", [field.path, field.title, field.description].filter(Boolean).join("\n")))}
      {terms.length > 0 && <div className="px-2 pt-1.5 pb-0.5 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">Search in a source column</div>}
      {terms.map((term, at) => option(searchIn.choices.length + at + 1, () => onPick(searchIn.choices.length + at + 1), (
        <>
          <Columns3 className="size-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 flex-1 truncate">
            <span>{term.term.name}</span>
            <span className="text-muted-foreground"> {CONDITION_LABELS[termSearchInCondition(term.route)]} </span>
            <span className="font-medium">{typed}</span>
          </span>
          {term.showSystem && <span className="shrink-0 rounded-sm border px-1 text-[10px] text-muted-foreground">{term.term.system}</span>}
        </>
      ), "term", termTitle(term)))}
      {option(searchIn.choices.length + terms.length + 1, () => onPick(searchIn.choices.length + terms.length + 1), (
        <>
          <ListFilter className="size-3.5 shrink-0 text-muted-foreground" />
          <span className="min-w-0 flex-1 truncate text-muted-foreground">
            {searchIn.reading ? "Reading the properties..." : searchIn.hasTerms ? "Search in another property or source column..." : "Search in another property..."}
          </span>
        </>
      ), "choose")}
    </div>
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
        <Button variant="outline" size="sm" className="gap-1.5 px-2.5 text-[13px]" title="The records opened last" data-testid="explorer-recent">
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
