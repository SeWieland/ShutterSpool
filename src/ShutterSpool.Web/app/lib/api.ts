import { DefaultRequestAdapter } from "@microsoft/kiota-bundle";
import { HttpClient } from "@microsoft/kiota-http-fetchlibrary";
import { createShutterSpoolClient } from "../api/shutterSpoolClient";
import { clearAccess, token, onAccessLost } from "./session";
import { withPreviewSlot } from "./preview-queue";
import type { SourceActivity } from './source-activity';

const requests = new Set<AbortController>();
onAccessLost(() => {
    for (const request of requests) request.abort();
    requests.clear();
});

// Commands must never be silently replayed by transport retry middleware.
const http = new HttpClient(undefined, {
    next: undefined,
    execute: async (url, init, options) => {
        const controller = new AbortController();
        const signal = (options?.['shutterspool.abort'] as ReturnType<typeof cancellable> | undefined)?.signal;
        requests.add(controller);
        try {
            const response = await fetch(url, {
                ...init,
                signal: AbortSignal.any([controller.signal, ...(init.signal ? [init.signal] : []), ...(signal ? [signal] : [])]),
            });
            if (response.status === 401 || response.status === 403)
                handleFailure({ status: response.status });
            return response;
        } finally {
            requests.delete(controller);
        }
    },
});
const adapter = new DefaultRequestAdapter(
    {
        authenticateRequest: async (request) => {
            request.headers.add("Authorization", `Bearer ${await token()}`);
        },
    },
    undefined,
    undefined,
    http,
);
adapter.baseUrl = location.origin;
export const api = createShutterSpoolClient(adapter);
export function cancellable(signal: AbortSignal) {
    return { getKey: () => 'shutterspool.abort', signal };
}

export interface SourceAuthenticationObservation {
    sourceId: string;
    outcome: "Succeeded" | "Rejected";
    observedAt: string;
    version: number;
}

export async function readSourceAuthenticationObservations(sourceIds: string[], signal: AbortSignal) {
    if (!sourceIds.length || sourceIds.length > 100)
        throw new RangeError("Source authentication reads are limited to 100 identities.");
    signal.throwIfAborted();
    const result = await api.management.sources.authenticationObservations.get({ queryParameters: { sourceIds }, options: [cancellable(signal)] });
    signal.throwIfAborted();
    if (!result) throw new Error('Source authentication history is unavailable.');
    const items: SourceAuthenticationObservation[] = (result.items ?? []).map(item => {
        if (!item.sourceId || !item.observedAt || !Number.isFinite(item.observedAt.getTime()) ||
            !Number.isSafeInteger(item.version) || (item.outcome !== 'Succeeded' && item.outcome !== 'Rejected'))
            throw new Error('Source authentication history is unavailable.');
        return { sourceId: String(item.sourceId), observedAt: item.observedAt.toISOString(), version: item.version!, outcome: item.outcome };
    });
    return { coverageComplete: result.coverageComplete === true, items };
}

export async function readSourceActivity(sourceIds: string[], signal: AbortSignal, transferIds?: string[]) {
    if (!sourceIds.length || sourceIds.length > 100) throw new RangeError('Activity reads are limited to 100 Sources.');
    if (transferIds && (!transferIds.length || transferIds.length > 100)) throw new RangeError('Activity reads are limited to 100 transfers.');
    signal.throwIfAborted();
    const result = await api.management.activity.transfers.get({ queryParameters: { sourceIds: transferIds ? undefined : sourceIds, transferIds, limit: 100 }, options: [cancellable(signal)] });
    signal.throwIfAborted();
    if (!result?.epoch) throw new Error('Source activity is unavailable.');
    const items: SourceActivity[] = (result.items ?? []).map(item => {
        if (!item.transferId || !item.sourceId || !item.observedAt || !Number.isFinite(item.observedAt.getTime()) ||
            !Number.isSafeInteger(item.version) || !['Receiving', 'AcceptedAwaitingGrouping', 'PreparingPreview', 'Ready', 'FailedUnavailable', 'Stale'].includes(item.phase ?? ''))
            throw new Error('Source activity is unavailable.');
        return { transferId: String(item.transferId), sourceId: String(item.sourceId),
            assetId: item.assetId ? String(item.assetId) : undefined, captureId: item.captureId ? String(item.captureId) : undefined,
            phase: item.phase as SourceActivity['phase'], observedAt: item.observedAt.toISOString(), version: item.version!,
            accepted: item.accepted === true, previewReady: item.previewReady === true };
    });
    return { epoch: result.epoch, coverageComplete: result.coverageComplete === true && !result.nextCursor, items };
}

export function handleFailure(error: unknown) {
    const status =
        (error as { status?: number; responseStatusCode?: number })?.status ??
        (error as { responseStatusCode?: number })?.responseStatusCode;
    if (status === 401 || status === 403)
        clearAccess(
            status === 403
                ? "Access is forbidden or your local grant was revoked. Contact the operator before signing in again."
                : "Your session ended. Sign in again to continue.",
        );
    return status;
}

export async function derivative(
    captureId: string,
    profile: "thumbnail" | "detail",
    signal: AbortSignal,
    etag?: string,
    maximumDimension?: number,
) {
    return withPreviewSlot(async () => {
        signal.throwIfAborted();
        const headers = new Headers({
            Authorization: `Bearer ${await token()}`,
        });
        if (etag) headers.set("If-None-Match", etag);
        const response = await fetch(
            `/media/previews/${encodeURIComponent(captureId)}/${profile === "thumbnail" ? "grid" : "detail"}${maximumDimension ? `?maximumDimension=${maximumDimension}` : ''}`,
            { headers, signal },
        );
        if (response.status === 401 || response.status === 403) {
            handleFailure({ status: response.status });
            throw new Error("Access ended.");
        }
        const blob = response.ok ? await response.blob() : undefined;
        return { response, blob };
    });
}
