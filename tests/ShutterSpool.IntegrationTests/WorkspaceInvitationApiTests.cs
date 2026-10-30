using Microsoft.EntityFrameworkCore;
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
            WorkspaceInvitationRecipient recipient = await store.RecordRecipientAsync(Guid.NewGuid(), new(Issuer, "invited-http", "private-contact-label", "synthetic-host-operator"), Token);
            WorkspaceMembership owner = Assert.Single(await store.ListAsync(Token));
            offer = (await store.ChangeInvitationAsync(new(Guid.NewGuid(), owner.Issuer, owner.Subject, "create", recipient.Id), Token)).Offer!;
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
            WorkspaceInvitationRecipient recipient = await store.RecordRecipientAsync(Guid.NewGuid(), new(Issuer, "unverified", "Synthetic unverified", "synthetic-host-operator"), Token);
            WorkspaceMembership owner = Assert.Single(await store.ListAsync(Token));
            offer = (await store.ChangeInvitationAsync(new(Guid.NewGuid(), owner.Issuer, owner.Subject, "create", recipient.Id), Token)).Offer!;
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
    public async Task Workspace_invitation_HTTP_admin_selects_only_local_operator_references_and_unavailable_workspace_never_grants()
    {
        await using var fixture = await TenantHttpFixture.CreateAsync();
        var workspace = fixture.Workspaces[0];
        var other = fixture.Workspaces[1];
        Guid local;
        Guid foreign;
        await using (var scope = fixture.Storage.Scope(fixture.Server.Services, workspace))
            local = (await new EfAccessGrantStore(scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>()).RecordRecipientAsync(Guid.NewGuid(), new(Issuer, "selected-reference", "Selected local tester", "synthetic-host-operator"), Token)).Id;
        await using (var scope = fixture.Storage.Scope(fixture.Server.Services, other))
            foreign = (await new EfAccessGrantStore(scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>()).RecordRecipientAsync(Guid.NewGuid(), new(Issuer, "other-reference", "Other private label", "synthetic-host-operator"), Token)).Id;
        using HttpClient owner = fixture.Client(workspace.Name);
        using HttpResponseMessage recipients = await SendAsync(owner, HttpMethod.Get, "/management/invitation-recipients", workspace.Id);
        string text = await recipients.Content.ReadAsStringAsync(Token);
        Assert.Contains("Selected local tester", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Other private label", text, StringComparison.Ordinal);
        Assert.DoesNotContain("selected-reference", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Issuer, text, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(owner, HttpMethod.Post, "/management/invitations", workspace.Id,
            new WorkspaceInvitationCreateRequest(Guid.NewGuid(), foreign))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(owner, HttpMethod.Post, "/management/invitations", workspace.Id,
            new WorkspaceInvitationCreateRequest(Guid.NewGuid(), local, "Owner"))).StatusCode);
        using HttpResponseMessage create = await SendAsync(owner, HttpMethod.Post, "/management/invitations", workspace.Id,
            new WorkspaceInvitationCreateRequest(Guid.NewGuid(), local));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        WorkspaceInvitationChangeResponse created = (await create.Content.ReadFromJsonAsync<WorkspaceInvitationChangeResponse>(Token))!;
        Assert.Equal("Viewer", created.Offer!.Role);
        using HttpClient client = fixture.Client("selected-reference");
        client.DefaultRequestHeaders.Authorization = new("Bearer", InvitationBearer("selected-reference", "true", ClaimValueTypes.Boolean));
        string marker = Path.Combine(workspace.StorageRoot, ".shutterspool-workspace.json");
        string retained = await File.ReadAllTextAsync(marker, Token);
        await File.WriteAllTextAsync(marker, "{}", Token);
        try
        {
            using HttpResponseMessage unavailable = await client.PostAsJsonAsync($"/workspace-offers/{workspace.Id:D}/{created.Offer.Id:D}/accept",
                new WorkspaceInvitationChangeRequest(Guid.NewGuid(), created.Offer.Revision), Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        }
        finally { await File.WriteAllTextAsync(marker, retained, Token); }
        await using var verify = fixture.Storage.Scope(fixture.Server.Services, workspace);
        Assert.False(await verify.ServiceProvider.GetRequiredService<IAccessGrantStore>().IsActiveAsync(Issuer, "selected-reference", Token));
        Assert.Equal("pending", Assert.Single(await verify.ServiceProvider.GetRequiredService<IWorkspaceInvitationStore>().ListInvitationsAsync(Token)).Status);
    }

    private static string InvitationBearer(string subject, string value, string valueType) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer,
        "shutterspool", [new Claim(ClaimTypes.NameIdentifier, subject), new Claim("email_verified", value, valueType)],
        expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new(new SymmetricSecurityKey(SigningKey), SecurityAlgorithms.HmacSha256)));
}
