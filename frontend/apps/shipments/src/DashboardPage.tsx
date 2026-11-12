import { Box, Grid2 as Grid, Paper, Stack, Typography } from '@mui/material';
import {
  allStatuses,
  ErrorState,
  LoadingState,
  PageHeader,
  statusLabels,
  statusTone,
  weightCategoryLabels,
  type WeightCategory,
} from '@postal/shared';
import { useStatistics } from './api';

function StatCard({ label, value, tone }: { label: string; value: number; tone?: string }) {
  return (
    <Paper sx={{ p: 2.5, height: '100%' }}>
      <Typography variant="body2" color="text.secondary">
        {label}
      </Typography>
      <Typography variant="h4" sx={{ color: tone, fontWeight: 600 }}>
        {value.toLocaleString()}
      </Typography>
    </Paper>
  );
}

const toneColor: Record<string, string> = {
  default: '#9E9E9E',
  info: '#0288D1',
  primary: '#B71C1C',
  success: '#2E7D32',
  warning: '#ED6C02',
  error: '#D32F2F',
};

/** Exposed as its own remote module so the shell can show it as the home page. */
export default function DashboardPage() {
  const stats = useStatistics();
  if (stats.isLoading) return <LoadingState />;
  if (stats.isError) return <ErrorState error={stats.error} onRetry={() => stats.refetch()} />;
  const s = stats.data!;
  const max = Math.max(1, ...Object.values(s.byStatus));

  return (
    <>
      <PageHeader title="Dashboard" subtitle="Refreshes every 30 seconds" />
      <Grid container spacing={2} sx={{ mb: 2 }}>
        <Grid size={{ xs: 6, md: 2.4 }}>
          <StatCard label="Total shipments" value={s.total} />
        </Grid>
        <Grid size={{ xs: 6, md: 2.4 }}>
          <StatCard label="In transit" value={s.inTransit} tone={toneColor.info} />
        </Grid>
        <Grid size={{ xs: 6, md: 2.4 }}>
          <StatCard label="Delivered" value={s.delivered} tone={toneColor.success} />
        </Grid>
        <Grid size={{ xs: 6, md: 2.4 }}>
          <StatCard label="Failed deliveries" value={s.failedDeliveries} tone={toneColor.warning} />
        </Grid>
        <Grid size={{ xs: 12, md: 2.4 }}>
          <StatCard label="Lost" value={s.lost} tone={toneColor.error} />
        </Grid>
      </Grid>

      <Grid container spacing={2}>
        <Grid size={{ xs: 12, md: 8 }}>
          <Paper sx={{ p: 3 }}>
            <Typography variant="h6" gutterBottom>
              Shipments by status
            </Typography>
            <Stack spacing={1.25} role="list" aria-label="Shipments by status">
              {allStatuses.map((status) => {
                const count = s.byStatus[status] ?? 0;
                return (
                  <Stack key={status} direction="row" alignItems="center" spacing={2} role="listitem">
                    <Typography variant="body2" sx={{ width: 190, flexShrink: 0 }}>
                      {statusLabels[status]}
                    </Typography>
                    <Box sx={{ flexGrow: 1, bgcolor: 'grey.100', borderRadius: 1, height: 14 }}>
                      <Box
                        sx={{
                          width: `${(count / max) * 100}%`,
                          minWidth: count > 0 ? 4 : 0,
                          height: '100%',
                          borderRadius: 1,
                          bgcolor: toneColor[statusTone[status]],
                          transition: 'width .4s',
                        }}
                      />
                    </Box>
                    <Typography variant="body2" sx={{ width: 48, textAlign: 'right' }}>
                      {count}
                    </Typography>
                  </Stack>
                );
              })}
            </Stack>
          </Paper>
        </Grid>
        <Grid size={{ xs: 12, md: 4 }}>
          <Paper sx={{ p: 3 }}>
            <Typography variant="h6" gutterBottom>
              By weight
            </Typography>
            {(Object.keys(weightCategoryLabels) as WeightCategory[]).map((w) => (
              <Stack key={w} direction="row" justifyContent="space-between" sx={{ py: 1, borderBottom: 1, borderColor: 'divider' }}>
                <Typography variant="body2">{weightCategoryLabels[w]}</Typography>
                <Typography variant="body2" fontWeight={600}>
                  {s.byWeightCategory[w] ?? 0}
                </Typography>
              </Stack>
            ))}
          </Paper>
        </Grid>
      </Grid>
    </>
  );
}
