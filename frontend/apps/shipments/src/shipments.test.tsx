import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { mockApi, paged, renderWithApp, testUser } from '@postal/shared/testing';
import { allStatuses, Permissions, statusLabels } from '@postal/shared';
import ShipmentListPage from './ShipmentListPage';
import ShipmentFormPage from './ShipmentFormPage';
import ShipmentDetailsPage from './ShipmentDetailsPage';
import DashboardPage from './DashboardPage';
import { office, shipment } from './fixtures';

const operator = testUser([Permissions.ShipmentsRead, Permissions.ShipmentsWrite, Permissions.ShipmentsUpdateStatus, Permissions.PostOfficesRead]);
const viewer = testUser([Permissions.ShipmentsRead, Permissions.PostOfficesRead], { role: 'ShipmentViewer' });
const offices = paged([office('500001', 'Hyderabad GPO'), office('560001', 'Bengaluru GPO')]);

describe('ShipmentListPage', () => {
  it('renders rows and paging metadata from the API', async () => {
    mockApi({
      'GET /api/shipments': { body: paged([shipment(), shipment({ id: 's2', shipmentNumber: 'SHP-20261110-ZZZZZZZZ', status: 'InTransit' })], 1, 20, 42) },
      'GET /api/postoffices': { body: offices },
    });
    renderWithApp(<ShipmentListPage />, { user: operator });

    const table = await screen.findByRole('table', { name: 'Shipments' });
    expect(within(table).getByText('SHP-20261110-ABCDEFGH')).toBeInTheDocument();
    expect(within(table).getByText('In transit')).toBeInTheDocument();
    expect(screen.getByText('42 shipment(s)')).toBeInTheDocument();
    expect(screen.getByText('1–20 of 42')).toBeInTheDocument();
  });

  it('sends filters and paging to the server', async () => {
    const { calls } = mockApi({
      'GET /api/shipments': ({ url }) => ({ body: paged([shipment()], Number(url.searchParams.get('pageNumber') ?? 1), 20, 42) }),
      'GET /api/postoffices': { body: offices },
    });
    renderWithApp(<ShipmentListPage />, { user: operator, route: '/?status=InTransit&weightCategory=Above5Kg' });

    await screen.findByRole('table', { name: 'Shipments' });
    const first = calls.find((c) => c.url.pathname === '/api/shipments')!;
    expect(first.url.searchParams.get('status')).toBe('InTransit');
    expect(first.url.searchParams.get('weightCategory')).toBe('Above5Kg');

    await userEvent.click(screen.getByRole('button', { name: /next page/i }));
    await waitFor(() =>
      expect(calls.some((c) => c.url.pathname === '/api/shipments' && c.url.searchParams.get('pageNumber') === '2')).toBe(true),
    );
  });

  it('shows an empty state', async () => {
    mockApi({ 'GET /api/shipments': { body: paged([]) }, 'GET /api/postoffices': { body: offices } });
    renderWithApp(<ShipmentListPage />, { user: operator });
    expect(await screen.findByText('No shipments yet')).toBeInTheDocument();
  });

  it('shows API errors with a retry', async () => {
    mockApi({ 'GET /api/shipments': { status: 500, body: { detail: 'Boom' } }, 'GET /api/postoffices': { body: offices } });
    renderWithApp(<ShipmentListPage />, { user: operator });
    expect(await screen.findByText('Boom')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();
  });

  it('hides "New shipment" from viewers', async () => {
    mockApi({ 'GET /api/shipments': { body: paged([shipment()]) }, 'GET /api/postoffices': { body: offices } });
    renderWithApp(<ShipmentListPage />, { user: viewer });
    await screen.findByRole('table', { name: 'Shipments' });
    expect(screen.queryByRole('link', { name: /new shipment/i })).not.toBeInTheDocument();
  });
});

describe('ShipmentFormPage', () => {
  it('validates before submitting', async () => {
    const { calls } = mockApi({ 'GET /api/postoffices': { body: offices } });
    renderWithApp(<ShipmentFormPage />, { user: operator });

    await userEvent.click(await screen.findByRole('button', { name: /register shipment/i }));

    expect(await screen.findByText('Enter a weight in kg')).toBeInTheDocument();
    expect(screen.getByText('Choose the origin post office')).toBeInTheDocument();
    expect(calls.filter((c) => c.method === 'POST')).toHaveLength(0);
  });

  it('submits with an Idempotency-Key and shows the live weight category', async () => {
    const { calls } = mockApi({
      'GET /api/postoffices': { body: offices },
      'POST /api/shipments': { status: 201, body: shipment() },
      'GET /api/shipments/s1': { body: shipment() },
      'GET /api/shipments/s1/history': { body: [] },
    });
    renderWithApp(<ShipmentFormPage />, { user: operator });

    await userEvent.type(await screen.findByLabelText(/weight/i), '7.5');
    expect(screen.getByText('Category: More than 5 kg')).toBeInTheDocument();

    await userEvent.click(screen.getByLabelText('Origin post office'));
    await userEvent.click(await screen.findByRole('option', { name: /Hyderabad GPO/ }));
    await userEvent.click(screen.getByLabelText('Destination post office'));
    await userEvent.click(await screen.findByRole('option', { name: /Bengaluru GPO/ }));
    await userEvent.click(screen.getByRole('button', { name: /register shipment/i }));

    await waitFor(() => expect(calls.some((c) => c.method === 'POST')).toBe(true));
    const post = calls.find((c) => c.method === 'POST')!;
    expect(post.body).toEqual({ type: 'Package', weightKg: 7.5, originZip: '500001', destinationZip: '560001' });
    expect(post.headers.get('Idempotency-Key')).toMatch(/[0-9a-f-]{36}/);
  });

  it('maps server field errors onto the form', async () => {
    mockApi({
      'GET /api/postoffices': { body: offices },
      'POST /api/shipments': { status: 422, body: { detail: 'Post office 560001 is not accepting shipments.', code: 'postoffice.inactive' } },
    });
    renderWithApp(<ShipmentFormPage />, { user: operator });

    await userEvent.type(await screen.findByLabelText(/weight/i), '1');
    await userEvent.click(screen.getByLabelText('Origin post office'));
    await userEvent.click(await screen.findByRole('option', { name: /Hyderabad GPO/ }));
    await userEvent.click(screen.getByLabelText('Destination post office'));
    await userEvent.click(await screen.findByRole('option', { name: /Bengaluru GPO/ }));
    await userEvent.click(screen.getByRole('button', { name: /register shipment/i }));

    expect(await screen.findByText('Post office 560001 is not accepting shipments.')).toBeInTheDocument();
  });
});

describe('ShipmentDetailsPage', () => {
  const history = [
    {
      id: 1,
      previousStatus: null,
      status: 'ReceivedAtOrigin',
      postOffice: { zipCode: '500001', name: 'Hyderabad GPO', city: 'Hyderabad' },
      performedBy: 'clerk',
      remarks: 'Shipment registered',
      isCorrection: false,
      occurredAtUtc: '2026-11-10T09:00:00Z',
    },
  ];

  it('shows the timeline and records the next status with the current version', async () => {
    const { calls } = mockApi({
      'GET /api/shipments/s1': { body: shipment({ version: 4 }) },
      'GET /api/shipments/s1/history': { body: history },
      'PATCH /api/shipments/s1/status': { body: shipment({ status: 'ProcessedAtOrigin', version: 5 }) },
    });
    renderWithApp(<ShipmentDetailsPage />, { user: operator, route: '/s1', path: '/:id' });

    expect(await screen.findByText('“Shipment registered”')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: /update status/i }));
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH')).toBe(true));
    expect(calls.find((c) => c.method === 'PATCH')!.body).toEqual({
      status: 'ProcessedAtOrigin',
      postOfficeZip: '500001',
      version: 4,
    });
  });

  it('offers every other status in the regular update dialog', async () => {
    mockApi({
      'GET /api/shipments/s1': { body: shipment() },
      'GET /api/shipments/s1/history': { body: history },
    });
    renderWithApp(<ShipmentDetailsPage />, { user: operator, route: '/s1', path: '/:id' });

    await userEvent.click(await screen.findByRole('button', { name: /update status/i }));
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('combobox', { name: 'New status' }));

    for (const status of allStatuses.filter((value) => value !== 'ReceivedAtOrigin')) {
      expect(screen.getByRole('option', { name: statusLabels[status] })).toBeInTheDocument();
    }
    expect(screen.getByText('Only valid next steps in the shipment lifecycle will be accepted.')).toBeInTheDocument();
  });

  it('explains a concurrent update conflict', async () => {
    mockApi({
      'GET /api/shipments/s1': { body: shipment() },
      'GET /api/shipments/s1/history': { body: history },
      'PATCH /api/shipments/s1/status': { status: 409, body: { detail: 'Shipment was changed by someone else.' } },
    });
    renderWithApp(<ShipmentDetailsPage />, { user: operator, route: '/s1', path: '/:id' });

    await userEvent.click(await screen.findByRole('button', { name: /update status/i }));
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('Shipment was changed by someone else.')).toBeInTheDocument();
  });

  it('hides write actions from viewers', async () => {
    mockApi({ 'GET /api/shipments/s1': { body: shipment() }, 'GET /api/shipments/s1/history': { body: history } });
    renderWithApp(<ShipmentDetailsPage />, { user: viewer, route: '/s1', path: '/:id' });
    await screen.findByText('SHP-20261110-ABCDEFGH');
    expect(screen.queryByRole('button', { name: /update status/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /delete/i })).not.toBeInTheDocument();
  });
});

describe('DashboardPage', () => {
  it('shows totals and status distribution', async () => {
    mockApi({
      'GET /api/shipments/statistics': {
        body: {
          total: 12,
          inTransit: 3,
          delivered: 5,
          failedDeliveries: 1,
          lost: 0,
          byStatus: { ReceivedAtOrigin: 3, InTransit: 3, Delivered: 5, FailedDeliveryAttempt: 1 },
          byWeightCategory: { LessThan1Kg: 4, Between1And5Kg: 6, Above5Kg: 2 },
        },
      },
    });
    renderWithApp(<DashboardPage />, { user: viewer });
    expect(await screen.findByText('Total shipments')).toBeInTheDocument();
    expect(screen.getByText('12')).toBeInTheDocument();
    expect(within(screen.getByRole('list', { name: 'Shipments by status' })).getAllByRole('listitem')).toHaveLength(9);
  });
});
