export interface WorkspaceMembership {
    id: string;
    name: string;
}

export type WorkspaceSelection =
    | { state: 'selected'; workspace: WorkspaceMembership }
    | { state: 'legacy' }
    | { state: 'choose' }
    | { state: 'empty' }
    | { state: 'incomplete' }
    | { state: 'unavailable' };

const workspaceIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function readWorkspaceId(search: string): string | null {
    const values = new URLSearchParams(search).getAll('workspace');
    const [value] = values;
    if (values.length !== 1 || !value || !workspaceIdPattern.test(value)) return null;
    return value.toLowerCase();
}

export function workspaceSelection(
    memberships: WorkspaceMembership[],
    incomplete: boolean,
    selectionRequired: boolean,
    selectedId: string | null,
): WorkspaceSelection {
    const workspace = selectedId && memberships.find(item => item.id.toLowerCase() === selectedId);
    if (workspace) return { state: 'selected', workspace };
    if (selectedId) return { state: incomplete ? 'incomplete' : 'unavailable' };
    if (!selectionRequired) return { state: 'legacy' };
    if (memberships.length) return { state: 'choose' };
    return { state: incomplete ? 'incomplete' : 'empty' };
}

export function withWorkspace(path: string, workspaceId: string | null): string {
    const url = new URL(path, 'http://workspace.local');
    if (workspaceId) url.searchParams.set('workspace', workspaceId);
    else url.searchParams.delete('workspace');
    return `${url.pathname}${url.search}${url.hash}`;
}

export function workspaceRequestHeaders(
    url: string,
    headers: HeadersInit | undefined,
    workspaceId: string | null,
): Headers {
    const result = new Headers(headers);
    const path = new URL(url, 'http://workspace.local').pathname;
    if (workspaceId && path !== '/workspaces' && !path.startsWith('/workspace-offers'))
        result.set('X-ShutterSpool-Workspace', workspaceId);
    return result;
}

const authCallbackPage = typeof window !== 'undefined' && window.location.pathname === '/auth/callback';
export let pageWorkspaceId = typeof window === 'undefined' || window.location.pathname.startsWith('/auth/')
    ? null
    : readWorkspaceId(window.location.search);
let pageWorkspaceScopeInitialized = typeof window !== 'undefined' && !window.location.pathname.startsWith('/auth/');

export function initializePageWorkspaceFromReturnTarget(target: string): boolean {
    if (pageWorkspaceScopeInitialized || !authCallbackPage || typeof window === 'undefined') return false;
    pageWorkspaceScopeInitialized = true;
    try {
        const url = new URL(target, window.location.origin);
        if (url.origin !== window.location.origin || url.pathname.startsWith('/auth/')) return false;
        pageWorkspaceId = readWorkspaceId(url.search);
        return true;
    } catch { return false; }
}
