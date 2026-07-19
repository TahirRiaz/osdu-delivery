import { formatDistanceToNow } from "date-fns";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { parseUtc } from "../lib/time";

/** "3 minutes ago" with the absolute UTC instant in the tooltip. Renders a dash for null (consistent tables). */
export function RelativeTime({ value }: { value: string | null | undefined }) {
  if (!value) {
    return <span>-</span>;
  }

  const date = parseUtc(value);
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span>{formatDistanceToNow(date, { addSuffix: true })}</span>
      </TooltipTrigger>
      <TooltipContent className="font-mono text-[11px]">
        {date.toISOString().replace("T", " ").replace(/\.\d+Z$/, " UTC")}
      </TooltipContent>
    </Tooltip>
  );
}
