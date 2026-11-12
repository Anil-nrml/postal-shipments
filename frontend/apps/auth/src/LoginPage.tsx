import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { useLocation, useNavigate } from 'react-router-dom';
import { Alert, Box, Button, Paper, Stack, TextField, Typography } from '@mui/material';
import { errorMessage, useLogin } from '@postal/shared';

const schema = z.object({
  userName: z.string().trim().min(1, 'User name is required'),
  password: z.string().min(1, 'Password is required'),
});

type FormValues = z.infer<typeof schema>;

export default function LoginPage() {
  const login = useLogin();
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as { from?: string } | null)?.from ?? '/';

  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<FormValues>({ resolver: zodResolver(schema), defaultValues: { userName: '', password: '' } });

  const onSubmit = handleSubmit((values) =>
    login.mutate(values, { onSuccess: () => navigate(from, { replace: true }) }),
  );

  return (
    <Box sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center', p: 2, bgcolor: 'background.default' }}>
      <Paper sx={{ p: 4, width: '100%', maxWidth: 400 }}>
        <Stack spacing={3} component="form" onSubmit={onSubmit} noValidate aria-label="Sign in">
          <Box>
            <Typography variant="h5" component="h1">
              Postal Shipments
            </Typography>
            <Typography color="text.secondary">Sign in with your staff account</Typography>
          </Box>

          {login.isError && <Alert severity="error">{errorMessage(login.error)}</Alert>}

          <TextField
            label="User name"
            autoComplete="username"
            autoFocus
            {...register('userName')}
            error={!!errors.userName}
            helperText={errors.userName?.message}
          />
          <TextField
            label="Password"
            type="password"
            autoComplete="current-password"
            {...register('password')}
            error={!!errors.password}
            helperText={errors.password?.message}
          />
          <Button type="submit" variant="contained" size="large" disabled={login.isPending}>
            {login.isPending ? 'Signing in…' : 'Sign in'}
          </Button>
        </Stack>
      </Paper>
    </Box>
  );
}
