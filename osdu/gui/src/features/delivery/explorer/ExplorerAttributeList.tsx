import { Info, Loader2 } from "lucide-react";
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from "@/components/ui/command";
import { RichTooltip } from "@/components/RichTooltip";
import type { ExplorerFieldInfo, ExplorerFields } from "../../../api/explorer";
import { ExplorerErrorText } from "./ExplorerProblem";
import { nestedLabel, offeredFields, originText } from "./explorerFields";
import { counted, fieldLabel } from "./explorerModel";

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
 * The foot says what the list was read from.
 */
export function ExplorerAttributeList({ read, kind, onPick, placeholder = "Find a property", testId }: {
  read: { data?: { answer: ExplorerFields }; isPending: boolean; isError: boolean; error: unknown };
  kind: string | undefined;
  onPick: (field: ExplorerFieldInfo) => void;
  placeholder?: string;
  testId: string;
}) {
  const answer = read.data?.answer;
  const offered = answer === undefined ? [] : offeredFields(answer.fields, kind);
  const notes = answer?.notes ?? [];

  return (
    <Command data-testid={testId}>
      <CommandInput placeholder={placeholder} data-testid={`${testId}-find`} />
      <CommandList className="max-h-80">
        {read.isPending && (
          <div className="flex items-center gap-2 px-3 py-3 text-[12px] text-muted-foreground">
            <Loader2 className="size-3.5 animate-spin" />
            Reading the properties of the records and their schema
          </div>
        )}
        {read.isError && <ExplorerErrorText error={read.error} className="px-3 py-3" />}
        {answer !== undefined && <CommandEmpty>No property matches.</CommandEmpty>}
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
