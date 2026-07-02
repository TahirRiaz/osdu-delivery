import { useState, type FormEvent } from "react";
import { Navigate, useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import Accordion from "@mui/material/Accordion";
import AccordionDetails from "@mui/material/AccordionDetails";
import AccordionSummary from "@mui/material/AccordionSummary";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Card from "@mui/material/Card";
import CardContent from "@mui/material/CardContent";
import Divider from "@mui/material/Divider";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import ExpandMoreIcon from "@mui/icons-material/ExpandMore";
import MicrosoftIcon from "@mui/icons-material/Window";
import { authApi } from "../api/endpoints";
import { isApiError } from "../api/client";
import { CorrelationError } from "../components/CorrelationError";
import { useAuth } from "./AuthContext";

/**
 * The sign-in page: username/password for regular SQLFlow users, "Sign in with Microsoft" when the control plane
 * has Entra enabled, and the break-glass bootstrap secret tucked behind an expander when one is configured.
 * Which options render is driven by GET /auth/providers, so this page never guesses the server's configuration.
 */
export default function LoginPage() {
  const { session, sessionEndedReason, loginLocal, loginEntra, loginBootstrap } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const returnTo = decodeReturnTo(params.get("returnTo"));

  const providers = useQuery({ queryKey: ["auth", "providers"], queryFn: authApi.providers, staleTime: 60_000 });

  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
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
    void run(() => loginLocal(username, password));
  };

  const submitBootstrap = (event: FormEvent) => {
    event.preventDefault();
    void run(() => loginBootstrap(secret));
  };

  return (
    <Box sx={{ minHeight: "100vh", display: "flex", alignItems: "center", justifyContent: "center", p: 2 }}>
      <Card sx={{ width: 420, maxWidth: "100%" }} data-testid="login-card">
        <CardContent>
          <Stack spacing={2}>
            <Box textAlign="center">
              <Typography variant="h5" component="h1">SQLFlow</Typography>
              <Typography variant="body2" color="text.secondary">Control plane sign-in</Typography>
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
                <Button
                  type="submit"
                  variant="contained"
                  disabled={busy || username.trim() === "" || password === ""}
                  data-testid="login-submit"
                >
                  Sign in
                </Button>
              </Stack>
            </form>

            {providers.data?.entra.enabled && (
              <>
                <Divider>or</Divider>
                <Button
                  variant="outlined"
                  startIcon={<MicrosoftIcon />}
                  disabled={busy}
                  onClick={() => void run(() => loginEntra(providers.data.entra))}
                  data-testid="login-entra"
                >
                  Sign in with Microsoft
                </Button>
              </>
            )}

            {providers.data?.bootstrap && (
              <Accordion disableGutters elevation={0} sx={{ "&:before": { display: "none" } }}>
                <AccordionSummary expandIcon={<ExpandMoreIcon />} data-testid="login-bootstrap-expander">
                  <Typography variant="body2" color="text.secondary">Advanced: bootstrap secret</Typography>
                </AccordionSummary>
                <AccordionDetails>
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
        </CardContent>
      </Card>
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
