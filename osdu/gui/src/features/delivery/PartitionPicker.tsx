import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

/**
 * The OSDU partition a view of a flow reads and acts in, for a flow that works in partitions (the ones it names under
 * `partitions:`, or every registered one). Always a dropdown of the partitions the flow serves, never free text, and
 * shown even when it serves one: which partition a view is about is what an operator needs to know before acting on it.
 * Each partition keeps a ledger of its own.
 */
export function PartitionPicker({ partitions, partition, onSelect, caption, testId = "delivery-partition-picker" }: {
  partitions: string[];
  partition: string | null;
  onSelect: (next: string) => void;
  caption?: string;
  testId?: string;
}) {
  return (
    <div className="flex flex-wrap items-center gap-2" data-testid={testId}>
      <Label className="text-[13px] text-muted-foreground">Partition</Label>
      <Select value={partition ?? ""} onValueChange={onSelect}>
        <SelectTrigger size="sm" className="h-8 w-56 font-mono" data-testid={`${testId}-select`}>
          <SelectValue placeholder="Choose a partition" />
        </SelectTrigger>
        <SelectContent>
          {partitions.map((name) => <SelectItem key={name} value={name} className="font-mono">{name}</SelectItem>)}
        </SelectContent>
      </Select>
      {caption && <span className="text-[13px] text-muted-foreground">{caption}</span>}
    </div>
  );
}
