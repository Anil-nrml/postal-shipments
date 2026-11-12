import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import federation from '@originjs/vite-plugin-federation';
import { apiProxy, remotePorts, sharedDeps } from '../../federation.config';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), 'VITE_');
  // Remote locations are configuration, not code: each MFE can be redeployed independently
  // (e.g. to a CDN path per version) by changing these URLs.
  const remote = (name: keyof typeof remotePorts, envKey: string) =>
    env[envKey] ?? `http://localhost:${remotePorts[name]}/assets/remoteEntry.js`;

  return {
    plugins: [
      react(),
      federation({
        name: 'shell',
        remotes: {
          auth: remote('auth', 'VITE_REMOTE_AUTH'),
          shipments: remote('shipments', 'VITE_REMOTE_SHIPMENTS'),
          postOffices: remote('postOffices', 'VITE_REMOTE_POST_OFFICES'),
          tracking: remote('tracking', 'VITE_REMOTE_TRACKING'),
        },
        shared: sharedDeps,
      }),
    ],
    build: { target: 'esnext', modulePreload: false, minify: true, cssCodeSplit: false },
    server: { port: 5173, strictPort: true, proxy: apiProxy(env.VITE_GATEWAY_URL) },
    preview: { port: 5173, strictPort: true, proxy: apiProxy(env.VITE_GATEWAY_URL) },
  };
});
