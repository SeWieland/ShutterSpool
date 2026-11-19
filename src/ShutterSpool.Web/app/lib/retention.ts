import type { CaptureRetentionSummaryResponse } from '../api/models';

export type RetentionEvidence = CaptureRetentionSummaryResponse;

export function retentionLabel(value?: string | null) {
    const labels: Record<string, string> = { active: 'In your photographs', trash: 'Trash', purging: 'Removal in progress', purged: 'Originals removed', confirmedDelivery: 'Delivered to all intended destinations', explicitNoDelivery: 'Delivery not needed', confirmedFavorite: 'Favorite', waiting: 'Waiting for a delivery decision', heldOrUnfinished: 'On hold or delivery unfinished', available: 'Available', pendingRemoval: 'Removal pending', failedRemoval: 'Removal failed', removed: 'Removed', missing: 'Original missing · needs checking' };
    return value ? labels[value] ?? value : 'Unavailable';
}

export function originalRemovalScope(evidence?: RetentionEvidence | null) {
    if (!evidence?.availableOriginals || evidence.isFavorite == null) return undefined;
    const remaining = evidence.availableOriginals.filter(asset => asset.state !== 'removed');
    if (remaining.some(asset => !asset.variant || asset.byteLength == null || !Number.isFinite(asset.byteLength) || asset.byteLength < 0 || !['available', 'pendingRemoval', 'failedRemoval', 'missing'].includes(asset.state ?? ''))) return undefined;
    return remaining.map(asset => `${asset.variant} (${asset.byteLength!.toLocaleString('en')} bytes, ${retentionLabel(asset.state)})`).join('; ') || 'No remaining original variants recorded';
}

function validTime(date?: Date | null) {
    const time = date?.getTime();
    return time !== undefined && Number.isFinite(time) ? time : undefined;
}

export function retentionTiming(evidence: RetentionEvidence | null | undefined, now: number, details: boolean, unavailable = false) {
    const interval = evidence?.timeline;
    const observed = validTime(interval?.observedAt ?? evidence?.asOf);
    if (observed === undefined) return { visible: false, stale: true, title: 'Retention observation unavailable. Refresh to establish current evidence.', fraction: undefined, tone: 'neutral' };
    const stale = unavailable || now - observed > 90000;
    const clock = stale ? observed : now;
    const end = validTime(interval?.endsAt);
    if (end === undefined) return { visible: false, stale, title: 'Retention interval unavailable', fraction: undefined, tone: 'neutral' };
    const start = validTime(interval?.startedAt);
    const duration = start !== undefined && start < end ? end - start : undefined;
    const remaining = Math.max(0, end - clock);
    const recovery = interval?.kind === 'trashRecovery';
    const day = 86400000;
    const fixedWarning = interval?.kind === 'heldOrBlocked' || recovery;
    const warningWindow = fixedWarning ? day : Math.min(day, (duration ?? 0) * .2);
    const approaching = remaining <= warningWindow;
    const defaultMeaning = recovery ? 'Trash recovery' : 'Retention eligibility';
    const meaning = interval?.meaning ?? defaultMeaning;
    const deadline = remaining ? new Date(end).toLocaleString() : 'eligible · pending server action';
    const currentTitle = `${meaning} · ${deadline}`;
    const title = stale ? `Last known · ${currentTitle} · observed ${new Date(observed).toLocaleString()}` : currentTitle;
    const fraction = duration == null ? undefined : Math.min(1, remaining / duration);
    const warningTone = recovery ? 'red' : 'amber';
    const tone = approaching ? warningTone : 'neutral';
    return { visible: details || recovery || approaching, stale, title, fraction, tone };
}

export function durationHours(value?: string | null) {
    const match = /^(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2}(?:\.\d{1,7})?)$/.exec(value ?? '');
    if (!match || Number(match[2]) > 23 || Number(match[3]) > 59 || Number(match[4]) >= 60) return undefined;
    const hours = Number(match[1] ?? 0) * 24 + Number(match[2]) + Number(match[3]) / 60 + Number(match[4]) / 3600;
    return Number.isFinite(hours) ? hours : undefined;
}
export function durationFromHours(hours: number) {
    if (!Number.isFinite(hours) || hours <= 0) throw new RangeError('Use a positive finite duration.');
    const seconds = Math.round(hours * 3600 * 1e7) / 1e7;
    const days = Math.floor(seconds / 86400), remainder = seconds - days * 86400;
    const hour = Math.floor(remainder / 3600), minute = Math.floor(remainder % 3600 / 60);
    const [whole, fraction] = (remainder % 60).toFixed(7).split('.');
    const fractional = fraction!.replace(/0+$/, '');
    const second = whole!.padStart(2, '0') + (fractional ? `.${fractional}` : '');
    return `${days}.${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}:${second}`;
}
