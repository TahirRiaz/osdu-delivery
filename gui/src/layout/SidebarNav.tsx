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
import SyncIcon from "@mui/icons-material/Sync";
import HubIcon from "@mui/icons-material/Hub";
import SearchIcon from "@mui/icons-material/Search";
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

const catalog: NavItem[] = [
  { label: "Repos", to: "/repos", icon: <FolderIcon />, testId: "nav-repos" },
  { label: "Pipelines", to: "/pipelines", icon: <AccountTreeIcon />, testId: "nav-pipelines" },
  { label: "Schedules", to: "/schedules", icon: <ScheduleIcon />, testId: "nav-schedules" },
  { label: "Repo sources", to: "/repo-sources", icon: <SyncIcon />, testId: "nav-repo-sources" },
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

  const renderGroup = (title: string, items: NavItem[]) => (
    <List
      key={title}
      dense
      subheader={<ListSubheader component="div" disableSticky>{title}</ListSubheader>}
    >
      {items.map((item) => {
        const selected = item.to === "/" ? location.pathname === "/" : location.pathname.startsWith(item.to);
        return (
          <ListItemButton
            key={item.to}
            selected={selected}
            onClick={() => {
              navigate(item.to);
              onNavigate?.();
            }}
            data-testid={item.testId}
          >
            <ListItemIcon sx={{ minWidth: 36 }}>{item.icon}</ListItemIcon>
            <ListItemText primary={item.label} />
          </ListItemButton>
        );
      })}
    </List>
  );

  return (
    <nav aria-label="Main navigation">
      {renderGroup("Operate", operate)}
      {renderGroup("Catalog", catalog)}
      {renderGroup("Explore", explore)}
      {hasScope("admin") && renderGroup("Admin", admin)}
    </nav>
  );
}
