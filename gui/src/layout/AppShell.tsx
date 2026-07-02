import { useState, type FormEvent } from "react";
import { Outlet, useNavigate } from "react-router-dom";
import Alert from "@mui/material/Alert";
import AppBar from "@mui/material/AppBar";
import Avatar from "@mui/material/Avatar";
import Box from "@mui/material/Box";
import Divider from "@mui/material/Divider";
import Drawer from "@mui/material/Drawer";
import IconButton from "@mui/material/IconButton";
import InputBase from "@mui/material/InputBase";
import Menu from "@mui/material/Menu";
import MenuItem from "@mui/material/MenuItem";
import Toolbar from "@mui/material/Toolbar";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import useMediaQuery from "@mui/material/useMediaQuery";
import { alpha, useTheme } from "@mui/material/styles";
import MenuIcon from "@mui/icons-material/Menu";
import SearchIcon from "@mui/icons-material/Search";
import DarkModeIcon from "@mui/icons-material/DarkMode";
import LightModeIcon from "@mui/icons-material/LightMode";
import { useAuth } from "../auth/AuthContext";
import { useRateLimitPause } from "../hooks/usePolling";
import { useThemeMode } from "../theme/ThemeModeContext";
import { SidebarNav } from "./SidebarNav";

const drawerWidth = 240;

/**
 * The application frame: top bar (global search, theme toggle, account menu), the grouped sidebar, the passive
 * rate-limit banner, and the routed page as the content.
 */
export default function AppShell() {
  const { session, logout } = useAuth();
  const { mode, toggle } = useThemeMode();
  const navigate = useNavigate();
  const theme = useTheme();
  const isDesktop = useMediaQuery(theme.breakpoints.up("md"));
  const [mobileOpen, setMobileOpen] = useState(false);
  const [accountAnchor, setAccountAnchor] = useState<HTMLElement | null>(null);
  const [searchTerm, setSearchTerm] = useState("");
  const rateLimitedUntil = useRateLimitPause();

  const submitSearch = (event: FormEvent) => {
    event.preventDefault();
    const term = searchTerm.trim();
    if (term !== "") {
      navigate(`/search?q=${encodeURIComponent(term)}`);
    }
  };

  const drawerContent = (
    <Box sx={{ overflowY: "auto" }}>
      <Toolbar>
        <Typography variant="h6" sx={{ fontWeight: 700 }}>SQLFlow</Typography>
      </Toolbar>
      <Divider />
      <SidebarNav onNavigate={isDesktop ? undefined : () => setMobileOpen(false)} />
    </Box>
  );

  return (
    <Box sx={{ display: "flex", minHeight: "100vh" }}>
      <AppBar position="fixed" sx={{ zIndex: theme.zIndex.drawer + 1 }} data-testid="app-bar">
        <Toolbar sx={{ gap: 2 }}>
          {!isDesktop && (
            <IconButton color="inherit" edge="start" aria-label="Open navigation" onClick={() => setMobileOpen(true)}>
              <MenuIcon />
            </IconButton>
          )}
          <Typography variant="h6" noWrap sx={{ display: { xs: "none", sm: "block" } }}>
            Control plane
          </Typography>
          <Box
            component="form"
            onSubmit={submitSearch}
            sx={{
              display: "flex",
              alignItems: "center",
              px: 1.5,
              borderRadius: 1,
              bgcolor: alpha(theme.palette.common.white, 0.15),
              "&:hover": { bgcolor: alpha(theme.palette.common.white, 0.25) },
              flexGrow: 1,
              maxWidth: 480,
            }}
          >
            <SearchIcon fontSize="small" />
            <InputBase
              placeholder="Search objects, columns, definitions"
              value={searchTerm}
              onChange={(e) => setSearchTerm(e.target.value)}
              sx={{ ml: 1, color: "inherit", width: "100%" }}
              inputProps={{ "data-testid": "global-search" }}
            />
          </Box>
          <Box sx={{ flexGrow: 1 }} />
          <Tooltip title={mode === "dark" ? "Switch to light mode" : "Switch to dark mode"}>
            <IconButton color="inherit" onClick={toggle} data-testid="theme-toggle" aria-label="Toggle theme">
              {mode === "dark" ? <LightModeIcon /> : <DarkModeIcon />}
            </IconButton>
          </Tooltip>
          <Tooltip title={session ? `${session.subject}${session.role ? ` (${session.role})` : ""}` : ""}>
            <IconButton onClick={(e) => setAccountAnchor(e.currentTarget)} data-testid="account-menu-button" aria-label="Account">
              <Avatar sx={{ width: 32, height: 32 }}>
                {(session?.subject ?? "?").slice(0, 1).toUpperCase()}
              </Avatar>
            </IconButton>
          </Tooltip>
          <Menu anchorEl={accountAnchor} open={Boolean(accountAnchor)} onClose={() => setAccountAnchor(null)}>
            <MenuItem disabled data-testid="account-subject">
              {session?.subject}{session?.role ? ` (${session.role})` : ""}
            </MenuItem>
            <Divider />
            <MenuItem
              onClick={() => {
                setAccountAnchor(null);
                logout();
              }}
              data-testid="account-logout"
            >
              Sign out
            </MenuItem>
          </Menu>
        </Toolbar>
      </AppBar>

      {isDesktop ? (
        <Drawer
          variant="permanent"
          sx={{ width: drawerWidth, flexShrink: 0, [`& .MuiDrawer-paper`]: { width: drawerWidth, boxSizing: "border-box" } }}
        >
          {drawerContent}
        </Drawer>
      ) : (
        <Drawer
          variant="temporary"
          open={mobileOpen}
          onClose={() => setMobileOpen(false)}
          sx={{ [`& .MuiDrawer-paper`]: { width: drawerWidth } }}
        >
          {drawerContent}
        </Drawer>
      )}

      <Box component="main" sx={{ flexGrow: 1, p: 3, minWidth: 0 }}>
        <Toolbar />
        {rateLimitedUntil !== null && (
          <Alert severity="warning" sx={{ mb: 2 }} data-testid="rate-limit-banner">
            The API rate limit was reached; live updates resume shortly.
          </Alert>
        )}
        <Outlet />
      </Box>
    </Box>
  );
}
