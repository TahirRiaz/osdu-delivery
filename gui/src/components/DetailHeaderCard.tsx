import type { ReactNode } from "react";
import Box from "@mui/material/Box";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";

interface DetailHeaderCardProps {
  title: ReactNode;
  /** Inline chips next to the title: status, kind, sync state. */
  badges?: ReactNode;
  /** Right-aligned actions: sync now, lineage graph, trigger run. */
  actions?: ReactNode;
  /** The DetailPair cells; laid out on an even, responsive grid. */
  children: ReactNode;
  "data-testid"?: string;
}

/**
 * The outlined header card shared by the detail pages (repo, pipeline, run): a title row with inline badges
 * on the left and actions on the right, above a responsive grid of DetailPair cells. The grid (even minmax
 * columns) is what makes the label/value pairs line up in tidy columns instead of the ragged flex wrap the
 * pages used to produce. The title uses the theme's h5 weight, with no per-page override.
 */
export function DetailHeaderCard({ title, badges, actions, children, "data-testid": testId }: DetailHeaderCardProps) {
  return (
    <Card variant="outlined" data-testid={testId}>
      <CardContent>
        <Stack spacing={2.5}>
          <Stack direction="row" spacing={2} alignItems="center" flexWrap="wrap" useFlexGap>
            <Typography variant="h5" sx={{ minWidth: 0, wordBreak: "break-word" }}>{title}</Typography>
            {badges}
            <Box sx={{ flexGrow: 1 }} />
            {actions}
          </Stack>
          <Box
            sx={{
              display: "grid",
              gap: 2,
              gridTemplateColumns: "repeat(auto-fill, minmax(180px, 1fr))",
            }}
          >
            {children}
          </Box>
        </Stack>
      </CardContent>
    </Card>
  );
}
