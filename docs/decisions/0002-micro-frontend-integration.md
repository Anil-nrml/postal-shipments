# ADR-0002: Micro-frontends with Vite Module Federation

- **Status:** Accepted
- **Date:** 2026-11-10

## Context
The frontend must be split into independently deployable React/TypeScript micro-frontends: shell, auth, shipments, post offices and tracking.

## Problem
How do we compose the MFEs at runtime, share libraries safely, and keep authentication secure?

## Options considered
1. **Build-time composition** (npm packages): simple, but every change needs a shell rebuild, so deployments are not independent.
2. **iframes**: strong isolation, but poor UX (routing, modals, styling) and awkward auth sharing.
3. **single-spa**: framework-agnostic, but more ceremony than a React-only team needs.
4. **Module Federation** (Vite + `@originjs/vite-plugin-federation`).

## Decision
Option 4. The shell lazily imports route-level components from four remotes. Remote URLs are environment configuration (`VITE_REMOTE_*`).

## Rationale
- Each remote is built and deployed on its own. The shell picks up a new `remoteEntry.js` without rebuilding.
- The frontend Dockerfile builds one selected workspace into a static Nginx image. Remote entry files are served with CORS enabled and `Cache-Control: no-store`; hashed assets are cacheable.
- `react`, `react-dom`, `react-router-dom`, `@tanstack/react-query`, `@mui/material` and emotion are shared singletons. The shell's router, QueryClient (and therefore the session) and theme are visible inside remotes.
- `@postal/shared` (API client, auth hooks, UI primitives) is compiled into each remote, which gives reuse without runtime coupling.
- The exposed contract is tiny: prop-less, default-exported route components.

## Consequences
- Each remote is wrapped in `Suspense` + `ErrorBoundary`; a remote that is down only breaks its own routes.
- MFEs never import each other. They communicate through URLs and window events (`postal:session-expired`).
- Every remote can run standalone (`npm run dev -w @postal/mfe-shipments`).

## Trade-offs
- The originjs plugin requires remotes to be *built* (`vite build && vite preview`); only the host runs in dev mode. `npm run dev` builds remotes first.
- Shared singleton versions must stay compatible. They are pinned once at the workspace root; a breaking change ships as a new exposed module (`./AppV2`).
- Remote entry URLs are compiled into the shell from `VITE_REMOTE_*`; changing a remote's code does not require a shell deploy, but changing its URL does.
- Federation adds a network hop and some bundle complexity compared with a single SPA.
