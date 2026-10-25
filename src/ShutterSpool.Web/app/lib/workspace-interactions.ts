import type { CaptureCurationResponse } from '../api/models';

const deliveryStates: Record<string, { label: string; icon: string; tone: string }> = {
    Delivered: { label: 'Delivered', icon: 'i-lucide-check-check', tone: 'success' },
    Pending: { label: 'Queued', icon: 'i-lucide-clock', tone: 'pending' },
    Running: { label: 'Delivering', icon: 'i-lucide-loader', tone: 'pending' },
    RetryWait: { label: 'Retry scheduled', icon: 'i-lucide-timer-reset', tone: 'pending' },
    Failed: { label: 'Delivery failed', icon: 'i-lucide-circle-alert', tone: 'attention' },
    Ambiguous: { label: 'Delivery needs checking', icon: 'i-lucide-circle-help', tone: 'attention' },
    Disabled: { label: 'Delivery paused', icon: 'i-lucide-pause', tone: 'attention' },
    Superseded: { label: 'Replaced', icon: 'i-lucide-history', tone: 'muted' },
    Matched: { label: 'Delivery rule matched', icon: 'i-lucide-route', tone: 'pending' },
    NoMatch: { label: 'No matching rule', icon: 'i-lucide-route-off', tone: 'attention' },
    InvalidTarget: { label: 'Invalid destination path', icon: 'i-lucide-folder-x', tone: 'attention' },
    DestinationDisabled: { label: 'Destination disabled', icon: 'i-lucide-pause', tone: 'attention' },
};
export function deliveryPresentation(state?: string | null) {
    return deliveryStates[state ?? ''] ?? { label: 'Status unavailable', icon: 'i-lucide-info', tone: 'muted' };
}

export function curationTiming(curation: CaptureCurationResponse | null | undefined, now: number) {
    if (!curation) return { icon: 'i-lucide-info', title: 'Delivery stage could not be checked', fraction: undefined, urgent: false };
    if (curation?.releasedAt) return { icon: 'i-lucide-check', title: 'Released', fraction: undefined, urgent: false };
    if (curation?.held) return { icon: 'i-lucide-pause', title: 'Held · release paused', fraction: undefined, urgent: false };
    const due = curation?.releaseDueAt?.getTime();
    if (due == null || !Number.isFinite(due)) return { icon: 'i-lucide-clock', title: 'Waiting for release', fraction: undefined, urgent: false };
    const seconds = Math.max(0, Math.ceil((due - now) / 1000));
    const minutes = Math.floor(seconds / 60), hours = Math.floor(minutes / 60);
    const remaining = hours >= 24 ? `${Math.floor(hours / 24)}d ${hours % 24}h` : hours ? `${hours}h ${minutes % 60}m` : minutes ? `${minutes}m ${seconds % 60}s` : `${seconds}s`;
    const duration = curation?.delaySeconds;
    const fraction = duration != null && duration > 0 ? Math.max(0, Math.min(1, (due - now) / (duration * 1000))) : undefined;
    return { icon: 'i-lucide-clock', title: seconds ? `Waiting · release in ${remaining}` : 'Release due · waiting for server', fraction, urgent: fraction != null && fraction <= .2 };
}

export function ratingInterval(anchor: number, value: number) {
    return { rating: Math.min(anchor, value), ratingMaximum: Math.max(anchor, value) };
}
export function zoomAround(current: number, offset: {
    x: number;
    y: number;
}, requested: number, point: {
    x: number;
    y: number;
}) {
    const zoom = Math.min(6, Math.max(1, requested));
    return { zoom, offset: { x: point.x - (point.x - offset.x) * zoom / current, y: point.y - (point.y - offset.y) * zoom / current } };
}
export function clampPan(offset: {
    x: number;
    y: number;
}, zoom: number, photo: {
    width: number;
    height: number;
}, viewport: {
    width: number;
    height: number;
}) {
    const x = Math.max(0, (photo.width * zoom - viewport.width) / 2);
    const y = Math.max(0, (photo.height * zoom - viewport.height) / 2);
    return { x: Math.min(x, Math.max(-x, offset.x)), y: Math.min(y, Math.max(-y, offset.y)) };
}
export function effectiveMotion(preference: string, systemReduced: boolean, visible: boolean, online: boolean) {
    return visible && online && (preference === 'full' || preference === 'system' && !systemReduced);
}
export function formatFileSize(bytes?: number | null) {
    if (bytes == null) return 'Unavailable';
    const unit = bytes > 0 ? Math.min(4, Math.floor(Math.log(bytes) / Math.log(1024))) : 0;
    return `${(bytes / 1024 ** unit).toLocaleString('en', { maximumFractionDigits: 1 })} ${['B', 'KiB', 'MiB', 'GiB', 'TiB'][unit]}`;
}
export function reconcileCaptures<T extends {
    id?: unknown;
    acceptedAt?: Date | string | null;
}>(retained: T[], incoming: T[], append: boolean, afterId = String(retained.at(-1)?.id ?? ''), oldestFirst = false) {
    if (!append && !incoming.length)
        return [];
    const fresh = new Map(incoming.map(row => [String(row.id), row]));
    const timestamp = (row: T) => typeof row.acceptedAt === 'string' ? Date.parse(row.acceptedAt) : row.acceptedAt?.getTime() ?? NaN;
    const boundary = incoming.at(-1);
    const boundaryTime = boundary ? timestamp(boundary) : NaN;
    // Dates lose PostgreSQL sub-millisecond precision; never infer equal-millisecond keyset order from IDs.
    const keep = (row: T) => {
        const time = timestamp(row);
        return !fresh.has(String(row.id)) && (append || !Number.isFinite(time) || !Number.isFinite(boundaryTime) || (oldestFirst ? time >= boundaryTime : time <= boundaryTime));
    };
    const anchor = append ? retained.findIndex(row => String(row.id) === afterId) + 1 : 0;
    const rows = [...retained.slice(0, anchor).filter(keep), ...incoming, ...retained.slice(anchor).filter(keep)];
    return [...new Map(rows.map(row => [String(row.id), row])).values()];
}
