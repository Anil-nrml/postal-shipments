import { useEffect, useMemo, useState } from 'react';
import { Link as RouterLink, useNavigate, useSearchParams } from 'react-router-dom';
import {
  Box,
  Button,
  Grid2 as Grid,
  LinearProgress,
  Link,
  MenuItem,
  Paper,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TablePagination,
  TableRow,
  TableSortLabel,
  TextField,
} from '@mui/material';
import {
  allStatuses,
  EmptyState,
  ErrorState,
  formatDateTime,
  formatPostOffice,
  formatWeight,
  LoadingState,
  PageHeader,
  Permissions,
  RequirePermission,
  shipmentTypeLabels,
  statusLabels,
  StatusChip,
  usePostOfficeOptions,
  weightCategoryLabels,
} from '@postal/shared';
import { BASE_PATH, type ShipmentFilters, useShipments } from './api';

const filterKeys = [
  'shipmentNumber',
  'type',
  'status',
  'weightCategory',
  'originPostOffice',
  'destinationPostOffice',
  'currentPostOffice',
  'createdFrom',
  'createdTo',
] as const;

const sortable = [
  { key: 'shipmentNumber', label: 'Shipment no.' },
  { key: 'weight', label: 'Weight' },
  { key: 'status', label: 'Status' },
  { key: 'createdAt', label: 'Created' },
] as const;

/** Filter state lives in the URL so a filtered view can be bookmarked or shared. */
function useFiltersFromUrl(): [ShipmentFilters, (patch: Partial<Record<keyof ShipmentFilters, string | number | undefined>>) => void] {
  const [params, setParams] = useSearchParams();
  const filters = useMemo<ShipmentFilters>(() => {
    const f: Record<string, unknown> = {
      pageNumber: Number(params.get('pageNumber') ?? 1),
      pageSize: Number(params.get('pageSize') ?? 20),
      sortBy: params.get('sortBy') ?? 'createdAt',
      sortDirection: params.get('sortDirection') === 'asc' ? 'asc' : 'desc',
    };
    filterKeys.forEach((k) => {
      const v = params.get(k);
      if (v) f[k] = v;
    });
    return f as unknown as ShipmentFilters;
  }, [params]);

  const update = (patch: Partial<Record<keyof ShipmentFilters, string | number | undefined>>) => {
    const next = new URLSearchParams(params);
    Object.entries(patch).forEach(([k, v]) => (v === undefined || v === '' ? next.delete(k) : next.set(k, String(v))));
    if (!('pageNumber' in patch)) next.set('pageNumber', '1'); // any filter change resets paging
    setParams(next, { replace: true });
  };
  return [filters, update];
}

export default function ShipmentListPage() {
  const [filters, update] = useFiltersFromUrl();
  const query = useShipments(filters);
  const offices = usePostOfficeOptions();
  const navigate = useNavigate();

  // Debounce free-text search so we don't fire a request per keystroke.
  const [numberInput, setNumberInput] = useState(filters.shipmentNumber ?? '');
  useEffect(() => {
    const handle = setTimeout(() => {
      if ((filters.shipmentNumber ?? '') !== numberInput.trim()) update({ shipmentNumber: numberInput.trim() || undefined });
    }, 350);
    return () => clearTimeout(handle);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [numberInput]);

  const select = (name: (typeof filterKeys)[number], label: string, options: { value: string; label: string }[]) => (
    <TextField
      select
      fullWidth
      size="small"
      label={label}
      value={filters[name] ?? ''}
      onChange={(e) => update({ [name]: e.target.value || undefined })}
    >
      <MenuItem value="">
        <em>Any</em>
      </MenuItem>
      {options.map((o) => (
        <MenuItem key={o.value} value={o.value}>
          {o.label}
        </MenuItem>
      ))}
    </TextField>
  );

  const officeOptions = (offices.data ?? []).map((o) => ({ value: o.zipCode, label: `${o.name} (${o.zipCode})` }));
  const hasFilters = filterKeys.some((k) => filters[k]);
  const data = query.data;

  return (
    <>
      <PageHeader
        title="Shipments"
        subtitle={data ? `${data.totalRecords.toLocaleString()} shipment(s)` : undefined}
        actions={
          <RequirePermission permission={Permissions.ShipmentsWrite}>
            <Button variant="contained" component={RouterLink} to={`${BASE_PATH}/new`}>
              New shipment
            </Button>
          </RequirePermission>
        }
      />

      <Paper sx={{ p: 2, mb: 2 }}>
        <Grid container spacing={2}>
          <Grid size={{ xs: 12, sm: 6, md: 3 }}>
            <TextField
              fullWidth
              size="small"
              label="Shipment number"
              placeholder="SHP-2026…"
              value={numberInput}
              onChange={(e) => setNumberInput(e.target.value)}
            />
          </Grid>
          <Grid size={{ xs: 6, sm: 3, md: 2 }}>
            {select('status', 'Status', allStatuses.map((s) => ({ value: s, label: statusLabels[s] })))}
          </Grid>
          <Grid size={{ xs: 6, sm: 3, md: 2 }}>
            {select('type', 'Type', Object.entries(shipmentTypeLabels).map(([value, label]) => ({ value, label })))}
          </Grid>
          <Grid size={{ xs: 6, sm: 4, md: 2 }}>
            {select('weightCategory', 'Weight', Object.entries(weightCategoryLabels).map(([value, label]) => ({ value, label })))}
          </Grid>
          <Grid size={{ xs: 6, sm: 4, md: 3 }}>{select('currentPostOffice', 'Current post office', officeOptions)}</Grid>
          <Grid size={{ xs: 6, sm: 4, md: 3 }}>{select('originPostOffice', 'Origin', officeOptions)}</Grid>
          <Grid size={{ xs: 6, sm: 4, md: 3 }}>{select('destinationPostOffice', 'Destination', officeOptions)}</Grid>
          <Grid size={{ xs: 6, sm: 4, md: 2 }}>
            <TextField
              fullWidth
              size="small"
              type="date"
              label="Created from"
              InputLabelProps={{ shrink: true }}
              value={filters.createdFrom ?? ''}
              onChange={(e) => update({ createdFrom: e.target.value || undefined })}
            />
          </Grid>
          <Grid size={{ xs: 6, sm: 4, md: 2 }}>
            <TextField
              fullWidth
              size="small"
              type="date"
              label="Created to"
              InputLabelProps={{ shrink: true }}
              value={filters.createdTo ?? ''}
              onChange={(e) => update({ createdTo: e.target.value || undefined })}
            />
          </Grid>
          <Grid size={{ xs: 12, md: 2 }} sx={{ display: 'flex', alignItems: 'center' }}>
            <Button
              disabled={!hasFilters}
              onClick={() => {
                setNumberInput('');
                update(Object.fromEntries(filterKeys.map((k) => [k, undefined])));
              }}
            >
              Clear filters
            </Button>
          </Grid>
        </Grid>
      </Paper>

      {query.isLoading && <LoadingState />}
      {query.isError && <ErrorState error={query.error} onRetry={() => query.refetch()} />}
      {data && data.items.length === 0 && (
        <EmptyState
          title={hasFilters ? 'No shipments match these filters' : 'No shipments yet'}
          description={hasFilters ? 'Try widening the search.' : 'Registered shipments will appear here.'}
        />
      )}

      {data && data.items.length > 0 && (
        <Paper>
          {query.isFetching && <LinearProgress />}
          <TableContainer>
            <Table size="small" aria-label="Shipments">
              <TableHead>
                <TableRow>
                  {sortable.map((col) => (
                    <TableCell key={col.key} sortDirection={filters.sortBy === col.key ? filters.sortDirection : false}>
                      <TableSortLabel
                        active={filters.sortBy === col.key}
                        direction={filters.sortBy === col.key ? filters.sortDirection : 'asc'}
                        onClick={() =>
                          update({
                            sortBy: col.key,
                            sortDirection: filters.sortBy === col.key && filters.sortDirection === 'desc' ? 'asc' : 'desc',
                          })
                        }
                      >
                        {col.label}
                      </TableSortLabel>
                    </TableCell>
                  ))}
                  <TableCell>Type</TableCell>
                  <TableCell>Route</TableCell>
                  <TableCell>Current</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {data.items.map((s) => (
                  <TableRow
                    key={s.id}
                    hover
                    sx={{ cursor: 'pointer' }}
                    onClick={() => navigate(`${BASE_PATH}/${s.id}`)}
                  >
                    <TableCell sx={{ whiteSpace: 'nowrap', fontFamily: 'monospace' }}>
                      <Link component={RouterLink} to={`${BASE_PATH}/${s.id}`} onClick={(e) => e.stopPropagation()}>
                        {s.shipmentNumber}
                      </Link>
                    </TableCell>
                    <TableCell>{formatWeight(s.weightKg)}</TableCell>
                    <TableCell>
                      <StatusChip status={s.status} />
                    </TableCell>
                    <TableCell>{formatDateTime(s.createdAtUtc)}</TableCell>
                    <TableCell>{shipmentTypeLabels[s.type]}</TableCell>
                    <TableCell>
                      {s.origin.zipCode} → {s.destination.zipCode}
                    </TableCell>
                    <TableCell>{formatPostOffice(s.current)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </TableContainer>
          <Box sx={{ borderTop: 1, borderColor: 'divider' }}>
            <TablePagination
              component="div"
              count={data.totalRecords}
              page={data.pageNumber - 1}
              rowsPerPage={data.pageSize}
              rowsPerPageOptions={[10, 20, 50, 100]}
              onPageChange={(_, page) => update({ pageNumber: page + 1 })}
              onRowsPerPageChange={(e) => update({ pageSize: Number(e.target.value), pageNumber: 1 })}
            />
          </Box>
        </Paper>
      )}
    </>
  );
}
