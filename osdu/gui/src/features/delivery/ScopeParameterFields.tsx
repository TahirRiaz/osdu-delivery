import { useRef, useState, type ChangeEvent, type KeyboardEvent, type ReactNode } from "react";
import { ChevronsUpDown, Loader2, TriangleAlert } from "lucide-react";
import { Command, CommandEmpty, CommandItem, CommandList } from "@/components/ui/command";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Popover, PopoverAnchor, PopoverContent } from "@/components/ui/popover";
import { cn } from "@/lib/utils";
import type { DeliveryFlowScope, DeliveryParameter, DeliveryScopeParameterValues } from "../../api/delivery";
import { useScopeValues } from "./useScopeValues";

const count = new Intl.NumberFormat("en-US");

/** The input surface every field here wears, so a field with its name inside it reads as the inputs beside it. */
const SURFACE = "rounded-md border border-input bg-transparent shadow-xs transition-[color,box-shadow] dark:bg-input/30";

interface ScopeParameterFieldsProps {
  /** The flow whose scope the values fill, or null before one is known. */
  pipelineId: string | null;
  /** Its interface and partition. */
  scope: DeliveryFlowScope;
  /** The parameters the flow declares, each saying the column its scope reads, if any. */
  parameters: DeliveryParameter[];
  values: Record<string, string>;
  onChange: (name: string, value: string) => void;
  /** Whether the reader may queue the node read of the values; without it every field is typed. */
  canRead: boolean;
  /** Ids and testids are `<prefix>-parameter-<name>`. */
  prefix: string;
  /**
   * `stacked` puts each parameter's name above its field, for a form; `inline` puts it inside the field, for a toolbar
   * that keeps its controls on one line.
   */
  layout?: "stacked" | "inline";
  /** The width of each field. */
  className?: string;
}

/**
 * A flow's parameters as fields, for the pages that read a scope of it (a flow's Preview tab, a mapping's value check, the
 * trigger dialog). A parameter the scope binds to a column of the record table offers that column's values, with how many
 * rows hold each, read from the flow's own table whatever the source, and still takes any value typed; a value
 * the column holds no row of says the scope would read nothing. Any other parameter (one naming the work location) is
 * typed. The fields are returned side by side for the page to lay out.
 */
export function ScopeParameterFields({
  pipelineId, scope, parameters, values, onChange, canRead, prefix, layout = "stacked", className,
}: ScopeParameterFieldsProps) {
  const scoped = parameters.some((parameter) => parameter.scopeColumn);
  const read = useScopeValues(pipelineId, scope, canRead && scoped);
  const readError = read.isError ? (read.error instanceof Error ? read.error.message : String(read.error)) : null;

  return (
    <>
      {parameters.map((parameter) => {
        const id = `${prefix}-parameter-${parameter.name}`;
        const needed = parameter.required && (parameter.default === null || parameter.default === undefined);
        const placeholder = parameter.default ?? (parameter.required ? "required" : "optional");
        const column = parameter.scopeColumn ?? null;
        const name = (
          <>
            <span className="font-mono">{parameter.name}</span>
            {needed && <span className="text-destructive">*</span>}
          </>
        );
        const field = column === null || !canRead
          ? (
            <TypedInput
              id={id}
              value={values[parameter.name] ?? ""}
              onChange={(value) => onChange(parameter.name, value)}
              placeholder={placeholder}
              label={layout === "inline" ? name : null}
              title={parameter.description ?? undefined}
              className={className}
            />
          )
          : (
            <ScopeValueInput
              id={id}
              column={column}
              value={values[parameter.name] ?? ""}
              onChange={(value) => onChange(parameter.name, value)}
              placeholder={placeholder}
              listed={read.data?.parameters.find((listed) => listed.parameter === parameter.name)}
              loading={read.isFetching && read.data === undefined}
              error={readError}
              label={layout === "inline" ? name : null}
              title={parameter.description ?? undefined}
              className={className}
            />
          );

        return layout === "inline"
          ? <div key={parameter.name}>{field}</div>
          : (
            <div key={parameter.name} className="flex flex-col gap-1">
              <Label htmlFor={id} className="text-[12px] text-muted-foreground" title={parameter.description ?? undefined}>{name}</Label>
              {field}
            </div>
          );
      })}
    </>
  );
}

/**
 * A field whose name sits inside it, before the value, on the input's own surface: what a toolbar uses to keep a
 * parameter on one line. The whole field takes the focus ring its input would.
 */
function Shell({ id, label, title, className, invalid, children }: {
  id: string;
  label: ReactNode;
  title?: string;
  className?: string;
  invalid?: boolean;
  children: ReactNode;
}) {
  return (
    <div
      className={cn(
        "relative flex h-8 items-center", SURFACE,
        "focus-within:border-ring focus-within:ring-[3px] focus-within:ring-ring/50",
        invalid && "border-warning",
        className,
      )}
      title={title}
    >
      <label htmlFor={id} className="flex h-full shrink-0 items-center gap-0.5 border-r px-2 text-[11px] text-muted-foreground">{label}</label>
      {children}
    </div>
  );
}

const BARE = "h-full min-w-0 flex-1 bg-transparent px-2 font-mono text-[12px] outline-none placeholder:text-muted-foreground";

function TypedInput({ id, value, onChange, placeholder, label, title, className }: {
  id: string;
  value: string;
  onChange: (value: string) => void;
  placeholder: string;
  label: ReactNode | null;
  title?: string;
  className?: string;
}) {
  if (label === null) {
    return (
      <Input
        id={id}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder={placeholder}
        className={cn("h-8 font-mono text-[12px]", className)}
        spellCheck={false}
        autoComplete="off"
        data-testid={id}
      />
    );
  }

  return (
    <Shell id={id} label={label} title={title} className={className}>
      <input
        id={id}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder={placeholder}
        className={BARE}
        spellCheck={false}
        autoComplete="off"
        data-testid={id}
      />
    </Shell>
  );
}

/**
 * A value typed, with the values its column holds offered under it: the list filters as the value is typed, shows every
 * value again once the text is one of them, and puts a picked value into the field. A value the complete list does not
 * hold is flagged once the list is closed, since the scope would read no row of it.
 */
function ScopeValueInput({
  id, column, value, onChange, placeholder, listed, loading, error, label, title, className,
}: {
  id: string;
  column: string;
  value: string;
  onChange: (value: string) => void;
  placeholder: string;
  listed: DeliveryScopeParameterValues | undefined;
  loading: boolean;
  error: string | null;
  label: ReactNode | null;
  title?: string;
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  const anchorRef = useRef<HTMLDivElement>(null);
  const options = listed?.values ?? [];
  const text = value.trim();
  const exact = options.some((option) => option.value === text);
  const filter = text.toLowerCase();
  const matches = text === "" || exact ? options : options.filter((option) => option.value.toLowerCase().includes(filter));
  // Said once the list is closed: while it is open, the list itself shows what the text matches.
  const unknown = listed !== undefined && !listed.more && text !== "" && !exact && !open;
  const icon = (
    <span className="pointer-events-none flex shrink-0 items-center pr-2.5 text-muted-foreground">
      {loading ? <Loader2 className="size-4 animate-spin" /> : <ChevronsUpDown className="size-4 opacity-60" />}
    </span>
  );
  const inputProps = {
    id,
    role: "combobox",
    "aria-expanded": open,
    "aria-autocomplete": "list" as const,
    value,
    onChange: (event: ChangeEvent<HTMLInputElement>) => {
      onChange(event.target.value);
      setOpen(true);
    },
    onClick: () => setOpen(true),
    onKeyDown: (event: KeyboardEvent<HTMLInputElement>) => {
      if (event.key === "ArrowDown") {
        setOpen(true);
      } else if (event.key === "Escape") {
        setOpen(false);
      }
    },
    placeholder,
    spellCheck: false,
    autoComplete: "off",
    "data-testid": id,
  };

  return (
    <div className={cn("flex flex-col gap-1", label === null && className)}>
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverAnchor asChild>
          <div ref={anchorRef}>
            {label === null
              ? (
                <div className="relative">
                  <Input {...inputProps} className={cn("h-8 w-full pr-8 font-mono text-[12px]", unknown && "border-warning")} />
                  <span className="absolute top-1/2 right-0 -translate-y-1/2">{icon}</span>
                </div>
              )
              : (
                <Shell id={id} label={label} title={title} className={className} invalid={unknown}>
                  <input {...inputProps} className={BARE} />
                  {/* A toolbar keeps to one line, so the value the column holds no row of is flagged inside the field. */}
                  {unknown && (
                    <span className="flex shrink-0 text-warning" title={`${column} holds no row with this value`} data-testid={`${id}-unknown`}>
                      <TriangleAlert className="size-3.5" />
                    </span>
                  )}
                  {icon}
                </Shell>
              )}
          </div>
        </PopoverAnchor>
        <PopoverContent
          className="w-[max(var(--radix-popover-trigger-width),16rem)] p-0"
          align="start"
          onOpenAutoFocus={(event) => event.preventDefault()}
          onInteractOutside={(event) => {
            // A click back into the field must not dismiss the list it anchors.
            if (event.target instanceof Node && anchorRef.current?.contains(event.target)) {
              event.preventDefault();
            }
          }}
          data-testid={`${id}-values`}
        >
          <Command shouldFilter={false}>
            <CommandList>
              <CommandEmpty>
                {loading
                  ? `Reading the values of ${column}...`
                  : error !== null
                    ? `The values of ${column} could not be read: ${error}`
                    : options.length === 0 ? `${column} holds no value.` : `No value of ${column} holds "${text}".`}
              </CommandEmpty>
              {matches.map((option) => (
                <CommandItem
                  key={option.value}
                  value={option.value}
                  onSelect={() => {
                    onChange(option.value);
                    setOpen(false);
                  }}
                  className="flex items-center gap-3"
                  data-testid={`${id}-value`}
                >
                  <span className={cn("min-w-0 flex-1 truncate font-mono text-[12px]", option.value === text && "font-semibold")}>{option.value}</span>
                  <span className="shrink-0 font-mono text-[11px] text-muted-foreground tabular-nums">{`${count.format(option.rows)} rows`}</span>
                </CommandItem>
              ))}
            </CommandList>
          </Command>
          {listed?.more === true && (
            <p className="border-t px-2 py-1.5 text-[11px] text-muted-foreground">
              {`${column} holds more values than the ${count.format(options.length)} with the most rows listed here; type one to use it.`}
            </p>
          )}
        </PopoverContent>
      </Popover>
      {unknown && label === null && (
        <p className="flex items-center gap-1 text-[11px] text-warning" data-testid={`${id}-unknown`}>
          <TriangleAlert className="size-3.5 shrink-0" />
          {`${column} holds no row with this value`}
        </p>
      )}
    </div>
  );
}
