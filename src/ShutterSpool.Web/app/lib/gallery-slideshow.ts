export interface Slide { id?: unknown; mediaKind?: string | null; }
export interface SlidePage { items?: Slide[] | null; nextCursor?: string | null; }
export type DisplayState = 'pending' | 'ready' | 'unavailable';
export function slideshowState() {
    return { playing: false, loading: false, seconds: 5, message: '', retry: false, failure: '' as '' | 'preview' | 'sequence', photographsOnly: false };
}
type State = ReturnType<typeof slideshowState>;
interface Dependencies {
    read: (cursor: string | undefined, startAt: string | undefined, signal: AbortSignal, direction?: -1 | 1) => Promise<SlidePage>;
    show: (id: string) => void;
    schedule?: (action: () => void, milliseconds: number) => ReturnType<typeof setTimeout>;
    cancel?: (timer: ReturnType<typeof setTimeout>) => void;
    yield?: (signal: AbortSignal) => Promise<void>;
}

// One metadata page, no preview prefetch and no history that grows with a loop.
export class GallerySlideshow {
    private page: Slide[] = [];
    private index = -1;
    private cursor: string | undefined;
    private current = '';
    private decoded = false;
    private initialized = false;
    private version = 0;
    private request?: AbortController;
    private pendingRead?: () => Promise<void>;
    private timer?: ReturnType<typeof setTimeout>;
    constructor(readonly state: State, readonly dependencies: Dependencies) {}
    get generation() { return this.version; }
    private clearTimer() {
        if (this.timer !== undefined) (this.dependencies.cancel ?? clearTimeout)(this.timer);
        this.timer = undefined;
    }
    private later(action: () => void, milliseconds: number) {
        this.clearTimer();
        this.timer = (this.dependencies.schedule ?? setTimeout)(action, milliseconds);
    }
    pause(message = '') {
        this.version++;
        this.request?.abort();
        this.pendingRead = undefined;
        this.clearTimer();
        this.state.playing = this.state.loading = false;
        this.state.message = message;
    }
    stop() {
        this.pause();
        this.page = [];
        this.index = -1;
        this.cursor = undefined;
        this.current = '';
        this.decoded = this.initialized = false;
        Object.assign(this.state, slideshowState());
    }
    async play(id: string) {
        if (this.state.playing) return;
        this.state.playing = true;
        this.state.message = '';
        this.state.retry = false;
        if (!this.initialized || this.current !== id) {
            this.initialized = false;
            this.page = [];
            this.index = -1;
            this.cursor = undefined;
            await this.advance(id);
        } else this.arm();
    }
    interval(seconds: number) {
        if (![3, 5, 10].includes(seconds)) return;
        this.state.seconds = seconds;
        if (this.state.playing && this.decoded && !this.state.loading) this.arm();
    }
    preview(state: DisplayState) {
        const wasDecoded = this.decoded;
        this.decoded = state === 'ready';
        if (!this.state.playing) return;
        if (state === 'unavailable') this.fail('This preview could not be displayed. Retry the preview, then choose Play.');
        else if (this.state.loading) return;
        else if (this.decoded || wasDecoded || this.timer === undefined) this.arm();
    }
    private fail(message: string, kind: 'preview' | 'sequence' = 'preview') {
        this.pause(message);
        this.state.retry = true;
        this.state.failure = kind;
    }
    async retry(id: string) {
        this.pause();
        const request = this.request = new AbortController();
        const version = this.version;
        this.state.loading = true;
        try {
            this.pendingRead = () => this.retry(id);
            await this.dependencies.read(undefined, id, request.signal);
            if (request.signal.aborted || version !== this.version) return;
            this.pendingRead = undefined;
            this.state.retry = false;
            this.state.message = 'Sequence available. Choose Play to continue.';
        } catch {
            if (!request.signal.aborted && version === this.version) this.fail('The slideshow sequence could not be loaded. Retry the sequence, then choose Play.', 'sequence');
        } finally { if (version === this.version) this.state.loading = false; }
    }
    private arm() {
        this.later(() => {
            if (this.decoded) return this.advance();
            else this.fail('The preview did not load within 15 seconds. Retry the preview, then choose Play.');
        }, this.decoded ? this.state.seconds * 1000 : 15_000);
    }
    async navigate(id: string, direction: -1 | 1, removed = false) {
        this.pause();
        this.initialized = false;
        this.page = [];
        this.index = -1;
        this.current = '';
        const version = this.version;
        const request = this.request = new AbortController();
        this.state.loading = true;
        let cursor: string | undefined;
        try {
            do {
                this.pendingRead = () => this.navigate(id, direction, removed);
                const page = await this.dependencies.read(cursor, cursor ? undefined : id, request.signal, direction);
                if (version !== this.version || request.signal.aborted) return;
                this.pendingRead = undefined;
                for (const item of page.items ?? []) {
                    if (item.mediaKind === 'Video') { this.state.photographsOnly = true; continue; }
                    if (item.id && String(item.id) !== id) { this.dependencies.show(String(item.id)); return; }
                }
                cursor = page.nextCursor ?? undefined;
                if (cursor) await this.yieldPage(request.signal);
            } while (cursor && version === this.version && !request.signal.aborted);
            if (removed && version === this.version) this.pause('No photographs match these Gallery filters.');
        } catch {
            if (version === this.version && !request.signal.aborted) this.fail('The slideshow sequence could not be loaded. Retry the sequence, then choose Play.', 'sequence');
        } finally { if (version === this.version) this.state.loading = false; }
    }
    private yieldPage(signal: AbortSignal) {
        return this.dependencies.yield?.(signal) ?? new Promise<void>(resolve => {
            const done = () => { clearTimeout(timer); signal.removeEventListener('abort', done); resolve(); };
            const timer = setTimeout(done, 50);
            signal.addEventListener('abort', done, { once: true });
        });
    }
    async advance(startAt?: string) {
        if (!this.state.playing || this.state.loading) return;
        this.clearTimer();
        const version = ++this.version;
        const request = this.request = new AbortController();
        this.state.loading = true;
        let restarted = false;
        try {
            while (version === this.version && !request.signal.aborted) {
                let item = this.page[++this.index];
                if (!item) {
                    if (this.initialized && !this.cursor) {
                        if (restarted) { this.pause('No photographs match these Gallery filters.'); return; }
                        restarted = true;
                    }
                    this.pendingRead = () => this.advance(startAt);
                    const page = await this.dependencies.read(this.initialized ? this.cursor : undefined, this.initialized ? undefined : startAt, request.signal);
                    if (version !== this.version || request.signal.aborted) return;
                    this.pendingRead = undefined;
                    this.page = page.items ?? [];
                    this.index = -1;
                    this.cursor = page.nextCursor ?? undefined;
                    this.initialized = true;
                    if (!this.page.length && !this.cursor) { this.pause('No photographs match these Gallery filters.'); return; }
                    // Empty and future video-only pages yield between bounded reads, without polling.
                    if (!this.page.length || this.page.every(row => row.mediaKind === 'Video')) {
                        await this.yieldPage(request.signal);
                    }
                    continue;
                }
                if (item.mediaKind === 'Video') { this.state.photographsOnly = true; continue; }
                if (!item.id) continue;
                const id = String(item.id);
                if (id === this.current && !startAt) { this.pause('Only one photograph matches. It will stay on screen.'); return; }
                this.current = id;
                this.decoded = false;
                this.state.loading = false;
                this.dependencies.show(id);
                if (version === this.version && this.state.playing) this.arm();
                return;
            }
        } catch {
            if (version === this.version && !request.signal.aborted) this.fail('The slideshow sequence could not be loaded. Retry the sequence, then choose Play.', 'sequence');
        } finally {
            if (version === this.version) this.state.loading = false;
        }
    }
    remove(id: string, displayed = false) {
        const restart = this.pendingRead;
        this.page = this.page.map(row => String(row.id) === id ? {} : row);
        if (id === this.current || displayed || restart) {
            this.version++;
            this.request?.abort();
            this.pendingRead = undefined;
            this.state.loading = false;
        }
        if (id === this.current || displayed) {
            this.current = '';
            this.decoded = false;
            this.clearTimer();
            return this.state.playing ? this.advance() : this.navigate(id, 1, true);
        }
        if (restart) return restart();
    }
}
