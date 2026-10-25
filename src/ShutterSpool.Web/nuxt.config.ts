export default defineNuxtConfig({
  ssr: false,
  modules: ['@nuxt/ui'],
  css: ['~/assets/main.css', '~/assets/workspace.css'],
  colorMode: { preference: 'dark', fallback: 'dark' },
  ui: { fonts: false },
  app: { head: { title: 'ShutterSpool', htmlAttrs: { class: 'dark' }, link: [{ rel: 'icon', type: 'image/svg+xml', href: '/favicon.svg' }] } },
  nitro: { prerender: { routes: ['/'] } },
})
