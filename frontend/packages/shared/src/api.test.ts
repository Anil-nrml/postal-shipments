import { describe, expect, it, vi } from 'vitest';
import { api, ApiError, errorMessage, toQuery } from './api';
import { SESSION_EXPIRED } from './events';
import { mockApi } from './testing';
import { suggestedZipFor, weightCategoryOf } from './domain';
import { hasPermission } from './auth';
import { Permissions } from './types';

describe('api client', () => {
  it('sends the CSRF cookie as a header on writes but not on reads', async () => {
    document.cookie = 'csrf_token=abc123; path=/';
    const { calls } = mockApi({ 'GET /api/x': { body: {} }, 'POST /api/x': { body: {} } });

    await api.get('/x');
    await api.post('/x', { a: 1 });

    expect(calls[0]!.headers.get('X-CSRF-Token')).toBeNull();
    expect(calls[1]!.headers.get('X-CSRF-Token')).toBe('abc123');
    expect(calls[1]!.headers.get('Content-Type')).toBe('application/json');
  });

  it('maps ProblemDetails into ApiError with field errors and code', async () => {
    mockApi({
      'POST /api/shipments': {
        status: 400,
        body: { title: 'Validation', errors: { weightKg: ['Must be > 0'] }, correlationId: 'c-1' },
      },
    });
    const error = await api.post('/shipments', {}).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).fieldErrors.weightKg).toEqual(['Must be > 0']);
    expect((error as ApiError).correlationId).toBe('c-1');
  });

  it('refreshes once on 401 and replays the request', async () => {
    let calls = 0;
    mockApi({
      'GET /api/shipments': () => (++calls === 1 ? { status: 401 } : { body: { ok: true } }),
      'POST /api/auth/refresh': { body: {} },
    });
    await expect(api.get('/shipments')).resolves.toEqual({ ok: true });
    expect(calls).toBe(2);
  });

  it('emits session-expired when refresh fails', async () => {
    const listener = vi.fn();
    window.addEventListener(SESSION_EXPIRED, listener);
    mockApi({ 'GET /api/shipments': { status: 401 }, 'POST /api/auth/refresh': { status: 401 } });

    await expect(api.get('/shipments')).rejects.toMatchObject({ status: 401 });
    expect(listener).toHaveBeenCalledOnce();
    window.removeEventListener(SESSION_EXPIRED, listener);
  });

  it('turns network failures into a friendly error', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));
    const error = await api.get('/x').catch((e: unknown) => e);
    expect(errorMessage(error)).toMatch(/cannot reach the server/i);
  });

  it('builds query strings without empty values', () => {
    expect(toQuery({ a: 1, b: '', c: undefined, d: 'x y' })).toBe('?a=1&d=x+y');
    expect(toQuery({})).toBe('');
  });
});

describe('domain helpers', () => {
  it.each([
    [0.999, 'LessThan1Kg'],
    [1, 'Between1And5Kg'],
    [5, 'Between1And5Kg'],
    [5.001, 'Above5Kg'],
  ])('weight %s kg is %s', (kg, category) => expect(weightCategoryOf(kg)).toBe(category));

  it('suggests where a status is scanned', () => {
    const route = {
      origin: { zipCode: 'A00', name: null, city: null },
      destination: { zipCode: 'B00', name: null, city: null },
      current: { zipCode: 'C00', name: null, city: null },
    };
    expect(suggestedZipFor('InTransit', route)).toBe('A00');
    expect(suggestedZipFor('Delivered', route)).toBe('B00');
    expect(suggestedZipFor('Lost', route)).toBe('C00');
  });

  it('checks permissions', () => {
    const user = { permissions: [Permissions.ShipmentsRead] } as never;
    expect(hasPermission(user, Permissions.ShipmentsRead)).toBe(true);
    expect(hasPermission(user, Permissions.ShipmentsWrite)).toBe(false);
    expect(hasPermission(null, Permissions.ShipmentsRead)).toBe(false);
  });
});
