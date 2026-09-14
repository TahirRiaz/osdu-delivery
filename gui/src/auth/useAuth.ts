import { createContext, useContext } from "react";
import type { EntraProviderInfo } from "../api/types";

/** The signed-in session as the GUI holds it. The token also lives in the API client for request headers. */
export interface Session {
  token: string;
  subject: string;
  /** The user's role; null for a bootstrap-secret session (which has scopes but no user behind it). */
  role: string | null;
  scopes: string[];
  expiresAtMs: number;
  /** Which storage backer holds this session, and so whether it outlives the tab. Derived from where it was found
   * on restore rather than trusted from the stored payload. */
  remember: boolean;
  /** Whether this session may roll onto a fresh token. True for a user-backed sign-in; false for the break-glass
   * bootstrap session, which the server refuses to renew and which is meant to lapse. */
  renewable: boolean;
}

export interface AuthContextValue {
  session: Session | null;
  /** Set when the previous session ended involuntarily (expiry or a 401), so the login page can say why. */
  sessionEndedReason: string | null;
  hasScope: (scope: string) => boolean;
  loginLocal: (username: string, password: string, remember: boolean) => Promise<void>;
  loginEntra: (entra: EntraProviderInfo, remember: boolean) => Promise<void>;
  loginBootstrap: (secret: string) => Promise<void>;
  logout: () => void;
}

export const AuthContext = createContext<AuthContextValue | null>(null);

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error("useAuth must be used inside AuthProvider.");
  }

  return context;
}
