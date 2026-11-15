using Microsoft.EntityFrameworkCore;
using ShutterSpool.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using ShutterSpool.Application.Security;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Server.Api.Workspaces;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class WorkspaceTenancyHttpTests
{
    [Fact]
    public async Task Workspace_invitation_HTTP_discloses_only_exact_recipient_offer_then_joins_one_workspace_with_fresh_verified_access_token()
    {
        await using var fixture = await TenantHttpFixture.CreateAsync();
        var workspace = fixture.Workspaces[0];
        WorkspaceInvitation offer;
        await using (var scope = fixture.Storage.Scope(fixture.Server.Services, workspace))
        {
            var database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            var store = new EfAccessGrantStore(database);
            WorkspaceMembership owner = Assert.Single(await store.ListAsync(Token));
            offer = (await store.ChangeInvitationAsync(new WorkspaceInvitationCreate(Guid.NewGuid(), owner.Issuer, owner.Subject,
                new(Issuer, "invited-http", "private-contact-label")), Token)).Offer!;
        }
        using HttpClient anonymous = fixture.Server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/workspace-offers", Token)).StatusCode);
        using HttpClient wrong = fixture.Client("wrong-http");
        Assert.Empty((await wrong.GetFromJsonAsync<WorkspaceOffersResponse>("/workspace-offers", Token))!.Items);
        var attempt = new WorkspaceInvitationChangeRequest(Guid.NewGuid(), offer.Revision);
        string join = $"/workspace-offers/{workspace.Id:D}/{offer.Id:D}/accept";
        using HttpResponseMessage wrongPrincipal = await wrong.PostAsJsonAsync(join, attempt with { OperationId = Guid.NewGuid() }, Token);
        Assert.Equal(HttpStatusCode.NotFound, wrongPrincipal.StatusCode);
        string wrongBody = await wrongPrincipal.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain(workspace.Name, wrongBody, StringComparison.Ordinal);
        Assert.DoesNotContain("private-contact-label", wrongBody, StringComparison.Ordinal);
        using HttpClient recipientClient = fixture.Client("invited-http");
        JsonElement discovery = await recipientClient.GetFromJsonAsync<JsonElement>("/workspace-offers", Token);
        JsonElement visible = Assert.Single(discovery.GetProperty("items").EnumerateArray());
        Assert.Equal(workspace.Id, visible.GetProperty("workspaceId").GetGuid());
        Assert.Equal("Viewer", visible.GetProperty("role").GetString());
        Assert.False(discovery.GetProperty("incomplete").GetBoolean());
        Assert.False(visible.TryGetProperty("recipientLabel", out _));
        Assert.False(visible.TryGetProperty("recipientId", out _));
        Assert.False(visible.TryGetProperty("issuer", out _));
        using HttpResponseMessage beforeMembership = await SendAsync(recipientClient, HttpMethod.Get, "/management/access", workspace.Id);
        Assert.Equal(HttpStatusCode.Forbidden, beforeMembership.StatusCode);
        using HttpResponseMessage ordinaryGet = await recipientClient.GetAsync(join, Token);
        await using (var scope = fixture.Storage.Scope(fixture.Server.Services, workspace))
            Assert.False(await scope.ServiceProvider.GetRequiredService<IAccessGrantStore>().IsActiveAsync(Issuer, "invited-http", Token));
        using HttpResponseMessage missing = await recipientClient.PostAsJsonAsync(join, attempt with { OperationId = Guid.NewGuid() }, Token);
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal("email-verification-required", (await missing.Content.ReadFromJsonAsync<WorkspaceOfferProblemDetails>(Token))!.ErrorCode);
        recipientClient.DefaultRequestHeaders.Authorization = new("Bearer", InvitationBearer("invited-http", "true", ClaimValueTypes.Boolean));
        using HttpResponseMessage joined = await recipientClient.PostAsJsonAsync(join, attempt, Token);
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        WorkspaceOfferAcceptanceResponse result = (await joined.Content.ReadFromJsonAsync<WorkspaceOfferAcceptanceResponse>(Token))!;
        Assert.Equal("accepted", result.Offer!.Status);
        Assert.Equal("Viewer", result.Membership!.Role);
        Assert.True(result.Membership.IsActive);
        Assert.Single((await recipientClient.GetFromJsonAsync<WorkspaceListResponse>("/workspaces", Token))!.Items);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(recipientClient, HttpMethod.Get, "/management/access", workspace.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(recipientClient, HttpMethod.Get, "/management/invitations", workspace.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(recipientClient, HttpMethod.Get, "/management/access", fixture.Workspaces[1].Id)).StatusCode);
        using HttpClient ownerClient = fixture.Client(workspace.Name);
        using HttpResponseMessage removal = await SendAsync(ownerClient, HttpMethod.Post, $"/management/members/{result.Membership.Id:D}/remove", workspace.Id,
            new WorkspaceMemberChangeRequest(Guid.NewGuid(), result.Membership.Revision));
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);
        using HttpResponseMessage replay = await recipientClient.PostAsJsonAsync(join, attempt, Token);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal("membership-revoked", (await replay.Content.ReadFromJsonAsync<WorkspaceOfferProblemDetails>(Token))!.ErrorCode);
        Assert.Empty((await recipientClient.GetFromJsonAsync<WorkspaceListResponse>("/workspaces", Token))!.Items);
    }

    [Theory]
    [InlineData("false", ClaimValueTypes.Boolean)]
    [InlineData("true", ClaimValueTypes.String)]
    [InlineData("TRUE", ClaimValueTypes.String)]
    [InlineData("1", ClaimValueTypes.Integer)]
    [InlineData("malformed", ClaimValueTypes.String)]
    public async Task Workspace_invitation_HTTP_rejects_false_or_malformed_email_verification_claim(string value, string valueType)
    {
        await using var fixture = await TenantHttpFixture.CreateAsync();
        var workspace = fixture.Workspaces[0];
        WorkspaceInvitation offer;
        await using (var scope = fixture.Storage.Scope(fixture.Server.Services, workspace))
        {
            var database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            var store = new EfAccessGrantStore(database);
            WorkspaceMembership owner = Assert.Single(await store.ListAsync(Token));
            offer = (await store.ChangeInvitationAsync(new WorkspaceInvitationCreate(Guid.NewGuid(), owner.Issuer, owner.Subject,
                new(Issuer, "unverified", "Synthetic unverified")), Token)).Offer!;
        }
        using HttpClient client = fixture.Client("unverified");
        client.DefaultRequestHeaders.Authorization = new("Bearer", InvitationBearer("unverified", value, valueType));
        using HttpResponseMessage result = await client.PostAsJsonAsync($"/workspace-offers/{workspace.Id:D}/{offer.Id:D}/accept",
            new WorkspaceInvitationChangeRequest(Guid.NewGuid(), offer.Revision), Token);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal("email-verification-required", (await result.Content.ReadFromJsonAsync<WorkspaceOfferProblemDetails>(Token))!.ErrorCode);
        await using var verify = fixture.Storage.Scope(fixture.Server.Services, workspace);
        Assert.False(await verify.ServiceProvider.GetRequiredService<IAccessGrantStore>().IsActiveAsync(Issuer, "unverified", Token));
    }

    [Fact]
    public async Task Workspace_invitation_HTTP_authorizes_email_enrollment_and_retries_setup_without_duplicate_offer()
    {
        var enrollment = new InvitationEnrollment();
        await using var fixture = await TenantHttpFixture.CreateAsync(services => services.AddSingleton<IAccountEnrollment>(enrollment));
        var workspace = fixture.Workspaces[0];
        var request = new WorkspaceInvitationCreateRequest(Guid.NewGuid(), "selected@example.test");
        using HttpClient outsider = fixture.Client("outsider");
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(outsider, HttpMethod.Post, "/management/invitations", workspace.Id, request)).StatusCode);
        Assert.Equal(0, enrollment.ResolveCalls);
        using HttpClient owner = fixture.Client(workspace.Name);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(owner, HttpMethod.Post, "/management/invitations", workspace.Id,
            request with { Role = "Owner" })).StatusCode);
        Assert.Equal(0, enrollment.ResolveCalls);
        enrollment.SetupStatus = "setup-email-failed";
        using HttpResponseMessage create = await SendAsync(owner, HttpMethod.Post, "/management/invitations", workspace.Id, request);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        WorkspaceInvitationChangeResponse created = (await create.Content.ReadFromJsonAsync<WorkspaceInvitationChangeResponse>(Token))!;
        Assert.Equal("applied", created.Status);
        Assert.Equal("setup-email-failed", created.SetupEmailStatus);
        Assert.Equal("Viewer", created.Offer!.Role);
        await using (var saved = fixture.Storage.Scope(fixture.Server.Services, workspace))
        {
            var store = saved.ServiceProvider.GetRequiredService<IWorkspaceInvitationStore>();
            Assert.Equal(created.Offer.Id, Assert.Single(await store.ListInvitationsAsync(Token)).Id);
            Assert.False(await saved.ServiceProvider.GetRequiredService<IAccessGrantStore>().IsActiveAsync(Issuer, "selected-reference", Token));
        }
        enrollment.SetupStatus = "setup-email-sent";
        using HttpResponseMessage resend = await SendAsync(owner, HttpMethod.Post, "/management/invitations", workspace.Id, request);
        WorkspaceInvitationChangeResponse resent = (await resend.Content.ReadFromJsonAsync<WorkspaceInvitationChangeResponse>(Token))!;
        Assert.Equal(created.Offer, resent.Offer);
        Assert.Equal("setup-email-sent", resent.SetupEmailStatus);
        Assert.Equal(2, enrollment.SetupCalls);
        using HttpClient client = fixture.Client("selected-reference");
        client.DefaultRequestHeaders.Authorization = new("Bearer", InvitationBearer("selected-reference", "true", ClaimValueTypes.Boolean));
        string acceptPath = $"/workspace-offers/{workspace.Id:D}/{created.Offer.Id:D}/accept";
        var accept = new WorkspaceInvitationChangeRequest(Guid.NewGuid(), created.Offer.Revision);
        await fixture.Storage.SetDatabaseAvailableAsync(1, false);
        try
        {
            using HttpResponseMessage unavailable = await client.PostAsJsonAsync(acceptPath, accept, Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        }
        finally { await fixture.Storage.SetDatabaseAvailableAsync(1, true); }
        await using var verify = fixture.Storage.Scope(fixture.Server.Services, workspace);
        Assert.False(await verify.ServiceProvider.GetRequiredService<IAccessGrantStore>().IsActiveAsync(Issuer, "selected-reference", Token));
        Assert.Equal("pending", Assert.Single(await verify.ServiceProvider.GetRequiredService<IWorkspaceInvitationStore>().ListInvitationsAsync(Token)).Status);
        using HttpResponseMessage retry = await client.PostAsJsonAsync(acceptPath, accept, Token);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True(await verify.ServiceProvider.GetRequiredService<IAccessGrantStore>().IsActiveAsync(Issuer, "selected-reference", Token));
    }

    [Theory]
    [InlineData("enrollment-unavailable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("invalid-email", HttpStatusCode.BadRequest)]
    [InlineData("account-unavailable", HttpStatusCode.Conflict)]
    [InlineData("enrollment-rejected", HttpStatusCode.Conflict)]
    public async Task Workspace_invitation_HTTP_reports_enrollment_failure_without_saving_offer(string status, HttpStatusCode expected)
    {
        var enrollment = new InvitationEnrollment { ResolveStatus = status };
        await using var fixture = await TenantHttpFixture.CreateAsync(services => services.AddSingleton<IAccountEnrollment>(enrollment));
        var workspace = fixture.Workspaces[0];
        using HttpClient owner = fixture.Client(workspace.Name);
        using HttpResponseMessage result = await SendAsync(owner, HttpMethod.Post, "/management/invitations", workspace.Id,
            new WorkspaceInvitationCreateRequest(Guid.NewGuid(), "selected@example.test"));
        Assert.Equal(expected, result.StatusCode);
        Assert.Equal(status, (await result.Content.ReadFromJsonAsync<WorkspaceInvitationProblemDetails>(Token))!.ErrorCode);
        await using var scope = fixture.Storage.Scope(fixture.Server.Services, workspace);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<IWorkspaceInvitationStore>().ListInvitationsAsync(Token));
        Assert.Equal(0, enrollment.SetupCalls);
    }

    private sealed class InvitationEnrollment : IAccountEnrollment
    {
        public int ResolveCalls { get; private set; }
        public int SetupCalls { get; private set; }
        public string ResolveStatus { get; set; } = "resolved";
        public string SetupStatus { get; set; } = "not-required";

        public Task<AccountEnrollmentResult> ResolveAsync(string email, CancellationToken cancellationToken)
        {
            ResolveCalls++;
            Assert.Equal("selected@example.test", email);
            return Task.FromResult(new AccountEnrollmentResult(ResolveStatus,
                ResolveStatus == "resolved" ? new(Issuer, "selected-reference", email) : null));
        }

        public Task<string> SendSetupEmailAsync(AccountEnrollmentIdentity account, CancellationToken cancellationToken)
        {
            SetupCalls++;
            Assert.Equal(new(Issuer, "selected-reference", "selected@example.test"), account);
            return Task.FromResult(SetupStatus);
        }
    }

    private static string InvitationBearer(string subject, string value, string valueType) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer,
        "shutterspool", [new Claim(ClaimTypes.NameIdentifier, subject), new Claim("email_verified", value, valueType)],
        expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new(new SymmetricSecurityKey(SigningKey), SecurityAlgorithms.HmacSha256)));
}
