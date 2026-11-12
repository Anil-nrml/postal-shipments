import { Component, type ErrorInfo, type ReactNode } from 'react';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Stack,
  Typography,
} from '@mui/material';
import { errorMessage } from './api';
import { statusLabels, statusTone } from './domain';
import type { ShipmentStatus } from './types';

export function StatusChip({ status, size = 'small' }: { status: ShipmentStatus; size?: 'small' | 'medium' }) {
  const tone = statusTone[status];
  return (
    <Chip
      size={size}
      label={statusLabels[status]}
      color={tone === 'default' ? 'default' : tone}
      variant={status === 'Delivered' || status === 'Lost' ? 'filled' : 'outlined'}
    />
  );
}

export function PageHeader({ title, subtitle, actions }: { title: string; subtitle?: string; actions?: ReactNode }) {
  return (
    <Stack
      direction={{ xs: 'column', sm: 'row' }}
      justifyContent="space-between"
      alignItems={{ xs: 'flex-start', sm: 'center' }}
      spacing={2}
      sx={{ mb: 3 }}
    >
      <Box>
        <Typography variant="h5" component="h1">
          {title}
        </Typography>
        {subtitle && (
          <Typography variant="body2" color="text.secondary">
            {subtitle}
          </Typography>
        )}
      </Box>
      {actions && <Stack direction="row" spacing={1}>{actions}</Stack>}
    </Stack>
  );
}

export function LoadingState({ label = 'Loading…' }: { label?: string }) {
  return (
    <Stack alignItems="center" spacing={2} sx={{ py: 6 }} role="status" aria-live="polite">
      <CircularProgress size={32} />
      <Typography color="text.secondary">{label}</Typography>
    </Stack>
  );
}

export function EmptyState({ title, description, action }: { title: string; description?: string; action?: ReactNode }) {
  return (
    <Stack alignItems="center" spacing={1} sx={{ py: 6, textAlign: 'center' }}>
      <Typography variant="h6">{title}</Typography>
      {description && <Typography color="text.secondary">{description}</Typography>}
      {action}
    </Stack>
  );
}

export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  return (
    <Alert
      severity="error"
      action={
        onRetry && (
          <Button color="inherit" size="small" onClick={onRetry}>
            Retry
          </Button>
        )
      }
    >
      {errorMessage(error)}
    </Alert>
  );
}

export function ConfirmDialog({
  open,
  title,
  message,
  confirmLabel = 'Confirm',
  destructive,
  busy,
  onConfirm,
  onClose,
}: {
  open: boolean;
  title: string;
  message: ReactNode;
  confirmLabel?: string;
  destructive?: boolean;
  busy?: boolean;
  onConfirm: () => void;
  onClose: () => void;
}) {
  return (
    <Dialog open={open} onClose={busy ? undefined : onClose} aria-labelledby="confirm-title">
      <DialogTitle id="confirm-title">{title}</DialogTitle>
      <DialogContent>
        <DialogContentText component="div">{message}</DialogContentText>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>
          Cancel
        </Button>
        <Button onClick={onConfirm} color={destructive ? 'error' : 'primary'} variant="contained" disabled={busy}>
          {confirmLabel}
        </Button>
      </DialogActions>
    </Dialog>
  );
}

/**
 * Isolates failures: an exception inside one micro-frontend (or a remote that fails to load)
 * renders this fallback instead of taking down the shell.
 */
export class ErrorBoundary extends Component<
  { children: ReactNode; name?: string; fallback?: ReactNode },
  { error: Error | null }
> {
  override state = { error: null as Error | null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  override componentDidCatch(error: Error, info: ErrorInfo) {
    console.error(`[${this.props.name ?? 'app'}] render error`, error, info.componentStack);
  }

  override render() {
    if (!this.state.error) return this.props.children;
    if (this.props.fallback) return this.props.fallback;
    return (
      <Alert
        severity="error"
        sx={{ m: 2 }}
        action={
          <Button color="inherit" size="small" onClick={() => this.setState({ error: null })}>
            Try again
          </Button>
        }
      >
        <AlertTitle>{this.props.name ? `${this.props.name} is unavailable` : 'Something went wrong'}</AlertTitle>
        The rest of the application keeps working. Refresh the page or try again in a moment.
      </Alert>
    );
  }
}
