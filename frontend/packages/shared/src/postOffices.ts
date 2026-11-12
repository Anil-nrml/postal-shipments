import { useQuery } from '@tanstack/react-query';
import { api } from './api';
import type { PagedResult, PostOffice } from './types';

/**
 * Post office choices for selects and filters. Owned by the post-offices domain but read-only here,
 * so every MFE can offer the same picker without importing another MFE.
 */
export function usePostOfficeOptions(options: { activeOnly?: boolean } = {}) {
  return useQuery({
    queryKey: ['postoffices', 'options', options.activeOnly ?? false],
    queryFn: () =>
      api.get<PagedResult<PostOffice>>(`/postoffices?pageSize=100${options.activeOnly ? '&isActive=true' : ''}`),
    select: (page) => page.items,
    staleTime: 60_000,
  });
}
