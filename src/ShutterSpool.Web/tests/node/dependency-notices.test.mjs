import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { dependencyNotices, licenseInventory } from '../../scripts/dependency-notices.mjs'

describe('browser dependency notices', () => {
    let directory

    afterEach(() => {
        rmSync(directory, { recursive: true, force: true })
    })

    function fixture(license, data = { name: 'fixture-package', version: '1.0.0', identifier: 'MIT' }) {
        directory = mkdtempSync(join(tmpdir(), 'shutterspool-notices-'))
        const root = join(directory, 'node_modules', data.name)
        mkdirSync(join(root, 'src'), { recursive: true })
        writeFileSync(join(root, 'package.json'), JSON.stringify(data))
        writeFileSync(join(root, 'LICENSE'), license)
        writeFileSync(join(root, 'src', 'CopyrightNotice.txt'), 'Required nested copyright notice')
        writeFileSync(join(root, 'src', 'third_party_licences.txt'), 'Embedded third-party licence')
        const bundle = {
            'chunk.js': {
                type: 'chunk',
                moduleIds: [
                    join(root, 'index.js').replaceAll('\\', '/'),
                    '\0rolldown/runtime.js',
                    '\0vite/preload-helper.js',
                ],
            },
            [licenseInventory]: {
                type: 'asset',
                source: JSON.stringify([{ ...data, text: license.trim() }]),
            },
        }
        const emitFile = vi.fn()
        const build = () => dependencyNotices().generateBundle.handler.call({ environment: { name: 'client' }, emitFile }, {}, bundle)
        return { build, bundle, emitFile }
    }

    it('includes installed notice text and bundled virtual runtime licenses', () => {
        const { build, bundle, emitFile } = fixture('Upstream license terms')
        build()

        expect(bundle).not.toHaveProperty([licenseInventory])
        expect(emitFile).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({
            fileName: 'WEB-DEPENDENCY-NOTICES.md',
            source: expect.stringMatching(/fixture-package[\s\S]*Upstream license terms[\s\S]*Required nested copyright notice[\s\S]*rolldown[\s\S]*VoidZero/),
        }))
        const [{ source }] = emitFile.mock.calls[0]
        const fixtureNotice = source.split('\n## ').find(section => section.startsWith('fixture-package - '))
        expect(fixtureNotice).toContain('Embedded third-party licence')
        expect(source).toContain('# Vite core license')
        expect(source).not.toContain('# Licenses of bundled dependencies')
    })

    it('uses the shipped upstream license for the pinned URI-template package', () => {
        const { build, emitFile } = fixture('', {
            name: '@std-uritemplate/std-uritemplate', version: '2.0.13', identifier: 'Apache-2.0',
        })
        build()

        const [{ source }] = emitFile.mock.calls[0]
        expect(source).toMatch(/@std-uritemplate\/std-uritemplate - 2\.0\.13 \(Apache-2\.0\)[\s\S]*Apache License[\s\S]*Version 2\.0, January 2004/)
    })

    it('preserves licenses for source embedded in Preflight and FocusScope', () => {
        const { build, emitFile } = fixture('Reka license terms', { name: 'reka-ui', version: '2.11.0', identifier: 'MIT' })
        build()

        const [{ source }] = emitFile.mock.calls[0]
        const sections = source.split('\n## ')
        const reka = sections.find(section => section.startsWith('reka-ui - '))
        const tailwind = sections.find(section => section.startsWith('tailwindcss - '))
        expect(reka).toContain('Copyright (c) 2022 WorkOS')
        expect(reka).toContain('Copyright (c) 2020 Jon Egeland')
        expect(tailwind).toContain('Copyright (c) Nicolas Gallagher')
        expect(tailwind).toContain('Copyright (c) Jonathan Neal')
        expect(tailwind).toContain('Copyright (c) Sindre Sorhus')
    })

    it('rejects a bundled package with only an empty license file', () => {
        const { build, emitFile } = fixture('\n')
        expect(build).toThrow('No license text for bundled package fixture-package@1.0.0')
        expect(emitFile).not.toHaveBeenCalled()
    })
})
