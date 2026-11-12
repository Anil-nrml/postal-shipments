// Mirrors the backend DTOs (enums are serialised as strings).

export type ShipmentType = 'Letter' | 'Package';
export type WeightCategory = 'LessThan1Kg' | 'Between1And5Kg' | 'Above5Kg';
export type ShipmentStatus =
  | 'ReceivedAtOrigin'
  | 'ProcessedAtOrigin'
  | 'InTransit'
  | 'ReceivedAtDestination'
  | 'ProcessedAtDestination'
  | 'OutForDelivery'
  | 'Delivered'
  | 'FailedDeliveryAttempt'
  | 'Lost';

export interface PagedResult<T> {
  items: T[];
  totalRecords: number;
  pageNumber: number;
  pageSize: number;
  totalPages: number;
  hasNextPage: boolean;
  hasPreviousPage: boolean;
}

export interface PostOfficeSummary {
  zipCode: string;
  name: string | null;
  city: string | null;
}

export interface Shipment {
  id: string;
  shipmentNumber: string;
  type: ShipmentType;
  weightKg: number;
  weightCategory: WeightCategory;
  status: ShipmentStatus;
  origin: PostOfficeSummary;
  destination: PostOfficeSummary;
  current: PostOfficeSummary;
  isLocalDelivery: boolean;
  canEdit: boolean;
  allowedNextStatuses: ShipmentStatus[];
  createdAtUtc: string;
  updatedAtUtc: string;
  version: number;
}

export interface ShipmentHistoryEntry {
  id: number;
  previousStatus: ShipmentStatus | null;
  status: ShipmentStatus;
  postOffice: PostOfficeSummary;
  performedBy: string;
  remarks: string | null;
  isCorrection: boolean;
  occurredAtUtc: string;
}

export interface Tracking {
  shipmentId: string;
  shipmentNumber: string;
  type: ShipmentType;
  status: ShipmentStatus;
  origin: PostOfficeSummary;
  destination: PostOfficeSummary;
  current: PostOfficeSummary;
  updatedAtUtc: string;
  history: ShipmentHistoryEntry[];
}

export interface ShipmentStatistics {
  total: number;
  inTransit: number;
  delivered: number;
  failedDeliveries: number;
  lost: number;
  byStatus: Record<ShipmentStatus, number>;
  byWeightCategory: Record<WeightCategory, number>;
}

export interface PostOffice {
  id: string;
  zipCode: string;
  name: string;
  address: string;
  city: string;
  state: string;
  country: string;
  contactNumber: string;
  isActive: boolean;
  canAcceptShipments: boolean;
  createdAtUtc: string;
  updatedAtUtc: string;
  version: number;
}

export type Role = 'Administrator' | 'PostOfficeManager' | 'ShipmentOperator' | 'ShipmentViewer';

export interface User {
  id: string;
  userName: string;
  displayName: string;
  role: Role;
  isActive: boolean;
  permissions: string[];
  lastLoginAtUtc: string | null;
}

export const Permissions = {
  ShipmentsRead: 'shipments.read',
  ShipmentsWrite: 'shipments.write',
  ShipmentsDelete: 'shipments.delete',
  ShipmentsUpdateStatus: 'shipments.status',
  ShipmentsCorrectStatus: 'shipments.correct',
  PostOfficesRead: 'postoffices.read',
  PostOfficesWrite: 'postoffices.write',
  PostOfficesDelete: 'postoffices.delete',
  UsersManage: 'users.manage',
} as const;

export type Permission = (typeof Permissions)[keyof typeof Permissions];
