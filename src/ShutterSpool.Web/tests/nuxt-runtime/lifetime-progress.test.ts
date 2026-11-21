import { mountSuspended } from '@nuxt/test-utils/runtime';
import { expect, onTestFinished, test } from 'vitest';
import { nextTick, ref } from 'vue';
import ReleaseLifetime from '../../app/components/ReleaseLifetime.vue';
import RetentionLifetime from '../../app/components/RetentionLifetime.vue';
import { workspaceClockKey } from '../../app/composables/useWorkspaceClock';

const observed = Date.parse('2026-11-21T10:00:00Z');
const retentionEvidence = {
    asOf: new Date(observed),
    timeline: {
        kind: 'normalRetention',
        meaning: 'Whole Capture eligibility',
        startedAt: new Date(observed - 120_000),
        endsAt: new Date(observed + 120_000),
    },
};

test('release exposes a native countdown until it is due or held', async () => {
    const now = ref(observed);
    const curation = { delaySeconds: 120, releaseDueAt: new Date(observed + 30_000) };
    const lifetime = await mountSuspended(ReleaseLifetime, {
        props: { curation },
        global: { provide: { [workspaceClockKey]: now } },
    });
    onTestFinished(() => lifetime.unmount());

    const progress = lifetime.get<HTMLProgressElement>('progress').element;
    expect(progress.max).toBe(100);
    expect(progress.value).toBe(25);
    expect(progress.getAttribute('aria-label')).toBe('Time remaining until release');
    expect(progress.title).toBe('Waiting · release in 30s');
    expect(lifetime.get('span').attributes('aria-hidden')).toBe('true');

    now.value += 30_000;
    await nextTick();
    expect(progress.position).toBe(0);
    expect(progress.title).toBe('Release due · waiting for server');
    expect(lifetime.get('.release-lifetime').classes()).toContain('urgent');

    await lifetime.setProps({ curation: { ...curation, held: true } });
    expect(lifetime.find('.release-lifetime').exists()).toBe(false);
});

test('retention exposes native progress and freezes at the last observation when stale', async () => {
    const now = ref(observed);
    const lifetime = await mountSuspended(RetentionLifetime, {
        props: { evidence: retentionEvidence, details: true },
        global: { provide: { [workspaceClockKey]: now } },
    });
    onTestFinished(() => lifetime.unmount());

    const progress = lifetime.get<HTMLProgressElement>('progress').element;
    const deadline = `Whole Capture eligibility · ${retentionEvidence.timeline.endsAt.toLocaleString()}`;
    expect(progress.max).toBe(100);
    expect(progress.value).toBe(50);
    expect(progress.getAttribute('aria-label')).toBe('Whole Capture eligibility');
    expect(progress.title).toBe(deadline);
    expect(lifetime.get('span').attributes('aria-hidden')).toBe('true');

    now.value += 30_000;
    await nextTick();
    expect(progress.value).toBe(38);

    await lifetime.setProps({ stale: true });
    expect(progress.value).toBe(50);
    expect(progress.title).toBe(`Last known · ${deadline} · observed ${retentionEvidence.asOf.toLocaleString()}`);
    expect(lifetime.get('.retention-lifetime').classes()).toContain('stale');
});

test('retention requires a visible interval with a known start', async () => {
    const lifetime = await mountSuspended(RetentionLifetime, {
        props: { evidence: retentionEvidence },
        global: { provide: { [workspaceClockKey]: ref(observed) } },
    });
    onTestFinished(() => lifetime.unmount());

    expect(lifetime.find('.retention-lifetime').exists()).toBe(false);
    const evidence = { ...retentionEvidence, timeline: { ...retentionEvidence.timeline, meaning: null } };
    await lifetime.setProps({ evidence, details: true });
    expect(lifetime.get('progress').attributes('aria-label')).toBe('Retention time remaining');

    await lifetime.setProps({ evidence: { ...evidence, timeline: { ...evidence.timeline, startedAt: null } } });
    expect(lifetime.find('.retention-lifetime').exists()).toBe(false);
});
