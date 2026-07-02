import Alert from "@mui/material/Alert";
import AlertTitle from "@mui/material/AlertTitle";
import IconButton from "@mui/material/IconButton";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import ContentCopyIcon from "@mui/icons-material/ContentCopy";
import type { ApiError } from "../api/client";

/**
 * The one way API failures render: the RFC 7807 title/detail plus the correlation id (copyable), so what a user
 * reports maps straight to a server log line.
 */
export function CorrelationError({ error, "data-testid": testId }: { error: ApiError; "data-testid"?: string }) {
  return (
    <Alert severity="error" data-testid={testId ?? "api-error"}>
      <AlertTitle>{error.title}</AlertTitle>
      {error.detail && <Typography variant="body2">{error.detail}</Typography>}
      {error.correlationId && (
        <Typography variant="caption" color="text.secondary" sx={{ display: "inline-flex", alignItems: "center", gap: 0.5 }}>
          Correlation id: <code>{error.correlationId}</code>
          <Tooltip title="Copy correlation id">
            <IconButton
              size="small"
              aria-label="Copy correlation id"
              onClick={() => void navigator.clipboard.writeText(error.correlationId ?? "")}
            >
              <ContentCopyIcon fontSize="inherit" />
            </IconButton>
          </Tooltip>
        </Typography>
      )}
    </Alert>
  );
}
