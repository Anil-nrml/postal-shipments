import type { ReactElement } from 'react';
import { render } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient } from '@tanstack/react-query';
import { vi } from 'vitest';
import { AppProviders } from './providers';
import { sessionKey } from './auth';
import type { Permission, User } from './types';

export function testUser(permissions: Permission[], overrides: Partial<User> = {}): User {
  return {
    id: 'u1',
    userName: 'tester',
    displayName: 'Test User',
    role: 'ShipmentOperator',
    isActive: true,
    permissions,
    lastLoginAtUtc: null,
    ...overrides,
  };
}

/** Renders inside the real providers with a signed-in user and an in-memory router. */
export function renderWithApp(
  ui: ReactElement,
  { user = null, route = '/', path = '*' }: { user?: User | null; route?: string; path?: string } = {},
) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  client.setQueryData(sessionKey, user);
  const result = render(
    <AppProviders client={client}>
      <MemoryRouter initialEntries={[route]}>
        <Routes>
          <Route path={path} element={ui} />
        </Routes>
      </MemoryRouter>
    </AppProviders>,
  );
  return { ...result, client };
}

type Handler = (request: { url: URL; method: string; body: unknown; headers: Headers }) => {
  status?: number;
  body?: unknown;
};

/** Minimal fetch mock: route by "METHOD /path" (query string ignored for matching). */
export function mockApi(routes: Record<string, Handler | { status?: number; body?: unknown }>) {
  const calls: { method: string; url: URL; body: unknown; headers: Headers }[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost');
    const method = (init?.method ?? 'GET').toUpperCase();
    const body = init?.body ? JSON.parse(String(init.body)) : undefined;
    const headers = new Headers(init?.headers);
    calls.push({ method, url, body, headers });
    const route = routes[`${method} ${url.pathname}`];
    if (!route) return new Response(JSON.stringify({ title: 'Not mocked' }), { status: 404 });
    const result = typeof route === 'function' ? route({ url, method, body, headers }) : route;
    const status = result.status ?? 200;
    return new Response(status === 204 ? null : JSON.stringify(result.body ?? {}), {
      status,
      headers: { 'Content-Type': 'application/json' },
    });
  });
  vi.stubGlobal('fetch', fetchMock);
  return { calls, fetchMock };
}

export function paged<T>(items: T[], pageNumber = 1, pageSize = 20, totalRecords = items.length) {
  const totalPages = Math.ceil(totalRecords / pageSize);
  return {
    items,
    totalRecords,
    pageNumber,
    pageSize,
    totalPages,
    hasNextPage: pageNumber < totalPages,
    hasPreviousPage: pageNumber > 1,
  };
}
