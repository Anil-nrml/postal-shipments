import { useDeferredValue, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import {
  Alert,
  Button,
  Chip,
  IconButton,
  LinearProgress,
  MenuItem,
  Paper,
  Stack,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TablePagination,
  TableRow,
  TextField,
  Tooltip,
} from '@mui/material';
import DeleteOutline from '@mui/icons-material/DeleteOutline';
import EditOutlined from '@mui/icons-material/EditOutlined';
import {
  ApiError,
  ConfirmDialog,
  EmptyState,
  ErrorState,
  errorMessage,
  LoadingState,
  PageHeader,
  Permissions,
  type PostOffice,
  RequirePermission,
  usePermission,
} from '@postal/shared';
import { BASE_PATH, useDeletePostOffice, usePostOffices, useSetActive } from './api';

export default function PostOfficeListPage() {
  const [search, setSearch] = useState('');
  const [active, setActive] = useState<'all' | 'active' | 'inactive'>('all');
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const [toDelete, setToDelete] = useState<PostOffice | null>(null);
  const deferredSearch = useDeferredValue(search.trim());
  const canWrite = usePermission(Permissions.PostOfficesWrite);

  const query = usePostOffices({
    pageNumber: page,
    pageSize,
    search: deferredSearch || undefined,
    isActive: active === 'all' ? undefined : active === 'active',
  });
  const toggle = useSetActive();
  const remove = useDeletePostOffice();

  return (
    <>
      <PageHeader
        title="Post offices"
        subtitle="Each post office is identified by its unique ZIP code."
        actions={
          <RequirePermission permission={Permissions.PostOfficesWrite}>
            <Button variant="contained" component={RouterLink} to={`${BASE_PATH}/new`}>
              New post office
            </Button>
          </RequirePermission>
        }
      />

      <Paper sx={{ p: 2, mb: 2 }}>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
          <TextField
            size="small"
            label="Search ZIP, name or city"
            value={search}
            onChange={(e) => {
              setSearch(e.target.value);
              setPage(1);
            }}
            sx={{ flexGrow: 1 }}
          />
          <TextField
            select
            size="small"
            label="Status"
            value={active}
            onChange={(e) => {
              setActive(e.target.value as typeof active);
              setPage(1);
            }}
            sx={{ minWidth: 160 }}
          >
            <MenuItem value="all">All</MenuItem>
            <MenuItem value="active">Active</MenuItem>
            <MenuItem value="inactive">Inactive</MenuItem>
          </TextField>
        </Stack>
      </Paper>

      {toggle.isError && <Alert severity="error" sx={{ mb: 2 }}>{errorMessage(toggle.error)}</Alert>}
      {query.isLoading && <LoadingState />}
      {query.isError && <ErrorState error={query.error} onRetry={() => query.refetch()} />}
      {query.data && query.data.items.length === 0 && <EmptyState title="No post offices found" />}

      {query.data && query.data.items.length > 0 && (
        <Paper>
          {query.isFetching && <LinearProgress />}
          <TableContainer>
            <Table size="small" aria-label="Post offices">
              <TableHead>
                <TableRow>
                  <TableCell>ZIP</TableCell>
                  <TableCell>Name</TableCell>
                  <TableCell>City</TableCell>
                  <TableCell>Contact</TableCell>
                  <TableCell>Accepting shipments</TableCell>
                  <TableCell align="right">Actions</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {query.data.items.map((o) => (
                  <TableRow key={o.id} hover>
                    <TableCell sx={{ fontFamily: 'monospace' }}>{o.zipCode}</TableCell>
                    <TableCell>{o.name}</TableCell>
                    <TableCell>
                      {o.city}, {o.state}
                    </TableCell>
                    <TableCell>{o.contactNumber}</TableCell>
                    <TableCell>
                      {canWrite ? (
                        <Switch
                          checked={o.isActive}
                          disabled={toggle.isPending}
                          onChange={(e) => toggle.mutate({ office: o, active: e.target.checked })}
                          inputProps={{ 'aria-label': `${o.isActive ? 'Deactivate' : 'Activate'} ${o.zipCode}` }}
                        />
                      ) : (
                        <Chip size="small" color={o.isActive ? 'success' : 'default'} label={o.isActive ? 'Yes' : 'No'} />
                      )}
                    </TableCell>
                    <TableCell align="right">
                      <RequirePermission permission={Permissions.PostOfficesWrite}>
                        <Tooltip title="Edit">
                          <IconButton component={RouterLink} to={`${BASE_PATH}/${o.id}/edit`} aria-label={`Edit ${o.zipCode}`}>
                            <EditOutlined fontSize="small" />
                          </IconButton>
                        </Tooltip>
                      </RequirePermission>
                      <RequirePermission permission={Permissions.PostOfficesDelete}>
                        <Tooltip title="Delete">
                          <IconButton
                            color="error"
                            aria-label={`Delete ${o.zipCode}`}
                            onClick={() => {
                              remove.reset();
                              setToDelete(o);
                            }}
                          >
                            <DeleteOutline fontSize="small" />
                          </IconButton>
                        </Tooltip>
                      </RequirePermission>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </TableContainer>
          <TablePagination
            component="div"
            count={query.data.totalRecords}
            page={query.data.pageNumber - 1}
            rowsPerPage={query.data.pageSize}
            rowsPerPageOptions={[10, 20, 50, 100]}
            onPageChange={(_, p) => setPage(p + 1)}
            onRowsPerPageChange={(e) => {
              setPageSize(Number(e.target.value));
              setPage(1);
            }}
          />
        </Paper>
      )}

      <ConfirmDialog
        open={!!toDelete}
        title={`Delete ${toDelete?.name ?? ''}?`}
        message={
          <>
            Deletion is refused while active shipments reference ZIP {toDelete?.zipCode}. Consider deactivating instead.
            {remove.isError && (
              <Alert severity={remove.error instanceof ApiError && remove.error.isConflict ? 'warning' : 'error'} sx={{ mt: 2 }}>
                {errorMessage(remove.error)}
              </Alert>
            )}
          </>
        }
        confirmLabel="Delete"
        destructive
        busy={remove.isPending}
        onClose={() => setToDelete(null)}
        onConfirm={() => toDelete && remove.mutate(toDelete, { onSuccess: () => setToDelete(null) })}
      />
    </>
  );
}
