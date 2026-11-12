import { useMemo } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Alert, Button, Grid2 as Grid, Paper, Stack, TextField } from '@mui/material';
import { ApiError, ErrorState, errorMessage, LoadingState, PageHeader, ZIP_PATTERN } from '@postal/shared';
import { BASE_PATH, type PostOfficeInput, usePostOffice, useSavePostOffice } from './api';

const required = (max: number) => z.string().trim().min(1, 'Required').max(max, `At most ${max} characters`);

const schema = z.object({
  zipCode: z.string().trim().regex(ZIP_PATTERN, '3–10 letters or digits'),
  name: required(150),
  address: required(200),
  city: required(100),
  state: required(100),
  country: required(100),
  contactNumber: z.string().trim().regex(/^\+?[0-9 ()-]{6,20}$/, 'Digits, spaces, +, - and () only (6–20)'),
});

const empty: PostOfficeInput = { zipCode: '', name: '', address: '', city: '', state: '', country: 'India', contactNumber: '' };

export default function PostOfficeFormPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const existing = usePostOffice(id);
  const save = useSavePostOffice(id);

  const values = useMemo<PostOfficeInput>(() => (existing.data ? { ...existing.data } : empty), [existing.data]);
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors },
  } = useForm<PostOfficeInput>({ resolver: zodResolver(schema), values });

  if (id && existing.isLoading) return <LoadingState />;
  if (id && existing.isError) return <ErrorState error={existing.error} />;

  const onSubmit = handleSubmit((input) =>
    save.mutate(
      { input, version: existing.data?.version },
      {
        onSuccess: () => navigate(BASE_PATH),
        onError: (e) => {
          if (e instanceof ApiError) {
            Object.entries(e.fieldErrors).forEach(([f, m]) => setError(f as keyof PostOfficeInput, { message: m[0] }));
            if (e.code === 'postoffice.zip-exists') setError('zipCode', { message: e.message });
          }
        },
      },
    ),
  );

  const field = (name: keyof PostOfficeInput, label: string, props: Record<string, unknown> = {}) => (
    <TextField fullWidth label={label} {...register(name)} error={!!errors[name]} helperText={errors[name]?.message} {...props} />
  );

  return (
    <>
      <PageHeader title={id ? `Edit ${existing.data?.name}` : 'New post office'} subtitle={id ? 'The ZIP code cannot be changed.' : undefined} />
      <Paper sx={{ p: 3, maxWidth: 720 }}>
        <Stack component="form" spacing={2} onSubmit={onSubmit} noValidate aria-label="Post office form">
          {save.isError && <Alert severity="error">{errorMessage(save.error)}</Alert>}
          <Grid container spacing={2}>
            <Grid size={{ xs: 12, sm: 4 }}>{field('zipCode', 'ZIP code', { disabled: !!id })}</Grid>
            <Grid size={{ xs: 12, sm: 8 }}>{field('name', 'Name')}</Grid>
            <Grid size={12}>{field('address', 'Address')}</Grid>
            <Grid size={{ xs: 12, sm: 4 }}>{field('city', 'City')}</Grid>
            <Grid size={{ xs: 12, sm: 4 }}>{field('state', 'State')}</Grid>
            <Grid size={{ xs: 12, sm: 4 }}>{field('country', 'Country')}</Grid>
            <Grid size={{ xs: 12, sm: 6 }}>{field('contactNumber', 'Contact number', { type: 'tel' })}</Grid>
          </Grid>
          <Stack direction="row" spacing={1} justifyContent="flex-end">
            <Button onClick={() => navigate(BASE_PATH)}>Cancel</Button>
            <Button type="submit" variant="contained" disabled={save.isPending}>
              {save.isPending ? 'Saving…' : 'Save'}
            </Button>
          </Stack>
        </Stack>
      </Paper>
    </>
  );
}
