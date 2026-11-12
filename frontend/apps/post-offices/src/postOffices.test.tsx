import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { mockApi, paged, renderWithApp, testUser } from '@postal/shared/testing';
import { Permissions, type PostOffice } from '@postal/shared';
import PostOfficeListPage from './PostOfficeListPage';
import PostOfficeFormPage from './PostOfficeFormPage';

const admin = testUser([Permissions.PostOfficesRead, Permissions.PostOfficesWrite, Permissions.PostOfficesDelete], { role: 'Administrator' });
const viewer = testUser([Permissions.PostOfficesRead]);

const gpo: PostOffice = {
  id: 'p1',
  zipCode: '500001',
  name: 'Hyderabad GPO',
  address: 'Abids',
  city: 'Hyderabad',
  state: 'Telangana',
  country: 'India',
  contactNumber: '+91 40 2346 3000',
  isActive: true,
  canAcceptShipments: true,
  createdAtUtc: '2026-11-01T00:00:00Z',
  updatedAtUtc: '2026-11-01T00:00:00Z',
  version: 1,
};

describe('PostOfficeListPage', () => {
  it('lists offices and explains a delete refused for active shipments', async () => {
    mockApi({
      'GET /api/postoffices': { body: paged([gpo]) },
      'DELETE /api/postoffices/p1': {
        status: 409,
        body: { detail: 'Post office 500001 has 2 active shipment(s). Deactivate it instead.', code: 'postoffice.has-active-shipments' },
      },
    });
    renderWithApp(<PostOfficeListPage />, { user: admin });

    expect(await screen.findByText('Hyderabad GPO')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Delete 500001' }));
    await userEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Delete' }));

    expect(await screen.findByText(/has 2 active shipment/)).toBeInTheDocument();
  });

  it('deactivates via the switch', async () => {
    const { calls } = mockApi({
      'GET /api/postoffices': { body: paged([gpo]) },
      'POST /api/postoffices/p1/deactivate': { body: { ...gpo, isActive: false } },
    });
    renderWithApp(<PostOfficeListPage />, { user: admin });

    await userEvent.click(await screen.findByRole('checkbox', { name: 'Deactivate 500001' }));
    await waitFor(() => expect(calls.some((c) => c.url.pathname === '/api/postoffices/p1/deactivate')).toBe(true));
  });

  it('is read-only for viewers', async () => {
    mockApi({ 'GET /api/postoffices': { body: paged([gpo]) } });
    renderWithApp(<PostOfficeListPage />, { user: viewer });
    await screen.findByText('Hyderabad GPO');
    expect(screen.queryByRole('button', { name: /delete/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();
  });
});

describe('PostOfficeFormPage', () => {
  it('validates and shows a duplicate ZIP on the field', async () => {
    mockApi({
      'POST /api/postoffices': {
        status: 409,
        body: { detail: 'A post office with ZIP code 500001 already exists.', code: 'postoffice.zip-exists' },
      },
    });
    renderWithApp(<PostOfficeFormPage />, { user: admin });

    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('3–10 letters or digits')).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText('ZIP code'), '500001');
    await userEvent.type(screen.getByLabelText('Name'), 'Duplicate');
    await userEvent.type(screen.getByLabelText('Address'), 'Road 1');
    await userEvent.type(screen.getByLabelText('City'), 'Hyderabad');
    await userEvent.type(screen.getByLabelText('State'), 'Telangana');
    await userEvent.type(screen.getByLabelText('Contact number'), '+91 1234567');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findAllByText('A post office with ZIP code 500001 already exists.')).not.toHaveLength(0);
  });
});
