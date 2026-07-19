import { useState, type FormEvent } from "react";
import { Navigate, useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, CircleAlert, Info, Loader2 } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { authApi } from "../api/endpoints";
import { isApiError } from "../api/client";
import { CorrelationError } from "../components/CorrelationError";
import { useAuth } from "./AuthContext";
import { readLoginPrefs } from "./loginPrefs";

/** Which sign-in path is in flight, so only the button that started the work shows the spinner. */
type BusyAction = "local" | "entra" | "bootstrap" | null;

/** The Microsoft four-square mark, per their sign-in branding guidelines. */
function MicrosoftMark() {
  return (
    <svg width="16" height="16" viewBox="0 0 21 21" aria-hidden="true">
      <rect x="1" y="1" width="9" height="9" fill="#f25022" />
      <rect x="11" y="1" width="9" height="9" fill="#7fba00" />
      <rect x="1" y="11" width="9" height="9" fill="#00a4ef" />
      <rect x="11" y="11" width="9" height="9" fill="#ffb900" />
    </svg>
  );
}

/**
 * The sign-in page, the one surface that renders outside the workbench shell: a centered card on the
 * editor background with the SQLFlow lockup above it. Username/password for regular SQLFlow users,
 * "Sign in with Microsoft" when the control plane has Entra enabled, and the break-glass bootstrap
 * secret tucked behind an expander when one is configured. Which options render is driven by
 * GET /auth/providers, so this page never guesses the server's configuration.
 */
export default function LoginPage() {
  const { session, sessionEndedReason, loginLocal, loginEntra, loginBootstrap } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const returnTo = decodeReturnTo(params.get("returnTo"));

  const providers = useQuery({ queryKey: ["auth", "providers"], queryFn: authApi.providers, staleTime: 60_000 });

  // Read once per mount, not on every render: the stored values seed the form, and from then on the form is the
  // truth. `prefs` is kept so focus can land on the field that is actually still empty.
  const [prefs] = useState(readLoginPrefs);
  const [username, setUsername] = useState(prefs.username);
  const [password, setPassword] = useState("");
  const [remember, setRemember] = useState(prefs.remember);
  const [secret, setSecret] = useState("");
  const [busy, setBusy] = useState<BusyAction>(null);
  const [error, setError] = useState<unknown>(null);

  if (session) {
    return <Navigate to={returnTo} replace />;
  }

  const anyBusy = busy !== null;

  const run = async (action: Exclude<BusyAction, null>, work: () => Promise<void>) => {
    setBusy(action);
    setError(null);
    try {
      await work();
      navigate(returnTo, { replace: true });
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(null);
    }
  };

  // Every sign-in path records its own prefill from the server's answer, inside beginSession; nothing to do here.
  const submitLocal = (event: FormEvent) => {
    event.preventDefault();
    void run("local", () => loginLocal(username, password, remember));
  };

  const submitBootstrap = (event: FormEvent) => {
    event.preventDefault();
    void run("bootstrap", () => loginBootstrap(secret));
  };

  return (
    <div className="flex min-h-screen flex-col items-center justify-center gap-5 bg-background p-4">
      {/* The lockup above the card: the colored mark works on both the light and the dark background. */}
      <div className="flex items-center gap-2.5">
        <img src="/brand/logo-icon.png" alt="" className="size-9 rounded-md object-contain" />
        <span className="text-lg font-semibold">SQLFlow</span>
      </div>

      <Card className="w-full max-w-sm gap-0 rounded-lg p-6" data-testid="login-card">
        <div className="flex flex-col gap-4">
          <div>
            <h1 className="text-base font-medium">Sign in</h1>
            <p className="mt-0.5 text-[13px] text-muted-foreground">Use your SQLFlow account to continue.</p>
          </div>

          {sessionEndedReason && (
            <Alert data-testid="session-ended-notice">
              <Info />
              <AlertDescription>{sessionEndedReason}</AlertDescription>
            </Alert>
          )}

          {error !== null && (
            isApiError(error)
              ? <CorrelationError error={error} data-testid="login-error" />
              : (
                <Alert variant="destructive" data-testid="login-error">
                  <CircleAlert />
                  <AlertTitle>Sign-in failed</AlertTitle>
                  <AlertDescription>{String((error as Error).message ?? error)}</AlertDescription>
                </Alert>
              )
          )}

          <form onSubmit={submitLocal} className="flex flex-col gap-3">
            {/* A password manager keys off name + autocomplete together: with autocomplete alone the field is
                recognised inconsistently, so both are set here and the pair is what makes save/autofill offer. */}
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="login-username">Username</Label>
              <Input
                id="login-username"
                name="username"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                autoComplete="username"
                // A returning user's username is already filled in, so the caret belongs on the password instead.
                autoFocus={prefs.username === ""}
                className="h-8"
                data-testid="login-username"
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="login-password">Password</Label>
              <Input
                id="login-password"
                name="password"
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoComplete="current-password"
                autoFocus={prefs.username !== ""}
                className="h-8"
                data-testid="login-password"
              />
            </div>
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Checkbox
                checked={remember}
                onCheckedChange={(checked) => setRemember(checked === true)}
                data-testid="login-remember"
              />
              Keep me signed in on this device
            </Label>
            <Button
              type="submit"
              size="sm"
              className="w-full"
              disabled={anyBusy || username.trim() === "" || password === ""}
              data-testid="login-submit"
            >
              {busy === "local" && <Loader2 className="animate-spin" />}
              Sign in
            </Button>
          </form>

          {providers.data?.entra.enabled && (
            <>
              <div className="flex items-center gap-3">
                <div className="h-px flex-1 bg-border" />
                <span className="text-xs text-muted-foreground">or</span>
                <div className="h-px flex-1 bg-border" />
              </div>
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="w-full"
                disabled={anyBusy}
                onClick={() => void run("entra", () => loginEntra(providers.data.entra, remember))}
                data-testid="login-entra"
              >
                {busy === "entra" ? <Loader2 className="animate-spin" /> : <MicrosoftMark />}
                Sign in with Microsoft
              </Button>
            </>
          )}

          {providers.data?.bootstrap && (
            <Collapsible>
              <CollapsibleTrigger
                className="group flex w-full items-center gap-1 text-[13px] text-muted-foreground hover:text-foreground"
                data-testid="login-bootstrap-expander"
              >
                <ChevronDown className="size-4 shrink-0 transition-transform duration-120 group-data-[state=open]:rotate-180" />
                Advanced: bootstrap secret
              </CollapsibleTrigger>
              <CollapsibleContent>
                <form onSubmit={submitBootstrap} className="flex flex-col gap-3 pt-3">
                  <p className="text-xs text-muted-foreground">
                    The break-glass credential from the control plane configuration. Grants full access; use it
                    only to provision real users.
                  </p>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="login-bootstrap-secret">Bootstrap secret</Label>
                    <Input
                      id="login-bootstrap-secret"
                      type="password"
                      value={secret}
                      onChange={(e) => setSecret(e.target.value)}
                      autoComplete="off"
                      className="h-8"
                      data-testid="login-bootstrap-secret"
                    />
                  </div>
                  <Button
                    type="submit"
                    variant="outline"
                    size="sm"
                    className="w-full"
                    disabled={anyBusy || secret === ""}
                    data-testid="login-bootstrap-submit"
                  >
                    {busy === "bootstrap" && <Loader2 className="animate-spin" />}
                    Sign in with bootstrap secret
                  </Button>
                </form>
              </CollapsibleContent>
            </Collapsible>
          )}
        </div>
      </Card>
    </div>
  );
}

function decodeReturnTo(raw: string | null): string {
  if (!raw) {
    return "/";
  }

  const decoded = decodeURIComponent(raw);
  // Only same-app absolute paths: a foreign origin in returnTo must never become a redirect target.
  return decoded.startsWith("/") && !decoded.startsWith("//") ? decoded : "/";
}
