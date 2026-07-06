import type { ReactNode } from "react";
import Stack from "@mui/material/Stack";

/**
 * The wrapper every filter row renders through: one gap, one wrap behaviour, stacking full-width on the
 * narrowest screens and sitting in a row from the small breakpoint up. Combined with using a single select
 * control (a small MUI `TextField select`) inside it, this makes the filter bars line up the same on every
 * list page instead of drifting in gap and control height.
 */
export function FilterBar({ children }: { children: ReactNode }) {
  return (
    <Stack
      direction={{ xs: "column", sm: "row" }}
      spacing={1.5}
      alignItems={{ xs: "stretch", sm: "center" }}
      flexWrap="wrap"
      useFlexGap
    >
      {children}
    </Stack>
  );
}
