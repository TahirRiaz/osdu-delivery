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

const fontStack = [
  "'Inter Variable'",
  "Inter",
  "-apple-system",
  "BlinkMacSystemFont",
  "'Segoe UI'",
  "Roboto",
  "sans-serif",
].join(", ");

/**
 * Light/dark theming: the OS preference is the default, the user's explicit choice (persisted) wins. The MUI
 * theme is built from the SQLFlow branding tokens in branding.css (the logo's navy/cream identity plus the
 * previous GUI's accent palette), so MUI components and plain-CSS surfaces share one palette.
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
      typography: {
        fontFamily: fontStack,
        h5: { fontWeight: 650, letterSpacing: "-0.01em" },
        h6: { fontWeight: 600, letterSpacing: "-0.01em" },
        subtitle1: { fontWeight: 600 },
        button: { fontWeight: 600 },
      },
      components: {
        MuiButton: {
          styleOverrides: {
            // Sentence-case buttons read as a product, not a spec sheet.
            root: { textTransform: "none", borderRadius: 8 },
          },
        },
        MuiToggleButton: {
          styleOverrides: { root: { textTransform: "none" } },
        },
        MuiTab: {
          styleOverrides: { root: { textTransform: "none", fontWeight: 600 } },
        },
        MuiChip: {
          styleOverrides: { root: { fontWeight: 500 } },
        },
        MuiTableCell: {
          styleOverrides: {
            root: { whiteSpace: "nowrap" },
            head: { fontWeight: 650, color: brandToken("--sf-text-secondary") },
          },
        },
        MuiCard: {
          styleOverrides: {
            root: { backgroundImage: "none" },
          },
        },
        MuiAppBar: {
          styleOverrides: {
            root: {
              backgroundColor: brandToken("--sf-appbar"),
              color: brandToken("--sf-appbar-text"),
              backgroundImage: "none",
              boxShadow: "0 1px 0 rgba(0, 0, 0, 0.25)",
            },
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
