import List from "@mui/material/List";
import ListItemButton from "@mui/material/ListItemButton";
import ListItemIcon from "@mui/material/ListItemIcon";
import ListItemText from "@mui/material/ListItemText";
import ListSubheader from "@mui/material/ListSubheader";
import DashboardIcon from "@mui/icons-material/Dashboard";
import PlayCircleOutlineIcon from "@mui/icons-material/PlayCircleOutline";
import DnsIcon from "@mui/icons-material/Dns";
import FolderIcon from "@mui/icons-material/Folder";
import AccountTreeIcon from "@mui/icons-material/AccountTree";
import ScheduleIcon from "@mui/icons-material/Schedule";
import TimelineIcon from "@mui/icons-material/Timeline";
import StorageIcon from "@mui/icons-material/Storage";
import CableIcon from "@mui/icons-material/Cable";
import VpnKeyIcon from "@mui/icons-material/VpnKey";
import HubIcon from "@mui/icons-material/Hub";
import SearchIcon from "@mui/icons-material/Search";
import TravelExploreIcon from "@mui/icons-material/TravelExplore";
import PeopleIcon from "@mui/icons-material/People";
import { useLocation, useNavigate } from "react-router-dom";
import { useAuth } from "../auth/AuthContext";
import type { ReactNode } from "react";

interface NavItem {
  label: string;
  to: string;
  icon: ReactNode;
  testId: string;
}

const operate: NavItem[] = [
  { label: "Dashboard", to: "/", icon: <DashboardIcon />, testId: "nav-dashboard" },
  { label: "Runs", to: "/runs", icon: <PlayCircleOutlineIcon />, testId: "nav-runs" },
  { label: "Nodes", to: "/nodes", icon: <DnsIcon />, testId: "nav-nodes" },
];

const workspace: NavItem[] = [
  { label: "Repos", to: "/repos", icon: <FolderIcon />, testId: "nav-repos" },
  { label: "Pipelines", to: "/pipelines", icon: <AccountTreeIcon />, testId: "nav-pipelines" },
  { label: "Schedules", to: "/schedules", icon: <ScheduleIcon />, testId: "nav-schedules" },
  { label: "Timeline", to: "/schedules/timeline", icon: <TimelineIcon />, testId: "nav-schedule-timeline" },
  { label: "Datasources", to: "/datasources", icon: <StorageIcon />, testId: "nav-datasources" },
];

const tools: NavItem[] = [
  { label: "Discover", to: "/discover", icon: <TravelExploreIcon />, testId: "nav-discover" },
  { label: "Integrations", to: "/integrations", icon: <CableIcon />, testId: "nav-integrations" },
  { label: "Key detection", to: "/key-detection", icon: <VpnKeyIcon />, testId: "nav-key-detection" },
];

const explore: NavItem[] = [
  { label: "Lineage", to: "/lineage", icon: <HubIcon />, testId: "nav-lineage" },
  { label: "Search", to: "/search", icon: <SearchIcon />, testId: "nav-search" },
];

const admin: NavItem[] = [
  { label: "Users", to: "/users", icon: <PeopleIcon />, testId: "nav-users" },
];

/** The grouped sidebar. The Admin group renders only for sessions holding the admin scope. */
export function SidebarNav({ onNavigate }: { onNavigate?: () => void }) {
  const { hasScope } = useAuth();
  const location = useLocation();
  const navigate = useNavigate();

  // Exactly one item highlights: the one whose path is the longest prefix of the current location. This lets a
  // nested route (e.g. /schedules/timeline) own the highlight instead of also lighting up its parent (/schedules).
  const allItems = [...operate, ...workspace, ...tools, ...explore, ...admin];
  const selectedTo = allItems.reduce((best, item) => {
    const matches = item.to === "/" ? location.pathname === "/" : location.pathname.startsWith(item.to);
    return matches && item.to.length > best.length ? item.to : best;
  }, "");

  // The sidebar wears the brand navy (see branding.css chrome tokens); every piece inherits its light text so
  // MUI's paper-oriented defaults never bleed through.
  const renderGroup = (title: string, items: NavItem[]) => (
    <List
      key={title}
      dense
      subheader={(
        <ListSubheader
          component="div"
          disableSticky
          sx={{
            bgcolor: "transparent",
            color: "var(--sf-sidenav-muted)",
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: "0.08em",
            textTransform: "uppercase",
            lineHeight: "32px",
          }}
        >
          {title}
        </ListSubheader>
      )}
    >
      {items.map((item) => {
        const selected = item.to === selectedTo;
        return (
          <ListItemButton
            key={item.to}
            selected={selected}
            onClick={() => {
              navigate(item.to);
              onNavigate?.();
            }}
            data-testid={item.testId}
            sx={{
              mx: 1,
              my: 0.25,
              borderRadius: 1.5,
              color: "var(--sf-sidenav-text)",
              "&:hover": { bgcolor: "rgba(255, 255, 255, 0.06)" },
              "&.Mui-selected": {
                bgcolor: "var(--sf-sidenav-selected)",
                boxShadow: "inset 3px 0 0 var(--sf-sidenav-accent)",
                "&:hover": { bgcolor: "var(--sf-sidenav-selected)" },
              },
            }}
          >
            <ListItemIcon sx={{ minWidth: 36, color: "inherit", opacity: selected ? 1 : 0.8 }}>
              {item.icon}
            </ListItemIcon>
            <ListItemText
              primary={item.label}
              primaryTypographyProps={{ fontWeight: selected ? 600 : 500 }}
            />
          </ListItemButton>
        );
      })}
    </List>
  );

  return (
    <nav aria-label="Main navigation">
      {renderGroup("Operate", operate)}
      {renderGroup("Workspace", workspace)}
      {renderGroup("Tools", tools)}
      {renderGroup("Explore", explore)}
      {hasScope("admin") && renderGroup("Admin", admin)}
    </nav>
  );
}
