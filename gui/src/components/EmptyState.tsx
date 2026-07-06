import type { ReactNode } from "react";
import Box from "@mui/material/Box";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";

interface EmptyStateProps {
  /** An optional muted icon above the text. */
  icon?: ReactNode;
  title: ReactNode;
  /** An optional secondary line explaining what fills this space. */
  description?: ReactNode;
  /** An optional action (a button) under the text. */
  action?: ReactNode;
  "data-testid"?: string;
}

/**
 * The one empty / no-data panel: a centered, muted message with an optional icon, secondary description, and
 * action. Used by the table shell's empty branch and by every page's own no-data state (search hint, graph
 * empty, no pipelines) so they all read the same instead of ranging from bare text to full panels.
 */
export function EmptyState({ icon, title, description, action, "data-testid": testId }: EmptyStateProps) {
  return (
    <Stack spacing={1} alignItems="center" textAlign="center" sx={{ px: 3, py: 6 }} data-testid={testId}>
      {icon !== undefined && (
        <Box sx={{ color: "text.disabled", lineHeight: 0, mb: 0.5, "& svg": { fontSize: 40 } }}>{icon}</Box>
      )}
      <Typography variant="subtitle1" color="text.secondary">{title}</Typography>
      {description !== undefined && (
        <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 460 }}>{description}</Typography>
      )}
      {action !== undefined && <Box sx={{ mt: 1 }}>{action}</Box>}
    </Stack>
  );
}
