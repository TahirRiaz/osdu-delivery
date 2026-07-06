import type { ReactNode } from "react";
import Box from "@mui/material/Box";
import Typography from "@mui/material/Typography";

/**
 * One caption-over-value cell in a detail header: the single implementation shared by every detail page
 * (repo, pipeline, run) and the lineage drawer, replacing the three near-identical copies (DetailPair,
 * Field, DetailRow) that had drifted apart. Laid out by DetailHeaderCard's grid, so values line up in even
 * columns; long values (URLs, hashes) wrap inside their cell rather than overflowing.
 */
export function DetailPair({ label, children }: { label: ReactNode; children: ReactNode }) {
  return (
    <Box sx={{ minWidth: 0 }}>
      <Typography variant="caption" color="text.secondary" display="block">{label}</Typography>
      <Typography variant="body2" component="div" sx={{ wordBreak: "break-word" }}>{children}</Typography>
    </Box>
  );
}
