import Box from "@mui/material/Box";
import Tooltip from "@mui/material/Tooltip";

interface TruncatedTextProps {
  /** The full value: clipped with an ellipsis and shown in full on hover. */
  text: string | null | undefined;
  /**
   * Hard cap on the rendered width in pixels; the cell never grows past this no matter how long the value is,
   * which is what keeps one long path or URL from stretching a whole table column. Short values render at their
   * natural width and are not truncated.
   */
  maxWidth?: number;
  /** Render in monospace, for paths, ids, keys, and other code-like values. */
  mono?: boolean;
  /** Shown when `text` is null/empty (matches the tables' "-" placeholder). */
  placeholder?: string;
}

/**
 * A single-line value that clips with an ellipsis at a fixed pixel cap and reveals the full text in a tooltip.
 * The full string stays in the DOM (only visually clipped), so it remains selectable and copyable. This is the
 * one canonical way to render potentially long free-text values (paths, URLs, object keys, SQL, errors) in a
 * table cell or detail row, replacing the scattered `slice`/`truncate` + `Tooltip` + `Mono` combinations so a
 * long value can never blow out the surrounding layout.
 */
export function TruncatedText({ text, maxWidth = 360, mono = false, placeholder = "-" }: TruncatedTextProps) {
  if (text === null || text === undefined || text === "") {
    return <>{placeholder}</>;
  }

  return (
    <Tooltip title={text} enterDelay={400} placement="top-start">
      <Box
        component="span"
        sx={{
          display: "inline-block",
          maxWidth,
          overflow: "hidden",
          textOverflow: "ellipsis",
          whiteSpace: "nowrap",
          // Align the clipped inline-block with adjacent baseline text in the same cell.
          verticalAlign: "bottom",
          ...(mono ? { fontFamily: "monospace", fontSize: "0.8125rem" } : {}),
        }}
      >
        {text}
      </Box>
    </Tooltip>
  );
}
