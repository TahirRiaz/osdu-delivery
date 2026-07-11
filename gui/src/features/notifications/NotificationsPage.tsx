import { useState } from "react";
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
import Paper from "@mui/material/Paper";
import Stack from "@mui/material/Stack";
import Table from "@mui/material/Table";
import TableBody from "@mui/material/TableBody";
import TableCell from "@mui/material/TableCell";
import TableContainer from "@mui/material/TableContainer";
import TableHead from "@mui/material/TableHead";
import TableRow from "@mui/material/TableRow";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import { isApiError } from "../../api/client";
import { notificationApi } from "../../api/endpoints";
import type { MyNotificationOptions, NotificationDelivery, NotificationSubscription } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";

type Channel = NotificationSubscription["channel"];
type Mode = NotificationSubscription["mode"];

const CHANNEL_LABELS: Record<Channel, string> = { email: "Email", slack: "Slack" };
const MODE_LABELS: Record<Mode, string> = { immediate: "Immediate", digest: "Digest" };

/** Display names for the run event kinds; an unknown kind falls back to its raw identifier. */
const KIND_LABELS: Record<string, string> = {
  run_failed: "Run failed",
  run_cancelled: "Run cancelled",
  run_skipped: "Run skipped",
  assertion_failed: "Assertions failed",
};

function kindLabel(kind: string): string {
  return KIND_LABELS[kind] ?? kind;
}

/** Formats a minute count for pacing text: "45 min", "6 h", "1 h 30 min". */
function formatMinutes(minutes: number): string {
  if (minutes < 60) {
    return `${minutes} min`;
  }
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`;
}

function pacingText(subscription: NotificationSubscription): string {
  if (subscription.mode === "digest") {
    return `Digest every ${formatMinutes(subscription.digestIntervalMinutes)}`;
  }

  return subscription.cooldownMinutes === 0
    ? "Immediate"
    : `Immediate, ${formatMinutes(subscription.cooldownMinutes)} cooldown`;
}

/** Where messages go: the explicit target, or the channel's fallback (account email / Slack direct message). */
function destinationText(subscription: NotificationSubscription, userEmail: string | null): string {
  if (subscription.channel === "email") {
    if (subscription.emailAddress !== null && subscription.emailAddress !== "") {
      return subscription.emailAddress;
    }

    return userEmail !== null && userEmail !== "" ? `${userEmail} (account email)` : "Account email";
  }

  return subscription.slackTarget !== null && subscription.slackTarget !== ""
    ? subscription.slackTarget
    : "Direct message";
}

/** Parses a whole-minute text field; null when empty or not an integer. */
function parseMinutes(raw: string): number | null {
  const trimmed = raw.trim();
  if (trimmed === "") {
    return null;
  }

  const value = Number(trimmed);
  return Number.isInteger(value) ? value : null;
}

function DeliveryStatusChip({ status }: { status: NotificationDelivery["status"] }) {
  const color = status === "sent" ? "success" : status === "failed" ? "error" : "default";
  return <Chip size="small" label={status} color={color} variant="outlined" data-testid="delivery-status" />;
}

/** The one create/edit dialog: creating picks a channel, editing keeps the subscription's channel locked. */
function SubscriptionDialog({
  options,
  subscription,
  onClose,
}: {
  options: MyNotificationOptions;
  /** Null creates a new subscription; otherwise the dialog edits this one. */
  subscription: NotificationSubscription | null;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const [channel, setChannel] = useState<Channel>(
    subscription?.channel ?? (options.email.available ? "email" : "slack"),
  );
  const [mode, setMode] = useState<Mode>(subscription?.mode ?? "immediate");
  const [kinds, setKinds] = useState<string[]>(subscription?.kinds ?? options.defaultKinds);
  const [flowPattern, setFlowPattern] = useState(subscription?.flowPattern ?? "");
  const [destination, setDestination] = useState(
    subscription === null
      ? ""
      : (subscription.channel === "email" ? subscription.emailAddress : subscription.slackTarget) ?? "",
  );
  const [cooldownText, setCooldownText] = useState(
    String(subscription?.cooldownMinutes ?? options.defaultCooldownMinutes),
  );
  const [digestText, setDigestText] = useState(
    String(subscription?.digestIntervalMinutes ?? options.defaultDigestIntervalMinutes),
  );

  // Only configured channels are offered; an existing subscription keeps its locked channel listed even if the
  // deployment no longer configures that channel.
  const channelOptions: Channel[] = [];
  if (options.email.available) {
    channelOptions.push("email");
  }
  if (options.slack.available) {
    channelOptions.push("slack");
  }
  if (subscription !== null && !channelOptions.includes(subscription.channel)) {
    channelOptions.push(subscription.channel);
  }

  const knownModes = options.modes.filter((m): m is Mode => m === "immediate" || m === "digest");
  const modeOptions: Mode[] = knownModes.length > 0 ? knownModes : ["immediate", "digest"];

  // The server's kinds plus any the subscription already carries, so nothing becomes un-toggleable.
  const kindOptions = Array.from(new Set([...options.kinds, ...(subscription?.kinds ?? [])]));

  const save = useMutation({
    mutationFn: () => {
      const pattern = flowPattern.trim();
      const target = destination.trim();
      const cooldown = parseMinutes(cooldownText);
      const digest = parseMinutes(digestText);
      if (subscription === null) {
        return notificationApi.createSubscription({
          channel,
          mode,
          kinds,
          flowPattern: pattern === "" ? null : pattern,
          emailAddress: channel === "email" && target !== "" ? target : null,
          slackTarget: channel === "slack" && target !== "" ? target : null,
          digestIntervalMinutes: mode === "digest" && digest !== null ? digest : undefined,
          cooldownMinutes: mode === "immediate" && cooldown !== null ? cooldown : undefined,
        });
      }

      // PUT semantics: an empty string clears flowPattern/emailAddress/slackTarget, so the saved state always
      // matches exactly what the dialog shows.
      return notificationApi.updateSubscription(subscription.id, {
        mode,
        kinds,
        flowPattern: pattern,
        emailAddress: channel === "email" ? target : undefined,
        slackTarget: channel === "slack" ? target : undefined,
        digestIntervalMinutes: mode === "digest" && digest !== null ? digest : undefined,
        cooldownMinutes: mode === "immediate" && cooldown !== null ? cooldown : undefined,
      });
    },
    onSuccess: () => {
      enqueueSnackbar(subscription === null ? "Subscription created." : "Subscription updated.", { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["me-notifications"] });
      onClose();
    },
  });

  const toggleKind = (kind: string) =>
    setKinds((current) =>
      current.includes(kind) ? current.filter((k) => k !== kind) : [...current, kind]);

  const cooldown = parseMinutes(cooldownText);
  const cooldownValid = cooldown !== null && cooldown >= 0 && cooldown <= 1440;
  const digest = parseMinutes(digestText);
  const digestValid = digest !== null && digest >= 5 && digest <= 10080;
  const canSubmit = kinds.length > 0 && (mode === "immediate" ? cooldownValid : digestValid) && !save.isPending;

  return (
    <Dialog
      open
      onClose={save.isPending ? undefined : onClose}
      fullWidth
      maxWidth="sm"
      data-testid="create-subscription-dialog"
    >
      <DialogTitle>{subscription === null ? "New subscription" : "Edit subscription"}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          {save.isError && isApiError(save.error) && <CorrelationError error={save.error} />}
          {save.isError && !isApiError(save.error) && (
            <Typography color="error">{String(save.error)}</Typography>
          )}

          <TextField
            select
            label="Channel"
            value={channel}
            onChange={(e) => {
              setChannel(e.target.value as Channel);
              setDestination("");
            }}
            disabled={subscription !== null}
            SelectProps={{ native: true }}
            InputLabelProps={{ shrink: true }}
            inputProps={{ "data-testid": "create-subscription-channel" }}
          >
            {channelOptions.map((option) => (
              <option key={option} value={option}>{CHANNEL_LABELS[option]}</option>
            ))}
          </TextField>

          <TextField
            select
            label="Mode"
            value={mode}
            onChange={(e) => setMode(e.target.value as Mode)}
            SelectProps={{ native: true }}
            InputLabelProps={{ shrink: true }}
            inputProps={{ "data-testid": "create-subscription-mode" }}
          >
            {modeOptions.map((option) => (
              <option key={option} value={option}>{MODE_LABELS[option]}</option>
            ))}
          </TextField>

          {mode === "immediate" ? (
            <TextField
              type="number"
              label="Cooldown (minutes)"
              value={cooldownText}
              onChange={(e) => setCooldownText(e.target.value)}
              error={!cooldownValid}
              helperText={cooldownValid
                ? "Minimum gap between messages; 0 sends every event."
                : "Enter a whole number of minutes between 0 and 1440."}
              inputProps={{ min: 0, max: 1440, "data-testid": "create-subscription-cooldown" }}
            />
          ) : (
            <TextField
              type="number"
              label="Digest interval (minutes)"
              value={digestText}
              onChange={(e) => setDigestText(e.target.value)}
              error={!digestValid}
              helperText={digestValid
                ? "How often bundled events are sent."
                : "Enter a whole number of minutes between 5 and 10080."}
              inputProps={{ min: 5, max: 10080, "data-testid": "create-subscription-digest-interval" }}
            />
          )}

          <Box>
            <Typography variant="subtitle2" gutterBottom>Events</Typography>
            <FormGroup>
              {kindOptions.map((kind) => (
                <FormControlLabel
                  key={kind}
                  control={(
                    <Checkbox
                      checked={kinds.includes(kind)}
                      onChange={() => toggleKind(kind)}
                      inputProps={{ "data-testid": `create-subscription-kind-${kind}` } as Record<string, string>}
                    />
                  )}
                  label={kindLabel(kind)}
                />
              ))}
            </FormGroup>
            {kinds.length === 0 && (
              <Typography variant="caption" color="error">Select at least one event.</Typography>
            )}
          </Box>

          <TextField
            label="Flow pattern"
            placeholder="e.g. sales_*, finance_* (empty = all flows)"
            value={flowPattern}
            onChange={(e) => setFlowPattern(e.target.value)}
            InputLabelProps={{ shrink: true }}
            inputProps={{ "data-testid": "create-subscription-flow-pattern" }}
          />

          {channel === "email" ? (
            <TextField
              label="Email address"
              value={destination}
              onChange={(e) => setDestination(e.target.value)}
              helperText="Leave empty to use your account email"
              inputProps={{ "data-testid": "create-subscription-destination" }}
            />
          ) : (
            <TextField
              label="Slack channel"
              placeholder="Channel id, e.g. C0123ABCD"
              value={destination}
              onChange={(e) => setDestination(e.target.value)}
              helperText="Leave empty to be direct-messaged"
              InputLabelProps={{ shrink: true }}
              inputProps={{ "data-testid": "create-subscription-destination" }}
            />
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={save.isPending} data-testid="create-subscription-cancel">Cancel</Button>
        <Button
          variant="contained"
          onClick={() => save.mutate()}
          disabled={!canSubmit}
          data-testid="create-subscription-submit"
        >
          {subscription === null ? "Create" : "Save"}
        </Button>
      </DialogActions>
    </Dialog>
  );
}

/**
 * Self-service notification subscriptions: email or Slack messages when the caller's flows fail, are cancelled
 * or skipped, or fail assertions, sent either immediately (with a cooldown) or bundled into a digest.
 */
export default function NotificationsPage() {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();

  const [dialog, setDialog] = useState<{ subscription: NotificationSubscription | null } | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<NotificationSubscription | null>(null);

  const optionsQuery = useQuery({ queryKey: ["me-notifications", "options"], queryFn: notificationApi.options });
  const subscriptionsQuery = useQuery({
    queryKey: ["me-notifications", "subscriptions"],
    queryFn: notificationApi.listSubscriptions,
  });
  const deliveriesQuery = useQuery({
    queryKey: ["me-notifications", "deliveries"],
    queryFn: () => notificationApi.listDeliveries(50),
  });

  const options = optionsQuery.data;
  const subscriptions = subscriptionsQuery.data ?? [];
  const deliveries = deliveriesQuery.data ?? [];
  const channelAvailable = options !== undefined && (options.email.available || options.slack.available);
  const notConfigured =
    options !== undefined && (!options.enabled || (!options.email.available && !options.slack.available));

  const toggleEnabled = useMutation({
    mutationFn: ({ subscription, enabled }: { subscription: NotificationSubscription; enabled: boolean }) =>
      notificationApi.updateSubscription(subscription.id, { enabled }),
    onSuccess: (updated) => {
      enqueueSnackbar(updated.enabled ? "Subscription enabled." : "Subscription disabled.", { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["me-notifications"] });
    },
    onError: (error) => {
      enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" });
    },
  });

  const test = useMutation({
    mutationFn: (subscription: NotificationSubscription) => notificationApi.testSubscription(subscription.id),
    onSuccess: () => {
      enqueueSnackbar("Test message queued", { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["me-notifications", "deliveries"] });
    },
    onError: (error) => {
      enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" });
    },
  });

  const remove = useMutation({
    mutationFn: (subscription: NotificationSubscription) => notificationApi.deleteSubscription(subscription.id),
    onSuccess: () => {
      enqueueSnackbar("Subscription deleted.", { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["me-notifications"] });
      setDeleteTarget(null);
    },
    onError: (error) => {
      enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" });
      setDeleteTarget(null);
    },
  });

  return (
    <Page data-testid="page-notifications">
      <PageHeader
        title="Notifications"
        actions={(
          <Button
            variant="contained"
            onClick={() => setDialog({ subscription: null })}
            disabled={!channelAvailable}
            data-testid="open-create-subscription"
          >
            New subscription
          </Button>
        )}
      />

      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Get an email or Slack message when your flows fail, are cancelled or skipped, or fail assertions.
        Immediate subscriptions respect a per-subscription cooldown; digest subscriptions bundle events on an interval.
      </Typography>

      {notConfigured && (
        <Alert severity="info" sx={{ mb: 2 }}>
          No notification channel is configured on this deployment, so no messages can be sent. An administrator
          must configure <code>ControlPlane:Notifications:Email</code> or <code>ControlPlane:Notifications:Slack</code>{" "}
          before subscriptions deliver anything; existing subscriptions remain saved below.
        </Alert>
      )}

      {optionsQuery.isError && (
        <Alert severity="error" sx={{ mb: 2 }}>
          {optionsQuery.error instanceof Error ? optionsQuery.error.message : "Could not load notification options."}
        </Alert>
      )}

      {subscriptionsQuery.isError && (
        <Alert severity="error" sx={{ mb: 2 }}>
          {subscriptionsQuery.error instanceof Error
            ? subscriptionsQuery.error.message
            : "Could not load your subscriptions."}
        </Alert>
      )}

      <TableContainer component={Paper} variant="outlined">
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Channel</TableCell>
              <TableCell>Pacing</TableCell>
              <TableCell>Events</TableCell>
              <TableCell>Flows</TableCell>
              <TableCell>Destination</TableCell>
              <TableCell>Enabled</TableCell>
              <TableCell>Last sent</TableCell>
              <TableCell align="right">Actions</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {subscriptions.length === 0 ? (
              <TableRow>
                <TableCell colSpan={8}>
                  <Typography variant="body2" color="text.secondary" sx={{ py: 2, textAlign: "center" }}>
                    You have no notification subscriptions yet.
                  </Typography>
                </TableCell>
              </TableRow>
            ) : (
              subscriptions.map((subscription) => (
                <TableRow key={subscription.id} data-testid="subscription-row">
                  <TableCell>
                    <Chip size="small" label={CHANNEL_LABELS[subscription.channel]} variant="outlined" />
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2">{pacingText(subscription)}</Typography>
                  </TableCell>
                  <TableCell>
                    <Stack direction="row" spacing={0.5} sx={{ flexWrap: "wrap", gap: 0.5 }}>
                      {subscription.kinds.map((kind) => (
                        <Chip key={kind} size="small" label={kindLabel(kind)} variant="outlined" />
                      ))}
                    </Stack>
                  </TableCell>
                  <TableCell>
                    {subscription.flowPattern === null || subscription.flowPattern === "" ? (
                      <Typography variant="body2" color="text.secondary">All flows</Typography>
                    ) : (
                      <Box component="code" sx={{ fontFamily: "monospace" }}>{subscription.flowPattern}</Box>
                    )}
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2">
                      {destinationText(subscription, options?.userEmail ?? null)}
                    </Typography>
                  </TableCell>
                  <TableCell>
                    <Checkbox
                      size="small"
                      checked={subscription.enabled}
                      onChange={(e) => toggleEnabled.mutate({ subscription, enabled: e.target.checked })}
                      disabled={toggleEnabled.isPending}
                      inputProps={{ "aria-label": "Enabled", "data-testid": "subscription-enabled" } as Record<string, string>}
                    />
                  </TableCell>
                  <TableCell><RelativeTime value={subscription.lastSentUtc} /></TableCell>
                  <TableCell align="right">
                    <Button
                      size="small"
                      onClick={() => test.mutate(subscription)}
                      disabled={test.isPending}
                      data-testid="subscription-test"
                    >
                      Test
                    </Button>
                    <Button
                      size="small"
                      onClick={() => setDialog({ subscription })}
                      disabled={options === undefined}
                      data-testid="subscription-edit"
                    >
                      Edit
                    </Button>
                    <Button
                      size="small"
                      color="error"
                      onClick={() => setDeleteTarget(subscription)}
                      data-testid="subscription-delete"
                    >
                      Delete
                    </Button>
                  </TableCell>
                </TableRow>
              ))
            )}
          </TableBody>
        </Table>
      </TableContainer>

      <Box>
        <Typography variant="h6" sx={{ mb: 1 }}>Recent deliveries</Typography>
        {deliveriesQuery.isError && (
          <Alert severity="error" sx={{ mb: 2 }}>
            {deliveriesQuery.error instanceof Error
              ? deliveriesQuery.error.message
              : "Could not load recent deliveries."}
          </Alert>
        )}
        <TableContainer component={Paper} variant="outlined" data-testid="deliveries-table">
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Subject</TableCell>
                <TableCell>Channel</TableCell>
                <TableCell>Target</TableCell>
                <TableCell>Status</TableCell>
                <TableCell>Attempts</TableCell>
                <TableCell>Created</TableCell>
                <TableCell>Error</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {deliveries.length === 0 ? (
                <TableRow>
                  <TableCell colSpan={7}>
                    <Typography variant="body2" color="text.secondary" sx={{ py: 2, textAlign: "center" }}>
                      No messages have been sent yet.
                    </Typography>
                  </TableCell>
                </TableRow>
              ) : (
                deliveries.map((delivery) => (
                  <TableRow key={delivery.id} data-testid="delivery-row">
                    <TableCell>
                      <Typography variant="body2">{delivery.subject}</Typography>
                    </TableCell>
                    <TableCell>
                      <Chip size="small" label={CHANNEL_LABELS[delivery.channel]} variant="outlined" />
                    </TableCell>
                    <TableCell>
                      <Typography variant="body2">{delivery.target}</Typography>
                    </TableCell>
                    <TableCell><DeliveryStatusChip status={delivery.status} /></TableCell>
                    <TableCell>{delivery.attempts}</TableCell>
                    <TableCell><RelativeTime value={delivery.createdUtc} /></TableCell>
                    <TableCell sx={{ maxWidth: 280 }}>
                      {delivery.lastError === null || delivery.lastError === "" ? (
                        <Typography variant="body2" color="text.secondary">-</Typography>
                      ) : (
                        <Typography variant="body2" noWrap title={delivery.lastError}>
                          {delivery.lastError}
                        </Typography>
                      )}
                    </TableCell>
                  </TableRow>
                ))
              )}
            </TableBody>
          </Table>
        </TableContainer>
      </Box>

      {dialog !== null && options !== undefined && (
        <SubscriptionDialog
          options={options}
          subscription={dialog.subscription}
          onClose={() => setDialog(null)}
        />
      )}

      <ConfirmDialog
        open={deleteTarget !== null}
        title="Delete subscription"
        message={`Delete this ${deleteTarget === null ? "" : deleteTarget.channel === "email" ? "email " : "Slack "}subscription? You will stop receiving its messages immediately.`}
        confirmLabel="Delete"
        danger
        busy={remove.isPending}
        onConfirm={() => {
          if (deleteTarget !== null) {
            remove.mutate(deleteTarget);
          }
        }}
        onClose={() => setDeleteTarget(null)}
      />
    </Page>
  );
}
