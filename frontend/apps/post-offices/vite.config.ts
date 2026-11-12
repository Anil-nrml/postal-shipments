import { fileURLToPath } from 'node:url';
import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import federation from '@originjs/vite-plugin-federation';
import { apiProxy, remotePorts, sharedDeps } from '../../federation.config';

export default defineConfig(({ mode }) => {
  const envDir = fileURLToPath(new URL('../..', import.meta.url));
  const env = loadEnv(mode, envDir, 'VITE_');

  return {
    envDir,
    plugins: [
      react(),
      federation({
        name: 'postOffices',
        filename: 'remoteEntry.js',
        exposes: { './App': './src/App.tsx' },
        shared: sharedDeps,
      }),
    ],
    build: { target: 'esnext', modulePreload: false, minify: true, cssCodeSplit: false },
    server: { port: remotePorts.postOffices, strictPort: true, proxy: apiProxy(env.VITE_GATEWAY_URL) },
    preview: { port: remotePorts.postOffices, strictPort: true, cors: true, proxy: apiProxy(env.VITE_GATEWAY_URL) },
  };
});
