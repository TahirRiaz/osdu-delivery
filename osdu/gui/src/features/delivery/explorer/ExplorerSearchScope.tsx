import { Check, ChevronDown, Columns3, Search, TextSearch } from "lucide-react";
import { Button } from "@/components/ui/button";
import { CommandGroup, CommandItem } from "@/components/ui/command";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/utils";
import type { ExplorerFields } from "../../../api/explorer";
import { ExplorerAttributeList } from "./ExplorerAttributeList";
import { scopeKey, scopeName, scopeTitle, type SearchScopeTarget } from "./explorerScope";
import type { OfferedTerm } from "./explorerTerms";

/**
 * Where a place's search field searches, picked at its start: every property (what the field always does), or one source
 * column or one property, found by any part of its name. The list is the condition editor's: the source columns of the
 * type first, then the properties, the one picked marked. Opening it reads them, as typing in the field does.
 */
export function ExplorerSearchScope({ target, terms, fields, kind, open, onOpenChange, onWanted, onChoose, returnFocus, testId }: {
  /** The attribute searched in; null for every property. */
  target: SearchScopeTarget | null;
  terms: OfferedTerm[];
  fields: { data?: { answer: ExplorerFields }; isPending: boolean; isError: boolean; error: unknown };
  kind: string | undefined;
  /** Whether the list is open: the field opens it too, from the option that picks another property. */
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Called as the list opens, so the properties and the source columns are read only once they are wanted. */
  onWanted: () => void;
  onChoose: (target: SearchScopeTarget | null) => void;
  /** Where the cursor goes once the list closes: the search field, for the value. */
  returnFocus: () => void;
  testId: string;
}) {
  const choose = (next: SearchScopeTarget | null) => {
    onOpenChange(false);
    onChoose(next);
  };

  return (
    <Popover
      open={open}
      onOpenChange={(next) => {
        onOpenChange(next);
        if (next) {
          onWanted();
        }
      }}
    >
      <PopoverTrigger asChild>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          className={cn(
            "h-8 max-w-[240px] shrink-0 gap-1 rounded-r-none border border-r-0 border-input bg-secondary/70 px-2 text-[12px] font-normal hover:bg-accent dark:bg-input/60",
            target !== null && "text-foreground",
          )}
          title={target === null ? "Search every property, or pick one source column or property to search in" : `Searching in ${scopeName(target)}\n${scopeTitle(target)}`}
          aria-label={target === null ? "Search in every property; pick where to search" : `Search in ${scopeName(target)}; pick where to search`}
          data-search-scope=""
          data-testid={testId}
        >
          {target === null
            ? <Search className="size-3.5 text-muted-foreground" />
            : target.kind === "term" ? <Columns3 className="size-3.5 text-muted-foreground" /> : <TextSearch className="size-3.5 text-muted-foreground" />}
          {/* Every property is what the field does unless told otherwise: on a narrow window its glyph says it, so the field keeps its room. */}
          <span className={cn("text-muted-foreground", target === null && "max-xl:hidden")}>In</span>
          <span className={cn("min-w-0 truncate", target?.kind === "field" && "font-mono text-[11px]", target === null && "max-xl:hidden")} data-testid={`${testId}-name`}>
            {target === null ? "every property" : scopeName(target)}
          </span>
          <ChevronDown className="size-3.5 shrink-0 text-muted-foreground" />
        </Button>
      </PopoverTrigger>
      <PopoverContent
        align="start"
        className="w-[420px] p-0"
        onCloseAutoFocus={(event) => {
          event.preventDefault();
          // The cursor goes back to the field unless the reader has moved on meanwhile (opened Filter, say), whose focus stays.
          const focused = document.activeElement;
          if (focused === null || focused === document.body || (focused instanceof HTMLElement && focused.closest("[data-search-scope]") !== null)) {
            returnFocus();
          }
        }}
        data-search-scope=""
        data-testid={`${testId}-panel`}
      >
        {open && (
          <ExplorerAttributeList
            read={fields}
            kind={kind}
            onPick={(field) => choose({ kind: "field", field })}
            terms={terms}
            onPickTerm={(term) => choose({ kind: "term", term })}
            lead={(
              <CommandGroup>
                <CommandItem value="every property all" onSelect={() => choose(null)} className="gap-2" data-testid={`${testId}-every`}>
                  <Check className={cn("size-3.5 shrink-0", target === null ? "text-primary" : "invisible")} aria-hidden />
                  <span className="min-w-0 flex-1 truncate text-[13px]">Every property</span>
                  <span className="shrink-0 text-[11px] text-muted-foreground">ids, names and any text</span>
                </CommandItem>
              </CommandGroup>
            )}
            selected={target === null ? "every" : scopeKey(target)}
            placeholder={terms.length > 0 ? "Find a source column or property to search in" : "Find a property to search in"}
            testId={`${testId}-attributes`}
          />
        )}
      </PopoverContent>
    </Popover>
  );
}
