import { test } from 'node:test'
import assert from 'node:assert/strict'
import { withPreviewSlot } from '../app/lib/preview-queue.ts'

test('preview bursts stay within four reads and failed reads release their slot', async () => {
  let active = 0
  let highest = 0
  const reads = Array.from({ length: 30 }, (_, index) => withPreviewSlot(async () => {
    active++; highest = Math.max(highest, active)
    try {
      await new Promise(resolve => setTimeout(resolve, 1))
      if (index % 3 === 0) throw new Error('unavailable')
      return index
    } finally { active-- }
  }))
  const results = await Promise.allSettled(reads)
  assert.equal(highest, 4)
  assert.equal(active, 0)
  assert.equal(results.filter(result => result.status === 'fulfilled').length, 20)
  assert.equal(await withPreviewSlot(async () => 'recovered'), 'recovered')
})
