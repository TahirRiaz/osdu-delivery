import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { Menu, Moon, Search, Sun } from "lucide-react";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Sheet, SheetContent, SheetTitle, SheetTrigger } from "@/components/ui/sheet";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { useAuth } from "../../auth/AuthContext";
import { useThemeMode } from "../../theme/ThemeModeContext";
import { SideBarSections } from "./SideBar";

/**
 * The workbench title bar (DESIGN.md section 6): brand on the left, the centered global search, theme
 * toggle and account menu on the right. On mobile, a hamburger opens the navigation sheet.
 */
export function TitleBar({ onOpenPalette }: { onOpenPalette: () => void }) {
  const { session, logout } = useAuth();
  const { mode, toggle } = useThemeMode();
  const navigate = useNavigate();
  const [searchTerm, setSearchTerm] = useState("");
  const [mobileNavOpen, setMobileNavOpen] = useState(false);

  const submitSearch = (event: FormEvent) => {
    event.preventDefault();
    const term = searchTerm.trim();
    if (term !== "") {
      navigate(`/search?q=${encodeURIComponent(term)}`);
    }
  };

  return (
    <header
      data-testid="app-bar"
      className="flex h-9 shrink-0 select-none items-center gap-2 bg-activity-bar px-2 text-white"
    >
      <Sheet open={mobileNavOpen} onOpenChange={setMobileNavOpen}>
        <SheetTrigger
          aria-label="Open navigation"
          className="flex size-7 items-center justify-center rounded-md text-white/80 hover:bg-white/10 hover:text-white md:hidden"
        >
          <Menu className="size-4" />
        </SheetTrigger>
        <SheetContent side="left" className="w-72 gap-0 bg-sidebar p-0">
          <SheetTitle className="sr-only">Navigation</SheetTitle>
          <nav aria-label="Main navigation" className="h-full overflow-y-auto py-1">
            <SideBarSections reveal={null} onNavigate={() => setMobileNavOpen(false)} />
          </nav>
        </SheetContent>
      </Sheet>

      <div className="flex items-center gap-2 pl-1">
        <img src="/brand/logo-white.png" alt="" className="size-5 object-contain" />
        <span className="hidden text-[13px] font-semibold sm:block">SQLFlow</span>
      </div>

      <form
        onSubmit={submitSearch}
        className="mx-auto flex h-6 w-full max-w-xl items-center gap-1.5 rounded-md bg-white/10 px-2 transition-colors focus-within:bg-white/15 hover:bg-white/15"
      >
        <Search className="size-3.5 shrink-0 opacity-60" />
        <input
          data-testid="global-search"
          value={searchTerm}
          onChange={(event) => setSearchTerm(event.target.value)}
          placeholder="Search objects, columns, definitions"
          className="w-full bg-transparent text-xs text-white outline-none placeholder:text-white/50"
        />
        <button
          type="button"
          onClick={onOpenPalette}
          aria-label="Open command palette"
          className="hidden shrink-0 items-center rounded-sm border border-white/20 px-1 font-mono text-[10px] leading-4 text-white/60 hover:border-white/40 hover:text-white sm:flex"
        >
          Ctrl K
        </button>
      </form>

      <Tooltip>
        <TooltipTrigger asChild>
          <button
            data-testid="theme-toggle"
            aria-label="Toggle theme"
            onClick={toggle}
            className="flex size-7 shrink-0 items-center justify-center rounded-md text-white/80 hover:bg-white/10 hover:text-white"
          >
            {mode === "dark" ? <Sun className="size-4" /> : <Moon className="size-4" />}
          </button>
        </TooltipTrigger>
        <TooltipContent side="bottom">
          {mode === "dark" ? "Switch to light mode" : "Switch to dark mode"}
        </TooltipContent>
      </Tooltip>

      <DropdownMenu>
        <DropdownMenuTrigger
          data-testid="account-menu-button"
          aria-label="Account"
          className="flex size-7 shrink-0 items-center justify-center rounded-md hover:bg-white/10"
        >
          <Avatar className="size-6">
            <AvatarFallback className="bg-primary text-[11px] font-semibold text-primary-foreground">
              {(session?.subject ?? "?").slice(0, 1).toUpperCase()}
            </AvatarFallback>
          </Avatar>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-56">
          <DropdownMenuLabel data-testid="account-subject" className="font-normal text-muted-foreground">
            {session?.subject}
            {session?.role ? ` (${session.role})` : ""}
          </DropdownMenuLabel>
          <DropdownMenuSeparator />
          <DropdownMenuItem data-testid="account-tokens" onSelect={() => navigate("/settings/tokens")}>
            Personal access tokens
          </DropdownMenuItem>
          <DropdownMenuItem data-testid="account-notifications" onSelect={() => navigate("/settings/notifications")}>
            Notification settings
          </DropdownMenuItem>
          <DropdownMenuSeparator />
          <DropdownMenuItem data-testid="account-logout" onSelect={() => logout()}>
            Sign out
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </header>
  );
}
