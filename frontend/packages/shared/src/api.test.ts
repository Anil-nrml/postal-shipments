import { describe, expect, it, vi } from 'vitest';
import { api, ApiError, errorMessage, toQuery } from './api';
import { SESSION_EXPIRED } from './events';
import { mockApi } from './testing';
import { suggestedZipFor, weightCategoryOf } from './domain';
import { hasPermission } from './auth';
import { Permissions } from './types';

describe('api client', () => {
  it('sends correlation and W3C trace context on each request', async () => {
    const { calls } = mockApi({ 'GET /api/x': { body: {} } });

    await api.get('/x');

    const headers = calls[0]!.headers;
    const correlationId = headers.get('X-Correlation-ID');
    const traceId = headers.get('X-Trace-ID');
    const traceparent = headers.get('traceparent');
    expect(correlationId).toMatch(/^[a-f0-9]{32}$/);
    expect(traceId).toMatch(/^[a-f0-9]{32}$/);
    expect(traceparent).toMatch(/^00-([a-f0-9]{32})-[a-f0-9]{16}-01$/);
    expect(traceparent?.split('-')[1]).toBe(traceId);
  });

  it('logs frontend request lifecycle with identifiers and without query values', async () => {
    const requestLog = vi.spyOn(console, 'info').mockImplementation(() => {});
    mockApi({ 'GET /api/shipments': { body: {} } });

    await api.get('/shipments?search=private-value');

    expect(requestLog).toHaveBeenCalledWith(
      'API request started',
      expect.objectContaining({
        event: 'api.request.started',
        path: '/shipments',
        correlationId: expect.any(String),
        traceId: expect.any(String),
      }),
    );
    expect(requestLog).toHaveBeenCalledWith(
      'API request completed',
      expect.objectContaining({
        event: 'api.request.completed',
        path: '/shipments',
        status: 200,
        durationMs: expect.any(Number),
      }),
    );
    expect(JSON.stringify(requestLog.mock.calls)).not.toContain('private-value');
  });

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
    const errorLog = vi.spyOn(console, 'error').mockImplementation(() => {});
    const { calls } = mockApi({
      'POST /api/shipments': {
        status: 400,
        body: {
          title: 'Validation',
          errors: { weightKg: ['Must be > 0'] },
          correlationId: 'c-1',
          traceId: 't-1',
        },
      },
    });
    const error = await api.post('/shipments', {}).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).fieldErrors.weightKg).toEqual(['Must be > 0']);
    expect((error as ApiError).correlationId).toBe('c-1');
    expect((error as ApiError).traceId).toBe('t-1');
    expect(errorLog).toHaveBeenCalledWith(
      'API request failed',
      expect.objectContaining({
        event: 'api.request.failed',
        correlationId: calls[0]!.headers.get('X-Correlation-ID'),
        traceId: calls[0]!.headers.get('X-Trace-ID'),
        status: 400,
      }),
    );
  });

  it('refreshes once on 401 and replays the request', async () => {
    let calls = 0;
    const { calls: requests } = mockApi({
      'GET /api/shipments': () => (++calls === 1 ? { status: 401 } : { body: { ok: true } }),
      'POST /api/auth/refresh': { body: {} },
    });
    await expect(api.get('/shipments')).resolves.toEqual({ ok: true });
    expect(calls).toBe(2);
    expect(requests[0]!.headers.get('X-Correlation-ID')).toBe(requests[2]!.headers.get('X-Correlation-ID'));
    expect(requests[0]!.headers.get('X-Trace-ID')).toBe(requests[2]!.headers.get('X-Trace-ID'));
    expect(requests[0]!.headers.get('traceparent')?.split('-')[1]).toBe(
      requests[2]!.headers.get('traceparent')?.split('-')[1],
    );
    expect(requests[0]!.headers.get('traceparent')?.split('-')[2]).not.toBe(
      requests[2]!.headers.get('traceparent')?.split('-')[2],
    );
  });

  it('emits session-expired when refresh fails', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const listener = vi.fn();
    window.addEventListener(SESSION_EXPIRED, listener);
    mockApi({ 'GET /api/shipments': { status: 401 }, 'POST /api/auth/refresh': { status: 401 } });

    await expect(api.get('/shipments')).rejects.toMatchObject({ status: 401 });
    expect(listener).toHaveBeenCalledOnce();
    window.removeEventListener(SESSION_EXPIRED, listener);
  });

  it('turns network failures into a friendly error', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
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
