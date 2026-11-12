import type { PostOffice, Shipment } from '@postal/shared';

export function shipment(overrides: Partial<Shipment> = {}): Shipment {
  return {
    id: 's1',
    shipmentNumber: 'SHP-20261110-ABCDEFGH',
    type: 'Package',
    weightKg: 2.5,
    weightCategory: 'Between1And5Kg',
    status: 'ReceivedAtOrigin',
    origin: { zipCode: '500001', name: 'Hyderabad GPO', city: 'Hyderabad' },
    destination: { zipCode: '560001', name: 'Bengaluru GPO', city: 'Bengaluru' },
    current: { zipCode: '500001', name: 'Hyderabad GPO', city: 'Hyderabad' },
    isLocalDelivery: false,
    canEdit: true,
    allowedNextStatuses: ['ProcessedAtOrigin', 'Lost'],
    createdAtUtc: '2026-11-10T09:00:00Z',
    updatedAtUtc: '2026-11-10T09:00:00Z',
    version: 1,
    ...overrides,
  };
}

export function office(zipCode: string, name: string): PostOffice {
  return {
    id: zipCode,
    zipCode,
    name,
    address: 'Main road',
    city: 'City',
    state: 'State',
    country: 'India',
    contactNumber: '+91 1234567',
    isActive: true,
    canAcceptShipments: true,
    createdAtUtc: '2026-11-01T00:00:00Z',
    updatedAtUtc: '2026-11-01T00:00:00Z',
    version: 1,
  };
}
