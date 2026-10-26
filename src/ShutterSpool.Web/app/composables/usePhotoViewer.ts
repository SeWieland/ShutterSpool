import { reactive, ref, type Ref } from 'vue';
import { clampPan, zoomAround } from '../lib/workspace-interactions';

export function usePhotoViewer(frameElement: Ref<HTMLElement | undefined>, enabled: Ref<boolean>) {
    const zoom = ref(1);
    const offset = reactive({ x: 0, y: 0 });
    const pointers = new Map<number, { x: number; y: number }>();
    let distance = 0;

    function frameDimensions() {
        const viewport = { width: frameElement.value?.clientWidth ?? 1, height: frameElement.value?.clientHeight ?? 1 };
        const image = frameElement.value?.querySelector('img');
        const ratio = image?.naturalWidth && image.naturalHeight ? image.naturalWidth / image.naturalHeight : viewport.width / viewport.height;
        const width = Math.min(viewport.width, viewport.height * ratio);
        return { viewport, photo: { width, height: width / ratio } };
    }
    function boundOffset() {
        const { viewport, photo } = frameDimensions();
        Object.assign(offset, clampPan(offset, zoom.value, photo, viewport));
    }
    function setZoom(requested: number, point = { x: 0, y: 0 }) {
        const result = zoomAround(zoom.value, offset, requested, point);
        zoom.value = result.zoom;
        Object.assign(offset, result.offset);
        boundOffset();
    }
    function reset() {
        zoom.value = 1;
        offset.x = offset.y = 0;
        pointers.clear();
        distance = 0;
    }
    function wheel(event: WheelEvent) {
        if (!enabled.value) return;
        event.preventDefault();
        const frame = frameElement.value?.getBoundingClientRect();
        setZoom(zoom.value * Math.exp(-event.deltaY * .002), { x: event.clientX - (frame?.left ?? 0) - (frame?.width ?? 0) / 2, y: event.clientY - (frame?.top ?? 0) - (frame?.height ?? 0) / 2 });
    }
    function pointerDown(event: PointerEvent) {
        pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
        (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
        if (pointers.size === 2) {
            const points = [...pointers.values()];
            distance = Math.hypot(points[0]!.x - points[1]!.x, points[0]!.y - points[1]!.y);
        }
    }
    function pointerMove(event: PointerEvent) {
        const previous = pointers.get(event.pointerId);
        if (!previous || !enabled.value) return;
        pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
        if (pointers.size === 2) {
            const points = [...pointers.values()];
            const next = Math.hypot(points[0]!.x - points[1]!.x, points[0]!.y - points[1]!.y);
            const frame = frameElement.value?.getBoundingClientRect();
            if (distance)
                setZoom(zoom.value * next / distance, { x: (points[0]!.x + points[1]!.x) / 2 - (frame?.left ?? 0) - (frame?.width ?? 0) / 2, y: (points[0]!.y + points[1]!.y) / 2 - (frame?.top ?? 0) - (frame?.height ?? 0) / 2 });
            distance = next;
        } else if (zoom.value > 1) {
            offset.x += event.clientX - previous.x;
            offset.y += event.clientY - previous.y;
            boundOffset();
        }
    }
    function pointerUp(event: PointerEvent) {
        pointers.delete(event.pointerId);
        distance = 0;
    }
    return { zoom, offset, setZoom, boundOffset, reset, wheel, pointerDown, pointerMove, pointerUp };
}
