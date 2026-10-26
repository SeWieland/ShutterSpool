import { onMounted, onUnmounted, ref, watch, type Ref } from 'vue';

export function galleryGridWindow(count: number, scrollOffset: number, tileHeight: number, columns: number, viewportHeight: number) {
    const safeColumns = Math.max(1, columns);
    const safeTileHeight = Math.max(1, tileHeight);
    const start = Math.min(Math.max(0, Math.floor(Math.max(0, scrollOffset) / safeTileHeight) - 3) * safeColumns,
        Math.max(0, Math.floor((count - 1) / safeColumns) * safeColumns));
    const maxRows = Math.max(1, Math.floor(180 / safeColumns));
    const size = Math.min(maxRows, Math.ceil(viewportHeight / safeTileHeight) + 8) * safeColumns;
    return { start, end: Math.min(count, start + size) };
}

export function useGalleryGrid(rowCount: Ref<number>, enabled: Ref<boolean>, layout: Ref<unknown>) {
    const element = ref<HTMLElement>();
    const start = ref(0);
    const end = ref(90);
    const tileHeight = ref(220);
    const columns = ref(4);
    let resizeObserver: ResizeObserver | undefined;
    let observedTile: Element | null = null;
    let frame = 0;

    function measure() {
        const grid = element.value;
        if (!grid || !enabled.value) return;
        columns.value = Math.max(1, getComputedStyle(grid).gridTemplateColumns.split(' ').length);
        const tile = grid.querySelector('.gallery-tile');
        if (tile) {
            if (observedTile !== tile) {
                if (observedTile) resizeObserver?.unobserve(observedTile);
                observedTile = tile;
                resizeObserver?.observe(tile);
            }
            tileHeight.value = tile.getBoundingClientRect().height + parseFloat(getComputedStyle(grid).rowGap || '0');
        }
        const range = galleryGridWindow(rowCount.value, -grid.getBoundingClientRect().top, tileHeight.value, columns.value, window.innerHeight);
        start.value = range.start;
        end.value = range.end;
    }

    function schedule() {
        if (frame) return;
        frame = requestAnimationFrame(() => {
            frame = 0;
            measure();
        });
    }

    watch(element, (next, previous) => {
        if (previous) resizeObserver?.unobserve(previous);
        if (next) resizeObserver?.observe(next);
        schedule();
    });
    watch([rowCount, enabled, layout], schedule);
    onMounted(() => {
        resizeObserver = new ResizeObserver(schedule);
        if (element.value) resizeObserver.observe(element.value);
        window.addEventListener('resize', schedule);
        window.addEventListener('scroll', schedule, { passive: true });
        schedule();
    });
    onUnmounted(() => {
        resizeObserver?.disconnect();
        observedTile = null;
        window.removeEventListener('resize', schedule);
        window.removeEventListener('scroll', schedule);
        cancelAnimationFrame(frame);
    });

    return { element, start, end, tileHeight, columns, measure, schedule };
}
