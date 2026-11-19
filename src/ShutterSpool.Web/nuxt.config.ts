import sftpgo from '../../artifacts/attribution/sftpgo.json'

export default defineNuxtConfig({
  $production: {
    // Keep the production manifest location stable before and after typecheck.
    buildDir: 'node_modules/.cache/nuxt/.nuxt',
    typesDir: '.nuxt',
  },
  ssr: false,
  modules: ['@nuxt/ui'],
  css: ['~/assets/main.css', '~/assets/workspace.css'],
  colorMode: { preference: 'dark', fallback: 'dark' },
  ui: { fonts: false },
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
  nitro: { prerender: { routes: ['/'] } },
})
