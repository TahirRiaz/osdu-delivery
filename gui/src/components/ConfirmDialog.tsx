import Button from "@mui/material/Button";
import Dialog from "@mui/material/Dialog";
import DialogActions from "@mui/material/DialogActions";
import DialogContent from "@mui/material/DialogContent";
import DialogContentText from "@mui/material/DialogContentText";
import DialogTitle from "@mui/material/DialogTitle";

interface ConfirmDialogProps {
  open: boolean;
  title: string;
  message: string;
  confirmLabel: string;
  danger?: boolean;
  busy?: boolean;
  onConfirm: () => void;
  onClose: () => void;
}

/** The one confirmation dialog for destructive/irreversible actions (cancel run, delete schedule, deactivate). */
export function ConfirmDialog({ open, title, message, confirmLabel, danger, busy, onConfirm, onClose }: ConfirmDialogProps) {
  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} data-testid="confirm-dialog">
      <DialogTitle>{title}</DialogTitle>
      <DialogContent>
        <DialogContentText>{message}</DialogContentText>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy} data-testid="confirm-dialog-cancel">Cancel</Button>
        <Button
          onClick={onConfirm}
          color={danger ? "error" : "primary"}
          variant="contained"
          disabled={busy}
          data-testid="confirm-dialog-confirm"
        >
          {confirmLabel}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
