import type { CaptureCurationResponse } from '../api/models';
export type CurationValues = {
    rating: number;
    label: string | null;
    tags: string[];
};
export const values = (value: CaptureCurationResponse): CurationValues => ({ rating: value.rating ?? 0, label: value.label ?? null, tags: [...(value.tags ?? [])] });
const same = (a: CurationValues, b: CurationValues) => a.rating === b.rating && a.label === b.label && JSON.stringify(a.tags) === JSON.stringify(b.tags);
export class CurationSession {
    opening: CurationValues;
    intent: CurationValues;
    server: CaptureCurationResponse;
    status = 'Saved';
    external = false;
    private running?: Promise<void>;
    private stopped = false;
    private orderedReset = false;
    private observed?: CaptureCurationResponse;
    private paused = false;
    constructor(server: CaptureCurationResponse, private readonly write: (body: CurationValues & {
        expectedRevision: number;
    }) => Promise<CaptureCurationResponse | undefined>, private readonly read: () => Promise<CaptureCurationResponse>, private readonly failure: (error: unknown) => void = () => { }) {
        this.server = server;
        this.opening = values(server);
        this.intent = values(server);
    }
    get unresolved() { return this.status !== 'Saved' || !same(this.intent, values(this.server)); }
    get editable() { return !this.server.releasedAt && !this.observed?.releasedAt && !this.stopped && !this.orderedReset; }
    change(patch: Partial<CurationValues>) {
        if (!this.editable || ['Conflict', 'Save failed', 'Checking outcome'].includes(this.status))
            return;
        this.intent = { ...this.intent, ...patch };
        this.start();
    }
    observe(server: CaptureCurationResponse) {
        if ((server.revision ?? 0) < (this.server.revision ?? 0))
            return;
        if (this.running) {
            if ((server.revision ?? 0) >= (this.observed?.revision ?? 0))
                this.observed = server;
            return;
        }
        if (server.revision !== this.server.revision) {
            const retained = this.unresolved;
            this.external = true;
            this.server = server;
            if (retained)
                this.status = server.releasedAt ? 'Released · unresolved edits' : 'Conflict';
            else
                this.intent = values(server);
        }
        else
            this.server = server;
    }
    private start() {
        if (this.running || this.stopped)
            return;
        this.running = this.flush().finally(() => { this.running = undefined; this.orderedReset = false; });
    }
    private async flush() {
        while (!this.stopped && !same(this.intent, values(this.server))) {
            const submitted = this.prepareWrite();
            if (!submitted) return;
            try {
                this.acknowledge(await this.write(submitted));
                if (this.stopped || this.paused) return;
            } catch (error) {
                this.failure(error);
                if (this.stopped) return;
                const status = this.beginOutcomeCheck(error);
                try {
                    this.reconcileFailedWrite(await this.read(), status);
                } catch {
                    this.status = 'Checking outcome';
                }
                return;
            }
        }
        if (!this.stopped) this.status = 'Saved';
    }

    private prepareWrite() {
        if (this.paused) return;
        if (this.server.releasedAt) {
            this.status = 'Released · unresolved edits';
            return;
        }
        const submitted = { ...this.intent, tags: [...this.intent.tags], expectedRevision: this.server.revision ?? 0 };
        this.status = 'Saving';
        return submitted;
    }

    private takeNewerObservation(result: CaptureCurationResponse) {
        const observed = this.observed;
        this.observed = undefined;
        if (observed && (observed.revision ?? 0) > (result.revision ?? 0)) return observed;
    }

    private acknowledge(result: CaptureCurationResponse | undefined) {
        if (this.stopped) return;
        if (!result) throw new Error('Missing acknowledgement');
        const newer = this.takeNewerObservation(result);
        this.server = newer ?? result;
        if (!newer) return;
        this.external = true;
        this.status = this.reviewStatus();
        this.paused = this.status !== 'Saved';
    }

    private reviewStatus() {
        if (same(this.intent, values(this.server))) return 'Saved';
        return this.server.releasedAt ? 'Released · unresolved edits' : 'Conflict';
    }

    private beginOutcomeCheck(error: unknown) {
        const failure = error as { responseStatusCode?: number; status?: number };
        const status = failure.responseStatusCode ?? failure.status;
        this.status = status === 400 ? 'Save failed' : 'Checking outcome';
        return status;
    }

    private reconcileFailedWrite(result: CaptureCurationResponse, status: number | undefined) {
        if (this.stopped) return;
        const latest = this.takeNewerObservation(result) ?? result;
        this.external = latest.revision !== this.server.revision || this.external;
        this.server = latest;
        const matchesIntent = same(this.intent, values(latest));
        if (latest.releasedAt && !matchesIntent) this.status = 'Released · unresolved edits';
        else if (status === 409) this.status = 'Conflict';
        else this.status = matchesIntent ? 'Saved' : 'Save failed';
        if (this.status === 'Saved') this.paused = false;
    }
    async settle() { await this.running; }
    acceptBatch(server: CaptureCurationResponse) {
        if (this.stopped || this.unresolved || (server.revision ?? 0) < (this.server.revision ?? 0)) return;
        this.server = server;
        this.intent = values(server);
        this.status = 'Saved';
    }
    async check() {
        await this.settle();
        try {
            this.observe(await this.read());
            this.status = this.reviewStatus();
            if (this.status === 'Saved') this.paused = false;
        }
        catch (error) {
            this.failure(error);
            this.status = 'Checking outcome';
        }
    }
    async useServer() {
        if (this.running) await this.running;
        if (this.stopped) return;
        this.intent = values(this.server);
        this.paused = false;
        this.status = 'Saved';
    }
    reapply() {
        if (this.server.releasedAt || this.observed?.releasedAt) {
            this.status = 'Released · unresolved edits';
            return;
        }
        this.paused = false;
        this.status = 'Saving';
        this.start();
    }
    reset(all: boolean) {
        if (this.server.releasedAt || this.observed?.releasedAt || this.stopped)
            return;
        const restoration = all ? { rating: 0, label: null, tags: [] } : this.opening;
        this.intent = { ...restoration, tags: [...restoration.tags] };
        this.orderedReset = true;
        if (this.external || ['Conflict', 'Checking outcome', 'Save failed'].includes(this.status)) {
            this.status = 'Conflict';
            this.paused = true;
            this.orderedReset = false;
            return;
        }
        this.start();
    }
    stop() { this.stopped = true; }
}
