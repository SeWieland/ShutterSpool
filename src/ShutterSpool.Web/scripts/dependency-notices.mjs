import assert from 'node:assert/strict'
import { readFileSync, readdirSync } from 'node:fs'
import { createRequire } from 'node:module'
import { basename, dirname, join, relative, resolve, sep } from 'node:path'

export const licenseInventory = '.vite/web-dependency-licenses.json'

const runtimeOwners = new Map([
    ['\0plugin-vue:export-helper', '@vitejs/plugin-vue'],
    ['\0rolldown/runtime.js', 'rolldown'],
    ['\0vite/modulepreload-polyfill.js', 'vite'],
    ['\0vite/preload-helper.js', 'vite'],
])

// The tarball omits the license. The upstream 2.0.13 tag and npm gitHead both resolve to e00d6116d8d22b3d394b31346a5a2cf409bfb5fc.
const upstreamLicenses = new Map([
    ['@std-uritemplate/std-uritemplate@2.0.13', new URL('./licenses/std-uritemplate-2.0.13.LICENSE', import.meta.url)],
])

const embeddedNotices = new Map([
    ['reka-ui', new URL('./licenses/reka-focus-scope-NOTICES.md', import.meta.url)],
    ['tailwindcss', new URL('./licenses/tailwind-preflight-NOTICES.md', import.meta.url)],
])

const licenseFile = /^(?:licen[cs]es?|copying)(?:[_. -]|$)/i
const noticeFiles = [
    /^notices?(?:[_. -]|$)/i,
    /^copyright(?:[_. -]?notices?)?(?:[_. -]|$)/i,
    /^third[_. -]?party[_. -]?(?:licen[cs]es?|notices?)(?:[_. -]|$)/i,
]

function compareOrdinal(left, right) {
    if (left === right) return 0
    return left < right ? -1 : 1
}

function bundledPackageRoots(bundle) {
    const builder = createRequire(import.meta.resolve('@nuxt/vite-builder/package.json'))
    const vite = createRequire(builder.resolve('vite/package.json'))
    // Tailwind contributes imported CSS even if its JS is eliminated in a future build.
    const roots = new Set([dirname(builder.resolve('tailwindcss/package.json'))])
    const modules = Object.values(bundle).flatMap(file => file.type === 'chunk' ? file.moduleIds : [])
    for (const id of modules) {
        const owner = runtimeOwners.get(id)
        if (owner) {
            roots.add(dirname((owner === 'rolldown' ? vite : builder).resolve(`${owner}/package.json`)))
        } else if (!id.startsWith('\0')) {
            const root = /^.*\/node_modules\/(?:@[^/]+\/)?[^/]+(?=\/)/.exec(id)?.[0]
            if (root) roots.add(resolve(root))
        }
    }
    return roots
}

function packageNoticeFiles(root) {
    return readdirSync(root, { recursive: true, withFileTypes: true })
        .filter(file => file.isFile() && (licenseFile.test(file.name) || noticeFiles.some(pattern => pattern.test(file.name))))
        .filter(file => !relative(root, file.parentPath).split(sep).includes('node_modules'))
        .map(file => join(file.parentPath, file.name))
}

function formatPackageNotice(entry, paths) {
    const { name, version, identifier, text } = entry
    const texts = new Set()
    let hasLicense = false
    for (const path of [...paths].sort(compareOrdinal)) {
        const source = readFileSync(path, 'utf8').replaceAll('\r\n', '\n').trim()
        if (!source) continue
        const isLicense = licenseFile.test(basename(path))
        hasLicense ||= isLicense
        // Vite's browser helpers use its own license. The rest lists Vite's build-time dependencies.
        texts.add(name === 'vite' && isLicense ? source.split('\n# Licenses of bundled dependencies')[0].trim() : source)
    }
    if (!hasLicense) {
        const key = `${name}@${version}`
        const fallback = upstreamLicenses.get(key)
        const license = text?.trim() || (fallback && readFileSync(fallback, 'utf8').trim())
        assert(license, `No license text for bundled package ${key}`)
        texts.add(license)
    }
    const embedded = embeddedNotices.get(name)
    if (embedded) texts.add(readFileSync(embedded, 'utf8').replaceAll('\r\n', '\n').trim())
    const licenseSuffix = identifier ? ` (${identifier})` : ''
    const heading = `## ${name} - ${version}${licenseSuffix}`
    return [heading, ...texts].join('\n\n')
}

/** @returns {import('vite').Plugin} */
export function dependencyNotices() {
    return {
        name: 'shutterspool:dependency-notices',
        apply: 'build',
        generateBundle: {
            order: 'post',
            handler(_, bundle) {
                if (this.environment.name !== 'client') return

                const inventory = bundle[licenseInventory]
                assert(inventory?.type === 'asset', 'Vite did not emit its browser license inventory')
                const dependencies = JSON.parse(inventory.source.toString())
                assert(dependencies.length > 0, 'Vite emitted an empty browser license inventory')
                const entries = new Map(dependencies.map(entry => [`${entry.name}@${entry.version}`, entry]))

                const notices = new Map()
                for (const root of bundledPackageRoots(bundle)) {
                    const { name, version, license } = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'))
                    const key = `${name}@${version}`
                    if (!entries.has(key)) entries.set(key, { name, version, identifier: license })
                    notices.set(key, [...(notices.get(key) ?? []), ...packageNoticeFiles(root)])
                }

                const sections = ['# Web dependency notices', 'The following package code or assets are included in ShutterSpool’s browser build.']
                for (const key of [...entries.keys()].sort(compareOrdinal)) {
                    const files = notices.get(key)
                    assert(files, `Could not locate the installed files for ${key}`)
                    sections.push(formatPackageNotice(entries.get(key), files))
                }

                delete bundle[licenseInventory]
                this.emitFile({ type: 'asset', fileName: 'WEB-DEPENDENCY-NOTICES.md', source: `${sections.join('\n\n')}\n` })
            },
        },
    }
}
