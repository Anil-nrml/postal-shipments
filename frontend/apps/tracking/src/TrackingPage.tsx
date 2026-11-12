import { useEffect, useState } from 'react';
import { Link as RouterLink, useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Alert,
  Button,
  Chip,
  Grid2 as Grid,
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
  api,
  ApiError,
  ErrorState,
  formatDateTime,
  formatPostOffice,
  LoadingState,
  PageHeader,
  shipmentTypeLabels,
  statusLabels,
  StatusChip,
  type Tracking,
} from '@postal/shared';

const NUMBER_PATTERN = /^SHP-\d{8}-[A-Z0-9]{8}$/;

export default function TrackingPage() {
  const { number } = useParams();
  const navigate = useNavigate();
  const [input, setInput] = useState(number ?? '');
  const [touched, setTouched] = useState(false);
  useEffect(() => setInput(number ?? ''), [number]);

  const tracking = useQuery({
    queryKey: ['tracking', number],
    queryFn: () => api.get<Tracking>(`/tracking/${encodeURIComponent(number!)}`),
    enabled: !!number,
  });

  const normalized = input.trim().toUpperCase();
  const invalid = touched && !NUMBER_PATTERN.test(normalized);

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    setTouched(true);
    if (NUMBER_PATTERN.test(normalized)) navigate(`/tracking/${normalized}`);
  };

  const notFound = tracking.error instanceof ApiError && tracking.error.status === 404;
  const t = tracking.data;
  // Newest first reads naturally for "where is it now?"
  const timeline = t ? [...t.history].reverse() : [];

  return (
    <>
      <PageHeader title="Track a shipment" subtitle="Full movement history with who recorded each step and where." />
      <Paper sx={{ p: 2, mb: 3 }}>
        <Stack component="form" direction={{ xs: 'column', sm: 'row' }} spacing={2} onSubmit={submit} noValidate aria-label="Track">
          <TextField
            fullWidth
            label="Shipment number"
            placeholder="SHP-20261110-ABCDEFGH"
            value={input}
            onChange={(e) => setInput(e.target.value)}
            error={invalid}
            helperText={invalid ? 'Format: SHP-YYYYMMDD-XXXXXXXX' : undefined}
            inputProps={{ 'aria-label': 'Shipment number', spellCheck: false }}
          />
          <Button type="submit" variant="contained" sx={{ minWidth: 120 }}>
            Track
          </Button>
        </Stack>
      </Paper>

      {tracking.isLoading && <LoadingState label="Looking up shipment…" />}
      {notFound && <Alert severity="info">No shipment found with number {number}.</Alert>}
      {tracking.isError && !notFound && <ErrorState error={tracking.error} onRetry={() => tracking.refetch()} />}

      {t && (
        <Grid container spacing={2}>
          <Grid size={{ xs: 12, md: 5 }}>
            <Paper sx={{ p: 3 }}>
              <Stack spacing={1.5}>
                <Typography variant="h6">{t.shipmentNumber}</Typography>
                <StatusChip status={t.status} size="medium" />
                <Typography variant="body2">{shipmentTypeLabels[t.type]}</Typography>
                <Typography variant="body2">
                  <strong>From:</strong> {formatPostOffice(t.origin)}
                </Typography>
                <Typography variant="body2">
                  <strong>To:</strong> {formatPostOffice(t.destination)}
                </Typography>
                <Typography variant="body2">
                  <strong>Now at:</strong> {formatPostOffice(t.current)}
                </Typography>
                <Typography variant="caption" color="text.secondary">
                  Last update {formatDateTime(t.updatedAtUtc)}
                </Typography>
                <Button component={RouterLink} to={`/shipments/${t.shipmentId}`} variant="outlined">
                  Open shipment
                </Button>
              </Stack>
            </Paper>
          </Grid>
          <Grid size={{ xs: 12, md: 7 }}>
            <Paper sx={{ p: 3 }}>
              <Typography variant="h6" gutterBottom>
                Timeline
              </Typography>
              <Stepper orientation="vertical" nonLinear activeStep={0} aria-label="Tracking timeline">
                {timeline.map((h, i) => (
                  <Step key={h.id} completed={i > 0} expanded active={i === 0}>
                    <StepLabel optional={<Typography variant="caption">{formatDateTime(h.occurredAtUtc)}</Typography>}>
                      <Stack direction="row" spacing={1} alignItems="center">
                        <span>{statusLabels[h.status]}</span>
                        {h.isCorrection && <Chip size="small" color="warning" label="Correction" />}
                      </Stack>
                    </StepLabel>
                    <StepContent>
                      <Typography variant="body2">
                        {formatPostOffice(h.postOffice)} · recorded by {h.performedBy}
                      </Typography>
                      {h.remarks && (
                        <Typography variant="body2" color="text.secondary">
                          {h.remarks}
                        </Typography>
                      )}
                    </StepContent>
                  </Step>
                ))}
              </Stepper>
            </Paper>
          </Grid>
        </Grid>
      )}
    </>
  );
}
