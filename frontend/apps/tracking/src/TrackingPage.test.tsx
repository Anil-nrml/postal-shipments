import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { render } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { AppProviders, Permissions, sessionKey, type Tracking } from '@postal/shared';
import { mockApi, testUser } from '@postal/shared/testing';
import TrackingApp from './App';

const tracking: Tracking = {
  shipmentId: 's1',
  shipmentNumber: 'SHP-20261110-ABCDEFGH',
  type: 'Package',
  status: 'InTransit',
  origin: { zipCode: '500001', name: 'Hyderabad GPO', city: 'Hyderabad' },
  destination: { zipCode: '560001', name: 'Bengaluru GPO', city: 'Bengaluru' },
  current: { zipCode: '500001', name: 'Hyderabad GPO', city: 'Hyderabad' },
  updatedAtUtc: '2026-11-10T11:00:00Z',
  history: [
    { id: 1, previousStatus: null, status: 'ReceivedAtOrigin', postOffice: { zipCode: '500001', name: 'Hyderabad GPO', city: null }, performedBy: 'clerk', remarks: null, isCorrection: false, occurredAtUtc: '2026-11-10T09:00:00Z' },
    { id: 2, previousStatus: 'ReceivedAtOrigin', status: 'ProcessedAtOrigin', postOffice: { zipCode: '500001', name: 'Hyderabad GPO', city: null }, performedBy: 'sorter', remarks: null, isCorrection: false, occurredAtUtc: '2026-11-10T10:00:00Z' },
    { id: 3, previousStatus: 'ProcessedAtOrigin', status: 'InTransit', postOffice: { zipCode: '500001', name: 'Hyderabad GPO', city: null }, performedBy: 'driver', remarks: 'Truck 12', isCorrection: false, occurredAtUtc: '2026-11-10T11:00:00Z' },
  ],
};

function renderAt(route: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(sessionKey, testUser([Permissions.ShipmentsRead]));
  return render(
    <AppProviders client={client}>
      <MemoryRouter initialEntries={[route]}>
        <Routes>
          <Route path="/tracking/*" element={<TrackingApp />} />
        </Routes>
      </MemoryRouter>
    </AppProviders>,
  );
}

describe('TrackingPage', () => {
  it('rejects malformed numbers without calling the API', async () => {
    const { calls } = mockApi({});
    renderAt('/tracking');
    await userEvent.type(screen.getByLabelText('Shipment number'), 'abc');
    await userEvent.click(screen.getByRole('button', { name: 'Track' }));
    expect(screen.getByText('Format: SHP-YYYYMMDD-XXXXXXXX')).toBeInTheDocument();
    expect(calls).toHaveLength(0);
  });

  it('shows the timeline newest first', async () => {
    mockApi({ 'GET /api/tracking/SHP-20261110-ABCDEFGH': { body: tracking } });
    renderAt('/tracking');
    await userEvent.type(screen.getByLabelText('Shipment number'), 'shp-20261110-abcdefgh');
    await userEvent.click(screen.getByRole('button', { name: 'Track' }));

    expect(await screen.findByText('Truck 12')).toBeInTheDocument();
    const labels = screen.getAllByText(/^(Received at origin|Processed at origin|In transit)$/).map((e) => e.textContent);
    expect(labels[labels.length - 3]).toBe('In transit');
    expect(screen.getByRole('link', { name: 'Open shipment' })).toHaveAttribute('href', '/shipments/s1');
  });

  it('reports unknown shipments', async () => {
    mockApi({ 'GET /api/tracking/SHP-20261110-ZZZZZZZZ': { status: 404, body: { detail: 'not found' } } });
    renderAt('/tracking/SHP-20261110-ZZZZZZZZ');
    expect(await screen.findByText('No shipment found with number SHP-20261110-ZZZZZZZZ.')).toBeInTheDocument();
  });
});
