import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { BellPlus, BellRing, CircleCheck, CircleX, Clock3, Info, Loader2 } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import {
  Sheet, SheetContent, SheetFooter, SheetHeader, SheetTitle,
} from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { isApiError } from "../../api/client";
import { notificationApi } from "../../api/endpoints";
import type { MyNotificationOptions, NotificationDelivery, NotificationSubscription } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { TruncatedText } from "../../components/TruncatedText";

type Channel = NotificationSubscription["channel"];
type Mode = NotificationSubscription["mode"];

const CHANNEL_LABELS: Record<Channel, string> = { email: "Email", slack: "Slack" };
const MODE_LABELS: Record<Mode, string> = { immediate: "Immediate", digest: "Digest" };

/** One error-to-text mapping for every toast on this page (the API's detail wins over a generic title). */
function errorText(error: unknown): string {
  if (isApiError(error)) {
    return error.detail ?? error.title;
  }

  return error instanceof Error ? error.message : String(error);
}

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

/** The delivery outcome pill (DESIGN.md 7.3): icon + label so color never carries the state alone. */
function DeliveryStatusBadge({ status }: { status: NotificationDelivery["status"] }) {
  const { classes, Icon } = status === "sent"
    ? { classes: "bg-success/12 text-success", Icon: CircleCheck }
    : status === "failed"
      ? { classes: "bg-destructive/12 text-destructive", Icon: CircleX }
      : { classes: "bg-muted text-muted-foreground", Icon: Clock3 };
  return (
    <span
      data-testid="delivery-status"
      className={`inline-flex items-center gap-1 whitespace-nowrap rounded-full px-2 py-0.5 text-[11px] font-medium leading-4 ${classes}`}
    >
      <Icon className="size-3.5 shrink-0" />
      {status}
    </span>
  );
}

/** The one create/edit form, in a right side sheet: creating picks a channel, editing keeps it locked. */
function SubscriptionSheet({
  options,
  subscription,
  onClose,
}: {
  options: MyNotificationOptions;
  /** Null creates a new subscription; otherwise the sheet edits this one. */
  subscription: NotificationSubscription | null;
  onClose: () => void;
}) {
  const queryClient = useQueryClient();

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
      // matches exactly what the sheet shows.
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
      toast.success(subscription === null ? "Subscription created." : "Subscription updated.");
      void queryClient.invalidateQueries({ queryKey: ["me-notifications"] });
      onClose();
    },
    onError: (error) => toast.error(errorText(error)),
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
    <Sheet
      open
      onOpenChange={(next) => {
        if (!next && !save.isPending) {
          onClose();
        }
      }}
    >
      <SheetContent className="w-full sm:max-w-xl" data-testid="create-subscription-dialog">
        <SheetHeader>
          <SheetTitle>{subscription === null ? "New subscription" : "Edit subscription"}</SheetTitle>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-4">
          {save.isError && isApiError(save.error) && <CorrelationError error={save.error} />}
          {save.isError && !isApiError(save.error) && (
            <p className="text-[13px] text-destructive">{String(save.error)}</p>
          )}

          <div className="flex flex-col gap-1.5">
            <Label>Channel</Label>
            <Select
              value={channel}
              onValueChange={(value) => {
                setChannel(value as Channel);
                setDestination("");
              }}
              disabled={subscription !== null}
            >
              <SelectTrigger size="sm" className="h-8 w-full" data-testid="create-subscription-channel">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {channelOptions.map((option) => (
                  <SelectItem key={option} value={option}>{CHANNEL_LABELS[option]}</SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          <div className="flex flex-col gap-1.5">
            <Label>Mode</Label>
            <Select value={mode} onValueChange={(value) => setMode(value as Mode)}>
              <SelectTrigger size="sm" className="h-8 w-full" data-testid="create-subscription-mode">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {modeOptions.map((option) => (
                  <SelectItem key={option} value={option}>{MODE_LABELS[option]}</SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>

          {mode === "immediate" ? (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="create-subscription-cooldown">Cooldown (minutes)</Label>
              <Input
                id="create-subscription-cooldown"
                type="number"
                min={0}
                max={1440}
                value={cooldownText}
                onChange={(e) => setCooldownText(e.target.value)}
                aria-invalid={!cooldownValid || undefined}
                className="h-8"
                data-testid="create-subscription-cooldown"
              />
              <p className={cooldownValid ? "text-xs text-muted-foreground" : "text-xs text-destructive"}>
                {cooldownValid
                  ? "Minimum gap between messages; 0 sends every event."
                  : "Enter a whole number of minutes between 0 and 1440."}
              </p>
            </div>
          ) : (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="create-subscription-digest-interval">Digest interval (minutes)</Label>
              <Input
                id="create-subscription-digest-interval"
                type="number"
                min={5}
                max={10080}
                value={digestText}
                onChange={(e) => setDigestText(e.target.value)}
                aria-invalid={!digestValid || undefined}
                className="h-8"
                data-testid="create-subscription-digest-interval"
              />
              <p className={digestValid ? "text-xs text-muted-foreground" : "text-xs text-destructive"}>
                {digestValid
                  ? "How often bundled events are sent."
                  : "Enter a whole number of minutes between 5 and 10080."}
              </p>
            </div>
          )}

          <div className="flex flex-col gap-1.5">
            <Label>Events</Label>
            <div className="flex flex-col gap-2 pt-1">
              {kindOptions.map((kind) => (
                <Label key={kind} className="flex items-center gap-2 text-[13px] font-normal">
                  <Checkbox
                    checked={kinds.includes(kind)}
                    onCheckedChange={() => toggleKind(kind)}
                    data-testid={`create-subscription-kind-${kind}`}
                  />
                  {kindLabel(kind)}
                </Label>
              ))}
            </div>
            {kinds.length === 0 && (
              <p className="text-xs text-destructive">Select at least one event.</p>
            )}
          </div>

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="create-subscription-flow-pattern">Flow pattern</Label>
            <Input
              id="create-subscription-flow-pattern"
              placeholder="e.g. sales_*, finance_* (empty = all flows)"
              value={flowPattern}
              onChange={(e) => setFlowPattern(e.target.value)}
              className="h-8 font-mono text-[12px]"
              data-testid="create-subscription-flow-pattern"
            />
          </div>

          {channel === "email" ? (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="create-subscription-destination">Email address</Label>
              <Input
                id="create-subscription-destination"
                value={destination}
                onChange={(e) => setDestination(e.target.value)}
                className="h-8"
                data-testid="create-subscription-destination"
              />
              <p className="text-xs text-muted-foreground">Leave empty to use your account email</p>
            </div>
          ) : (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="create-subscription-destination">Slack channel</Label>
              <Input
                id="create-subscription-destination"
                placeholder="Channel id, e.g. C0123ABCD"
                value={destination}
                onChange={(e) => setDestination(e.target.value)}
                className="h-8 font-mono text-[12px]"
                data-testid="create-subscription-destination"
              />
              <p className="text-xs text-muted-foreground">Leave empty to be direct-messaged</p>
            </div>
          )}
        </div>
        <SheetFooter className="flex-row justify-end">
          <Button
            variant="ghost"
            size="sm"
            onClick={onClose}
            disabled={save.isPending}
            data-testid="create-subscription-cancel"
          >
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={() => save.mutate()}
            disabled={!canSubmit}
            data-testid="create-subscription-submit"
          >
            {save.isPending && <Loader2 className="animate-spin" />}
            {subscription === null ? "Create" : "Save"}
          </Button>
        </SheetFooter>
      </SheetContent>
    </Sheet>
  );
}

const SUBSCRIPTION_HEADERS = ["Channel", "Pacing", "Events", "Flows", "Destination", "Enabled", "Last sent", "Actions"];
const DELIVERY_HEADERS = ["Subject", "Channel", "Target", "Status", "Attempts", "Created", "Error"];

/** Skeleton rows for one of the hand-rolled tables while its query loads. */
function SkeletonRows({ headers, count }: { headers: string[]; count: number }) {
  return (
    <>
      {Array.from({ length: count }, (_, i) => (
        <TableRow key={`skeleton-${i}`}>
          {headers.map((header) => (
            <TableCell key={header} className="px-3 py-2">
              <Skeleton className="h-4 w-full" />
            </TableCell>
          ))}
        </TableRow>
      ))}
    </>
  );
}

/**
 * Self-service notification subscriptions: email or Slack messages when the caller's flows fail, are cancelled
 * or skipped, or fail assertions, sent either immediately (with a cooldown) or bundled into a digest.
 */
export default function NotificationsPage() {
  const queryClient = useQueryClient();

  const [sheet, setSheet] = useState<{ subscription: NotificationSubscription | null } | null>(null);
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
  const subscriptions = subscriptionsQuery.data;
  const deliveries = deliveriesQuery.data;
  const channelAvailable = options !== undefined && (options.email.available || options.slack.available);
  const notConfigured =
    options !== undefined && (!options.enabled || (!options.email.available && !options.slack.available));

  const toggleEnabled = useMutation({
    mutationFn: ({ subscription, enabled }: { subscription: NotificationSubscription; enabled: boolean }) =>
      notificationApi.updateSubscription(subscription.id, { enabled }),
    onSuccess: (updated) => {
      toast.success(updated.enabled ? "Subscription enabled." : "Subscription disabled.");
      void queryClient.invalidateQueries({ queryKey: ["me-notifications"] });
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const test = useMutation({
    mutationFn: (subscription: NotificationSubscription) => notificationApi.testSubscription(subscription.id),
    onSuccess: () => {
      toast.success("Test message queued");
      void queryClient.invalidateQueries({ queryKey: ["me-notifications", "deliveries"] });
    },
    onError: (error) => toast.error(errorText(error)),
  });

  const remove = useMutation({
    mutationFn: (subscription: NotificationSubscription) => notificationApi.deleteSubscription(subscription.id),
    onSuccess: () => {
      toast.success("Subscription deleted.");
      void queryClient.invalidateQueries({ queryKey: ["me-notifications"] });
      setDeleteTarget(null);
    },
    onError: (error) => {
      toast.error(errorText(error));
      setDeleteTarget(null);
    },
  });

  return (
    <Page data-testid="page-notifications">
      <PageHeader
        title="Notifications"
        subtitle="Get an email or Slack message when your flows fail, are cancelled or skipped, or fail assertions. Immediate subscriptions respect a per-subscription cooldown; digest subscriptions bundle events on an interval."
        actions={(
          <Button
            size="sm"
            onClick={() => setSheet({ subscription: null })}
            disabled={!channelAvailable}
            data-testid="open-create-subscription"
          >
            <BellPlus />
            New subscription
          </Button>
        )}
      />

      {notConfigured && (
        <Alert>
          <Info />
          <AlertDescription>
            No notification channel is configured on this deployment, so no messages can be sent. An administrator
            must configure <code className="font-mono text-[12px]">ControlPlane:Notifications:Email</code> or{" "}
            <code className="font-mono text-[12px]">ControlPlane:Notifications:Slack</code> before subscriptions
            deliver anything; existing subscriptions remain saved below.
          </AlertDescription>
        </Alert>
      )}

      {optionsQuery.isError && (
        isApiError(optionsQuery.error)
          ? <CorrelationError error={optionsQuery.error} />
          : <p className="text-[13px] text-destructive">{errorText(optionsQuery.error)}</p>
      )}

      {subscriptionsQuery.isError && (
        isApiError(subscriptionsQuery.error)
          ? <CorrelationError error={subscriptionsQuery.error} />
          : <p className="text-[13px] text-destructive">{errorText(subscriptionsQuery.error)}</p>
      )}

      {/* Hand-rolled on the ui table primitives (not DataTable) so each row keeps its `subscription-row` testid. */}
      <Card className="gap-0 overflow-hidden rounded-lg p-0">
        <Table>
          <TableHeader>
            <TableRow className="hover:bg-transparent">
              {SUBSCRIPTION_HEADERS.map((header, i) => (
                <TableHead
                  key={header}
                  className={`h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground${i === SUBSCRIPTION_HEADERS.length - 1 ? " text-right" : ""}`}
                >
                  {header}
                </TableHead>
              ))}
            </TableRow>
          </TableHeader>
          <TableBody>
            {subscriptions === undefined && !subscriptionsQuery.isError && (
              <SkeletonRows headers={SUBSCRIPTION_HEADERS} count={2} />
            )}
            {subscriptions !== undefined && subscriptions.length === 0 && (
              <TableRow className="hover:bg-transparent">
                <TableCell colSpan={SUBSCRIPTION_HEADERS.length} className="border-0 p-0">
                  <EmptyState
                    icon={<BellRing />}
                    title="You have no notification subscriptions yet"
                    description="Subscribe to be told when your flows fail, are cancelled or skipped, or fail assertions."
                    action={channelAvailable ? (
                      <Button size="sm" variant="outline" onClick={() => setSheet({ subscription: null })}>
                        New subscription
                      </Button>
                    ) : undefined}
                  />
                </TableCell>
              </TableRow>
            )}
            {subscriptions?.map((subscription) => (
              <TableRow key={subscription.id} data-testid="subscription-row">
                <TableCell className="whitespace-nowrap px-3 py-1.5">
                  <Badge variant="outline">{CHANNEL_LABELS[subscription.channel]}</Badge>
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                  {pacingText(subscription)}
                </TableCell>
                <TableCell className="px-3 py-1.5">
                  <div className="flex flex-wrap gap-1">
                    {subscription.kinds.map((kind) => (
                      <Badge key={kind} variant="outline">{kindLabel(kind)}</Badge>
                    ))}
                  </div>
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                  {subscription.flowPattern === null || subscription.flowPattern === "" ? (
                    <span className="text-muted-foreground">All flows</span>
                  ) : (
                    <code className="font-mono text-[12px]">{subscription.flowPattern}</code>
                  )}
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                  {destinationText(subscription, options?.userEmail ?? null)}
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5">
                  <Switch
                    checked={subscription.enabled}
                    onCheckedChange={(enabled) => toggleEnabled.mutate({ subscription, enabled })}
                    disabled={toggleEnabled.isPending}
                    aria-label="Enabled"
                    data-testid="subscription-enabled"
                  />
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                  <RelativeTime value={subscription.lastSentUtc} />
                </TableCell>
                <TableCell className="whitespace-nowrap px-3 py-1.5 text-right">
                  <div className="flex justify-end gap-0.5">
                    <Button
                      variant="ghost"
                      size="xs"
                      onClick={() => test.mutate(subscription)}
                      disabled={test.isPending}
                      data-testid="subscription-test"
                    >
                      {test.isPending && test.variables?.id === subscription.id && (
                        <Loader2 className="animate-spin" />
                      )}
                      Test
                    </Button>
                    <Button
                      variant="ghost"
                      size="xs"
                      onClick={() => setSheet({ subscription })}
                      disabled={options === undefined}
                      data-testid="subscription-edit"
                    >
                      Edit
                    </Button>
                    <Button
                      variant="ghost"
                      size="xs"
                      className="text-destructive hover:text-destructive"
                      onClick={() => setDeleteTarget(subscription)}
                      data-testid="subscription-delete"
                    >
                      Delete
                    </Button>
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Card>

      <div className="flex flex-col gap-2">
        <h2 className="text-base font-medium">Recent deliveries</h2>
        {deliveriesQuery.isError && (
          isApiError(deliveriesQuery.error)
            ? <CorrelationError error={deliveriesQuery.error} />
            : <p className="text-[13px] text-destructive">{errorText(deliveriesQuery.error)}</p>
        )}
        <Card className="gap-0 overflow-hidden rounded-lg p-0" data-testid="deliveries-table">
          <Table>
            <TableHeader>
              <TableRow className="hover:bg-transparent">
                {DELIVERY_HEADERS.map((header) => (
                  <TableHead key={header} className="h-8 whitespace-nowrap px-3 text-xs font-medium text-muted-foreground">
                    {header}
                  </TableHead>
                ))}
              </TableRow>
            </TableHeader>
            <TableBody>
              {deliveries === undefined && !deliveriesQuery.isError && (
                <SkeletonRows headers={DELIVERY_HEADERS} count={3} />
              )}
              {deliveries !== undefined && deliveries.length === 0 && (
                <TableRow className="hover:bg-transparent">
                  <TableCell colSpan={DELIVERY_HEADERS.length} className="border-0 p-0">
                    <EmptyState title="No messages have been sent yet." />
                  </TableCell>
                </TableRow>
              )}
              {deliveries?.map((delivery) => (
                <TableRow key={delivery.id} data-testid="delivery-row">
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">{delivery.subject}</TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5">
                    <Badge variant="outline">{CHANNEL_LABELS[delivery.channel]}</Badge>
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">{delivery.target}</TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5">
                    <DeliveryStatusBadge status={delivery.status} />
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5">
                    <span className="font-mono text-[13px] tabular-nums">{delivery.attempts}</span>
                  </TableCell>
                  <TableCell className="whitespace-nowrap px-3 py-1.5 text-[13px]">
                    <RelativeTime value={delivery.createdUtc} />
                  </TableCell>
                  <TableCell className="max-w-[280px] px-3 py-1.5 text-[13px]">
                    {delivery.lastError === null || delivery.lastError === "" ? (
                      <span className="text-muted-foreground">-</span>
                    ) : (
                      <TruncatedText text={delivery.lastError} maxWidth={280} />
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Card>
      </div>

      {sheet !== null && options !== undefined && (
        <SubscriptionSheet
          options={options}
          subscription={sheet.subscription}
          onClose={() => setSheet(null)}
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
