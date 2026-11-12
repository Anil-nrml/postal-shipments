import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient } from '@tanstack/react-query';
import { AppProviders, Permissions, sessionKey, type User } from '@postal/shared';
import { testUser } from '@postal/shared/testing';
import { ShellRoutes, type RemoteComponents } from './ShellRoutes';

const stub = (text: string) => () => <div>{text}</div>;
const remotes: RemoteComponents = {
  Login: stub('LOGIN REMOTE'),
  Users: stub('USERS REMOTE'),
  Shipments: stub('SHIPMENTS REMOTE'),
  Dashboard: stub('DASHBOARD REMOTE'),
  PostOffices: stub('POST OFFICES REMOTE'),
  Tracking: () => {
    throw new Error('remote failed to load');
  },
};

// jsdom has no matchMedia; pretend to be a desktop so the permanent navigation drawer renders.
beforeEach(() => {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: true,
    media: query,
    onchange: null,
    addListener: () => {},
    removeListener: () => {},
    addEventListener: () => {},
    removeEventListener: () => {},
    dispatchEvent: () => false,
  }));
});

function renderShell(route: string, user: User | null) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  client.setQueryData(sessionKey, user);
  return render(
    <AppProviders client={client}>
      <MemoryRouter initialEntries={[route]}>
        <ShellRoutes remotes={remotes} />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe('shell', () => {
  it('redirects anonymous users to the login remote', () => {
    renderShell('/shipments', null);
    expect(screen.getByText('LOGIN REMOTE')).toBeInTheDocument();
  });

  it('mounts the matching remote and filters navigation by permission', () => {
    renderShell('/shipments', testUser([Permissions.ShipmentsRead]));
    expect(screen.getByText('SHIPMENTS REMOTE')).toBeInTheDocument();
    const nav = screen.getByRole('navigation', { name: 'Main' });
    expect(nav).toHaveTextContent('Shipments');
    expect(nav).not.toHaveTextContent('Post offices');
    expect(nav).not.toHaveTextContent('Users & roles');
  });

  it('blocks routes the user may not open', () => {
    renderShell('/users', testUser([Permissions.ShipmentsRead]));
    expect(screen.getByText('You do not have access to this page.')).toBeInTheDocument();
    expect(screen.queryByText('USERS REMOTE')).not.toBeInTheDocument();
  });

  it('isolates a failing remote behind an error boundary', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => {});
    renderShell('/tracking', testUser([Permissions.ShipmentsRead]));
    expect(screen.getByText('Tracking is unavailable')).toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument(); // shell still works
    spy.mockRestore();
  });
});
