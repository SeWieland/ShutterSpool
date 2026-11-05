import assert from 'node:assert/strict';
import test from 'node:test';
import { isDestinationKindEnabled } from '../app/lib/destination-capabilities.ts';

test('only enables destination kinds explicitly returned by deployment capabilities', () => {
    assert.equal(isDestinationKindEnabled(['local', 'webdav'], 'local'), true);
    assert.equal(isDestinationKindEnabled(['webdav'], 'local'), false);
    assert.equal(isDestinationKindEnabled([], 'webdav'), false);
    assert.equal(isDestinationKindEnabled(undefined, 'webdav'), false);
    assert.equal(isDestinationKindEnabled(['local'], undefined), false);
});
