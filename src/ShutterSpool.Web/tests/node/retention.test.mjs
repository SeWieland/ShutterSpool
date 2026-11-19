import { test } from 'vitest';
import assert from 'node:assert/strict';
import { originalRemovalScope, durationHours, durationFromHours, retentionTiming } from '../../app/lib/retention.ts';
test('removal confirmation lists every remaining variant and rejects unavailable scope', () => {

    assert.equal(originalRemovalScope(undefined), undefined);
    assert.equal(originalRemovalScope({ isFavorite: false }), undefined);
    const evidence = { isFavorite: true, availableOriginals: [{ variant: 'RAW', byteLength: 2048, state: 'available' }, { variant: 'JPEG', byteLength: 1024, state: 'failedRemoval' }, { variant: 'OTHER', byteLength: 4096, state: 'removed' }] };
    assert.match(originalRemovalScope(evidence), /RAW \(2,048 bytes/); assert.match(originalRemovalScope(evidence), /JPEG \(1,024 bytes/); assert.doesNotMatch(originalRemovalScope(evidence), /OTHER/);
    evidence.availableOriginals[0].state = 'unknown'; assert.equal(originalRemovalScope(evidence), undefined);
});
const now = Date.parse('2026-10-13T12:00:00Z');
function summary(kind, duration, remaining) {
    return { asOf: new Date(now), timeline: { kind: kind, startedAt: new Date(now + remaining - duration), endsAt: new Date(now + remaining), meaning: 'Whole Capture eligibility' } };
}
test('TimeSpan settings preserve days and fractional seconds without accepting malformed values', () => {

    assert.equal(durationHours('180.00:00:00'), 4320);
    assert.equal(durationHours('01:30:00'), 1.5);
    assert.equal(durationFromHours(72), '3.00:00:00');
    assert.equal(durationFromHours(1.5), '0.01:30:00');
    assert.equal(durationFromHours(.5 / 3600), '0.00:00:00.5');
    assert.equal(durationHours('00:00:00.5000000'), .5 / 3600);
    assert.equal(durationHours('invalid'), undefined);
    assert.equal(durationHours('-1.00:00:00'), undefined);
    assert.equal(durationHours('25:00:00'), undefined);
    assert.throws(() => durationFromHours(Infinity));
});
test('180-day intervals retain their scale and normal warning is capped at one day', () => {
    const long = summary('normalRetention', 180 * 86400000, 2 * 86400000);
    assert.equal(retentionTiming(long, now, false).visible, false);
    assert.equal(retentionTiming(long, now, true).fraction, 2 / 180);
    assert.equal(retentionTiming(summary('normalRetention', 180 * 86400000, 86400000), now, false).tone, 'amber');
});
test('held and Trash retain exact 24-hour warning boundaries', () => {
    assert.equal(retentionTiming(summary('heldOrBlocked', 48 * 3600000, 24 * 3600000), now, false).tone, 'amber');
    assert.equal(retentionTiming(summary('trashRecovery', 72 * 3600000, 24 * 3600000), now, false).tone, 'red');
    assert.equal(retentionTiming(summary('trashRecovery', 72 * 3600000, 25 * 3600000), now, false).visible, true);
});
test('deadline-only authority never creates progress; zero means pending', () => {
    const deadline = summary('normalRetention', 10000, 1000); deadline.timeline.startedAt = null;
    assert.equal(retentionTiming(deadline, now, true).fraction, undefined);
    const expired = retentionTiming(summary('trashRecovery', 10000, 0), now, true);
    assert.match(expired.title, /pending/); assert.equal(expired.fraction, 0);
});
test('stale evidence freezes at observation; unknown and unscheduled state get no clocks', () => {
    const result = retentionTiming(summary('trashRecovery', 1000000, 500000), now + 120000, true);
    assert.equal(result.stale, true); assert.equal(result.fraction, .5); assert.match(result.title, /Last known/);
    assert.equal(retentionTiming(undefined, now, true).visible, false);
    assert.equal(retentionTiming({ asOf: new Date(now), timeline: {} }, now, true).fraction, undefined);
    const unknown = summary('trashRecovery', 10000, 5000); delete unknown.asOf;
    assert.equal(retentionTiming(unknown, now, true).fraction, undefined);
    unknown.asOf = new Date('invalid');
    assert.equal(retentionTiming(unknown, now, true).fraction, undefined);
});
