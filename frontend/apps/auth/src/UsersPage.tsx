import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Controller, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import {
  Alert,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  MenuItem,
  Paper,
  Stack,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
} from '@mui/material';
import {
  api,
  ApiError,
  EmptyState,
  ErrorState,
  errorMessage,
  formatDateTime,
  LoadingState,
  PageHeader,
  Permissions,
  RequirePermission,
  type Role,
  type User,
} from '@postal/shared';

const roles: Role[] = ['Administrator', 'PostOfficeManager', 'ShipmentOperator', 'ShipmentViewer'];

const password = z
  .string()
  .min(10, 'At least 10 characters')
  .regex(/[A-Z]/, 'Needs an upper-case letter')
  .regex(/[a-z]/, 'Needs a lower-case letter')
  .regex(/[0-9]/, 'Needs a digit')
  .regex(/[^A-Za-z0-9]/, 'Needs a symbol');

const createSchema = z.object({
  userName: z.string().trim().min(1, 'Required').max(50).regex(/^[A-Za-z0-9._-]+$/, 'Letters, digits, . _ - only'),
  displayName: z.string().trim().min(1, 'Required').max(100),
  role: z.enum(['Administrator', 'PostOfficeManager', 'ShipmentOperator', 'ShipmentViewer']),
  password,
});

type CreateValues = z.infer<typeof createSchema>;

function CreateUserDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const client = useQueryClient();
  const form = useForm<CreateValues>({
    resolver: zodResolver(createSchema),
    defaultValues: { userName: '', displayName: '', role: 'ShipmentViewer', password: '' },
  });
  const create = useMutation({
    mutationFn: (values: CreateValues) => api.post<User>('/users', values),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['users'] });
      form.reset();
      onClose();
    },
  });
  const { errors } = form.formState;

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="xs">
      <form onSubmit={form.handleSubmit((v) => create.mutate(v))} noValidate>
        <DialogTitle>New user</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ mt: 1 }}>
            {create.isError && <Alert severity="error">{errorMessage(create.error)}</Alert>}
            <TextField label="User name" {...form.register('userName')} error={!!errors.userName} helperText={errors.userName?.message} />
            <TextField label="Display name" {...form.register('displayName')} error={!!errors.displayName} helperText={errors.displayName?.message} />
            <Controller
              control={form.control}
              name="role"
              render={({ field }) => (
                <TextField select label="Role" {...field}>
                  {roles.map((r) => (
                    <MenuItem key={r} value={r}>
                      {r}
                    </MenuItem>
                  ))}
                </TextField>
              )}
            />
            <TextField
              label="Initial password"
              type="password"
              autoComplete="new-password"
              {...form.register('password')}
              error={!!errors.password}
              helperText={errors.password?.message}
            />
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="contained" disabled={create.isPending}>
            Create
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}

function UserRow({ user }: { user: User }) {
  const client = useQueryClient();
  const update = useMutation({
    mutationFn: (changes: Partial<Pick<User, 'role' | 'isActive'>>) =>
      api.put<User>(`/users/${user.id}`, {
        displayName: user.displayName,
        role: changes.role ?? user.role,
        isActive: changes.isActive ?? user.isActive,
      }),
    onSettled: () => client.invalidateQueries({ queryKey: ['users'] }),
  });

  return (
    <TableRow hover>
      <TableCell>{user.userName}</TableCell>
      <TableCell>{user.displayName}</TableCell>
      <TableCell>
        <TextField
          select
          size="small"
          value={user.role}
          onChange={(e) => update.mutate({ role: e.target.value as Role })}
          inputProps={{ 'aria-label': `Role for ${user.userName}` }}
        >
          {roles.map((r) => (
            <MenuItem key={r} value={r}>
              {r}
            </MenuItem>
          ))}
        </TextField>
        {update.isError && (
          <Alert severity="error" sx={{ mt: 1 }}>
            {update.error instanceof ApiError ? update.error.message : 'Update failed'}
          </Alert>
        )}
      </TableCell>
      <TableCell>
        <FormControlLabel
          control={<Switch checked={user.isActive} onChange={(e) => update.mutate({ isActive: e.target.checked })} />}
          label={user.isActive ? <Chip size="small" color="success" label="Active" /> : <Chip size="small" label="Disabled" />}
        />
      </TableCell>
      <TableCell>{user.lastLoginAtUtc ? formatDateTime(user.lastLoginAtUtc) : '—'}</TableCell>
    </TableRow>
  );
}

export default function UsersPage() {
  const [creating, setCreating] = useState(false);
  const users = useQuery({ queryKey: ['users'], queryFn: () => api.get<User[]>('/users') });

  return (
    <RequirePermission permission={Permissions.UsersManage} fallback={<Alert severity="warning">Administrators only.</Alert>}>
      <PageHeader
        title="Users & roles"
        subtitle="Changing a role or disabling a user ends their session at the next token refresh."
        actions={
          <Button variant="contained" onClick={() => setCreating(true)}>
            New user
          </Button>
        }
      />
      {users.isLoading && <LoadingState />}
      {users.isError && <ErrorState error={users.error} onRetry={() => users.refetch()} />}
      {users.data && users.data.length === 0 && <EmptyState title="No users" />}
      {users.data && users.data.length > 0 && (
        <TableContainer component={Paper}>
          <Table size="small" aria-label="Users">
            <TableHead>
              <TableRow>
                <TableCell>User name</TableCell>
                <TableCell>Display name</TableCell>
                <TableCell>Role</TableCell>
                <TableCell>Status</TableCell>
                <TableCell>Last login</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {users.data.map((u) => (
                <UserRow key={u.id} user={u} />
              ))}
            </TableBody>
          </Table>
        </TableContainer>
      )}
      <CreateUserDialog open={creating} onClose={() => setCreating(false)} />
    </RequirePermission>
  );
}
