import { useEffect, useState, type ReactNode } from 'react';
import { QueryClient, QueryClientProvider, useQueryClient } from '@tanstack/react-query';
import { createTheme, CssBaseline, ThemeProvider } from '@mui/material';
import { ApiError } from './api';
import { sessionKey } from './auth';
import type { User } from './types';
import { onWindowEvent, SESSION_EXPIRED } from './events';

export const theme = createTheme({
  palette: {
    primary: { main: '#B71C1C' }, // postal red
    secondary: { main: '#1A237E' },
    background: { default: '#F7F7F9' },
  },
  shape: { borderRadius: 10 },
  typography: { fontFamily: '"Inter", "Segoe UI", Roboto, Arial, sans-serif' },
  components: {
    MuiButton: { defaultProps: { disableElevation: true } },
    MuiPaper: { defaultProps: { elevation: 0 }, styleOverrides: { root: { border: '1px solid #E4E4EA' } } },
    MuiAppBar: { styleOverrides: { root: { border: 'none' } } },
  },
});

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // Retry transient failures only; 4xx answers will not change on retry.
        retry: (count, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && count < 2,
        refetchOnWindowFocus: false,
        staleTime: 15_000,
      },
      mutations: { retry: false }, // never auto-retry writes
    },
  });
}

function SessionExpiryListener() {
  const client = useQueryClient();
  useEffect(() => onWindowEvent(SESSION_EXPIRED, () => client.setQueryData<User | null>(sessionKey, null)), [client]);
  return null;
}

/** Used by the shell and by each remote when it runs standalone. */
export function AppProviders({ children, client }: { children: ReactNode; client?: QueryClient }) {
  const [queryClient] = useState(() => client ?? createQueryClient());
  return (
    <QueryClientProvider client={queryClient}>
      <ThemeProvider theme={theme}>
        <CssBaseline />
        <SessionExpiryListener />
        {children}
      </ThemeProvider>
    </QueryClientProvider>
  );
}
