import type { ReactNode } from "react";
import Stack from "@mui/material/Stack";

interface PageProps {
  children: ReactNode;
  "data-testid"?: string;
}

/**
 * The frame every routed page renders through: one vertical rhythm (a single Stack spacing) so the gaps
 * between the header, an optional filter bar, and the content are identical across the whole application.
 * Pages pass their sections as children, the PageHeader first. Replaces the mix of Box + per-section margins
 * and Stack spacings the pages used to hand-roll.
 */
export function Page({ children, "data-testid": testId }: PageProps) {
  return (
    <Stack spacing={3} data-testid={testId}>
      {children}
    </Stack>
  );
}
