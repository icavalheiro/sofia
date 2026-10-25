import { fileURLToPath, URL } from 'node:url'

import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

// Sofia.Server address the dev server proxies `/api` and `/hubs` to.
const serverTarget = process.env.SOFIA_SERVER_URL ?? 'http://127.0.0.1:5199'

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    vue({
      // Vapor mode is ready to switch on once Vue 3.6 is stable.
      //
      // Mantine Vue components are virtual-DOM based, so the app root must stay
      // `createApp()` + `vaporInteropPlugin` and only Sofia's own SFCs would be
      // compiled in vapor mode. Enable it by uncommenting the line below and
      // adding `vaporInteropPlugin` in `src/main.ts`. See README.md,
      // section "Enabling Vapor".
      //
      // features: { vapor: true },
    }),
  ],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    port: 5183,
    host: true,
    proxy: {
      // xfwd: forward the real client IP so the server only trusts loopback.
      '/api': { target: serverTarget, changeOrigin: true, xfwd: true, ws: true },
      '/hubs': { target: serverTarget, changeOrigin: true, ws: true, xfwd: true },
    },
  },
  build: {
    target: 'es2022',
  },
})
