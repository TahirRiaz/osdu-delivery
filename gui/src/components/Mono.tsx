import type { ReactNode } from "react";
import Typography from "@mui/material/Typography";
import type { SxProps, Theme } from "@mui/material/styles";

/**
 * Inline monospace text (commit SHAs, keys, paths, ids): the one helper replacing the scattered raw
 * `<span style={{ fontFamily: "monospace" }}>` and repeated `sx={{ fontFamily: "monospace" }}` so code-like
 * values render identically everywhere.
 */
export function Mono({ children, sx }: { children: ReactNode; sx?: SxProps<Theme> }) {
  return (
    <Typography component="span" variant="body2" sx={{ fontFamily: "monospace", ...sx }}>
      {children}
    </Typography>
  );
}
