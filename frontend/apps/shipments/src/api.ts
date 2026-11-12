import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  api,
  toQuery,
  type PagedResult,
  type Shipment,
  type ShipmentHistoryEntry,
  type ShipmentStatistics,
  type ShipmentStatus,
  type ShipmentType,
  type WeightCategory,
} from '@postal/shared';

export const BASE_PATH = '/shipments';

export interface ShipmentFilters {
  pageNumber: number;
  pageSize: number;
  sortBy: string;
  sortDirection: 'asc' | 'desc';
  shipmentNumber?: string;
  type?: ShipmentType;
  status?: ShipmentStatus;
  weightCategory?: WeightCategory;
  originPostOffice?: string;
  destinationPostOffice?: string;
  currentPostOffice?: string;
  createdFrom?: string;
  createdTo?: string;
}

export interface ShipmentInput {
  type: ShipmentType;
  weightKg: number;
  originZip: string;
  destinationZip: string;
}

const keys = {
  all: ['shipments'] as const,
  list: (f: ShipmentFilters) => ['shipments', 'list', f] as const,
  detail: (id: string) => ['shipments', 'detail', id] as const,
  history: (id: string) => ['shipments', 'history', id] as const,
  stats: ['shipments', 'stats'] as const,
};

export function useShipments(filters: ShipmentFilters) {
  return useQuery({
    queryKey: keys.list(filters),
    queryFn: ({ signal }) =>
      api.get<PagedResult<Shipment>>(
        `/shipments${toQuery({
          ...filters,
          // date inputs give local dates; send the full local day as UTC instants
          createdFrom: filters.createdFrom ? new Date(`${filters.createdFrom}T00:00:00`).toISOString() : undefined,
          createdTo: filters.createdTo ? new Date(`${filters.createdTo}T23:59:59.999`).toISOString() : undefined,
        })}`,
        signal,
      ),
    placeholderData: keepPreviousData, // keep the table steady while paging
  });
}

export function useShipment(id: string | undefined) {
  return useQuery({ queryKey: keys.detail(id ?? ''), queryFn: () => api.get<Shipment>(`/shipments/${id}`), enabled: !!id });
}

export function useShipmentHistory(id: string) {
  return useQuery({ queryKey: keys.history(id), queryFn: () => api.get<ShipmentHistoryEntry[]>(`/shipments/${id}/history`) });
}

export function useStatistics() {
  return useQuery({ queryKey: keys.stats, queryFn: () => api.get<ShipmentStatistics>('/shipments/statistics'), refetchInterval: 30_000 });
}

function useInvalidateShipments() {
  const client = useQueryClient();
  return (shipment?: Shipment) => {
    if (shipment) client.setQueryData(keys.detail(shipment.id), shipment);
    return client.invalidateQueries({ queryKey: keys.all });
  };
}

export function useCreateShipment() {
  const invalidate = useInvalidateShipments();
  return useMutation({
    mutationFn: ({ input, idempotencyKey }: { input: ShipmentInput; idempotencyKey: string }) =>
      api.post<Shipment>('/shipments', input, { 'Idempotency-Key': idempotencyKey }),
    onSuccess: (s) => invalidate(s),
  });
}

export function useUpdateShipment(id: string) {
  const invalidate = useInvalidateShipments();
  return useMutation({
    mutationFn: (input: Omit<ShipmentInput, 'originZip'> & { version: number }) => api.put<Shipment>(`/shipments/${id}`, input),
    onSuccess: (s) => invalidate(s),
  });
}

export function useDeleteShipment() {
  const invalidate = useInvalidateShipments();
  return useMutation({
    mutationFn: (s: Shipment) => api.delete(`/shipments/${s.id}?version=${s.version}`),
    onSuccess: () => invalidate(),
  });
}

export function useChangeStatus(id: string) {
  const invalidate = useInvalidateShipments();
  return useMutation({
    mutationFn: (input: { status: ShipmentStatus; postOfficeZip: string; remarks?: string; version: number }) =>
      api.patch<Shipment>(`/shipments/${id}/status`, input),
    onSuccess: (s) => invalidate(s),
  });
}

export function useCorrectStatus(id: string) {
  const invalidate = useInvalidateShipments();
  return useMutation({
    mutationFn: (input: { status: ShipmentStatus; postOfficeZip: string; reason: string; version: number }) =>
      api.post<Shipment>(`/shipments/${id}/status/corrections`, input),
    onSuccess: (s) => invalidate(s),
  });
}
