import type { ReactNode } from "react";
import { Check, Info, Loader2 } from "lucide-react";
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from "@/components/ui/command";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import type { ExplorerFieldInfo, ExplorerFields } from "../../../api/explorer";
import { ExplorerErrorText } from "./ExplorerProblem";
import { nestedLabel, offeredFields, originText } from "./explorerFields";
import { counted, fieldLabel } from "./explorerModel";
import { termTitle, type OfferedTerm } from "./explorerTerms";

/**
 * The groups the properties are listed in, in order: the content the schema declares, what only the records hold, and the
 * record's own properties, which every kind shares.
 */
const GROUPS: { origin: NonNullable<ExplorerFieldInfo["origin"]>; heading: string }[] = [
  { origin: "schema", heading: "Content" },
  { origin: "records", heading: "Not in the schema" },
  { origin: "record", heading: "Record" },
];

/**
 * A property of the records in view to pick, found by typing any part of its path or title: the record's own, the content
 * the kind's schema declares, and what the records hold beyond the schema (an index augmentation such as Equinor.*). Each
 * says how it is indexed and the nested list it sits in; its title and description, and where it was found, are on hover.
 * Where `terms` are given, the columns of the source systems the delivery flows read (osdu/docs/search-terms.md) are listed
 * first, each by its name with its system and the property it is searched in, found by any of them. The foot says what the
 * list was read from.
 */
export function ExplorerAttributeList({ read, kind, onPick, terms, onPickTerm, lead, selected, placeholder = "Find a property", testId }: {
  read: { data?: { answer: ExplorerFields }; isPending: boolean; isError: boolean; error: unknown };
  kind: string | undefined;
  onPick: (field: ExplorerFieldInfo) => void;
  /** The search terms of the type in view, offered before its properties; none where the list offers properties alone. */
  terms?: OfferedTerm[];
  onPickTerm?: (term: OfferedTerm) => void;
  /** What the list offers before the attributes (every property, for a search field's choice of where to search). */
  lead?: ReactNode;
  /** The attribute picked, marked: `term:<id>` or `field:<path>`; with it given, every item keeps room for the mark. */
  selected?: string;
  placeholder?: string;
  testId: string;
}) {
  const answer = read.data?.answer;
  const offered = answer === undefined ? [] : offeredFields(answer.fields, kind);
  const notes = answer?.notes ?? [];
  const mark = (key: string) => (selected === undefined ? null : <Check className={cn("size-3.5 shrink-0", selected === key ? "text-primary" : "invisible")} aria-hidden />);

  return (
    <Command data-testid={testId}>
      <CommandInput placeholder={placeholder} data-testid={`${testId}-find`} />
      <CommandList className="max-h-80">
        {lead}
        {read.isPending && (
          <div className="flex items-center gap-2 px-3 py-3 text-[12px] text-muted-foreground">
            <Loader2 className="size-3.5 animate-spin" />
            Reading the properties of the records and their schema
          </div>
        )}
        {read.isError && <ExplorerErrorText error={read.error} className="px-3 py-3" />}
        {answer !== undefined && <CommandEmpty>No property matches.</CommandEmpty>}
        {terms !== undefined && terms.length > 0 && onPickTerm !== undefined && (
          <CommandGroup heading="Source columns">
            {terms.map((offered) => (
              <CommandItem
                key={offered.term.id}
                value={`term ${offered.term.id} ${offered.term.name} ${offered.term.columnLabel} ${offered.term.system} ${offered.route.path}`}
                onSelect={() => onPickTerm(offered)}
                className="gap-2"
                title={termTitle(offered)}
                data-testid={`${testId}-term`}
                data-term={offered.term.id}
              >
                {mark(`term:${offered.term.id}`)}
                <span className="min-w-0 flex-1 truncate text-[13px]">{offered.term.name}</span>
                {offered.showSystem && <span className="shrink-0 rounded-sm border px-1 text-[10px] text-muted-foreground">{offered.term.system}</span>}
                <span className="max-w-[45%] shrink-0 truncate text-right font-mono text-[11px] text-muted-foreground">{fieldLabel(offered.route.path)}</span>
              </CommandItem>
            ))}
          </CommandGroup>
        )}
        {GROUPS.map(({ origin, heading }) => {
          // A property read before properties had an origin is content.
          const fields = offered.filter((field) => (field.origin ?? (field.path.startsWith("data.") ? "schema" : "record")) === origin);
          return fields.length === 0 ? null : (
            <CommandGroup key={origin} heading={heading}>
              {fields.map((field) => (
                <CommandItem
                  key={field.path}
                  value={`${field.path} ${field.title ?? ""}`}
                  onSelect={() => onPick(field)}
                  className="gap-2"
                  title={[field.path, field.title, field.description, originText(field, answer?.schemaKind)].filter(Boolean).join("\n")}
                  data-testid={`${testId}-field`}
                  data-path={field.path}
                >
                  {mark(`field:${field.path}`)}
                  <span className="min-w-0 flex-1 truncate font-mono text-[12px]">{fieldLabel(field.path)}</span>
                  {nestedLabel(field.nested) !== null && (
                    <span className="shrink-0 rounded-sm border px-1 text-[10px] text-muted-foreground" title={`Inside the nested list ${field.nested}`}>
                      in {nestedLabel(field.nested)}
                    </span>
                  )}
                  <span className="w-14 shrink-0 text-right text-[11px] text-muted-foreground">{field.index}</span>
                </CommandItem>
              ))}
            </CommandGroup>
          );
        })}
      </CommandList>
      {answer !== undefined && (
        <div className="flex items-center gap-1.5 border-t px-3 py-1.5 text-[11px] text-muted-foreground" data-testid={`${testId}-source`}>
          <span className="min-w-0 flex-1 truncate" title={answer.schemaKind ? `The schema of ${answer.schemaKind}, read from the Schema service, and the first ${counted(answer.sampled ?? 0, "record")} of the place` : undefined}>
            {answer.schemaKind
              ? `From the schema of ${answer.schemaKind} and ${counted(answer.sampled ?? 0, "record")}`
              : `From ${counted(answer.sampled ?? 0, "record")}`}
          </span>
          {notes.length > 0 && (
            <RichTooltip title="How the list was read" body={notes.join("\n\n")}>
              <Info className="size-3.5 shrink-0 text-warning" aria-label="How the list was read" data-testid={`${testId}-notes`} />
            </RichTooltip>
          )}
        </div>
      )}
    </Command>
  );
}
