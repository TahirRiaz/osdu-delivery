import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { setAuthToken, setUnauthorizedHandler } from "../api/client";
import { authApi } from "../api/endpoints";
import type { EntraProviderInfo } from "../api/types";
import { signInWithEntra } from "./msal";

/** The signed-in session as the GUI holds it. The token also lives in the API client for request headers. */
export interface Session {
  token: string;
  subject: string;
  /** The user's role; null for a bootstrap-secret session (which has scopes but no user behind it). */
  role: string | null;
  scopes: string[];
  expiresAtMs: number;
}

interface AuthContextValue {
  session: Session | null;
  /** Set when the previous session ended involuntarily (expiry or a 401), so the login page can say why. */
  sessionEndedReason: string | null;
  hasScope: (scope: string) => boolean;
  loginLocal: (username: string, password: string) => Promise<void>;
  loginEntra: (entra: EntraProviderInfo) => Promise<void>;
  loginBootstrap: (secret: string) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

const STORAGE_KEY = "sqlflow.session";

// The token is kept in memory + sessionStorage: an F5 keeps the session (an ops tool that logs you out on every
// reload is unusable), the tab closing ends it, and localStorage's cross-restart persistence is deliberately not
// used. The bootstrap secret / password never persists anywhere.
function restoreSession(): Session | null {
  try {
    const raw = window.sessionStorage.getItem(STORAGE_KEY);
    if (!raw) {
      return null;
    }

    const parsed = JSON.parse(raw) as Session;
    if (!parsed.token || typeof parsed.expiresAtMs !== "number" || parsed.expiresAtMs <= Date.now()) {
      window.sessionStorage.removeItem(STORAGE_KEY);
      return null;
    }

    return parsed;
  } catch {
    window.sessionStorage.removeItem(STORAGE_KEY);
    return null;
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(() => {
    const restored = restoreSession();
    setAuthToken(restored?.token ?? null);
    return restored;
  });
  const [sessionEndedReason, setSessionEndedReason] = useState<string | null>(null);
  const expiryTimer = useRef<number | null>(null);

  const endSession = useCallback((reason: string | null) => {
    window.sessionStorage.removeItem(STORAGE_KEY);
    setAuthToken(null);
    setSession(null);
    setSessionEndedReason(reason);
  }, []);

  const beginSession = useCallback((next: Session) => {
    window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(next));
    setAuthToken(next.token);
    setSession(next);
    setSessionEndedReason(null);
  }, []);

  // A 401 on an authenticated call means the token no longer works (expired or the signing key rotated).
  useEffect(() => {
    setUnauthorizedHandler(() => endSession("Your session is no longer valid; sign in again."));
    return () => setUnauthorizedHandler(null);
  }, [endSession]);

  // Proactive expiry: end the session shortly before the token lapses instead of letting a call fail first.
  useEffect(() => {
    if (expiryTimer.current !== null) {
      window.clearTimeout(expiryTimer.current);
      expiryTimer.current = null;
    }

    if (session) {
      const msLeft = session.expiresAtMs - Date.now() - 30_000;
      expiryTimer.current = window.setTimeout(
        () => endSession("Your session expired; sign in again."),
        Math.max(0, msLeft),
      );
    }

    return () => {
      if (expiryTimer.current !== null) {
        window.clearTimeout(expiryTimer.current);
      }
    };
  }, [session, endSession]);

  const loginLocal = useCallback(async (username: string, password: string) => {
    const response = await authApi.login(username, password);
    beginSession({
      token: response.accessToken,
      subject: response.subject,
      role: response.role,
      scopes: response.scopes,
      expiresAtMs: Date.now() + response.expiresIn * 1000,
    });
  }, [beginSession]);

  const loginEntra = useCallback(async (entra: EntraProviderInfo) => {
    const idToken = await signInWithEntra(entra);
    const response = await authApi.exchange(idToken);
    beginSession({
      token: response.accessToken,
      subject: response.subject,
      role: response.role,
      scopes: response.scopes,
      expiresAtMs: Date.now() + response.expiresIn * 1000,
    });
  }, [beginSession]);

  const loginBootstrap = useCallback(async (secret: string) => {
    // The bootstrap secret is the root credential: request everything it grants.
    const scopes = ["read", "operate", "admin"];
    const response = await authApi.bootstrapToken(secret, scopes);
    beginSession({
      token: response.accessToken,
      subject: "bootstrap",
      role: null,
      scopes,
      expiresAtMs: Date.now() + response.expiresIn * 1000,
    });
  }, [beginSession]);

  const logout = useCallback(() => endSession(null), [endSession]);

  const value = useMemo<AuthContextValue>(() => ({
    session,
    sessionEndedReason,
    hasScope: (scope: string) => session?.scopes.includes(scope) ?? false,
    loginLocal,
    loginEntra,
    loginBootstrap,
    logout,
  }), [session, sessionEndedReason, loginLocal, loginEntra, loginBootstrap, logout]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error("useAuth must be used inside AuthProvider.");
  }

  return context;
}
