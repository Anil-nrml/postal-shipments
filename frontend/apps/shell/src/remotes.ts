import { lazy } from 'react';
import type { RemoteComponents } from './ShellRoutes';

/**
 * Lazy-loaded federated modules: a remote's code is fetched only when its route is first visited,
 * and a remote that is down only affects its own routes (see RemoteBoundary).
 */
export const remotes: RemoteComponents = {
  Login: lazy(() => import('auth/Login')),
  Users: lazy(() => import('auth/Users')),
  Shipments: lazy(() => import('shipments/App')),
  Dashboard: lazy(() => import('shipments/Dashboard')),
  PostOffices: lazy(() => import('postOffices/App')),
  Tracking: lazy(() => import('tracking/App')),
};
