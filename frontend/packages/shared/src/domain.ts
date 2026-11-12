import type { PostOfficeSummary, ShipmentStatus, ShipmentType, WeightCategory } from './types';

/** Presentation metadata only. Business rules (transitions, locations) are enforced by the API. */
export const statusLabels: Record<ShipmentStatus, string> = {
  ReceivedAtOrigin: 'Received at origin',
  ProcessedAtOrigin: 'Processed at origin',
  InTransit: 'In transit',
  ReceivedAtDestination: 'Received at destination',
  ProcessedAtDestination: 'Processed at destination',
  OutForDelivery: 'Out for delivery',
  Delivered: 'Delivered',
  FailedDeliveryAttempt: 'Failed delivery attempt',
  Lost: 'Lost',
};

export const allStatuses = Object.keys(statusLabels) as ShipmentStatus[];

export type StatusTone = 'default' | 'info' | 'primary' | 'success' | 'warning' | 'error';

export const statusTone: Record<ShipmentStatus, StatusTone> = {
  ReceivedAtOrigin: 'default',
  ProcessedAtOrigin: 'default',
  InTransit: 'info',
  ReceivedAtDestination: 'primary',
  ProcessedAtDestination: 'primary',
  OutForDelivery: 'warning',
  Delivered: 'success',
  FailedDeliveryAttempt: 'warning',
  Lost: 'error',
};

export const weightCategoryLabels: Record<WeightCategory, string> = {
  LessThan1Kg: 'Less than 1 kg',
  Between1And5Kg: '1 – 5 kg',
  Above5Kg: 'More than 5 kg',
};

export const shipmentTypeLabels: Record<ShipmentType, string> = { Letter: 'Letter', Package: 'Package' };

/** Live preview while typing; the server computes the stored value with the same boundaries. */
export function weightCategoryOf(kg: number): WeightCategory {
  if (kg < 1) return 'LessThan1Kg';
  if (kg <= 5) return 'Between1And5Kg';
  return 'Above5Kg';
}

/**
 * UI default for where a status is usually scanned. Only pre-fills the form; the API validates
 * the location and answers 422 if it is wrong.
 */
export function suggestedZipFor(
  status: ShipmentStatus,
  route: { origin: PostOfficeSummary; destination: PostOfficeSummary; current: PostOfficeSummary },
): string {
  switch (status) {
    case 'ProcessedAtOrigin':
    case 'InTransit':
      return route.origin.zipCode;
    case 'Lost':
      return route.current.zipCode;
    default:
      return route.destination.zipCode;
  }
}

export function formatPostOffice(po: PostOfficeSummary): string {
  return po.name ? `${po.name} (${po.zipCode})` : po.zipCode;
}

export function formatDateTime(utc: string): string {
  return new Date(utc).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

export function formatWeight(kg: number): string {
  return kg < 1 ? `${Math.round(kg * 1000)} g` : `${kg.toLocaleString(undefined, { maximumFractionDigits: 3 })} kg`;
}

export const ZIP_PATTERN = /^[A-Za-z0-9]{3,10}$/;

/** New key per form submission attempt; the API returns the original shipment on a retry. */
export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}
