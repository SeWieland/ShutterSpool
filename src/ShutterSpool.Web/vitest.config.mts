import { registerHooks } from 'node:module';
import { defineVitestProject } from '@nuxt/test-utils/config';
import { defineConfig } from 'vitest/config';

async function nuxtProject() {
    // Use synthetic attribution while loading Nuxt, so tests need neither generated files nor registry access.
    const nuxtConfig = new URL('./nuxt.config.ts', import.meta.url).href;
    const attribution = {
        version: 'v0.0.0-test',
        source: 'https://example.test/source',
        license: 'https://example.test/license',
        notice: 'https://example.test/notice',
    };
    const fixture = `data:application/json,${encodeURIComponent(JSON.stringify(attribution))}`;
    const hooks = registerHooks({
        resolve(specifier, context, nextResolve) {
            if (context.parentURL?.split('?')[0] === nuxtConfig && specifier === '../../artifacts/attribution/sftpgo.json') {
                return { url: fixture, shortCircuit: true };
            }
            return nextResolve(specifier, context);
        },
    });
    try {
        return await defineVitestProject({
            test: {
                name: 'nuxt',
                environment: 'nuxt',
                // Keep Vitest's ambient Node declarations outside Nuxt's production typecheck.
                include: ['tests/nuxt-runtime/**/*.test.ts'],
                // Allow for cold Nuxt transforms during setup and the first component mount.
                hookTimeout: 30_000,
                testTimeout: 30_000,
                environmentOptions: { nuxt: { domEnvironment: 'happy-dom' } },
            },
        });
    } finally {
        hooks.deregister();
    }
}

export default defineConfig(async () => ({
    test: {
        projects: [
            {
                test: {
                    name: 'node',
                    environment: 'node',
                    include: ['tests/node/**/*.test.{ts,mjs}', '../../tools/generate-attribution.test.mjs'],
                },
            },
            await nuxtProject(),
        ],
        coverage: {
            provider: 'v8',
            include: ['app/**/*.{ts,vue}'],
            exclude: ['app/api/**', 'app/**/{tests,__tests__}/**', 'app/**/*.{test,spec}.{ts,vue}'],
            reporter: ['text-summary', 'lcovonly'],
            reportOnFailure: true,
        },
    },
}));
