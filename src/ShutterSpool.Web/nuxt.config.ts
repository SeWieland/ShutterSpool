import sftpgo from '../../artifacts/attribution/sftpgo.json' with { type: 'json' }
import { dependencyNotices, licenseInventory } from './scripts/dependency-notices.mjs'

export default defineNuxtConfig({
  ssr: false,
  router: { options: { sensitive: false } },
  modules: ['@nuxt/ui'],
  css: ['~/assets/main.css', '~/assets/workspace.css'],
  colorMode: { preference: 'dark', fallback: 'dark' },
  ui: { fonts: false, experimental: { componentDetection: true } },
  vite: {
    plugins: [dependencyNotices()],
    environments: {
      client: {
        build: {
          license: { fileName: licenseInventory },
          // Keep Rolldown's shared chunks instead of merging the initial graph into one large entry.
          rolldownOptions: { output: { codeSplitting: {} } },
        },
      },
    },
  },
  icon: {
    provider: 'none',
    clientBundle: { scan: { globInclude: ['app/**/*.{vue,ts}'] } },
  },
  appConfig: { sftpgo },
  hooks: {
    'build:manifest': manifest => {
      // Keep authenticated and inactive sections on demand, including on the static sign-in page.
      for (const chunk of Object.values(manifest)) chunk.prefetch = false;
    },
  },
  app: { head: { title: 'ShutterSpool', htmlAttrs: { lang: 'en', class: 'dark' }, link: [{ rel: 'icon', type: 'image/svg+xml', href: '/favicon.svg' }] } },
  prerender: { routes: ['/'] },
})
