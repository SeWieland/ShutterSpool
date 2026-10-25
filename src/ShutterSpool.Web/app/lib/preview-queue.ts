let active = 0
const waiting: (() => void)[] = []

export async function withPreviewSlot<T>(read: () => Promise<T>): Promise<T> {
  if (active >= 4) await new Promise<void>(resolve => waiting.push(resolve))
  else active++
  try { return await read() } finally {
    const next = waiting.shift()
    if (next) next()
    else active--
  }
}
