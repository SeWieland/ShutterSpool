import { cpSync, rmSync } from 'node:fs'

const source = '.output/public'
const destination = '../ShutterSpool.Server/wwwroot'

rmSync(destination, { force: true, recursive: true })
cpSync(source, destination, { recursive: true })
