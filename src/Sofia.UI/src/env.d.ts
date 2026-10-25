/// <reference types="vite/client" />

interface ImportMetaEnv {
  /**
   * Optional absolute Sofia.Server URL, for UIs hosted where the server is not
   * reachable on the same origin. Empty = same origin / Vite dev proxy.
   */
  readonly VITE_SOFIA_SERVER?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}

declare module '*.vue' {
  import type { DefineComponent } from 'vue'

  const component: DefineComponent<Record<string, unknown>, Record<string, unknown>, unknown>
  export default component
}
