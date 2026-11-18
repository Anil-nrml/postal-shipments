# ADR-0009: RS256 JWTs in HttpOnly cookies with rotating refresh tokens

- **Status:** Accepted
- **Date:** 2026-11-10

## Context
JWT authentication is required, with role and permission authorisation, refresh token rotation and revocation, and no tokens in localStorage.

## Problem
How do the browser, several MFEs and several services share authentication securely?

## Options considered
1. Access token in localStorage plus a Bearer header: readable by any injected script (XSS steals the session).
2. Access token in memory plus a refresh token in an HttpOnly cookie: good, but every MFE would need the in-memory token, which means passing secrets across federation boundaries.
3. **Both tokens in HttpOnly, Secure, SameSite=Strict cookies, plus a double-submit CSRF token.**
4. BFF with server-side sessions: the strongest option, but it adds a stateful component.

## Decision
Option 3.
- Identity signs **RS256** tokens (15 min) with a private key held only by Identity (PEM file locally; Key Vault in production). Resource services fetch the public key from `/.well-known/jwks.json`; no shared secret exists.
- Claims: `sub`, `name`, `role`, one `perm` claim per permission. Services authorise **by permission**, never by role name.
- `access_token` cookie, path `/api`. `refresh_token` cookie, path `/api/auth` (it is sent only to refresh and logout). `csrf_token` readable cookie, echoed by the SPA as `X-CSRF-Token` on writes and checked by every service.
- Refresh tokens are random 384-bit values stored as SHA-256 hashes. They rotate on each use; replaying a rotated token revokes the whole family (theft detection). A role change, deactivation or password reset revokes all of the user's refresh tokens.
- Passwords use ASP.NET Core Identity's PBKDF2-HMAC-SHA512 (100k iterations, salted, versioned and rehashed on upgrade). Accounts lock after 5 failures for 5 minutes, login is rate limited per IP, and the error message is generic.

## Rationale
JavaScript never touches a token, so XSS cannot exfiltrate one. SameSite=Strict plus the double-submit token defends against CSRF. All MFEs share the session automatically because the cookies belong to the shell's origin.

## Consequences
- Frontend and API must be same-site (the gateway behind the same host, or the Vite proxy in development).
- Development returns the access token in the login body for Swagger (`Auth:ExposeAccessTokenInBody`). It is off by default.
- Revocation of access tokens is bounded by their 15-minute lifetime.

## Trade-offs
- In production, federate Identity to Entra ID (OIDC) or put a BFF in front, and keep this service for application roles. A BFF would also allow sliding sessions and instant revocation.
