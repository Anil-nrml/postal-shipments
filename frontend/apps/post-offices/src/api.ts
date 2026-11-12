import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, toQuery, type PagedResult, type PostOffice } from '@postal/shared';

export const BASE_PATH = '/post-offices';

export interface PostOfficeInput {
  zipCode: string;
  name: string;
  address: string;
  city: string;
  state: string;
  country: string;
  contactNumber: string;
}

export function usePostOffices(params: { pageNumber: number; pageSize: number; search?: string; isActive?: boolean }) {
  return useQuery({
    queryKey: ['postoffices', 'list', params],
    queryFn: ({ signal }) => api.get<PagedResult<PostOffice>>(`/postoffices${toQuery(params)}`, signal),
    placeholderData: keepPreviousData,
  });
}

export function usePostOffice(id: string | undefined) {
  return useQuery({ queryKey: ['postoffices', 'detail', id], queryFn: () => api.get<PostOffice>(`/postoffices/${id}`), enabled: !!id });
}

function useInvalidate() {
  const client = useQueryClient();
  // Also refreshes the shared picker used by the shipments MFE.
  return () => client.invalidateQueries({ queryKey: ['postoffices'] });
}

export function useSavePostOffice(id?: string) {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: ({ input, version }: { input: PostOfficeInput; version?: number }) =>
      id ? api.put<PostOffice>(`/postoffices/${id}`, { ...input, version }) : api.post<PostOffice>('/postoffices', input),
    onSuccess: invalidate,
  });
}

export function useSetActive() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: ({ office, active }: { office: PostOffice; active: boolean }) =>
      api.post<PostOffice>(`/postoffices/${office.id}/${active ? 'activate' : 'deactivate'}`),
    onSuccess: invalidate,
  });
}

export function useDeletePostOffice() {
  const invalidate = useInvalidate();
  return useMutation({ mutationFn: (office: PostOffice) => api.delete(`/postoffices/${office.id}`), onSuccess: invalidate });
}
