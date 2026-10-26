type Waiter = { signal: AbortSignal; grant: () => void; cancel: () => void };

/** Limits active metadata transports while allowing obsolete queued work to be removed. */
export function createReadPool(limit: number) {
    if (!Number.isInteger(limit) || limit < 1) throw new RangeError('Read pool limit must be a positive integer.');
    let active = 0;
    const waiting: Waiter[] = [];

    function release() {
        const next = waiting.shift();
        if (next) {
            next.signal.removeEventListener('abort', next.cancel);
            next.grant();
        } else {
            active--;
        }
    }

    async function run<T>(signal: AbortSignal, operation: () => Promise<T>): Promise<T> {
        if (signal.aborted) throw signal.reason ?? new Error('Read cancelled.');
        if (active >= limit) {
            await new Promise<void>((resolve, reject) => {
                const waiter: Waiter = {
                    signal,
                    grant: resolve,
                    cancel: () => {
                        const index = waiting.indexOf(waiter);
                        if (index >= 0) waiting.splice(index, 1);
                        reject(signal.reason ?? new Error('Read cancelled.'));
                    },
                };
                waiting.push(waiter);
                signal.addEventListener('abort', waiter.cancel, { once: true });
                if (signal.aborted) waiter.cancel();
            });
        } else {
            active++;
        }
        if (signal.aborted) {
            release();
            throw signal.reason ?? new Error('Read cancelled.');
        }
        try {
            return await operation();
        } finally {
            release();
        }
    }

    return { run };
}
