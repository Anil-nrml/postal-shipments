import { describe, expect, it } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { mockApi, renderWithApp, testUser } from '@postal/shared/testing';
import { sessionKey } from '@postal/shared';
import LoginPage from './LoginPage';

describe('LoginPage', () => {
  it('validates required fields without calling the API', async () => {
    const { calls } = mockApi({});
    renderWithApp(<LoginPage />);

    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByText('User name is required')).toBeInTheDocument();
    expect(screen.getByText('Password is required')).toBeInTheDocument();
    expect(calls).toHaveLength(0);
  });

  it('signs in and stores the session', async () => {
    const user = testUser(['shipments.read']);
    const { calls } = mockApi({ 'POST /api/auth/login': { body: { user, accessTokenExpiresAtUtc: '2030-01-01T00:00:00Z' } } });
    const { client } = renderWithApp(<LoginPage />);

    await userEvent.type(screen.getByLabelText(/user name/i), 'operator');
    await userEvent.type(screen.getByLabelText(/password/i), 'Operator@12345!');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() => expect(client.getQueryData(sessionKey)).toEqual(user));
    expect(calls[0]!.body).toEqual({ userName: 'operator', password: 'Operator@12345!' });
  });

  it('shows the server message on failure', async () => {
    mockApi({ 'POST /api/auth/login': { status: 401, body: { detail: 'Invalid user name or password.' } } });
    renderWithApp(<LoginPage />);

    await userEvent.type(screen.getByLabelText(/user name/i), 'x');
    await userEvent.type(screen.getByLabelText(/password/i), 'y');
    await userEvent.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByText('Invalid user name or password.')).toBeInTheDocument();
  });
});
