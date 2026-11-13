using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShutterSpool.Application.Security;
using ShutterSpool.Domain.Security;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Server.Security;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed class WorkspaceAuthorizationPolicyTests
{
    [Theory]
    [InlineData(WorkspaceRole.Owner, true, true, true)]
    [InlineData(WorkspaceRole.Administrator, true, true, false)]
    [InlineData(WorkspaceRole.Curator, true, false, false)]
    [InlineData(WorkspaceRole.Viewer, false, false, false)]
    public async Task Named_policies_admit_current_local_permissions_with_one_lookup_per_request(
        WorkspaceRole role, bool curate, bool administer, bool manageOwners)
    {
        var store = new CurrentMembershipStore(role);
        await using WebApplication app = CreatePolicyServer(store);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = app.GetTestClient();
        foreach ((string policy, bool allowed) in new[] { (WorkspacePolicies.Member, true),
            (WorkspacePolicies.Curate, curate), (WorkspacePolicies.Administer, administer),
            (WorkspacePolicies.ManageOwners, manageOwners) })
        {
            int previous = store.Lookups;
            using HttpResponseMessage response = await client.GetAsync($"/management/{policy}", TestContext.Current.CancellationToken);
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(previous + 1, store.Lookups);
        }
    }

    [Fact]
    public async Task New_management_endpoint_without_classification_denies_even_owner_with_default_authorization()
    {
        var store = new CurrentMembershipStore(WorkspaceRole.Owner);
        await using WebApplication app = CreatePolicyServer(store);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync("/management/unclassified", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("permission-denied", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Same_sign_in_loses_permission_and_membership_on_subsequent_requests()
    {
        var store = new CurrentMembershipStore(WorkspaceRole.Owner);
        await using WebApplication app = CreatePolicyServer(store);
        await app.StartAsync(TestContext.Current.CancellationToken);
        using HttpClient client = app.GetTestClient();
        using HttpResponseMessage admitted = await client.GetAsync($"/management/{WorkspacePolicies.Administer}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        store.Member = store.Member with { Role = WorkspaceRole.Viewer };
        using HttpResponseMessage downgraded = await client.GetAsync($"/management/{WorkspacePolicies.Administer}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, downgraded.StatusCode);
        store.Member = store.Member with { IsActive = false };
        using HttpResponseMessage revoked = await client.GetAsync($"/management/{WorkspacePolicies.Member}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        JsonElement problem = await revoked.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("membership-revoked", problem.GetProperty("errorCode").GetString());
    }

    private static WebApplication CreatePolicyServer(CurrentMembershipStore store)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddShutterSpoolSecurity(builder.Configuration);
        builder.Services.AddScoped<WorkspaceAccessContext>();
        builder.Services.AddSingleton<IAccessGrantStore>(store);
        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            // Provider roles cannot override the authoritative local membership.
            context.User = new ClaimsPrincipal(new ClaimsIdentity([
                new("iss", "https://issuer.test"), new(ClaimTypes.NameIdentifier, "operator"),
                new(ClaimTypes.Role, "Owner")], "synthetic"));
            await next(context);
        });
        app.UseAuthorization();
        RouteGroupBuilder management = app.MapManagementGroup();
        foreach (string policy in new[] { WorkspacePolicies.Member, WorkspacePolicies.Curate,
            WorkspacePolicies.Administer, WorkspacePolicies.ManageOwners })
            management.MapGet($"/{policy}", () => Results.Ok()).RequireAuthorization(policy);
        management.MapGet("/unclassified", () => Results.Ok()).RequireAuthorization();
        return app;
    }

    private sealed class CurrentMembershipStore(WorkspaceRole role) : IAccessGrantStore
    {
        public WorkspaceMembership Member { get; set; } = new(Guid.NewGuid(), "https://issuer.test", "operator", role, 0, true);
        public int Lookups { get; private set; }
        public Task<WorkspaceMembership?> GetAsync(string issuer, string subject, CancellationToken cancellationToken)
        {
            Lookups++;
            return Task.FromResult<WorkspaceMembership?>(Member.Issuer == issuer && Member.Subject == subject ? Member : null);
        }
        public Task<WorkspaceViewerPolicy> GetViewerPolicyAsync(CancellationToken cancellationToken) => Task.FromResult(new WorkspaceViewerPolicy(false, 0));
        public Task<bool> IsActiveAsync(string issuer, string subject, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkspaceMembership[]> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateDisplayNameAsync(string issuer, string subject, string? displayName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AccessChangeResult> ChangeAsync(AccessChange change, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Management_endpoints_publish_explicit_native_permissions_without_duplicate_grant_checks()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        RouteEndpoint[] endpoints = [.. fixture.Server.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/management", StringComparison.Ordinal) == true)];
        Assert.NotEmpty(endpoints);
        foreach (RouteEndpoint endpoint in endpoints)
        {
            IAuthorizeData permission = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Contains(permission.Policy, new[] { WorkspacePolicies.Member, WorkspacePolicies.Curate,
                WorkspacePolicies.Administer, WorkspacePolicies.ManageOwners, WorkspacePolicies.Unclassified });
        }
        Assert.Equal(WorkspacePolicies.Member, Permission("/management/captures", "GET"));
        Assert.Equal(WorkspacePolicies.Curate, Permission("/management/captures/{id:guid}/curation", "PUT"));
        Assert.Equal(WorkspacePolicies.Administer, Permission("/management/sources", "GET"));
        Assert.Equal(WorkspacePolicies.ManageOwners, Permission("/management/members/{id:guid}/promote-owner", "POST"));
        Assert.Equal(WorkspacePolicies.Unclassified, Permission("/management/retention/erasures/suppressions/exports", "POST"));

        string? Permission(string route, string method) => Assert.Single(endpoints, endpoint =>
            endpoint.RoutePattern.RawText?.TrimEnd('/') == route && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method))
            .Metadata.GetMetadata<IAuthorizeData>()!.Policy;
    }

    [Fact]
    public async Task Access_caches_and_clears_bounded_authenticated_display_name_without_permission_revision_change()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        fixture.SetAuthenticatedDisplayName("  " + new string('n', 300) + "  ");
        JsonElement access = await fixture.Client.GetFromJsonAsync<JsonElement>("/management/access", token);
        long revision = access.GetProperty("revision").GetInt64();
        JsonElement members = await fixture.Client.GetFromJsonAsync<JsonElement>("/management/members", token);
        Assert.Equal(new string('n', 256), Assert.Single(members.GetProperty("items").EnumerateArray()).GetProperty("displayName").GetString());
        JsonElement repeated = await fixture.Client.GetFromJsonAsync<JsonElement>("/management/access", token);
        Assert.Equal(revision, repeated.GetProperty("revision").GetInt64());
        fixture.SetAuthenticatedDisplayName(null);
        JsonElement cleared = await fixture.Client.GetFromJsonAsync<JsonElement>("/management/access", token);
        Assert.Equal(revision, cleared.GetProperty("revision").GetInt64());
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        AuthorizationGrant member = await database.AuthorizationGrants.SingleAsync(token);
        Assert.Null(member.DisplayName);
        Assert.Equal("https://issuer.test", member.Issuer);
        Assert.Equal("operator", member.Subject);
        Assert.Equal(WorkspaceRole.Owner, member.Role);
    }
}
