using System.Net;
using System.Text.Json;
using ShutterSpool.Application.Security;
using ShutterSpool.Infrastructure.Security;

namespace ShutterSpool.IntegrationTests;

public sealed class KeycloakAccountEnrollmentTests
{
    private const string Email = "guest@example.invalid";
    private const string Issuer = "https://identity.example.invalid/realms/shutterspool";

    [Fact]
    public async Task Existing_verified_account_requires_no_mail_or_credential_change()
    {
        var handler = new Handler("{\"access_token\":\"synthetic\"}", UserList(true),
            "{\"access_token\":\"synthetic\"}", User(true));
        var adapter = Adapter(handler);
        var result = await adapter.ResolveAsync(Email, TestContext.Current.CancellationToken);
        Assert.Equal("resolved", result.Status);
        Assert.Equal(new AccountEnrollmentIdentity(Issuer, "subject", Email), result.Account);
        Assert.Equal("not-required", await adapter.SendSetupEmailAsync(result.Account!, TestContext.Current.CancellationToken));
        Assert.All(handler.Calls.Where(call => !call.Path.EndsWith("/token")), call => Assert.Equal("GET", call.Method));
        Assert.Contains("email=guest%40example.invalid&exact=true&max=2", handler.Calls[1].Path);
    }

    [Fact]
    public async Task Existing_account_without_pending_actions_field_does_not_reset_password()
    {
        var handler = new Handler("{\"access_token\":\"synthetic\"}", "{\"id\":\"subject\",\"email\":\"guest@example.invalid\",\"enabled\":true,\"emailVerified\":true}");
        Assert.Equal("not-required", await Adapter(handler).SendSetupEmailAsync(new(Issuer, "subject", Email), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task New_account_retains_native_setup_actions_before_any_email_is_sent()
    {
        var handler = new Handler("{\"access_token\":\"synthetic\"}", "[]", "created", UserList(false, "UPDATE_PASSWORD", "VERIFY_EMAIL"));
        var result = await Adapter(handler).ResolveAsync(Email, TestContext.Current.CancellationToken);
        Assert.Equal("resolved", result.Status);
        var creation = Assert.Single(handler.Calls, call => call.Method == "POST" && !call.Path.EndsWith("/token"));
        using var body = JsonDocument.Parse(creation.Body);
        Assert.Equal(Email, body.RootElement.GetProperty("email").GetString());
        Assert.False(body.RootElement.GetProperty("emailVerified").GetBoolean());
        Assert.Equal(new[] { "VERIFY_EMAIL", "UPDATE_PASSWORD" }, body.RootElement.GetProperty("requiredActions").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain(handler.Calls, call => call.Path.Contains("execute-actions-email"));
    }

    [Theory]
    [InlineData(false, "VERIFY_EMAIL", "setup-email-sent", false)]
    [InlineData(false, "VERIFY_EMAIL", "setup-email-failed", true)]
    [InlineData(true, "UPDATE_PASSWORD", "setup-email-sent", false)]
    public async Task Native_email_uses_only_current_setup_actions_and_reports_failure(bool verified, string action, string expected, bool fail)
    {
        var handler = new Handler("{\"access_token\":\"synthetic\"}", User(verified, verified ? ["UPDATE_PASSWORD", "CONFIGURE_TOTP"] : ["CONFIGURE_TOTP"]), fail ? "failed" : "sent");
        Assert.Equal(expected, await Adapter(handler).SendSetupEmailAsync(new(Issuer, "subject", Email), TestContext.Current.CancellationToken));
        var mail = Assert.Single(handler.Calls, call => call.Method == "PUT");
        Assert.EndsWith("/execute-actions-email?lifespan=86400", mail.Path);
        Assert.Equal(new[] { action }, JsonSerializer.Deserialize<string[]>(mail.Body));
    }

    [Fact]
    public async Task Missing_configuration_and_ambiguous_or_disabled_accounts_fail_closed()
    {
        var empty = new Handler();
        var missing = new KeycloakAccountEnrollment(new HttpClient(empty), new(), Issuer);
        Assert.Equal("enrollment-unavailable", (await missing.ResolveAsync(Email, TestContext.Current.CancellationToken)).Status);
        Assert.Empty(empty.Calls);
        foreach (string users in new[] { $"[{User(true)},{User(true)}]", UserList(true).Replace("\"enabled\":true", "\"enabled\":false") })
        {
            var handler = new Handler("{\"access_token\":\"synthetic\"}", users);
            Assert.Equal("account-unavailable", (await Adapter(handler).ResolveAsync(Email, TestContext.Current.CancellationToken)).Status);
            Assert.Equal(2, handler.Calls.Count);
        }
    }

    [Fact]
    public async Task Creation_conflict_reuses_exact_email_account_and_native_profile_rejection_is_honest()
    {
        var race = new Handler("{\"access_token\":\"synthetic\"}", "[]", "conflict", UserList(false, "UPDATE_PASSWORD"));
        Assert.Equal("resolved", (await Adapter(race).ResolveAsync(Email, TestContext.Current.CancellationToken)).Status);
        var rejected = new Handler("{\"access_token\":\"synthetic\"}", "[]", "rejected");
        Assert.Equal("enrollment-rejected", (await Adapter(rejected).ResolveAsync(Email, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Invalid_email_or_external_login_issuer_never_contacts_native_identity()
    {
        var handler = new Handler();
        Assert.Equal("invalid-email", (await Adapter(handler).ResolveAsync(null!, TestContext.Current.CancellationToken)).Status);
        Assert.Equal("invalid-email", (await Adapter(handler).ResolveAsync("Guest <guest@example.invalid>", TestContext.Current.CancellationToken)).Status);
        var external = new KeycloakAccountEnrollment(new HttpClient(handler), new()
        {
            ServerUrl = "https://identity.example.invalid", Issuer = Issuer, ClientSecret = "synthetic-secret",
        }, "https://external.example.invalid");
        Assert.Equal("enrollment-unavailable", (await external.ResolveAsync(Email, TestContext.Current.CancellationToken)).Status);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Changed_native_email_does_not_send_setup_to_a_different_recipient()
    {
        var handler = new Handler("{\"access_token\":\"synthetic\"}", User(false).Replace(Email, "changed@example.invalid"));
        Assert.Equal("setup-email-failed", await Adapter(handler).SendSetupEmailAsync(new(Issuer, "subject", Email), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(handler.Calls, call => call.Method == "PUT");
    }

    private static KeycloakAccountEnrollment Adapter(Handler handler) => new(new HttpClient(handler), new()
    {
        ServerUrl = "https://identity.example.invalid", Issuer = Issuer, Realm = "shutterspool", ClientSecret = "synthetic-secret",
    }, Issuer);

    private static string User(bool verified, params string[] actions) => JsonSerializer.Serialize(new
    {
        id = "subject", email = Email, enabled = true, emailVerified = verified, requiredActions = actions,
    });
    private static string UserList(bool verified, params string[] actions) => $"[{User(verified, actions)}]";

    private sealed class Handler(params string[] responses) : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(request.Options.TryGetValue(new HttpRequestOptionsKey<bool>("ShutterSpool.PrivateProviderRequest"), out bool suppressed) && suppressed);
            if (!request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                Assert.Equal("Bearer synthetic", request.Headers.Authorization?.ToString());
            Calls.Add((request.Method.Method, request.RequestUri!.PathAndQuery, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            string response = responses[Calls.Count - 1];
            return new(response switch
            {
                "created" => HttpStatusCode.Created, "sent" => HttpStatusCode.NoContent,
                "failed" => HttpStatusCode.InternalServerError, "conflict" => HttpStatusCode.Conflict,
                "rejected" => HttpStatusCode.BadRequest, _ => HttpStatusCode.OK,
            }) { Content = new StringContent(response) };
        }
    }
}
