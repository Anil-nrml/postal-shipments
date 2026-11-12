import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import federation from '@originjs/vite-plugin-federation';
import { apiProxy, remotePorts, sharedDeps } from '../../federation.config';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), 'VITE_');

  return {
    plugins: [
      react(),
      federation({
        name: 'tracking',
        filename: 'remoteEntry.js',
        exposes: { './App': './src/App.tsx' },
        shared: sharedDeps,
      }),
    ],
    build: { target: 'esnext', modulePreload: false, minify: true, cssCodeSplit: false },
    server: { port: remotePorts.tracking, strictPort: true, proxy: apiProxy(env.VITE_GATEWAY_URL) },
    preview: { port: remotePorts.tracking, strictPort: true, cors: true, proxy: apiProxy(env.VITE_GATEWAY_URL) },
  };
});
