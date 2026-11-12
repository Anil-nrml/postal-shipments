import { useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { Controller, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import {
  Alert,
  Autocomplete,
  Button,
  FormControl,
  FormControlLabel,
  FormLabel,
  InputAdornment,
  Paper,
  Radio,
  RadioGroup,
  Stack,
  TextField,
} from '@mui/material';
import {
  ApiError,
  ErrorState,
  errorMessage,
  LoadingState,
  newIdempotencyKey,
  PageHeader,
  type PostOffice,
  usePostOfficeOptions,
  weightCategoryLabels,
  weightCategoryOf,
  ZIP_PATTERN,
} from '@postal/shared';
import { BASE_PATH, useCreateShipment, useShipment, useUpdateShipment } from './api';

const schema = z.object({
  type: z.enum(['Letter', 'Package']),
  weightKg: z.preprocess(
    (v) => (v === '' || v === null || v === undefined ? undefined : Number(v)),
    z
      .number({ required_error: 'Enter a weight in kg', invalid_type_error: 'Enter a weight in kg' })
      .gt(0, 'Weight must be greater than 0')
      .max(1000, 'Weight must not exceed 1000 kg')
      .refine((v) => Number.isInteger(Math.round(v * 1000 * 1e6) / 1e6), 'At most 3 decimals (grams)'),
  ),
  originZip: z.string().regex(ZIP_PATTERN, 'Choose the origin post office'),
  destinationZip: z.string().regex(ZIP_PATTERN, 'Choose the destination post office'),
});

type FormValues = z.infer<typeof schema>;

function OfficePicker({
  label,
  value,
  onChange,
  options,
  error,
  disabled,
}: {
  label: string;
  value: string;
  onChange: (zip: string) => void;
  options: PostOffice[];
  error?: string;
  disabled?: boolean;
}) {
  const selected = options.find((o) => o.zipCode === value) ?? null;
  return (
    <Autocomplete
      options={options}
      value={selected}
      disabled={disabled}
      onChange={(_, o) => onChange(o?.zipCode ?? '')}
      getOptionLabel={(o) => `${o.name} (${o.zipCode}) — ${o.city}`}
      isOptionEqualToValue={(a, b) => a.zipCode === b.zipCode}
      renderInput={(params) => <TextField {...params} label={label} error={!!error} helperText={error} />}
    />
  );
}

export default function ShipmentFormPage() {
  const { id } = useParams();
  const isEdit = !!id;
  const navigate = useNavigate();
  const existing = useShipment(id);
  const offices = usePostOfficeOptions({ activeOnly: true });
  const create = useCreateShipment();
  const update = useUpdateShipment(id ?? '');
  // One key per form instance: a double click or network retry returns the same shipment.
  const [idempotencyKey] = useState(newIdempotencyKey);

  const defaults = useMemo<FormValues>(
    () =>
      existing.data
        ? {
            type: existing.data.type,
            weightKg: existing.data.weightKg,
            originZip: existing.data.origin.zipCode,
            destinationZip: existing.data.destination.zipCode,
          }
        : { type: 'Package', weightKg: '' as unknown as number, originZip: '', destinationZip: '' },
    [existing.data],
  );

  const {
    control,
    register,
    handleSubmit,
    watch,
    setError,
    formState: { errors },
  } = useForm<FormValues>({ resolver: zodResolver(schema), values: defaults });

  const mutation = isEdit ? update : create;
  const weight = Number(watch('weightKg'));

  const onSubmit = handleSubmit((values) => {
    const onError = (error: unknown) => {
      // Map server-side field validation back onto the form.
      if (error instanceof ApiError)
        Object.entries(error.fieldErrors).forEach(([field, messages]) =>
          setError(field as keyof FormValues, { message: messages[0] }),
        );
    };
    if (isEdit && existing.data) {
      update.mutate(
        { type: values.type, weightKg: values.weightKg, destinationZip: values.destinationZip, version: existing.data.version },
        { onSuccess: (s) => navigate(`${BASE_PATH}/${s.id}`), onError },
      );
    } else {
      create.mutate({ input: values, idempotencyKey }, { onSuccess: (s) => navigate(`${BASE_PATH}/${s.id}`), onError });
    }
  });

  if (isEdit && existing.isLoading) return <LoadingState />;
  if (isEdit && existing.isError) return <ErrorState error={existing.error} />;
  if (isEdit && existing.data && !existing.data.canEdit)
    return <Alert severity="info">This shipment has already been processed and can no longer be edited.</Alert>;

  // Keep the current destination selectable even if that office was deactivated since.
  const officeOptions = offices.data ?? [];

  return (
    <>
      <PageHeader
        title={isEdit ? `Edit ${existing.data?.shipmentNumber ?? 'shipment'}` : 'Register shipment'}
        subtitle={isEdit ? 'Details can change until the shipment is processed at origin.' : 'The shipment number is generated automatically.'}
      />
      <Paper sx={{ p: 3, maxWidth: 640 }}>
        <Stack spacing={3} component="form" onSubmit={onSubmit} noValidate aria-label="Shipment form">
          {mutation.isError && (
            <Alert severity={mutation.error instanceof ApiError && mutation.error.isConflict ? 'warning' : 'error'}>
              {errorMessage(mutation.error)}
            </Alert>
          )}
          {offices.isError && <Alert severity="warning">Post offices could not be loaded: {errorMessage(offices.error)}</Alert>}

          <Controller
            control={control}
            name="type"
            render={({ field }) => (
              <FormControl>
                <FormLabel id="type-label">Type</FormLabel>
                <RadioGroup row aria-labelledby="type-label" {...field}>
                  <FormControlLabel value="Letter" control={<Radio />} label="Letter" />
                  <FormControlLabel value="Package" control={<Radio />} label="Package" />
                </RadioGroup>
              </FormControl>
            )}
          />

          <TextField
            label="Weight"
            type="number"
            inputProps={{ step: '0.001', min: 0, inputMode: 'decimal' }}
            InputProps={{ endAdornment: <InputAdornment position="end">kg</InputAdornment> }}
            {...register('weightKg')}
            error={!!errors.weightKg}
            helperText={
              errors.weightKg?.message ??
              (weight > 0 ? `Category: ${weightCategoryLabels[weightCategoryOf(weight)]}` : 'Up to 3 decimals')
            }
          />

          <Controller
            control={control}
            name="originZip"
            render={({ field }) => (
              <OfficePicker
                label="Origin post office"
                value={field.value}
                onChange={field.onChange}
                options={officeOptions}
                error={errors.originZip?.message}
                disabled={isEdit}
              />
            )}
          />
          <Controller
            control={control}
            name="destinationZip"
            render={({ field }) => (
              <OfficePicker
                label="Destination post office"
                value={field.value}
                onChange={field.onChange}
                options={officeOptions}
                error={errors.destinationZip?.message}
              />
            )}
          />

          <Stack direction="row" spacing={1} justifyContent="flex-end">
            <Button onClick={() => navigate(-1)}>Cancel</Button>
            <Button type="submit" variant="contained" disabled={mutation.isPending}>
              {mutation.isPending ? 'Saving…' : isEdit ? 'Save changes' : 'Register shipment'}
            </Button>
          </Stack>
        </Stack>
      </Paper>
    </>
  );
}
