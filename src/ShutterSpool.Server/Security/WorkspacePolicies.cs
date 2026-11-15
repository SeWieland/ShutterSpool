using Microsoft.AspNetCore.Authorization;

namespace ShutterSpool.Server.Security;

public static class WorkspacePolicies
{
    public const string Member = "workspace-member";
    public const string Curate = "workspace-curate";
    public const string Administer = "workspace-administer";
    public const string ManageOwners = "workspace-manage-owners";
    public const string Unclassified = "workspace-unclassified";

    public static RouteGroupBuilder MapManagementGroup(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder management = endpoints.MapGroup("/management");
        ((IEndpointConventionBuilder)management).Finally(endpoint =>
        {
            // A new management endpoint must choose a workspace permission before it can admit anyone.
            if (!endpoint.Metadata.OfType<IAuthorizeData>().Any(data =>
                data.Policy is Member or Curate or Administer or ManageOwners or Unclassified))
                endpoint.Metadata.Add(new AuthorizeAttribute(Unclassified));
        });
        return management;
    }
}
