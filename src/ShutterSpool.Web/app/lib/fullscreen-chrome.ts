export function chromeState() { return { visible: true }; }
interface Clock {
    schedule?: (callback: () => void, milliseconds: number) => ReturnType<typeof setTimeout>;
    cancel?: (timer: ReturnType<typeof setTimeout>) => void;
}

export class FullscreenChrome {
    private active = false;
    private keyboardInput = false;
    private focused = false;
    private pinned = false;
    private generation = 0;
    private timer?: ReturnType<typeof setTimeout>;
    constructor(readonly state: ReturnType<typeof chromeState>, readonly clock: Clock = {}) {}
    private cancel() {
        this.generation++;
        if (this.timer !== undefined) (this.clock.cancel ?? clearTimeout)(this.timer);
        this.timer = undefined;
    }
    private arm() {
        this.cancel();
        if (!this.active || !this.state.visible || this.pinned || this.keyboardInput && this.focused) return;
        const generation = this.generation;
        this.timer = (this.clock.schedule ?? setTimeout)(() => {
            if (generation !== this.generation) return;
            this.timer = undefined;
            this.state.visible = false;
        }, 3000);
    }
    private reveal() {
        if (!this.active) return;
        this.state.visible = true;
        this.arm();
    }
    start(pinned = false) {
        this.stop();
        this.active = true;
        this.pinned = pinned;
        this.reveal();
    }
    stop() {
        this.cancel();
        this.active = this.keyboardInput = this.focused = this.pinned = false;
        this.state.visible = true;
    }
    pointer(down = false) {
        if (down) this.keyboardInput = false;
        this.reveal();
    }
    keyboard() { this.keyboardInput = true; this.reveal(); }
    focus(focused: boolean) {
        this.focused = focused;
        if (focused) this.reveal();
        else this.arm();
    }
    pin(pinned: boolean) {
        if (pinned === this.pinned) return;
        this.pinned = pinned;
        this.reveal();
    }
}
