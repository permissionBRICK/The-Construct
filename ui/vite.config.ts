/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import preact from '@preact/preset-vite';

// One static bundle. `index.html` is the app; `preview.html` is a frame for showing the app with the
// fake backend on its three surfaces side by side.
export default defineConfig({
  base: './',
  plugins: [preact()],
  build: {
    target: 'es2022',
    rollupOptions: { input: { index: 'index.html', preview: 'preview.html' } },
  },
  test: {
    include: ['tests/unit/**/*.test.ts'],
    environment: 'node',
  },
});
