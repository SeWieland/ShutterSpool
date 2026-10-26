export default defineNuxtConfig({
  ssr: false,
  modules: ['@nuxt/ui'],
  css: ['~/assets/main.css', '~/assets/workspace.css'],
  colorMode: { preference: 'dark', fallback: 'dark' },
  ui: { fonts: false },
  hooks: {
    'build:manifest': manifest => {
      // Keep authenticated and inactive sections on demand, including on the static sign-in page.
      for (const chunk of Object.values(manifest)) chunk.prefetch = false;
    },
  },
  app: { head: { title: 'ShutterSpool', htmlAttrs: { lang: 'en', class: 'dark' }, link: [{ rel: 'icon', type: 'image/svg+xml', href: '/favicon.svg' }] } },
  nitro: { prerender: { routes: ['/'] } },
})
