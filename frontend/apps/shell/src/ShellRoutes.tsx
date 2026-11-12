import { Suspense, type ComponentType, type ReactNode } from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import { Alert } from '@mui/material';
import { ErrorBoundary, LoadingState, Permissions, type Permission, useSession, hasPermission } from '@postal/shared';
import { Layout } from './Layout';

export interface RemoteComponents {
  Login: ComponentType;
  Users: ComponentType;
  Shipments: ComponentType;
  Dashboard: ComponentType;
  PostOffices: ComponentType;
  Tracking: ComponentType;
}

/** Suspense + error boundary per remote: isolation between independently deployed MFEs. */
function RemoteBoundary({ name, children }: { name: string; children: ReactNode }) {
  return (
    <ErrorBoundary name={name}>
      <Suspense fallback={<LoadingState label={`Loading ${name}…`} />}>{children}</Suspense>
    </ErrorBoundary>
  );
}

function RequireAuth({ children }: { children: ReactNode }) {
  const session = useSession();
  const location = useLocation();
  if (session.isLoading) return <LoadingState label="Checking your session…" />;
  if (!session.data) return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
  return <>{children}</>;
}

function Guard({ permission, children }: { permission: Permission; children: ReactNode }) {
  const { data } = useSession();
  return hasPermission(data, permission) ? (
    <>{children}</>
  ) : (
    <Alert severity="warning">You do not have access to this page.</Alert>
  );
}

export function ShellRoutes({ remotes }: { remotes: RemoteComponents }) {
  const { Login, Users, Shipments, Dashboard, PostOffices, Tracking } = remotes;
  return (
    <Routes>
      <Route
        path="/login"
        element={
          <RemoteBoundary name="Sign-in">
            <Login />
          </RemoteBoundary>
        }
      />
      <Route
        element={
          <RequireAuth>
            <Layout />
          </RequireAuth>
        }
      >
        <Route
          index
          element={
            <Guard permission={Permissions.ShipmentsRead}>
              <RemoteBoundary name="Dashboard">
                <Dashboard />
              </RemoteBoundary>
            </Guard>
          }
        />
        <Route
          path="shipments/*"
          element={
            <Guard permission={Permissions.ShipmentsRead}>
              <RemoteBoundary name="Shipments">
                <Shipments />
              </RemoteBoundary>
            </Guard>
          }
        />
        <Route
          path="tracking/*"
          element={
            <Guard permission={Permissions.ShipmentsRead}>
              <RemoteBoundary name="Tracking">
                <Tracking />
              </RemoteBoundary>
            </Guard>
          }
        />
        <Route
          path="post-offices/*"
          element={
            <Guard permission={Permissions.PostOfficesRead}>
              <RemoteBoundary name="Post offices">
                <PostOffices />
              </RemoteBoundary>
            </Guard>
          }
        />
        <Route
          path="users/*"
          element={
            <Guard permission={Permissions.UsersManage}>
              <RemoteBoundary name="User management">
                <Users />
              </RemoteBoundary>
            </Guard>
          }
        />
        <Route path="*" element={<Alert severity="info">Page not found.</Alert>} />
      </Route>
    </Routes>
  );
}
