import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import CssBaseline from "@mui/material/CssBaseline";
import { ThemeProvider, createTheme } from "@mui/material/styles";
import useMediaQuery from "@mui/material/useMediaQuery";
import "./branding.css";
import { applyBrandMode, brandToken } from "./branding";

type ThemeMode = "light" | "dark";

interface ThemeModeValue {
  mode: ThemeMode;
  toggle: () => void;
}

const ThemeModeContext = createContext<ThemeModeValue | null>(null);

const STORAGE_KEY = "sqlflow.theme";

/**
 * Light/dark theming: the OS preference is the default, the user's explicit choice (persisted) wins. The MUI
 * theme is built from the SQLFlow branding tokens in branding.css (the previous GUI's color base), so MUI
 * components and plain-CSS surfaces share one palette.
 */
export function ThemeModeProvider({ children }: { children: ReactNode }) {
  const prefersDark = useMediaQuery("(prefers-color-scheme: dark)");
  const [stored, setStored] = useState<ThemeMode | null>(() => {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    return raw === "light" || raw === "dark" ? raw : null;
  });

  const mode: ThemeMode = stored ?? (prefersDark ? "dark" : "light");

  useEffect(() => {
    if (stored) {
      window.localStorage.setItem(STORAGE_KEY, stored);
    }
  }, [stored]);

  const theme = useMemo(() => {
    // The attribute must be on <html> before the tokens are read, so the dark block applies.
    applyBrandMode(mode);
    return createTheme({
      palette: {
        mode,
        primary: {
          main: brandToken("--sf-primary"),
          light: brandToken("--sf-primary-light"),
          dark: brandToken("--sf-primary-dark"),
          contrastText: brandToken("--sf-primary-contrast"),
        },
        secondary: {
          main: brandToken("--sf-secondary"),
          light: brandToken("--sf-secondary-light"),
          dark: brandToken("--sf-secondary-dark"),
          contrastText: brandToken("--sf-secondary-contrast"),
        },
        info: {
          main: brandToken("--sf-info"),
          light: brandToken("--sf-info-light"),
          dark: brandToken("--sf-info-dark"),
        },
        success: {
          main: brandToken("--sf-success"),
          light: brandToken("--sf-success-light"),
          dark: brandToken("--sf-success-dark"),
        },
        warning: {
          main: brandToken("--sf-warning"),
          light: brandToken("--sf-warning-light"),
          dark: brandToken("--sf-warning-dark"),
        },
        error: {
          main: brandToken("--sf-danger"),
          light: brandToken("--sf-danger-light"),
          dark: brandToken("--sf-danger-dark"),
        },
        background: {
          default: brandToken("--sf-background"),
          paper: brandToken("--sf-paper"),
        },
        text: {
          primary: brandToken("--sf-text-primary"),
          secondary: brandToken("--sf-text-secondary"),
        },
        divider: brandToken("--sf-divider"),
      },
      shape: { borderRadius: 8 },
      components: {
        MuiTableCell: { styleOverrides: { root: { whiteSpace: "nowrap" } } },
        MuiAppBar: {
          styleOverrides: {
            root: mode === "light"
              ? { backgroundColor: brandToken("--sf-primary") }
              : { backgroundColor: brandToken("--sf-paper"), color: brandToken("--sf-text-primary") },
          },
        },
      },
    });
  }, [mode]);

  const value = useMemo<ThemeModeValue>(() => ({
    mode,
    toggle: () => setStored(mode === "dark" ? "light" : "dark"),
  }), [mode]);

  return (
    <ThemeModeContext.Provider value={value}>
      <ThemeProvider theme={theme}>
        <CssBaseline />
        {children}
      </ThemeProvider>
    </ThemeModeContext.Provider>
  );
}

export function useThemeMode(): ThemeModeValue {
  const context = useContext(ThemeModeContext);
  if (!context) {
    throw new Error("useThemeMode must be used inside ThemeModeProvider.");
  }

  return context;
}
