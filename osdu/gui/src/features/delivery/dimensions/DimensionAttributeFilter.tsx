import { useState } from "react";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { ChevronLeft, ListFilter, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Command, CommandEmpty, CommandInput, CommandItem, CommandList } from "@/components/ui/command";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { useDebouncedValue } from "@/hooks/useDebouncedValue";
import { RichTooltip } from "@/components/RichTooltip";
import { deliveryApi, type DeliveryDimensionAttributeSpec, type DeliveryDimensionKey, type DimensionAttributeCondition } from "../../../api/delivery";
import { DimensionValueText } from "./DimensionValueText";

/** The attribute values a picker lists: those most records hold that match what is typed. */
const VALUE_LIMIT = 30;

const TYPING_DELAY_MS = 300;

/** The conditions grouped by attribute, in the order the dimension declares its attributes, for a line of chips. */
function grouped(conditions: DimensionAttributeCondition[], attributes: DeliveryDimensionAttributeSpec[]): { name: string; values: string[] }[] {
  return attributes
    .map((attribute) => ({ name: attribute.name, values: conditions.filter((c) => c.name === attribute.name).map((c) => c.value) }))
    .filter((group) => group.values.length > 0);
}

/**
 * Narrowing by the attributes a dimension's keys hold: a chip per attribute picked (<c>Country: Norway, Denmark</c>, any of
 * its values), and a picker that lists the dimension's attributes and then the values the one chosen holds, the most records
 * first, found by what is typed. Several attributes are all to hold, by the same key.
 */
export function DimensionAttributeFilter({ dimensionId, attributes, conditions, onChange, testId = "dimension-attribute-filter" }: {
  dimensionId: number;
  attributes: DeliveryDimensionAttributeSpec[];
  conditions: DimensionAttributeCondition[];
  onChange: (next: DimensionAttributeCondition[]) => void;
  testId?: string;
}) {
  const [open, setOpen] = useState(false);
  const [attribute, setAttribute] = useState<string | null>(null);
  const [typed, setTyped] = useState("");
  const search = useDebouncedValue(typed.trim(), TYPING_DELAY_MS);
  const values = useQuery({
    queryKey: ["delivery", "dimensions", "attribute-values", dimensionId, attribute, search],
    queryFn: () => deliveryApi.dimensionAttributeValues(dimensionId, attribute!, search, VALUE_LIMIT),
    enabled: open && attribute !== null,
    placeholderData: keepPreviousData,
    staleTime: 60_000,
  });

  if (attributes.length === 0) {
    return null;
  }

  const has = (name: string, value: string) => conditions.some((c) => c.name === name && c.value === value);
  const toggle = (name: string, value: string) => onChange(has(name, value)
    ? conditions.filter((c) => !(c.name === name && c.value === value))
    : [...conditions, { name, value }]);
  const openChange = (next: boolean) => {
    setOpen(next);
    if (next) {
      setTyped("");
      setAttribute(attributes.length === 1 ? attributes[0].name : null);
    }
  };

  return (
    <div className="flex flex-wrap items-center gap-1.5" data-testid={testId}>
      {grouped(conditions, attributes).map((group) => (
        <span
          key={group.name}
          className="inline-flex min-w-0 items-center gap-1 rounded-sm border border-primary/30 bg-primary/10 py-px pr-0.5 pl-1.5 text-[12px]"
          data-testid={`${testId}-chip`}
        >
          <span className="text-muted-foreground">{group.name}:</span>
          <span className="flex min-w-0 items-baseline">
            {group.values.map((value, index) => (
              <span key={value} className="inline-flex items-baseline">
                <DimensionValueText value={value} maxWidth={160} />
                {index < group.values.length - 1 && <span className="px-0.5 text-muted-foreground">or</span>}
              </span>
            ))}
          </span>
          <button
            type="button"
            onClick={() => onChange(conditions.filter((c) => c.name !== group.name))}
            className="rounded-sm p-0.5 text-muted-foreground outline-none hover:bg-accent hover:text-foreground focus-visible:bg-accent"
            aria-label={`Stop filtering by ${group.name}`}
            data-testid={`${testId}-remove`}
          >
            <X className="size-3" />
          </button>
        </span>
      ))}
      <Popover open={open} onOpenChange={openChange}>
        <PopoverTrigger asChild>
          <Button variant="outline" size="xs" data-testid={`${testId}-open`}>
            <ListFilter />
            {conditions.length === 0 ? "Attribute" : "Add"}
          </Button>
        </PopoverTrigger>
        <PopoverContent align="start" className="w-80 p-0" data-testid={`${testId}-picker`}>
          {attribute === null
            ? (
              <Command>
                <CommandList>
                  {attributes.map((spec) => (
                    <CommandItem key={spec.name} value={spec.name} onSelect={() => { setAttribute(spec.name); setTyped(""); }} data-testid={`${testId}-attribute`}>
                      <span className="text-[13px]">{spec.name}</span>
                      <span className="ml-auto truncate font-mono text-[11px] text-muted-foreground">{spec.steps.at(-1)}</span>
                    </CommandItem>
                  ))}
                </CommandList>
              </Command>
            )
            : (
              <Command shouldFilter={false}>
                <div className="flex items-center gap-1 border-b border-border px-1 py-1 text-[12px]">
                  {attributes.length > 1 && (
                    <Button variant="ghost" size="xs" onClick={() => setAttribute(null)} aria-label="Pick another attribute" data-testid={`${testId}-back`}>
                      <ChevronLeft />
                    </Button>
                  )}
                  <span className="font-medium">{attribute}</span>
                </div>
                <CommandInput placeholder={`A value of ${attribute}`} value={typed} onValueChange={setTyped} />
                <CommandList>
                  <CommandEmpty>
                    {values.isPending
                      ? "Reading the values..."
                      : values.isError
                        ? "The values could not be read."
                        : search === "" ? `No key holds ${attribute}.` : `No value of ${attribute} holds "${search}".`}
                  </CommandEmpty>
                  {(values.data ?? []).map((item) => (
                    <CommandItem
                      key={item.value}
                      value={item.value}
                      onSelect={() => toggle(attribute, item.value)}
                      className="flex items-center gap-2"
                      data-testid={`${testId}-value`}
                    >
                      <span className={has(attribute, item.value) ? "size-1.5 rounded-full bg-primary" : "size-1.5"} aria-hidden />
                      <span className="min-w-0 flex-1"><DimensionValueText value={item.value} maxWidth={180} /></span>
                      <span className="shrink-0 font-mono text-[11px] tabular-nums text-muted-foreground">{item.records.toLocaleString("en-US")}</span>
                    </CommandItem>
                  ))}
                </CommandList>
              </Command>
            )}
        </PopoverContent>
      </Popover>
    </div>
  );
}

/** A picked attribute value's column in a table of values: the values its keys hold, or a dash. */
export function ValueAttributeCell({ values }: { values: string[] }) {
  if (values.length === 0) {
    return <span className="text-muted-foreground/50">-</span>;
  }

  return (
    <span className="flex min-w-0 items-baseline" data-testid="dimension-attribute-cell">
      {values.map((value, index) => (
        <span key={value} className="inline-flex min-w-0 items-baseline">
          <DimensionValueText value={value} maxWidth={150} />
          {index < values.length - 1 && <span className="pr-1 text-muted-foreground">,</span>}
        </span>
      ))}
    </span>
  );
}

/** A key's attribute: the value read, with the record it was read from on hover; a dash when the key has none. */
export function KeyAttributeCell({ row, name }: { row: DeliveryDimensionKey; name: string }) {
  const attribute = row.attributes.find((a) => a.name === name);
  if (attribute === undefined) {
    return <span className="text-muted-foreground/50">-</span>;
  }

  return (
    <RichTooltip title={`${name}, read from`} body={attribute.from ?? "the record the key names"} mono>
      <span className="inline-flex min-w-0" data-testid="dimension-key-attribute">
        <DimensionValueText value={attribute.value} maxWidth={150} />
      </span>
    </RichTooltip>
  );
}
