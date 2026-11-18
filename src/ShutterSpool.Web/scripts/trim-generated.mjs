import { readFileSync, writeFileSync } from 'node:fs'

const path = 'app/api/shutterSpoolClient.ts'
writeFileSync(path, readFileSync(path, 'utf8').replace(/(?<![\t ])[\t ]+\r?\n/g, '\n'))
