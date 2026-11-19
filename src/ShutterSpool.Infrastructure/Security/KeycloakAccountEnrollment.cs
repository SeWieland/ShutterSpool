using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShutterSpool.Application.Security;

namespace ShutterSpool.Infrastructure.Security;

internal sealed class KeycloakEnrollmentOptions
{
    public string ServerUrl { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Realm { get; set; } = "shutterspool";
    public string ClientId { get; set; } = "shutterspool-enrollment";
    public string ClientSecret { get; set; } = "";
}

internal sealed class KeycloakAccountEnrollment(HttpClient http, KeycloakEnrollmentOptions options, string issuer) : IAccountEnrollment
{
    private const string SetupEmailFailed = "setup-email-failed";
    private static readonly string[] InitialRequiredActions = ["VERIFY_EMAIL", "UPDATE_PASSWORD"];

    private bool Configured => Uri.TryCreate(options.ServerUrl, UriKind.Absolute, out var url)
        && url.Scheme is "https" or "http" && !string.IsNullOrWhiteSpace(options.Realm)
        && !string.IsNullOrWhiteSpace(options.ClientId) && !string.IsNullOrWhiteSpace(options.ClientSecret)
        && !string.IsNullOrWhiteSpace(issuer) && options.Issuer == issuer;
    private string UsersPath => $"admin/realms/{Uri.EscapeDataString(options.Realm)}/users";

    public async Task<AccountEnrollmentResult> ResolveAsync(string email, CancellationToken token)
    {
        if (!MailAddress.TryCreate(email, out var address) || email.Length > 254 || address.Address != email)
            return new("invalid-email");
        if (!Configured) return new("enrollment-unavailable");
        try
        {
            string bearer = await AccessTokenAsync(token);
            var users = await FindAsync(email, bearer, token);
            if (users.Length == 0)
            {
                using var created = await SendAsync(HttpMethod.Post, UsersPath, bearer, JsonContent.Create(new
                {
                    username = email, email, enabled = true, emailVerified = false,
                    requiredActions = InitialRequiredActions,
                }), token);
                if (created.StatusCode == HttpStatusCode.BadRequest) return new("enrollment-rejected");
                if (created.StatusCode != HttpStatusCode.Conflict) created.EnsureSuccessStatusCode();
                // An interrupted create or concurrent invitation is recovered by the same exact-email lookup.
                users = await FindAsync(email, bearer, token);
            }
            if (users.Length != 1 || !Matches(users[0], email)) return new("account-unavailable");
            return new("resolved", new(issuer, users[0].Id!, users[0].Email!));
        }
        catch (Exception exception) when (Unavailable(exception, token))
        {
            return new("enrollment-unavailable");
        }
    }

    public async Task<string> SendSetupEmailAsync(AccountEnrollmentIdentity account, CancellationToken token)
    {
        if (!Configured || account.Issuer != issuer) return SetupEmailFailed;
        try
        {
            string bearer = await AccessTokenAsync(token);
            string path = $"{UsersPath}/{Uri.EscapeDataString(account.Subject)}";
            using var response = await SendAsync(HttpMethod.Get, path, bearer, null, token);
            response.EnsureSuccessStatusCode();
            var user = await response.Content.ReadFromJsonAsync<NativeUser>(token);
            if (user is null || user.Id != account.Subject || !Matches(user, account.Email)) return SetupEmailFailed;
            // Re-read pending actions so a retry after completed setup cannot impose a new password reset.
            var actions = new List<string>();
            if (!user.EmailVerified || user.RequiredActions?.Contains("VERIFY_EMAIL") == true) actions.Add("VERIFY_EMAIL");
            if (user.RequiredActions?.Contains("UPDATE_PASSWORD") == true) actions.Add("UPDATE_PASSWORD");
            if (actions.Count == 0) return "not-required";
            using var sent = await SendAsync(HttpMethod.Put, $"{path}/execute-actions-email?lifespan=86400", bearer, JsonContent.Create(actions), token);
            return sent.StatusCode == HttpStatusCode.NoContent ? "setup-email-sent" : SetupEmailFailed;
        }
        catch (Exception exception) when (Unavailable(exception, token))
        {
            return SetupEmailFailed;
        }
    }

    private static bool Matches(NativeUser user, string email) => user.Enabled && !string.IsNullOrWhiteSpace(user.Id)
        && string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase);

    private async Task<NativeUser[]> FindAsync(string email, string bearer, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"{UsersPath}?email={Uri.EscapeDataString(email)}&exact=true&max=2", bearer, null, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<NativeUser[]>(token) ?? throw new JsonException();
    }

    private async Task<string> AccessTokenAsync(CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Post,
            $"realms/{Uri.EscapeDataString(options.Realm)}/protocol/openid-connect/token", null,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = options.ClientId, ["client_secret"] = options.ClientSecret,
            }), token);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<NativeToken>(token);
        return !string.IsNullOrWhiteSpace(result?.AccessToken) ? result.AccessToken : throw new JsonException();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer, HttpContent? content, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, $"{options.ServerUrl.TrimEnd('/')}/{path}") { Content = content };
        request.Options.Set(new HttpRequestOptionsKey<bool>("ShutterSpool.PrivateProviderRequest"), true);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await http.SendAsync(request, token);
    }

    private static bool Unavailable(Exception exception, CancellationToken token) => exception is HttpRequestException or JsonException
        || exception is OperationCanceledException && !token.IsCancellationRequested;

    private sealed record NativeToken([property: JsonPropertyName("access_token")] string AccessToken);
    private sealed record NativeUser(string? Id, string? Email, bool Enabled, bool EmailVerified, string[]? RequiredActions);
}
