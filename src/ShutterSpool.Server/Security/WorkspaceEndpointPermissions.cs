using ShutterSpool.Application.Security;

namespace ShutterSpool.Server.Security;

public static class WorkspaceEndpointPermissions
{
    // Exact verb/template pairs make new private endpoints deny until explicitly classified.
    private static readonly HashSet<string> Member =
    [
        "GET /management/status", "GET /management/access", "GET /management/viewer-policy",
        "POST /management/membership/leave", "GET /management/captures", "GET /management/captures/{id}",
        "GET /management/tags", "GET /management/collections", "GET /management/collections/{id}",
        "GET /management/captures/{id}/curation", "GET /management/captures/{id}/delivery-summary",
        "GET /media/previews/{id}", "GET /media/previews/{id}/{profile}", "GET /media/originals/{id}",
        "GET /live/status", "GET /live/changes", "POST /live/changes", "POST /live/changes/negotiate",
        "DELETE /live/changes",
    ];
    private static readonly HashSet<string> Curator =
    [
        "PUT /management/captures/{id}/curation", "PUT /management/captures/{id}/favorite",
        "PUT /management/captures/{id}/collection", "POST /management/captures/collection-moves",
        "POST /management/captures/{id}/hold", "POST /management/captures/{id}/restore",
        "POST /management/captures/{id}/release", "POST /management/captures/batch/curation",
        "POST /management/captures/batch/hold", "POST /management/captures/batch/restore",
        "POST /management/captures/batch/release", "POST /management/captures/{id}/trash",
        "GET /management/trash", "POST /management/trash/{id}/restore", "POST /management/deliveries/{id}/retry",
    ];
    private static readonly HashSet<string> Administrator =
    [
        "GET /management/members", "POST /management/members/{id}/role", "POST /management/members/{id}/remove",
        "POST /management/members/{id}/promote-owner", "PUT /management/viewer-policy",
        "GET /management/sources", "POST /management/sources", "GET /management/sources/{id}",
        "PUT /management/sources/{id}", "DELETE /management/sources/{id}",
        "POST /management/sources/{id}/regenerate-secret", "PUT /management/sources/{id}/ingress",
        "PUT /management/sources/{id}/curation-policy", "GET /management/source-capabilities",
        "GET /management/source-capabilities/{profile}/ca-root", "GET /management/sources/{id}/display",
        "GET /management/sources/authentication-observations", "GET /management/activity/transfers", "GET /management/ingress/transfers",
        "POST /management/collections", "PUT /management/collections/{id}", "DELETE /management/collections/{id}",
        "GET /management/destinations/capabilities", "GET /management/destinations", "POST /management/destinations",
        "GET /management/destinations/{id}", "PUT /management/destinations/{id}", "DELETE /management/destinations/{id}",
        "GET /management/destinations/{id}/local", "PUT /management/destinations/{id}/local",
        "POST /management/destinations/{id}/local/test", "GET /management/destinations/local-folders",
        "GET /management/destinations/{id}/webdav", "PUT /management/destinations/{id}/webdav",
        "POST /management/destinations/{id}/webdav/test", "GET /management/destinations/{id}/routing",
        "PUT /management/destinations/{id}/routing", "GET /management/destinations/{id}/work",
        "GET /management/captures/{id}/routing", "GET /management/captures/{id}/deliveries",
        "GET /management/captures/{id}/actions", "GET /management/deliveries/{id}/attempts",
        "POST /management/deliveries/{id}/resend/preview", "POST /management/deliveries/{id}/resend",
        "GET /management/assets/{assetId}/destinations/{destinationId}/reevaluate-preview",
        "POST /management/assets/{assetId}/destinations/{destinationId}/reevaluate",
        "POST /management/trash/{id}/purge", "POST /management/captures/{id}/resume",
        "POST /management/captures/{id}/no-delivery-intent", "GET /management/retention/capacity",
        "GET /management/retention/history", "GET /management/retention/settings", "PUT /management/retention/settings",
        "POST /management/retention/settings/preview", "GET /management/retention/gallery-protections/{id}",
        "PUT /management/retention/gallery-protections/{id}", "POST /management/retention/gallery-protections",
        "POST /management/retention/gallery-protections/{id}/revoke-access",
        "GET /management/retention/preservation-holds/{id}", "POST /management/retention/preservation-holds",
        "POST /management/retention/preservation-holds/{id}/resolve", "POST /management/retention/erasures/preview",
        "POST /management/retention/erasures", "GET /management/retention/erasures",
        "GET /management/retention/erasures/{operationId}", "POST /management/retention/erasures/{operationId}/continue",
    ];
    private static readonly HashSet<string> Operator =
    [
        "POST /management/retention/erasures/suppressions/exports",
        "GET /management/retention/erasures/suppressions/exports/{snapshotId}/manifest",
        "GET /management/retention/erasures/suppressions/exports/{snapshotId}/pages/{pageIndex}",
        "POST /management/retention/erasures/suppressions/reconciliation",
        "POST /management/retention/erasures/suppressions/reconciliation/pages",
        "POST /management/retention/erasures/suppressions/reconciliation/{snapshotId}/complete",
    ];

    public static bool Allows(WorkspaceRole role, string method, string template, bool shared)
    {
        if (!Enum.IsDefined(role)) return false;
        string key = $"{method} {template.Replace(":guid", "", StringComparison.Ordinal).Replace(":int", "", StringComparison.Ordinal).TrimEnd('/')}";
        return Member.Contains(key) || role is not WorkspaceRole.Viewer && Curator.Contains(key)
            || role is WorkspaceRole.Owner or WorkspaceRole.Administrator && (Administrator.Contains(key) || !shared && Operator.Contains(key));
    }
}
