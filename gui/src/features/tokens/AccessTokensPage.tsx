import { useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Checkbox from "@mui/material/Checkbox";
import Chip from "@mui/material/Chip";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import FormControlLabel from "@mui/material/FormControlLabel";
import FormGroup from "@mui/material/FormGroup";
import IconButton from "@mui/material/IconButton";
import Stack from "@mui/material/Stack";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import ContentCopyIcon from "@mui/icons-material/ContentCopy";
import Paper from "@mui/material/Paper";
import { isApiError } from "../../api/client";
import { tokenApi } from "../../api/endpoints";
import type { AccessToken, CreatedAccessToken } from "../../api/types";
import { useAuth } from "../../auth/AuthContext";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { parseUtc } from "../../lib/time";

/** The lifecycle state a token is in, derived from its revoked/expires timestamps. */
type TokenState = "active" | "expired" | "revoked";

function tokenState(token: AccessToken, nowMs: number): TokenState {
  if (token.revokedUtc !== null) {
    return "revoked";
  }
  if (token.expiresUtc !== null && parseUtc(token.expiresUtc).getTime() <= nowMs) {
    return "expired";
  }

  return "active";
}

function StateBadge({ state }: { state: TokenState }) {
  switch (state) {
    case "active":
      return <Chip size="small" label="active" color="success" variant="outlined" data-testid="token-state" />;
    case "expired":
      return <Chip size="small" label="expired" color="warning" variant="outlined" data-testid="token-state" />;
    case "revoked":
      return <Chip size="small" label="revoked" color="default" variant="outlined" data-testid="token-state" />;
  }
}

/** Copies text to the clipboard, reporting success/failure through the snackbar. */
function useCopy() {
  const { enqueueSnackbar } = useSnackbar();
  return async (value: string, label: string) => {
    try {
      await navigator.clipboard.writeText(value);
      enqueueSnackbar(`${label} copied to the clipboard.`, { variant: "success" });
    } catch {
      enqueueSnackbar("Could not access the clipboard; copy it manually.", { variant: "warning" });
    }
  };
}

const EXPIRY_OPTIONS: { label: string; days: number | null }[] = [
  { label: "No expiry", days: null },
  { label: "30 days", days: 30 },
  { label: "60 days", days: 60 },
  { label: "90 days", days: 90 },
  { label: "1 year", days: 365 },
];

/** The one-time reveal of a freshly created secret: the only moment the full token is ever shown. */
function SecretReveal({ created, onClose }: { created: CreatedAccessToken; onClose: () => void }) {
  const copy = useCopy();
  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="sm" data-testid="token-secret-dialog">
      <DialogTitle>Copy your new token</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Alert severity="warning">
            This is the only time the token is shown. Copy it now and store it somewhere safe; you cannot see it again.
          </Alert>
          <Box
            sx={{
              display: "flex",
              alignItems: "center",
              gap: 1,
              p: 1,
              borderRadius: 1,
              bgcolor: "action.hover",
              fontFamily: "monospace",
              wordBreak: "break-all",
            }}
          >
            <Box sx={{ flexGrow: 1 }} data-testid="token-secret-value">{created.secret}</Box>
            <IconButton
              size="small"
              aria-label="Copy token"
              onClick={() => void copy(created.secret, "Token")}
              data-testid="token-secret-copy"
            >
              <ContentCopyIcon fontSize="small" />
            </IconButton>
          </Box>
          <Typography variant="body2" color="text.secondary">
            Use it as a bearer credential: send the header <code>Authorization: Bearer {created.token.prefix}…</code>{" "}
            to the control plane API.
          </Typography>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button variant="contained" onClick={onClose} data-testid="token-secret-done">Done</Button>
      </DialogActions>
    </Dialog>
  );
}

function CreateTokenDialog({
  ownScopes,
  onCreated,
  onClose,
}: {
  ownScopes: string[];
  onCreated: (created: CreatedAccessToken) => void;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const [name, setName] = useState("");
  const [scopes, setScopes] = useState<string[]>(ownScopes);
  const [expiryDays, setExpiryDays] = useState<number | null>(null);

  const create = useMutation({
    mutationFn: () => tokenApi.create({
      name: name.trim(),
      scopes,
      expiresInDays: expiryDays,
    }),
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: ["access-tokens"] });
      onCreated(created);
    },
  });

  const toggleScope = (scope: string) =>
    setScopes((current) =>
      current.includes(scope) ? current.filter((s) => s !== scope) : [...current, scope]);

  const canSubmit = name.trim() !== "" && scopes.length > 0 && !create.isPending;

  return (
    <Dialog open onClose={create.isPending ? undefined : onClose} fullWidth maxWidth="sm" data-testid="create-token-dialog">
      <DialogTitle>Create personal access token</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          {create.isError && isApiError(create.error) && <CorrelationError error={create.error} />}
          {create.isError && !isApiError(create.error) && (
            <Typography color="error">{String(create.error)}</Typography>
          )}

          <TextField
            label="Name"
            required
            placeholder="e.g. vscode-laptop"
            value={name}
            onChange={(e) => setName(e.target.value)}
            inputProps={{ "data-testid": "create-token-name", maxLength: 200 }}
          />

          <Box>
            <Typography variant="subtitle2" gutterBottom>Scopes</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
              A token can grant at most the scopes your own account holds.
            </Typography>
            <FormGroup>
              {ownScopes.map((scope) => (
                <FormControlLabel
                  key={scope}
                  control={(
                    <Checkbox
                      checked={scopes.includes(scope)}
                      onChange={() => toggleScope(scope)}
                      inputProps={{ "data-testid": `create-token-scope-${scope}` } as Record<string, string>}
                    />
                  )}
                  label={scope}
                />
              ))}
            </FormGroup>
          </Box>

          <TextField
            select
            label="Expiry"
            value={expiryDays === null ? "none" : String(expiryDays)}
            onChange={(e) => setExpiryDays(e.target.value === "none" ? null : Number(e.target.value))}
            SelectProps={{ native: true }}
            InputLabelProps={{ shrink: true }}
            inputProps={{ "data-testid": "create-token-expiry" }}
          >
            {EXPIRY_OPTIONS.map((option) => (
              <option key={option.label} value={option.days === null ? "none" : String(option.days)}>
                {option.label}
              </option>
            ))}
          </TextField>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={create.isPending} data-testid="create-token-cancel">Cancel</Button>
        <Button
          variant="contained"
          onClick={() => create.mutate()}
          disabled={!canSubmit}
          data-testid="create-token-submit"
        >
          Create
        </Button>
      </DialogActions>
    </Dialog>
  );
}

/**
 * Self-service management of the signed-in user's personal access tokens: long-lived bearer credentials for headless
 * clients (the CLI, the VSCode extension, automation) that cannot hold the browser's short session token.
 */
export default function AccessTokensPage() {
  const { session } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const ownScopes = useMemo(() => session?.scopes ?? [], [session]);

  const [createOpen, setCreateOpen] = useState(false);
  const [secret, setSecret] = useState<CreatedAccessToken | null>(null);
  const [revokeTarget, setRevokeTarget] = useState<AccessToken | null>(null);

  const tokensQuery = useQuery({ queryKey: ["access-tokens"], queryFn: tokenApi.list });
  const tokens = tokensQuery.data ?? [];
  const nowMs = Date.now();

  const revoke = useMutation({
    mutationFn: (token: AccessToken) => tokenApi.revoke(token.id),
    onSuccess: (_result, token) => {
      enqueueSnackbar(`Token “${token.name}” revoked.`, { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["access-tokens"] });
      setRevokeTarget(null);
    },
    onError: (error) => {
      enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" });
      setRevokeTarget(null);
    },
  });

  return (
    <Page data-testid="page-access-tokens">
      <PageHeader
        title="Personal access tokens"
        actions={(
          <Button variant="contained" onClick={() => setCreateOpen(true)} data-testid="open-create-token">
            Create token
          </Button>
        )}
      />

      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Use a token as a bearer credential to authenticate headless clients, such as the CLI or the VSCode extension,
        without signing in through the browser. Each token carries at most the scopes your account holds, and you can
        revoke any token here at any time.
      </Typography>

      {tokensQuery.isError && (
        <Alert severity="error" sx={{ mb: 2 }}>
          {tokensQuery.error instanceof Error ? tokensQuery.error.message : "Could not load your tokens."}
        </Alert>
      )}

      <TableContainer component={Paper} variant="outlined">
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Name</TableCell>
              <TableCell>Prefix</TableCell>
              <TableCell>Scopes</TableCell>
              <TableCell>Status</TableCell>
              <TableCell>Last used</TableCell>
              <TableCell>Expires</TableCell>
              <TableCell>Created</TableCell>
              <TableCell align="right">Actions</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {tokens.length === 0 ? (
              <TableRow>
                <TableCell colSpan={8}>
                  <Typography variant="body2" color="text.secondary" sx={{ py: 2, textAlign: "center" }}>
                    You have no personal access tokens yet.
                  </Typography>
                </TableCell>
              </TableRow>
            ) : (
              tokens.map((token) => {
                const state = tokenState(token, nowMs);
                return (
                  <TableRow key={token.id} data-testid="token-row">
                    <TableCell>
                      <Typography variant="body2" fontWeight={600}>{token.name}</Typography>
                    </TableCell>
                    <TableCell>
                      <Box component="code" sx={{ fontFamily: "monospace" }}>{token.prefix}…</Box>
                    </TableCell>
                    <TableCell>
                      <Stack direction="row" spacing={0.5} sx={{ flexWrap: "wrap", gap: 0.5 }}>
                        {token.scopes.map((scope) => (
                          <Chip key={scope} size="small" label={scope} variant="outlined" />
                        ))}
                      </Stack>
                    </TableCell>
                    <TableCell><StateBadge state={state} /></TableCell>
                    <TableCell><RelativeTime value={token.lastUsedUtc} /></TableCell>
                    <TableCell>
                      {token.expiresUtc === null
                        ? <Typography variant="body2" color="text.secondary">never</Typography>
                        : <RelativeTime value={token.expiresUtc} />}
                    </TableCell>
                    <TableCell><RelativeTime value={token.createdUtc} /></TableCell>
                    <TableCell align="right">
                      <Button
                        size="small"
                        color="error"
                        disabled={state === "revoked"}
                        onClick={() => setRevokeTarget(token)}
                        data-testid="token-revoke"
                      >
                        Revoke
                      </Button>
                    </TableCell>
                  </TableRow>
                );
              })
            )}
          </TableBody>
        </Table>
      </TableContainer>

      {createOpen && (
        <CreateTokenDialog
          ownScopes={ownScopes}
          onCreated={(created) => {
            setCreateOpen(false);
            setSecret(created);
          }}
          onClose={() => setCreateOpen(false)}
        />
      )}

      {secret !== null && <SecretReveal created={secret} onClose={() => setSecret(null)} />}

      <ConfirmDialog
        open={revokeTarget !== null}
        title="Revoke token"
        message={`Revoke “${revokeTarget?.name ?? ""}”? Any client using it will stop working immediately.`}
        confirmLabel="Revoke"
        danger
        busy={revoke.isPending}
        onConfirm={() => {
          if (revokeTarget !== null) {
            revoke.mutate(revokeTarget);
          }
        }}
        onClose={() => setRevokeTarget(null)}
      />
    </Page>
  );
}
