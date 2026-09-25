import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

/** The interface picker a source's views show when it delivers more than one interface. */
export function InterfacePicker({ names, interfaceName, onSelect, caption }: {
  names: string[];
  interfaceName: string | null;
  onSelect: (next: string) => void;
  caption: string;
}) {
  return (
    <div className="flex flex-wrap items-center gap-2" data-testid="delivery-interface-picker">
      <Label className="text-[13px] text-muted-foreground">Interface</Label>
      <Select value={interfaceName ?? ""} onValueChange={onSelect}>
        <SelectTrigger size="sm" className="h-8 w-56" data-testid="delivery-interface-select">
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {names.map((name) => <SelectItem key={name} value={name}>{name}</SelectItem>)}
        </SelectContent>
      </Select>
      <span className="text-[13px] text-muted-foreground">{caption}</span>
    </div>
  );
}
