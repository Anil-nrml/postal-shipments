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
        name: 'auth',
        filename: 'remoteEntry.js',
        exposes: {
          './Login': './src/LoginPage.tsx',
          './Users': './src/UsersPage.tsx',
        },
        shared: sharedDeps,
      }),
    ],
    build: { target: 'esnext', modulePreload: false, minify: true, cssCodeSplit: false },
    server: { port: remotePorts.auth, strictPort: true, proxy: apiProxy(env.VITE_GATEWAY_URL) },
    preview: { port: remotePorts.auth, strictPort: true, cors: true, proxy: apiProxy(env.VITE_GATEWAY_URL) },
  };
});
