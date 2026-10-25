import { createApp } from 'vue'

import App from './App.vue'

// Mantine's base stylesheet first, Sofia's overrides second so they win.
import '@mantine-vue/core/styles.css'
import './styles/global.css'

// The root stays the standard virtual-DOM app because Mantine Vue components are
// virtual-DOM based. Once Vue 3.6 is stable, add `vaporInteropPlugin` here so
// Sofia's own SFCs can opt into vapor mode without touching Mantine:
//
//   import { createApp, vaporInteropPlugin } from 'vue'
//   createApp(App).use(vaporInteropPlugin).mount('#app')
//
// See README.md, section "Enabling Vapor".
createApp(App).mount('#app')
