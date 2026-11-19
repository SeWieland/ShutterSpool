import assert from 'node:assert/strict';
import { test } from 'vitest';
import { galleryGridWindow } from '../../app/composables/useGalleryGrid';

test('grid window keeps spacer boundaries row-aligned and clamps at both list ends', () => {
    assert.deepEqual(galleryGridWindow(1000, 1200, 210, 3, 800), { start: 6, end: 42 });
    const wideWindow = galleryGridWindow(1000, 9000, 210, 7, 800);
    assert.equal((wideWindow.end - wideWindow.start) % 7, 0);
    assert.ok(wideWindow.end - wideWindow.start <= 180);
    assert.deepEqual(galleryGridWindow(10, 9000, 210, 3, 800), { start: 9, end: 10 });
    assert.deepEqual(galleryGridWindow(0, 0, 0, 0, 800), { start: 0, end: 0 });
});
