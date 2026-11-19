import { expect, test, vi } from 'vitest';
import { readSourceActivity, readSourceAuthenticationObservations } from '../../app/lib/api';

const calls = vi.hoisted(() => ({ auth: vi.fn(), transfers: vi.fn() }));
vi.mock('../../app/api/shutterSpoolClient', () => ({
    createShutterSpoolClient: () => ({ management: {
        sources: { authenticationObservations: { get: calls.auth } },
        activity: { transfers: { get: calls.transfers } },
    } }),
}));
test('source activity observation helpers pass their AbortSignal to Kiota requests', async () => {
    calls.auth.mockResolvedValue({ items: [], coverageComplete: true });
    calls.transfers.mockResolvedValue({ epoch: 'synthetic', items: [], coverageComplete: true });
    const { signal } = new AbortController();
    await readSourceAuthenticationObservations(['source'], signal);
    await readSourceActivity(['source'], signal);
    for (const read of [calls.auth, calls.transfers]) {
        expect(read).toHaveBeenCalledTimes(1);
        expect(read.mock.calls[0][0].options[0].signal).toBe(signal);
    }
});
