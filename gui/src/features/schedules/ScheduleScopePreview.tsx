import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Chip from "@mui/material/Chip";
import CircularProgress from "@mui/material/CircularProgress";
import Stack from "@mui/material/Stack";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import { runApi } from "../../api/endpoints";
import type { RunScope, RunScopePreviewMember } from "../../api/types";

/** One wave: the flows that start together once every earlier wave is finished. */
interface PreviewWave {
  wave: number;
  members: RunScopePreviewMember[];
}

/** Groups the flat, wave-ordered member list into waves, preserving the server's ordering. */
function toWaves(members: RunScopePreviewMember[]): PreviewWave[] {
  const byWave = new Map<number, RunScopePreviewMember[]>();
  for (const member of members) {
    const bucket = byWave.get(member.wave);
    if (bucket === undefined) {
      byWave.set(member.wave, [member]);
    } else {
      bucket.push(member);
    }
  }

  return [...byWave.entries()]
    .sort(([a], [b]) => a - b)
    .map(([wave, waveMembers]) => ({ wave, members: waveMembers }));
}

/** The round wave marker plus the rail that ties consecutive waves together. */
function WaveRail({ index, last }: { index: number; last: boolean }) {
  return (
    <Stack alignItems="center" sx={{ alignSelf: "stretch", width: 32, flexShrink: 0 }}>
      <Box
        sx={{
          width: 28,
          height: 28,
          borderRadius: "50%",
          display: "grid",
          placeItems: "center",
          bgcolor: "primary.main",
          color: "primary.contrastText",
          fontSize: 13,
          fontWeight: 700,
          lineHeight: 1,
          flexShrink: 0,
        }}
      >
        {index + 1}
      </Box>
      {!last && <Box sx={{ flexGrow: 1, width: "2px", bgcolor: "divider", minHeight: 12, mt: 0.5 }} />}
    </Stack>
  );
}

function WaveRow({ wave, index, last }: { wave: PreviewWave; index: number; last: boolean }) {
  const concurrent = wave.members.length > 1;
  return (
    <Stack direction="row" spacing={1.5} sx={{ pb: last ? 0 : 2 }} data-testid={`scope-preview-wave-${wave.wave}`}>
      <WaveRail index={index} last={last} />
      <Box sx={{ minWidth: 0, flexGrow: 1, pt: 0.25 }}>
        <Stack direction="row" spacing={1} alignItems="baseline" sx={{ mb: 0.75 }}>
          <Typography variant="subtitle2" fontWeight={700}>Wave {wave.wave}</Typography>
          <Typography variant="caption" color="text.secondary">
            {concurrent
              ? `${wave.members.length} flows run concurrently`
              : "1 flow"}
          </Typography>
        </Stack>
        <Stack direction="row" spacing={0.75} useFlexGap flexWrap="wrap">
          {wave.members.map((member) => (
            <Tooltip key={member.flowName} title={`${member.flowName} (${member.flowKind})`}>
              <Chip
                size="small"
                variant="outlined"
                label={member.flowName}
                data-testid={`scope-preview-member-${member.flowName}`}
                sx={{ maxWidth: 320, fontFamily: "monospace", fontSize: 12 }}
              />
            </Tooltip>
          ))}
        </Stack>
      </Box>
    </Stack>
  );
}

/**
 * What a schedule actually executes when it fires, resolved through lineage: the waves in the order they run, and the
 * flows inside each wave that run concurrently. It reads the same expansion the fire itself uses (the shared
 * `/runs/preview` endpoint), so what an operator sees here is exactly what the scheduler will enqueue. A flow-scoped
 * schedule has no graph to show and says so plainly instead of rendering a one-node diagram.
 */
export function ScheduleScopePreview({
  repoId,
  flowName,
  scope,
}: {
  repoId: string | null;
  flowName: string | null;
  scope: RunScope;
}) {
  const enabled = repoId !== null && flowName !== null && scope !== "flow";
  const preview = useQuery({
    queryKey: ["runs", "preview", repoId, flowName, scope],
    queryFn: () => runApi.previewScope({ repoId: repoId!, flowName: flowName!, scope }),
    enabled,
  });

  const waves = useMemo(() => toWaves(preview.data?.members ?? []), [preview.data]);

  if (scope === "flow") {
    return (
      <Alert severity="info" variant="outlined" data-testid="scope-preview-flow">
        Runs only <strong>{flowName ?? "this flow"}</strong>. Choose <strong>Node</strong> or <strong>Batch</strong> to
        run its dependents in order too.
      </Alert>
    );
  }

  if (!enabled) {
    return (
      <Alert severity="info" variant="outlined">
        Pick a repo and a flow to see what this schedule will run.
      </Alert>
    );
  }

  if (preview.isPending) {
    return (
      <Stack direction="row" spacing={1} alignItems="center" sx={{ py: 2 }}>
        <CircularProgress size={16} />
        <Typography variant="body2" color="text.secondary">Resolving the execution plan…</Typography>
      </Stack>
    );
  }

  if (preview.isError) {
    return (
      <Alert severity="error" variant="outlined" data-testid="scope-preview-error">
        {preview.error instanceof Error ? preview.error.message : String(preview.error)}
      </Alert>
    );
  }

  if (waves.length === 0) {
    return (
      <Alert severity="warning" variant="outlined" data-testid="scope-preview-empty">
        This scope resolves to no runnable flow, so the schedule would fire and do nothing. Every flow may be
        deactivated or set to <code>mode: manual</code>.
      </Alert>
    );
  }

  const memberCount = preview.data.memberCount;
  return (
    <Box data-testid="scope-preview">
      <Stack
        direction="row"
        spacing={1}
        alignItems="center"
        flexWrap="wrap"
        useFlexGap
        sx={{ mb: 1.5 }}
      >
        <Typography variant="body2" fontWeight={600}>
          Runs {memberCount} {memberCount === 1 ? "flow" : "flows"} across {waves.length}{" "}
          {waves.length === 1 ? "wave" : "waves"}
        </Typography>
        <Typography variant="caption" color="text.secondary">
          each wave waits for the one before it
        </Typography>
      </Stack>
      <Box
        sx={{
          p: 1.5,
          borderRadius: 1,
          border: 1,
          borderColor: "divider",
          bgcolor: "action.hover",
          maxHeight: 320,
          overflowY: "auto",
        }}
      >
        {waves.map((wave, index) => (
          <WaveRow key={wave.wave} wave={wave} index={index} last={index === waves.length - 1} />
        ))}
      </Box>
    </Box>
  );
}
