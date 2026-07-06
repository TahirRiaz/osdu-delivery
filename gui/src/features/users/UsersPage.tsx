import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import IconButton from "@mui/material/IconButton";
import Menu from "@mui/material/Menu";
import MenuItem from "@mui/material/MenuItem";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Tooltip from "@mui/material/Tooltip";
import Typography from "@mui/material/Typography";
import MoreVertIcon from "@mui/icons-material/MoreVert";
import { isApiError } from "../../api/client";
import { userApi } from "../../api/endpoints";
import type { Role, User } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { FilterBar } from "../../components/FilterBar";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { RelativeTime } from "../../components/RelativeTime";
import { ActiveBadge } from "../../components/StatusBadge";

const MIN_PASSWORD_LENGTH = 12;

/** The role options for a select: the server's roles, plus the current value so an unknown role still shows. */
function roleNames(roles: Role[], current?: string): string[] {
  const names = roles.map((role) => role.name);
  if (current !== undefined && !names.includes(current)) {
    names.push(current);
  }

  return names;
}

function ChangeRoleDialog({ user, roles, onClose }: { user: User; roles: Role[]; onClose: () => void }) {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [role, setRole] = useState(user.role);

  const setRoleMutation = useMutation({
    mutationFn: () => userApi.setRole(user.id, role),
    onSuccess: () => {
      enqueueSnackbar(`Role updated for ${user.username}.`, { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["users"] });
      onClose();
    },
    onError: (error) =>
      enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" }),
  });

  return (
    <Dialog open onClose={setRoleMutation.isPending ? undefined : onClose} fullWidth maxWidth="xs" data-testid="user-set-role-dialog">
      <DialogTitle>Change role for {user.username}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <TextField
            select
            label="Role"
            value={role}
            onChange={(e) => setRole(e.target.value)}
            SelectProps={{ native: true }}
            InputLabelProps={{ shrink: true }}
            inputProps={{ "data-testid": "user-set-role-select" }}
          >
            {roleNames(roles, user.role).map((name) => (
              <option key={name} value={name}>{name}</option>
            ))}
          </TextField>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={setRoleMutation.isPending} data-testid="user-set-role-cancel">Cancel</Button>
        <Button
          variant="contained"
          onClick={() => setRoleMutation.mutate()}
          disabled={setRoleMutation.isPending}
          data-testid="user-set-role-submit"
        >
          Save
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function ResetPasswordDialog({ user, onClose }: { user: User; onClose: () => void }) {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [password, setPassword] = useState("");

  const setPasswordMutation = useMutation({
    mutationFn: () => userApi.setPassword(user.id, password),
    onSuccess: () => {
      enqueueSnackbar(`Password reset for ${user.username}.`, { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["users"] });
      onClose();
    },
    onError: (error) =>
      enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" }),
  });

  const valid = password.length >= MIN_PASSWORD_LENGTH;

  return (
    <Dialog open onClose={setPasswordMutation.isPending ? undefined : onClose} fullWidth maxWidth="xs" data-testid="user-set-password-dialog">
      <DialogTitle>Reset password for {user.username}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <TextField
            label="New password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            error={password.length > 0 && !valid}
            helperText="At least 12 characters"
            inputProps={{ "data-testid": "user-set-password-input" }}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={setPasswordMutation.isPending} data-testid="user-set-password-cancel">Cancel</Button>
        <Button
          variant="contained"
          onClick={() => setPasswordMutation.mutate()}
          disabled={!valid || setPasswordMutation.isPending}
          data-testid="user-set-password-submit"
        >
          Reset
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function CreateUserDialog({ roles, onClose }: { roles: Role[]; onClose: () => void }) {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState("viewer");
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");

  const create = useMutation({
    mutationFn: () => userApi.create({
      username: username.trim(),
      password,
      role,
      email: email.trim() === "" ? null : email.trim(),
      displayName: displayName.trim() === "" ? null : displayName.trim(),
    }),
    onSuccess: (created) => {
      enqueueSnackbar(`User ${created.username} created.`, { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["users"] });
      onClose();
    },
  });

  const passwordValid = password.length >= MIN_PASSWORD_LENGTH;
  const canSubmit = username.trim() !== "" && passwordValid && role !== "" && !create.isPending;

  return (
    <Dialog open onClose={create.isPending ? undefined : onClose} fullWidth maxWidth="sm" data-testid="create-user-dialog">
      <DialogTitle>Create user</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          {create.isError && isApiError(create.error) && <CorrelationError error={create.error} />}
          {create.isError && !isApiError(create.error) && (
            <Typography color="error">{String(create.error)}</Typography>
          )}

          <TextField
            label="Username"
            required
            value={username}
            onChange={(e) => setUsername(e.target.value)}
            inputProps={{ "data-testid": "create-user-username" }}
          />
          <TextField
            label="Password"
            type="password"
            required
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            error={password.length > 0 && !passwordValid}
            helperText="At least 12 characters"
            inputProps={{ "data-testid": "create-user-password" }}
          />
          <TextField
            select
            label="Role"
            value={role}
            onChange={(e) => setRole(e.target.value)}
            SelectProps={{ native: true }}
            InputLabelProps={{ shrink: true }}
            inputProps={{ "data-testid": "create-user-role" }}
          >
            {roleNames(roles, "viewer").map((name) => (
              <option key={name} value={name}>{name}</option>
            ))}
          </TextField>
          <TextField
            label="Email"
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            inputProps={{ "data-testid": "create-user-email" }}
          />
          <TextField
            label="Display name"
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            inputProps={{ "data-testid": "create-user-displayname" }}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={create.isPending} data-testid="create-user-cancel">Cancel</Button>
        <Button
          variant="contained"
          onClick={() => create.mutate()}
          disabled={!canSubmit}
          data-testid="create-user-submit"
        >
          Create
        </Button>
      </DialogActions>
    </Dialog>
  );
}

/** User administration: list/filter accounts, create local users, and manage role, password, and active state. */
export default function UsersPage() {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [usernameInput, setUsernameInput] = useState("");
  const [usernameFilter, setUsernameFilter] = useState("");
  const [roleFilter, setRoleFilter] = useState("");
  const [providerFilter, setProviderFilter] = useState("");
  const [activeFilter, setActiveFilter] = useState("");
  const [createOpen, setCreateOpen] = useState(false);
  const [menuState, setMenuState] = useState<{ anchor: HTMLElement; user: User } | null>(null);
  const [roleDialogUser, setRoleDialogUser] = useState<User | null>(null);
  const [passwordDialogUser, setPasswordDialogUser] = useState<User | null>(null);
  const [deactivateUser, setDeactivateUser] = useState<User | null>(null);

  // The username filter debounces keystrokes so each pause, not each character, costs an API call.
  useEffect(() => {
    const timer = window.setTimeout(() => setUsernameFilter(usernameInput.trim()), 400);
    return () => window.clearTimeout(timer);
  }, [usernameInput]);

  const rolesQuery = useQuery({ queryKey: ["roles"], queryFn: userApi.roles });
  const roles = rolesQuery.data ?? [];

  const showError = (error: unknown) =>
    enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" });

  const activate = useMutation({
    mutationFn: (user: User) => userApi.activate(user.id),
    onSuccess: (updated) => {
      enqueueSnackbar(`User ${updated.username} activated.`, { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["users"] });
    },
    onError: showError,
  });

  const deactivate = useMutation({
    mutationFn: (user: User) => userApi.deactivate(user.id),
    onSuccess: (updated) => {
      enqueueSnackbar(`User ${updated.username} deactivated.`, { variant: "success" });
      void queryClient.invalidateQueries({ queryKey: ["users"] });
      setDeactivateUser(null);
    },
    onError: (error) => {
      showError(error);
      setDeactivateUser(null);
    },
  });

  const closeMenu = () => setMenuState(null);

  const columns: Column<User>[] = [
    {
      id: "username",
      header: "Username",
      render: (row) => <Typography variant="body2" fontWeight={600}>{row.username}</Typography>,
    },
    { id: "displayName", header: "Display name", render: (row) => row.displayName ?? "-" },
    { id: "role", header: "Role", render: (row) => <Chip size="small" label={row.role} /> },
    { id: "provider", header: "Provider", render: (row) => <Chip size="small" label={row.provider} variant="outlined" /> },
    { id: "status", header: "Status", render: (row) => <ActiveBadge active={row.active} /> },
    { id: "lastLogin", header: "Last login", render: (row) => <RelativeTime value={row.lastLoginUtc} /> },
    { id: "created", header: "Created", render: (row) => <RelativeTime value={row.createdUtc} /> },
    {
      id: "actions",
      header: "Actions",
      align: "right",
      render: (row) => (
        <IconButton
          size="small"
          aria-label={`Actions for ${row.username}`}
          onClick={(e) => {
            e.stopPropagation();
            setMenuState({ anchor: e.currentTarget, user: row });
          }}
          data-testid="user-actions"
        >
          <MoreVertIcon fontSize="small" />
        </IconButton>
      ),
    },
  ];

  return (
    <Page data-testid="page-users">
      <PageHeader
        title="Users"
        actions={(
          <Button variant="contained" onClick={() => setCreateOpen(true)} data-testid="open-create-user">
            Create user
          </Button>
        )}
      />

      <FilterBar>
        <TextField
          size="small"
          label="Username"
          placeholder="Filter by username"
          value={usernameInput}
          onChange={(e) => setUsernameInput(e.target.value)}
          inputProps={{ "data-testid": "filter-username" }}
          sx={{ minWidth: 220 }}
        />
        <TextField
          select
          size="small"
          label="Role"
          value={roleFilter}
          onChange={(e) => setRoleFilter(e.target.value)}
          inputProps={{ "data-testid": "filter-role" }}
          sx={{ minWidth: 160 }}
        >
          <MenuItem value="">all</MenuItem>
          {roles.map((role) => (
            <MenuItem key={role.name} value={role.name}>{role.name}</MenuItem>
          ))}
        </TextField>
        <TextField
          select
          size="small"
          label="Provider"
          value={providerFilter}
          onChange={(e) => setProviderFilter(e.target.value)}
          inputProps={{ "data-testid": "filter-provider" }}
          sx={{ minWidth: 140 }}
        >
          <MenuItem value="">all</MenuItem>
          <MenuItem value="local">local</MenuItem>
          <MenuItem value="entra">entra</MenuItem>
        </TextField>
        <TextField
          select
          size="small"
          label="Active"
          value={activeFilter}
          onChange={(e) => setActiveFilter(e.target.value)}
          inputProps={{ "data-testid": "filter-active" }}
          sx={{ minWidth: 140 }}
        >
          <MenuItem value="">all</MenuItem>
          <MenuItem value="active">active</MenuItem>
          <MenuItem value="inactive">inactive</MenuItem>
        </TextField>
      </FilterBar>

      <PagedTable
        queryKey={["users", "list", usernameFilter, roleFilter, providerFilter, activeFilter]}
        fetchPage={(page, pageSize) => userApi.list({
          page,
          pageSize,
          username: usernameFilter === "" ? undefined : usernameFilter,
          role: roleFilter === "" ? undefined : roleFilter,
          provider: providerFilter === "" ? undefined : providerFilter,
          active: activeFilter === "" ? undefined : activeFilter === "active",
        })}
        columns={columns}
        rowKey={(row) => row.id}
        emptyMessage="No users match the current filters."
      />

      {menuState !== null && (
        <Menu anchorEl={menuState.anchor} open onClose={closeMenu} data-testid="user-actions-menu">
          <MenuItem
            onClick={() => {
              setRoleDialogUser(menuState.user);
              closeMenu();
            }}
            data-testid="user-menu-change-role"
          >
            Change role
          </MenuItem>
          {menuState.user.provider === "local" ? (
            <MenuItem
              onClick={() => {
                setPasswordDialogUser(menuState.user);
                closeMenu();
              }}
              data-testid="user-menu-reset-password"
            >
              Reset password
            </MenuItem>
          ) : (
            <Tooltip title="Managed in Microsoft Entra ID">
              <span>
                <MenuItem disabled data-testid="user-menu-reset-password">
                  Reset password
                </MenuItem>
              </span>
            </Tooltip>
          )}
          {menuState.user.active ? (
            <MenuItem
              onClick={() => {
                setDeactivateUser(menuState.user);
                closeMenu();
              }}
              data-testid="user-menu-deactivate"
            >
              Deactivate
            </MenuItem>
          ) : (
            <MenuItem
              onClick={() => {
                activate.mutate(menuState.user);
                closeMenu();
              }}
              disabled={activate.isPending}
              data-testid="user-menu-activate"
            >
              Activate
            </MenuItem>
          )}
        </Menu>
      )}

      {roleDialogUser !== null && (
        <ChangeRoleDialog user={roleDialogUser} roles={roles} onClose={() => setRoleDialogUser(null)} />
      )}
      {passwordDialogUser !== null && (
        <ResetPasswordDialog user={passwordDialogUser} onClose={() => setPasswordDialogUser(null)} />
      )}
      {createOpen && <CreateUserDialog roles={roles} onClose={() => setCreateOpen(false)} />}

      <ConfirmDialog
        open={deactivateUser !== null}
        title="Deactivate user"
        message={`Deactivate ${deactivateUser?.username ?? ""}? They will no longer be able to sign in.`}
        confirmLabel="Deactivate"
        danger
        busy={deactivate.isPending}
        onConfirm={() => {
          if (deactivateUser !== null) {
            deactivate.mutate(deactivateUser);
          }
        }}
        onClose={() => setDeactivateUser(null)}
      />
    </Page>
  );
}
