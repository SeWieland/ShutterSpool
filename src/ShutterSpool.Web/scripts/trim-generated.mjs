import { readFileSync, writeFileSync } from 'node:fs'

const path = 'app/api/shutterSpoolClient.ts'
writeFileSync(path, readFileSync(path, 'utf8').replace(/[\t ]+\r?\n/g, '\n'))
