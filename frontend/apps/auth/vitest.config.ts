import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// Tests exercise components directly; Module Federation is a build/runtime concern.
export default defineConfig({
  plugins: [react()],
  test: { environment: 'jsdom', globals: true, setupFiles: ['../../packages/shared/src/test-setup.ts'] },
});
