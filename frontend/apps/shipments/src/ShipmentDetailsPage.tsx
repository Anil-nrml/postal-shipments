import { useState } from 'react';
import { Link as RouterLink, useNavigate, useParams } from 'react-router-dom';
import {
  Alert,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Grid2 as Grid,
  MenuItem,
  Paper,
  Stack,
  Step,
  StepContent,
  StepLabel,
  Stepper,
  TextField,
  Typography,
} from '@mui/material';
import {
  allStatuses,
  ApiError,
  ConfirmDialog,
  ErrorState,
  errorMessage,
  formatDateTime,
  formatPostOffice,
  formatWeight,
  LoadingState,
  PageHeader,
  Permissions,
  RequirePermission,
  type Shipment,
  type ShipmentHistoryEntry,
  type ShipmentStatus,
  shipmentTypeLabels,
  statusLabels,
  StatusChip,
  suggestedZipFor,
  usePermission,
  weightCategoryLabels,
} from '@postal/shared';
import { BASE_PATH, useChangeStatus, useCorrectStatus, useDeleteShipment, useShipment, useShipmentHistory } from './api';

function Field({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <Grid size={{ xs: 12, sm: 6, md: 4 }}>
      <Typography variant="caption" color="text.secondary">
        {label}
      </Typography>
      <Typography component="div">{value}</Typography>
    </Grid>
  );
}

/** Timeline shared in spirit with the tracking MFE but owned here (no cross-MFE imports). */
export function HistoryTimeline({ history }: { history: ShipmentHistoryEntry[] }) {
  return (
    <Stepper orientation="vertical" activeStep={history.length - 1} nonLinear>
      {history.map((h) => (
        <Step key={h.id} completed expanded>
          <StepLabel optional={<Typography variant="caption">{formatDateTime(h.occurredAtUtc)}</Typography>}>
            <Stack direction="row" spacing={1} alignItems="center">
              <span>{statusLabels[h.status]}</span>
              {h.isCorrection && <Chip size="small" color="warning" label="Correction" />}
            </Stack>
          </StepLabel>
          <StepContent>
            <Typography variant="body2">
              {formatPostOffice(h.postOffice)} · by {h.performedBy}
            </Typography>
            {h.remarks && (
              <Typography variant="body2" color="text.secondary">
                “{h.remarks}”
              </Typography>
            )}
          </StepContent>
        </Step>
      ))}
    </Stepper>
  );
}

function StatusDialog({
  shipment,
  mode,
  onClose,
}: {
  shipment: Shipment;
  mode: 'next' | 'correction';
  onClose: () => void;
}) {
  const choices: ShipmentStatus[] =
    allStatuses.filter((s) => s !== shipment.status);
  const [status, setStatus] = useState<ShipmentStatus | ''>(choices[0] ?? '');
  const [zip, setZip] = useState(choices[0] ? suggestedZipFor(choices[0], shipment) : shipment.current.zipCode);
  const [text, setText] = useState('');
  const change = useChangeStatus(shipment.id);
  const correct = useCorrectStatus(shipment.id);
  const mutation = mode === 'next' ? change : correct;
  const offices = Array.from(
    new Map([shipment.origin, shipment.destination, shipment.current].map((p) => [p.zipCode, p])).values(),
  );
  const reasonTooShort = mode === 'correction' && text.trim().length < 10;

  const submit = () => {
    if (!status) return;
    const done = { onSuccess: onClose };
    if (mode === 'next')
      change.mutate({ status, postOfficeZip: zip, remarks: text || undefined, version: shipment.version }, done);
    else correct.mutate({ status, postOfficeZip: zip, reason: text, version: shipment.version }, done);
  };

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="xs">
      <DialogTitle>{mode === 'next' ? 'Record status' : 'Correct status'}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          {mode === 'correction' && (
            <Alert severity="warning">Corrections bypass the normal lifecycle and are flagged in the audit trail.</Alert>
          )}
          {mutation.isError && (
            <Alert severity={mutation.error instanceof ApiError && mutation.error.isConflict ? 'warning' : 'error'}>
              {errorMessage(mutation.error)}
            </Alert>
          )}
          <TextField
            select
            label="New status"
            value={status}
            onChange={(e) => {
              const next = e.target.value as ShipmentStatus;
              setStatus(next);
              setZip(suggestedZipFor(next, shipment));
            }}
          >
            {choices.map((s) => (
              <MenuItem key={s} value={s}>
                {statusLabels[s]}
              </MenuItem>
            ))}
          </TextField>
          {mode === 'next' && (
            <Typography variant="caption" color="text.secondary">
              Only valid next steps in the shipment lifecycle will be accepted.
            </Typography>
          )}
          <TextField select label="Recorded at" value={zip} onChange={(e) => setZip(e.target.value)}>
            {offices.map((p) => (
              <MenuItem key={p.zipCode} value={p.zipCode}>
                {formatPostOffice(p)}
              </MenuItem>
            ))}
          </TextField>
          <TextField
            label={mode === 'next' ? 'Remarks (optional)' : 'Reason (required)'}
            multiline
            minRows={2}
            value={text}
            onChange={(e) => setText(e.target.value)}
            inputProps={{ maxLength: 500 }}
            error={mode === 'correction' && text.length > 0 && reasonTooShort}
            helperText={mode === 'correction' ? 'At least 10 characters' : undefined}
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="contained" onClick={submit} disabled={!status || mutation.isPending || reasonTooShort}>
          Save
        </Button>
      </DialogActions>
    </Dialog>
  );
}

export default function ShipmentDetailsPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const shipment = useShipment(id);
  const history = useShipmentHistory(id);
  const remove = useDeleteShipment();
  const [dialog, setDialog] = useState<'next' | 'correction' | 'delete' | null>(null);
  const canUpdate = usePermission(Permissions.ShipmentsUpdateStatus);

  if (shipment.isLoading) return <LoadingState />;
  if (shipment.isError) return <ErrorState error={shipment.error} onRetry={() => shipment.refetch()} />;
  const s = shipment.data!;
  const terminal = s.allowedNextStatuses.length === 0;

  return (
    <>
      <PageHeader
        title={s.shipmentNumber}
        subtitle={`${shipmentTypeLabels[s.type]} · ${formatWeight(s.weightKg)} · updated ${formatDateTime(s.updatedAtUtc)}`}
        actions={
          <>
            {canUpdate && !terminal && (
              <Button variant="contained" onClick={() => setDialog('next')}>
                Update status
              </Button>
            )}
            <RequirePermission permission={Permissions.ShipmentsCorrectStatus}>
              <Button color="warning" onClick={() => setDialog('correction')}>
                Correct
              </Button>
            </RequirePermission>
            {s.canEdit && (
              <RequirePermission permission={Permissions.ShipmentsWrite}>
                <Button component={RouterLink} to={`${BASE_PATH}/${s.id}/edit`}>
                  Edit
                </Button>
              </RequirePermission>
            )}
            {s.canEdit && (
              <RequirePermission permission={Permissions.ShipmentsDelete}>
                <Button color="error" onClick={() => setDialog('delete')}>
                  Delete
                </Button>
              </RequirePermission>
            )}
          </>
        }
      />

      <Grid container spacing={2}>
        <Grid size={{ xs: 12, md: 7 }}>
          <Paper sx={{ p: 3 }}>
            <Grid container spacing={2}>
              <Field label="Status" value={<StatusChip status={s.status} size="medium" />} />
              <Field label="Weight category" value={weightCategoryLabels[s.weightCategory]} />
              <Field label="Delivery" value={s.isLocalDelivery ? 'Local' : 'Inter-office'} />
              <Field label="Origin" value={formatPostOffice(s.origin)} />
              <Field label="Destination" value={formatPostOffice(s.destination)} />
              <Field label="Currently at" value={formatPostOffice(s.current)} />
              <Field label="Created" value={formatDateTime(s.createdAtUtc)} />
              <Field label="Version" value={s.version} />
            </Grid>
          </Paper>
        </Grid>
        <Grid size={{ xs: 12, md: 5 }}>
          <Paper sx={{ p: 3 }}>
            <Typography variant="h6" gutterBottom>
              Tracking history
            </Typography>
            {history.isLoading && <LoadingState />}
            {history.isError && <ErrorState error={history.error} />}
            {history.data && <HistoryTimeline history={history.data} />}
          </Paper>
        </Grid>
      </Grid>

      {(dialog === 'next' || dialog === 'correction') && (
        <StatusDialog
          shipment={s}
          mode={dialog}
          onClose={() => {
            setDialog(null);
            void history.refetch();
          }}
        />
      )}
      <ConfirmDialog
        open={dialog === 'delete'}
        title="Delete shipment?"
        message={
          <>
            {s.shipmentNumber} will be removed permanently.
            {remove.isError && <Alert severity="error" sx={{ mt: 2 }}>{errorMessage(remove.error)}</Alert>}
          </>
        }
        confirmLabel="Delete"
        destructive
        busy={remove.isPending}
        onClose={() => setDialog(null)}
        onConfirm={() => remove.mutate(s, { onSuccess: () => navigate(BASE_PATH) })}
      />
    </>
  );
}
