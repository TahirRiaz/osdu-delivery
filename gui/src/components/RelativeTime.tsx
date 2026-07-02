import Tooltip from "@mui/material/Tooltip";
import { formatDistanceToNow } from "date-fns";
import { parseUtc } from "../lib/time";

/** "3 minutes ago" with the absolute UTC instant in the tooltip. Renders a dash for null (consistent tables). */
export function RelativeTime({ value }: { value: string | null | undefined }) {
  if (!value) {
    return <span>-</span>;
  }

  const date = parseUtc(value);
  return (
    <Tooltip title={`${date.toISOString().replace("T", " ").replace(/\.\d+Z$/, " UTC")}`}>
      <span>{formatDistanceToNow(date, { addSuffix: true })}</span>
    </Tooltip>
  );
}
