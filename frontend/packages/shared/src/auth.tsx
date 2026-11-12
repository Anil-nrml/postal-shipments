import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { api, ApiError } from './api';
import { emitSessionChanged } from './events';
import type { Permission, User } from './types';

export const sessionKey = ['session'] as const;

interface LoginResponse {
  user: User;
  accessTokenExpiresAtUtc: string;
}

/**
 * Current user, shared by all MFEs through the singleton QueryClient (react-query is a
 * federation shared singleton). `null` means "not signed in".
 */
export function useSession() {
  return useQuery<User | null>({
    queryKey: sessionKey,
    queryFn: async () => {
      try {
        return await api.get<User>('/auth/me');
      } catch (e) {
        if (e instanceof ApiError && e.status === 401) return null;
        throw e;
      }
    },
    staleTime: 5 * 60_000,
    retry: false,
  });
}

export function useLogin() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (input: { userName: string; password: string }) => api.post<LoginResponse>('/auth/login', input),
    onSuccess: (result) => {
      client.setQueryData(sessionKey, result.user);
      emitSessionChanged();
    },
  });
}

export function useLogout() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () => api.post<void>('/auth/logout'),
    onSettled: () => {
      client.clear();
      client.setQueryData<User | null>(sessionKey, null);
      emitSessionChanged();
    },
  });
}

export function hasPermission(user: User | null | undefined, permission: Permission): boolean {
  return !!user?.permissions.includes(permission);
}

export function usePermission(permission: Permission): boolean {
  const { data } = useSession();
  return hasPermission(data, permission);
}

/** Hides UI the user may not use. The API enforces the same rule; this is only UX. */
export function RequirePermission({
  permission,
  children,
  fallback = null,
}: {
  permission: Permission;
  children: ReactNode;
  fallback?: ReactNode;
}) {
  return usePermission(permission) ? <>{children}</> : <>{fallback}</>;
}
