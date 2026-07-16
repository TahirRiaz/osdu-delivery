import { useState, type FormEvent, type InputHTMLAttributes } from "react";
import { Navigate, useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import Accordion from "@mui/material/Accordion";
import AccordionDetails from "@mui/material/AccordionDetails";
import AccordionSummary from "@mui/material/AccordionSummary";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Checkbox from "@mui/material/Checkbox";
import Divider from "@mui/material/Divider";
import FormControlLabel from "@mui/material/FormControlLabel";
import Paper from "@mui/material/Paper";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import ExpandMoreIcon from "@mui/icons-material/ExpandMore";
import { authApi } from "../api/endpoints";
import { isApiError } from "../api/client";
import { CorrelationError } from "../components/CorrelationError";
import { useAuth } from "./AuthContext";

/** Checkbox's inputProps is the strict InputHTMLAttributes, which has no data-* member; cast through unknown so the
 * test id is accepted without loosening the component's typing. */
const rememberInputProps = { "data-testid": "login-remember" } as unknown as InputHTMLAttributes<HTMLInputElement>;

/** The Microsoft four-square mark, per their sign-in branding guidelines. */
function MicrosoftMark() {
  return (
    <svg width="18" height="18" viewBox="0 0 21 21" aria-hidden="true">
      <rect x="1" y="1" width="9" height="9" fill="#f25022" />
      <rect x="11" y="1" width="9" height="9" fill="#7fba00" />
      <rect x="1" y="11" width="9" height="9" fill="#00a4ef" />
      <rect x="11" y="11" width="9" height="9" fill="#ffb900" />
    </svg>
  );
}

/**
 * The sign-in page: a brand panel (the SQLFlow mark on its navy) beside a focused form. Username/password for
 * regular SQLFlow users, "Sign in with Microsoft" when the control plane has Entra enabled, and the break-glass
 * bootstrap secret tucked behind an expander when one is configured. Which options render is driven by
 * GET /auth/providers, so this page never guesses the server's configuration.
 */
export default function LoginPage() {
  const { session, sessionEndedReason, loginLocal, loginEntra, loginBootstrap } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const returnTo = decodeReturnTo(params.get("returnTo"));

  const providers = useQuery({ queryKey: ["auth", "providers"], queryFn: authApi.providers, staleTime: 60_000 });

  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [remember, setRemember] = useState(false);
  const [secret, setSecret] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  if (session) {
    return <Navigate to={returnTo} replace />;
  }

  const run = async (work: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await work();
      navigate(returnTo, { replace: true });
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(false);
    }
  };

  const submitLocal = (event: FormEvent) => {
    event.preventDefault();
    void run(() => loginLocal(username, password, remember));
  };

  const submitBootstrap = (event: FormEvent) => {
    event.preventDefault();
    void run(() => loginBootstrap(secret));
  };

  return (
    <Box
      sx={{
        minHeight: "100vh",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        p: { xs: 2, sm: 3 },
        background:
          "radial-gradient(1100px 600px at 15% -10%, rgba(47, 111, 206, 0.18), transparent 60%), var(--sf-background)",
      }}
    >
      <Paper
        elevation={8}
        sx={{
          width: 880,
          maxWidth: "100%",
          display: "flex",
          overflow: "hidden",
          borderRadius: 3,
          minHeight: 560,
        }}
        data-testid="login-card"
      >
        {/* Brand panel: the full logo lockup on its own navy, hidden on small screens. */}
        <Box
          sx={{
            width: 380,
            flexShrink: 0,
            display: { xs: "none", md: "flex" },
            flexDirection: "column",
            p: 5,
            color: "var(--sf-brand-cream)",
            background:
              "linear-gradient(160deg, var(--sf-brand-navy) 0%, var(--sf-brand-navy-deep) 100%)",
          }}
        >
          <Box sx={{ my: "auto" }}>
            <Stack direction="row" spacing={2} alignItems="center" sx={{ mb: 3 }}>
              <Box component="img" src="/brand/logo-white.png" alt="" sx={{ width: 64, height: 64, objectFit: "contain" }} />
              <Typography variant="h4" sx={{ fontWeight: 700, color: "#ffffff" }}>SQLFlow</Typography>
            </Stack>
            <Typography variant="h5" sx={{ fontWeight: 650, color: "#ffffff", mb: 1 }}>
              Data flows, under control.
            </Typography>
            <Typography variant="body2" sx={{ color: "rgba(253, 243, 231, 0.75)", lineHeight: 1.7 }}>
              The SQLFlow control plane: YAML-first pipelines from git, executed near your data, observable from
              one place.
            </Typography>
          </Box>

          <Typography variant="caption" sx={{ color: "rgba(253, 243, 231, 0.5)" }}>
            SQLFlow control plane
          </Typography>
        </Box>

        {/* The form. */}
        <Box sx={{ flexGrow: 1, p: { xs: 3, sm: 5 }, display: "flex", flexDirection: "column", justifyContent: "center" }}>
          <Stack spacing={2.5} sx={{ maxWidth: 380, width: "100%", mx: "auto" }}>
            <Box sx={{ display: { xs: "flex", md: "none" }, alignItems: "center", gap: 1.25 }}>
              <Box component="img" src="/brand/logo-icon.png" alt="" sx={{ width: 36, height: 38, borderRadius: 1 }} />
              <Typography variant="h6" sx={{ fontWeight: 700 }}>SQLFlow</Typography>
            </Box>

            <Box>
              <Typography variant="h5" sx={{ mb: 0.5 }}>Sign in</Typography>
              <Typography variant="body2" color="text.secondary">
                Use your SQLFlow account to continue.
              </Typography>
            </Box>

            {sessionEndedReason && (
              <Alert severity="info" data-testid="session-ended-notice">{sessionEndedReason}</Alert>
            )}

            {error !== null && (
              isApiError(error)
                ? <CorrelationError error={error} data-testid="login-error" />
                : <Alert severity="error" data-testid="login-error">{String((error as Error).message ?? error)}</Alert>
            )}

            <form onSubmit={submitLocal}>
              <Stack spacing={2}>
                <TextField
                  label="Username"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  autoComplete="username"
                  autoFocus
                  fullWidth
                  inputProps={{ "data-testid": "login-username" }}
                />
                <TextField
                  label="Password"
                  type="password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  autoComplete="current-password"
                  fullWidth
                  inputProps={{ "data-testid": "login-password" }}
                />
                <FormControlLabel
                  control={
                    <Checkbox
                      checked={remember}
                      onChange={(e) => setRemember(e.target.checked)}
                      size="small"
                      // Checkbox's inputProps is the strict InputHTMLAttributes (no data-* key), so the test id is
                      // passed via a variable: assignability allows the extra property, a fresh literal would not.
                      inputProps={rememberInputProps}
                    />
                  }
                  label={<Typography variant="body2">Keep me signed in on this device</Typography>}
                  sx={{ mt: -0.5 }}
                />
                <Button
                  type="submit"
                  variant="contained"
                  size="large"
                  disabled={busy || username.trim() === "" || password === ""}
                  data-testid="login-submit"
                  sx={{ py: 1.25 }}
                >
                  Sign in
                </Button>
              </Stack>
            </form>

            {providers.data?.entra.enabled && (
              <>
                <Divider><Typography variant="caption" color="text.secondary">or</Typography></Divider>
                <Button
                  variant="outlined"
                  size="large"
                  startIcon={<MicrosoftMark />}
                  disabled={busy}
                  onClick={() => void run(() => loginEntra(providers.data.entra, remember))}
                  data-testid="login-entra"
                  sx={{ py: 1.25, color: "text.primary", borderColor: "divider" }}
                >
                  Sign in with Microsoft
                </Button>
              </>
            )}

            {providers.data?.bootstrap && (
              <Accordion
                disableGutters
                elevation={0}
                sx={{ "&:before": { display: "none" }, bgcolor: "transparent" }}
              >
                <AccordionSummary expandIcon={<ExpandMoreIcon />} data-testid="login-bootstrap-expander" sx={{ px: 0 }}>
                  <Typography variant="body2" color="text.secondary">Advanced: bootstrap secret</Typography>
                </AccordionSummary>
                <AccordionDetails sx={{ px: 0 }}>
                  <form onSubmit={submitBootstrap}>
                    <Stack spacing={2}>
                      <Typography variant="caption" color="text.secondary">
                        The break-glass credential from the control plane configuration. Grants full access; use it
                        only to provision real users.
                      </Typography>
                      <TextField
                        label="Bootstrap secret"
                        type="password"
                        value={secret}
                        onChange={(e) => setSecret(e.target.value)}
                        autoComplete="off"
                        fullWidth
                        inputProps={{ "data-testid": "login-bootstrap-secret" }}
                      />
                      <Button
                        type="submit"
                        variant="outlined"
                        color="warning"
                        disabled={busy || secret === ""}
                        data-testid="login-bootstrap-submit"
                      >
                        Sign in with bootstrap secret
                      </Button>
                    </Stack>
                  </form>
                </AccordionDetails>
              </Accordion>
            )}
          </Stack>
        </Box>
      </Paper>
    </Box>
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
