import type { ReactNode } from "react";
import Box from "@mui/material/Box";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";

interface PageHeaderProps {
  title: ReactNode;
  /** An optional line under the title (context, "as of" stamps, one-line descriptions). */
  subtitle?: ReactNode;
  /** Right-aligned actions: the page's primary button, a view toggle, and so on. */
  actions?: ReactNode;
}

/**
 * The one page title block. An h5 title at the theme's own weight (no per-page override), an optional
 * subtitle in muted body text, and a right-aligned actions slot via a single space-between idiom. Every
 * list page renders this as its first section so titles, their weight, and action alignment match everywhere.
 * Vertical spacing comes from the enclosing Page, not from this component.
 */
export function PageHeader({ title, subtitle, actions }: PageHeaderProps) {
  return (
    <Stack
      direction="row"
      spacing={2}
      alignItems={subtitle ? "flex-start" : "center"}
      justifyContent="space-between"
      flexWrap="wrap"
      useFlexGap
    >
      <Box sx={{ minWidth: 0 }}>
        <Typography variant="h5">{title}</Typography>
        {subtitle !== undefined && (
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
            {subtitle}
          </Typography>
        )}
      </Box>
      {actions !== undefined && (
        <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
          {actions}
        </Stack>
      )}
    </Stack>
  );
}
