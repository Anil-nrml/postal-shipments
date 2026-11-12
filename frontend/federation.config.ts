/**
 * Libraries every micro-frontend loads exactly once (singletons). Sharing react, the router,
 * react-query and the MUI/emotion theme means the shell's QueryClient, router and theme
 * contexts are visible inside each remote.
 */
export const sharedDeps = [
  'react',
  'react-dom',
  'react-router-dom',
  '@tanstack/react-query',
  '@mui/material',
  '@emotion/react',
  '@emotion/styled',
];

export const remotePorts = {
  auth: 5174,
  shipments: 5175,
  postOffices: 5176,
  tracking: 5177,
} as const;

/** Dev proxy: the browser only ever talks to its own origin, so auth cookies stay same-site. */
export const apiProxy = (gatewayUrl?: string) => ({
  '/api': { target: gatewayUrl || 'http://localhost:5000', changeOrigin: false },
});
