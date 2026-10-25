using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Retention;
using ShutterSpool.Application.Sources;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Security;
using ShutterSpool.Infrastructure.Spool;
using ShutterSpool.Server.Api.Captures;
using ShutterSpool.Server.Api.Destinations;
using ShutterSpool.Server.Api.Sources;
using ShutterSpool.Server.Hosting;
using ShutterSpool.Server.Security;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    private static object CameraRequest(string name, string connectionProfile = "ModernFtps") => new
    {
        name,
        sourceType = "CameraFtpUpload",
        connectionProfile,
    };

    private static async Task<CreatedSourceResponse> CreateCustomCameraAsync(ConfigurationApiFixture fixture, string name,
        string username, string password, CancellationToken token)
    {
        using HttpResponseMessage response = await fixture.Client.PostAsJsonAsync("/management/sources", new
        {
            name, sourceType = "CameraFtpUpload", connectionProfile = "ModernFtps", username,
            passwordMode = "Custom", password,
        }, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(token);
        Assert.DoesNotContain(password, body, StringComparison.Ordinal);
        return (await response.Content.ReadFromJsonAsync<CreatedSourceResponse>(token))!;
    }

    [Fact]
    public async Task Capture_cursor_is_bound_to_oldest_first_order()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid();
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-cursor-source" });
            DateTimeOffset acceptedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            database.Captures.AddRange(
                new CaptureEntity { Id = Guid.NewGuid(), SourceId = sourceId, NormalizedStem = "old", FirstAcceptedAt = acceptedAt, LastPrimaryIngestedAt = acceptedAt },
                new CaptureEntity { Id = Guid.NewGuid(), SourceId = sourceId, NormalizedStem = "new", FirstAcceptedAt = acceptedAt.AddSeconds(1), LastPrimaryIngestedAt = acceptedAt.AddSeconds(1) });
            await database.SaveChangesAsync(token);
        }

        using HttpResponseMessage first = await fixture.Client.GetAsync("/management/captures?limit=1", token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using JsonDocument document = await JsonDocument.ParseAsync(await first.Content.ReadAsStreamAsync(token), cancellationToken: token);
        string cursor = document.RootElement.GetProperty("nextCursor").GetString()!;
        using HttpResponseMessage reversed = await fixture.Client.GetAsync(
            $"/management/captures?limit=1&oldestFirst=true&cursor={Uri.EscapeDataString(cursor)}", token);
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
    }

    [Fact]
    public async Task Sources_can_be_created_and_their_secret_regenerated()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;

        using HttpResponseMessage response = await fixture.Client.PostAsJsonAsync("/management/sources", CameraRequest("Camera"), token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        CreatedSourceResponse source = (await response.Content.ReadFromJsonAsync<CreatedSourceResponse>(token))!;
        Assert.Equal(20, source.Secret!.Length);
        Assert.All(source.Secret, character => Assert.Contains(character, "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"));
        Assert.Matches("^[a-z]{6,8}$", source.Username);
        Guid credentialId;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            SourceEntity stored = await database.Sources.SingleAsync(token);
            Assert.Equal(source.Id, stored.Id);
            Assert.Equal("Delay", stored.CurationMode.ToString());
            Assert.Equal(3600, stored.CurationDelaySeconds);
            credentialId = (await database.SourceCredentials.SingleAsync(token)).Id;
        }

        using HttpResponseMessage regeneratedResponse = await fixture.Client.PostAsJsonAsync($"/management/sources/{source.Id}/regenerate-secret",
            new RegenerateSourceSecretRequest(0), token);
        regeneratedResponse.EnsureSuccessStatusCode();
        string regenerated = (await regeneratedResponse.Content.ReadFromJsonAsync<RegeneratedSourceSecretResponse>(token))!.Secret!;
        Assert.NotEqual(source.Secret, regenerated);
        await using (var scope = fixture.Server.Services.CreateAsyncScope())
        {
            var verifier = scope.ServiceProvider.GetRequiredService<SourceCredentialVerifier>();
            Assert.Null(await verifier.VerifyAsync(source.Username, source.Secret!, token));
            SourceAccess access = (await verifier.VerifyAsync(source.Username, regenerated, token))!;
            Assert.Equal(source.Id, access.SourceId);
            Assert.Equal(source.VirtualRoot, access.VirtualRoot);
            Assert.Equal(credentialId, (await scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>().SourceCredentials.SingleAsync(token)).Id);
        }
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            Assert.Equal(1, await database.Sources.CountAsync(token));
            Assert.Equal(1, await database.SourceCredentials.CountAsync(token));
        }
    }

    [Fact]
    public async Task Custom_camera_credentials_validate_inputs_and_never_return_custom_passwords()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        const string password = "!A23456789~!";
        var request = new
        {
            name = "Custom camera",
            sourceType = "CameraFtpUpload",
            connectionProfile = "ModernFtps",
            username = "camera-one",
            passwordMode = "Custom",
            password,
        };
        using HttpResponseMessage response = await fixture.Client.PostAsJsonAsync("/management/sources", request, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(token);
        Assert.DoesNotContain(password, body, StringComparison.Ordinal);
        CreatedSourceResponse source = (await response.Content.ReadFromJsonAsync<CreatedSourceResponse>(token))!;
        Assert.Equal("camera-one", source.Username);
        Assert.Null(source.Secret);

        using HttpResponseMessage duplicate = await fixture.Client.PostAsJsonAsync("/management/sources", new
        {
            name = "Other camera", request.sourceType, request.connectionProfile, request.username, request.passwordMode, request.password,
        }, token);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        string duplicateBody = await duplicate.Content.ReadAsStringAsync(token);
        Assert.DoesNotContain(password, duplicateBody, StringComparison.Ordinal);
        Assert.Contains("username", duplicateBody, StringComparison.OrdinalIgnoreCase);

        using HttpResponseMessage tooShort = await fixture.Client.PostAsJsonAsync("/management/sources", new
        {
            name = "Invalid password", request.sourceType, request.connectionProfile, username = "camera-short",
            passwordMode = "Custom", password = "Short!42",
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.DoesNotContain("Short!42", await tooShort.Content.ReadAsStringAsync(token), StringComparison.Ordinal);
        foreach ((string name, string invalidPassword) in new[]
        {
            ("Space password", "CameraPass word"),
            ("Control password", "CameraPass\tword"),
            ("Unicode password", "CámaraPassword42"),
            ("Long password", new string('A', 129)),
        })
        {
            using HttpResponseMessage invalid = await fixture.Client.PostAsJsonAsync("/management/sources", new
            {
                name, request.sourceType, request.connectionProfile, username = $"invalid-{name.Replace(' ', '-').ToLowerInvariant()}",
                passwordMode = "Custom", password = invalidPassword,
            }, token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            string invalidBody = await invalid.Content.ReadAsStringAsync(token);
            Assert.Contains("password", invalidBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(invalidPassword, invalidBody, StringComparison.Ordinal);
        }

        foreach (string invalidUsername in new[] { "_leading", "contains space", new string('a', 65), "cámara" })
        {
            using HttpResponseMessage invalid = await fixture.Client.PostAsJsonAsync("/management/sources", new
            {
                name = $"Invalid username {invalidUsername.Length}", request.sourceType, request.connectionProfile,
                username = invalidUsername, passwordMode = "Custom", password,
            }, token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains("username", await invalid.Content.ReadAsStringAsync(token), StringComparison.OrdinalIgnoreCase);
        }
        using HttpResponseMessage missingCustomPassword = await fixture.Client.PostAsJsonAsync("/management/sources", new
        {
            name = "Missing custom password", request.sourceType, request.connectionProfile,
            username = "missing-password", passwordMode = "Custom",
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, missingCustomPassword.StatusCode);
        Assert.Contains("password", await missingCustomPassword.Content.ReadAsStringAsync(token), StringComparison.OrdinalIgnoreCase);
        using HttpResponseMessage invalidMode = await fixture.Client.PostAsJsonAsync("/management/sources", new
        {
            name = "Invalid mode", request.sourceType, request.connectionProfile,
            username = "invalid-mode", passwordMode = "custom", password,
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidMode.StatusCode);
        Assert.Contains("passwordMode", await invalidMode.Content.ReadAsStringAsync(token), StringComparison.OrdinalIgnoreCase);

        using HttpResponseMessage capabilities = await fixture.Client.GetAsync("/management/source-capabilities", token);
        SourceCapabilitiesResponse policy = (await capabilities.Content.ReadFromJsonAsync<SourceCapabilitiesResponse>(token))!;
        Assert.Equal(12, policy.PasswordPolicy.MinimumLength);
        Assert.Equal(128, policy.PasswordPolicy.MaximumLength);
        Assert.Equal(20, policy.PasswordPolicy.GeneratedLength);
        Assert.False(policy.PasswordPolicy.WeakerProtection);
    }

    [Fact]
    public async Task Concurrent_camera_source_creation_allows_only_one_username_owner()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        const string password = "!A23456789~!";
        Task<HttpResponseMessage>[] requests = Enumerable.Range(1, 2).Select(index =>
            fixture.Client.PostAsJsonAsync("/management/sources", new
            {
                name = $"Race camera {index}", sourceType = "CameraFtpUpload", connectionProfile = "ModernFtps",
                username = "race-username", passwordMode = "Custom", password,
            }, token)).ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(requests);
        try
        {
            Assert.Equal(1, responses.Count(item => item.StatusCode == HttpStatusCode.Created));
            Assert.Equal(1, responses.Count(item => item.StatusCode == HttpStatusCode.Conflict));
            await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
            Assert.Equal(1, await database.SourceCredentials.CountAsync(item => item.Username == "race-username", token));
            foreach (HttpResponseMessage response in responses)
                Assert.DoesNotContain(password, await response.Content.ReadAsStringAsync(token), StringComparison.Ordinal);
        }
        finally { foreach (HttpResponseMessage response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Camera_credential_replacement_validates_same_pair_and_preserves_revision_guard()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        CreatedSourceResponse source = await CreateCustomCameraAsync(fixture, "Custom camera", "camera-one", "!A23456789~!", token);

        using HttpResponseMessage unchanged = await fixture.Client.PostAsJsonAsync($"/management/sources/{source.Id}/regenerate-secret",
            new RegenerateSourceSecretRequest(0, PasswordMode: "Custom", Password: "!A23456789~!"), token);
        Assert.Equal(HttpStatusCode.BadRequest, unchanged.StatusCode);
        Assert.DoesNotContain("!A23456789~!", await unchanged.Content.ReadAsStringAsync(token), StringComparison.Ordinal);

        using HttpResponseMessage replaced = await fixture.Client.PostAsJsonAsync($"/management/sources/{source.Id}/regenerate-secret",
            new RegenerateSourceSecretRequest(0, Username: "camera-two", PasswordMode: "Custom", Password: "CustomCamera!43"), token);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.DoesNotContain("CustomCamera!43", await replaced.Content.ReadAsStringAsync(token), StringComparison.Ordinal);
        Assert.Null((await replaced.Content.ReadFromJsonAsync<RegeneratedSourceSecretResponse>(token))!.Secret);
        using HttpResponseMessage stale = await fixture.Client.PostAsJsonAsync($"/management/sources/{source.Id}/regenerate-secret",
            new RegenerateSourceSecretRequest(0, Username: "stale-wont-apply", PasswordMode: "Custom", Password: "CustomCamera!47"), token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.DoesNotContain("CustomCamera!47", await stale.Content.ReadAsStringAsync(token), StringComparison.Ordinal);
        await using var scope = fixture.Server.Services.CreateAsyncScope();
        SourceCredentialVerifier verifier = scope.ServiceProvider.GetRequiredService<SourceCredentialVerifier>();
        Assert.Null(await verifier.VerifyAsync("camera-one", "!A23456789~!", token));
        Assert.Equal(source.Id, (await verifier.VerifyAsync("camera-two", "CustomCamera!43", token))?.SourceId);
    }

    [Fact]
    public async Task Concurrent_camera_credential_replacement_allows_only_one_source_to_claim_a_username()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        CreatedSourceResponse first = await CreateCustomCameraAsync(fixture, "First camera", "camera-two", "CustomCamera!43", token);
        CreatedSourceResponse second = await CreateCustomCameraAsync(fixture, "Second camera", "camera-other", "CustomCamera!44", token);
        Task<HttpResponseMessage>[] requests =
        [
            fixture.Client.PostAsJsonAsync($"/management/sources/{first.Id}/regenerate-secret",
                new RegenerateSourceSecretRequest(0, Username: "race-replacement", PasswordMode: "Custom", Password: "CustomCamera!45"), token),
            fixture.Client.PostAsJsonAsync($"/management/sources/{second.Id}/regenerate-secret",
                new RegenerateSourceSecretRequest(0, Username: "race-replacement", PasswordMode: "Custom", Password: "CustomCamera!46"), token),
        ];
        HttpResponseMessage[] responses = await Task.WhenAll(requests);
        try
        {
            Assert.Equal(1, responses.Count(item => item.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(item => item.StatusCode == HttpStatusCode.Conflict));
            bool firstWon = responses[0].StatusCode == HttpStatusCode.OK;
            foreach (HttpResponseMessage response in responses)
            {
                string body = await response.Content.ReadAsStringAsync(token);
                Assert.DoesNotContain("CustomCamera!45", body, StringComparison.Ordinal);
                Assert.DoesNotContain("CustomCamera!46", body, StringComparison.Ordinal);
            }

            await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
            Assert.Equal(1, await database.SourceCredentials.CountAsync(item => item.Username == "race-replacement", token));
            await using var scope = fixture.Server.Services.CreateAsyncScope();
            SourceCredentialVerifier verifier = scope.ServiceProvider.GetRequiredService<SourceCredentialVerifier>();
            Assert.Equal(firstWon ? null : first.Id,
                (await verifier.VerifyAsync("camera-two", "CustomCamera!43", token))?.SourceId);
            Assert.Equal(firstWon ? second.Id : null,
                (await verifier.VerifyAsync("camera-other", "CustomCamera!44", token))?.SourceId);
        }
        finally
        {
            foreach (HttpResponseMessage response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task Deployment_password_policy_accepts_the_documented_minimums_and_sets_generation_length()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using (var demo = await ConfigurationApiFixture.CreateAsync(minimumPasswordLength: "1"))
        {
            using HttpResponseMessage capabilities = await demo.Client.GetAsync("/management/source-capabilities", token);
            SourceCapabilitiesResponse policy = (await capabilities.Content.ReadFromJsonAsync<SourceCapabilitiesResponse>(token))!;
            Assert.Equal(1, policy.PasswordPolicy.MinimumLength);
            Assert.True(policy.PasswordPolicy.WeakerProtection);
            using HttpResponseMessage created = await demo.Client.PostAsJsonAsync("/management/sources", new
            {
                name = "Short policy", sourceType = "CameraFtpUpload", connectionProfile = "ModernFtps",
                username = "short-policy", passwordMode = "Custom", password = "!",
            }, token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        await using var maximum = await ConfigurationApiFixture.CreateAsync(minimumPasswordLength: "128");
        using HttpResponseMessage maximumCapabilities = await maximum.Client.GetAsync("/management/source-capabilities", token);
        SourceCapabilitiesResponse maximumPolicy = (await maximumCapabilities.Content.ReadFromJsonAsync<SourceCapabilitiesResponse>(token))!;
        Assert.Equal(128, maximumPolicy.PasswordPolicy.MinimumLength);
        Assert.Equal(128, maximumPolicy.PasswordPolicy.GeneratedLength);
        using HttpResponseMessage generated = await maximum.Client.PostAsJsonAsync("/management/sources", CameraRequest("Long policy"), token);
        Assert.Equal(HttpStatusCode.Created, generated.StatusCode);
        CreatedSourceResponse source = (await generated.Content.ReadFromJsonAsync<CreatedSourceResponse>(token))!;
        Assert.Equal(128, source.Secret!.Length);
    }

    [Fact]
    public async Task Source_profile_changes_are_explicit_revision_guarded_and_invalidate_credentials()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;

        using HttpResponseMessage defaultCreate = await fixture.Client.PostAsJsonAsync("/management/sources", CameraRequest("Modern only"), token);
        defaultCreate.EnsureSuccessStatusCode();
        CreatedSourceResponse modern = (await defaultCreate.Content.ReadFromJsonAsync<CreatedSourceResponse>(token))!;
        using HttpResponseMessage defaultRead = await fixture.Client.GetAsync($"/management/sources/{modern.Id}", token);
        SourceConfigurationResponse modernConfig = (await defaultRead.Content.ReadFromJsonAsync<SourceConfigurationResponse>(token))!;
        Assert.Equal("ModernFtps", modernConfig.ConnectionProfile);

        using HttpResponseMessage optedCreate = await fixture.Client.PostAsJsonAsync("/management/sources",
            CameraRequest("Legacy enabled", "LegacyFtps"), token);
        optedCreate.EnsureSuccessStatusCode();
        CreatedSourceResponse opted = (await optedCreate.Content.ReadFromJsonAsync<CreatedSourceResponse>(token))!;
        using HttpResponseMessage optedRead = await fixture.Client.GetAsync($"/management/sources/{opted.Id}", token);
        SourceConfigurationResponse optedConfig = (await optedRead.Content.ReadFromJsonAsync<SourceConfigurationResponse>(token))!;
        Assert.Equal("LegacyFtps", optedConfig.ConnectionProfile);
        Assert.Equal(1, optedConfig.CredentialGeneration);

        using HttpResponseMessage omittedTrue = await fixture.Client.PutAsJsonAsync($"/management/sources/{opted.Id}",
            new UpdateSourceConfigurationRequest(optedConfig.ConfigurationRevision, "Legacy enabled", optedConfig.CurationMode,
                "CameraFtpUpload", "LegacyFtps"), token);
        Assert.Equal(HttpStatusCode.OK, omittedTrue.StatusCode);
        SourceConfigurationResponse preservedTrue = (await omittedTrue.Content.ReadFromJsonAsync<SourceConfigurationResponse>(token))!;
        Assert.Equal("LegacyFtps", preservedTrue.ConnectionProfile);
        Assert.Equal(optedConfig.CredentialGeneration, preservedTrue.CredentialGeneration);

        using HttpResponseMessage enabled = await fixture.Client.PutAsJsonAsync($"/management/sources/{opted.Id}",
            new UpdateSourceConfigurationRequest(preservedTrue.ConfigurationRevision, "Legacy enabled", preservedTrue.CurationMode,
                "CameraFtpUpload", "ModernFtps"), token);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        SourceConfigurationResponse disabled = (await enabled.Content.ReadFromJsonAsync<SourceConfigurationResponse>(token))!;
        Assert.Equal("ModernFtps", disabled.ConnectionProfile);
        Assert.Equal(optedConfig.CredentialGeneration + 1, disabled.CredentialGeneration);

        using HttpResponseMessage stale = await fixture.Client.PutAsJsonAsync($"/management/sources/{opted.Id}",
            new UpdateSourceConfigurationRequest(preservedTrue.ConfigurationRevision, "Legacy enabled", preservedTrue.CurationMode,
                "CameraFtpUpload", "LegacyFtps"), token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using HttpResponseMessage omitted = await fixture.Client.PutAsJsonAsync($"/management/sources/{opted.Id}",
            new UpdateSourceConfigurationRequest(disabled.ConfigurationRevision, "Legacy enabled", disabled.CurationMode,
                "CameraFtpUpload", "ModernFtps"), token);
        Assert.Equal(HttpStatusCode.OK, omitted.StatusCode);
        SourceConfigurationResponse preserved = (await omitted.Content.ReadFromJsonAsync<SourceConfigurationResponse>(token))!;
        Assert.Equal("ModernFtps", preserved.ConnectionProfile);
        Assert.Equal(disabled.CredentialGeneration, preserved.CredentialGeneration);
    }

    [Fact]
    public async Task Source_requests_require_explicit_profiles_and_reject_obsolete_security_flags()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        using HttpResponseMessage missingCreate = await fixture.Client.PostAsJsonAsync("/management/sources", new { name = "Missing profile" }, token);
        Assert.Equal(HttpStatusCode.BadRequest, missingCreate.StatusCode);
        using HttpResponseMessage obsoleteCreate = await fixture.Client.PostAsJsonAsync("/management/sources", new
        {
            name = "Obsolete flag", sourceType = "CameraFtpUpload", connectionProfile = "ModernFtps", allowLegacyFtps = true,
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, obsoleteCreate.StatusCode);

        using HttpResponseMessage created = await fixture.Client.PostAsJsonAsync("/management/sources", CameraRequest("Explicit profile"), token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        CreatedSourceResponse source = (await created.Content.ReadFromJsonAsync<CreatedSourceResponse>(token))!;
        using HttpResponseMessage missingUpdate = await fixture.Client.PutAsJsonAsync($"/management/sources/{source.Id}", new
        {
            expectedRevision = 0, name = "Explicit profile", curationMode = "Delay",
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, missingUpdate.StatusCode);
        using HttpResponseMessage obsoleteUpdate = await fixture.Client.PutAsJsonAsync($"/management/sources/{source.Id}", new
        {
            expectedRevision = 0, name = "Explicit profile", curationMode = "Delay", sourceType = "CameraFtpUpload",
            connectionProfile = "ModernFtps", allowLegacyFtps = true,
        }, token);
        Assert.Equal(HttpStatusCode.BadRequest, obsoleteUpdate.StatusCode);
        using HttpResponseMessage unavailable = await fixture.Client.PostAsJsonAsync("/management/sources", CameraRequest("Unknown profile", "Sftp"), token);
        Assert.Equal(HttpStatusCode.BadRequest, unavailable.StatusCode);
    }

    [Fact]
    public async Task Source_display_is_redacted_cached_and_invalidated_after_committed_edits()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        using HttpResponseMessage created = await fixture.Client.PostAsJsonAsync("/management/sources", CameraRequest("Display camera"), token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        CreatedSourceResponse source = (await created.Content.ReadFromJsonAsync<CreatedSourceResponse>(token))!;

        using HttpResponseMessage displayResponse = await fixture.Client.GetAsync($"/management/sources/{source.Id}/display", token);
        Assert.Equal(HttpStatusCode.OK, displayResponse.StatusCode);
        using JsonDocument display = await JsonDocument.ParseAsync(await displayResponse.Content.ReadAsStreamAsync(token), cancellationToken: token);
        Assert.Equal("Display camera", display.RootElement.GetProperty("name").GetString());
        Assert.False(display.RootElement.TryGetProperty("username", out _));
        Assert.False(display.RootElement.TryGetProperty("credentialGeneration", out _));
        Assert.False(display.RootElement.TryGetProperty("secret", out _));
        Assert.False(display.RootElement.TryGetProperty("virtualRoot", out _));
        Assert.Equal("CameraFtpUpload", display.RootElement.GetProperty("sourceType").GetString());
        Assert.Equal("ModernFtps", display.RootElement.GetProperty("connectionProfile").GetString());

        using HttpResponseMessage editorResponse = await fixture.Client.GetAsync($"/management/sources/{source.Id}", token);
        SourceConfigurationResponse editor = (await editorResponse.Content.ReadFromJsonAsync<SourceConfigurationResponse>(token))!;
        Assert.Equal(source.Username, editor.Username);
        Assert.Equal(1, editor.CredentialGeneration);

        using HttpResponseMessage updated = await fixture.Client.PutAsJsonAsync($"/management/sources/{source.Id}",
            new UpdateSourceConfigurationRequest(editor.ConfigurationRevision, "Display camera renamed", editor.CurationMode,
                editor.SourceType, editor.ConnectionProfile), token);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using HttpResponseMessage refreshed = await fixture.Client.GetAsync($"/management/sources/{source.Id}/display", token);
        using JsonDocument afterEdit = await JsonDocument.ParseAsync(await refreshed.Content.ReadAsStreamAsync(token), cancellationToken: token);
        Assert.Equal("Display camera renamed", afterEdit.RootElement.GetProperty("name").GetString());
        Assert.Equal(1, afterEdit.RootElement.GetProperty("configurationRevision").GetInt64());
    }

    [Fact]
    public async Task Transfer_activity_requires_a_bounded_distinct_identity_batch_and_reports_unavailable_coverage()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync(profileSettings: new CameraIngressSettings { Enabled = false });
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid();

        using HttpResponseMessage valid = await fixture.Client.GetAsync(
            $"/management/activity/transfers?sourceIds={sourceId}&limit=30", token);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using JsonDocument body = await JsonDocument.ParseAsync(await valid.Content.ReadAsStreamAsync(token), cancellationToken: token);
        Assert.False(body.RootElement.GetProperty("coverageComplete").GetBoolean());
        Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.True(body.RootElement.GetProperty("epoch").GetString()!.Length > 0);

        using HttpResponseMessage duplicate = await fixture.Client.GetAsync(
            $"/management/activity/transfers?sourceIds={sourceId}&sourceIds={sourceId}", token);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        using HttpResponseMessage missing = await fixture.Client.GetAsync("/management/activity/transfers", token);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Transfer_snapshot_keeps_old_durable_receive_stale_until_a_recent_local_renewal()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync(enableCameraIngress: true);
        CancellationToken token = TestContext.Current.CancellationToken;
        CreatedSourceResponse source = await CreateSourceAsync(fixture.Client, token);
        Guid transferId = Guid.NewGuid();
        Guid loginId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long admittedAt = checked((now.ToUnixTimeMilliseconds() - 61_000) * 1_000_000);
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.CameraLogins.Add(new CameraLoginEntity
            {
                Id = loginId, SourceId = source.Id, Username = source.Username, VirtualRoot = source.VirtualRoot,
                CredentialGeneration = 1, CreatedAt = now,
            });
            database.CameraUploadAdmissions.Add(new CameraUploadAdmissionEntity
            {
                Id = transferId, LoginId = loginId, SourceId = source.Id, Username = source.Username,
                SessionId = "synthetic-stale-session", VirtualPath = "/DCIM/DSC0000.JPG",
                AdmissionTimestamp = admittedAt, State = CameraTransferState.Receiving,
            });
            await database.SaveChangesAsync(token);
        }

        using HttpResponseMessage staleResponse = await fixture.Client.GetAsync(
            $"/management/activity/transfers?sourceIds={source.Id}", token);
        staleResponse.EnsureSuccessStatusCode();
        using JsonDocument staleBody = await JsonDocument.ParseAsync(await staleResponse.Content.ReadAsStreamAsync(token), cancellationToken: token);
        JsonElement staleItem = Assert.Single(staleBody.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Stale", staleItem.GetProperty("phase").GetString());
        long snapshotVersion = staleItem.GetProperty("version").GetInt64();

        DateTimeOffset renewedAt = DateTimeOffset.UtcNow;
        fixture.Server.Services.GetRequiredService<ShutterSpool.Server.Api.Live.LiveChangeNotifier>().Publish(
            new(transferId, source.Id, null, null, CameraTransferPhase.Receiving, renewedAt, 1, false, false));
        using HttpResponseMessage renewedResponse = await fixture.Client.GetAsync(
            $"/management/activity/transfers?sourceIds={source.Id}", token);
        renewedResponse.EnsureSuccessStatusCode();
        using JsonDocument renewedBody = await JsonDocument.ParseAsync(await renewedResponse.Content.ReadAsStreamAsync(token), cancellationToken: token);
        JsonElement renewedItem = Assert.Single(renewedBody.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Receiving", renewedItem.GetProperty("phase").GetString());
        Assert.Equal(renewedAt, renewedItem.GetProperty("observedAt").GetDateTimeOffset());
        Assert.True(renewedItem.GetProperty("version").GetInt64() > snapshotVersion);
        Assert.False(renewedItem.GetProperty("accepted").GetBoolean());
        Assert.True(renewedBody.RootElement.GetProperty("coverageComplete").GetBoolean());
    }

    [Fact]
    public async Task Preview_activity_renews_real_jobs_and_only_marks_the_current_published_asset_ready()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync(enableCameraIngress: true);
        CancellationToken token = TestContext.Current.CancellationToken;
        CreatedSourceResponse source = await CreateSourceAsync(fixture.Client, token);
        Guid loginId = Guid.NewGuid();
        Guid captureId = Guid.NewGuid();
        Guid oldAssetId = Guid.NewGuid();
        Guid currentAssetId = Guid.NewGuid();
        Guid oldTransferId = Guid.NewGuid();
        Guid currentTransferId = Guid.NewGuid();
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        long admissionTimestamp = checked(capturedAt.ToUnixTimeMilliseconds() * 1_000_000);
        string oldKey = $"{captureId:N}/{oldAssetId:N}/grid-480x480.webp";
        string currentKey = $"{captureId:N}/{currentAssetId:N}/grid-480x480.webp";

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.CameraLogins.Add(new CameraLoginEntity
            {
                Id = loginId, SourceId = source.Id, Username = source.Username, VirtualRoot = source.VirtualRoot,
                CredentialGeneration = 1, CreatedAt = capturedAt,
            });
            database.Assets.AddRange(
                TestAsset(oldAssetId, source.Id, "old-primary.jpg", capturedAt),
                TestAsset(currentAssetId, source.Id, "current-primary.jpg", capturedAt.AddSeconds(1)));
            database.Captures.Add(new CaptureEntity
            {
                Id = captureId, SourceId = source.Id, NormalizedStem = "preview-phase",
                FirstAcceptedAt = capturedAt, LastPrimaryIngestedAt = capturedAt.AddSeconds(1),
            });
            database.CaptureAssets.AddRange(
                new CaptureAssetEntity { AssetId = oldAssetId, CaptureId = captureId, Role = "JPEG", IsPrimary = true, GroupingReason = "synthetic" },
                new CaptureAssetEntity { AssetId = currentAssetId, CaptureId = captureId, Role = "JPEG", IsPrimary = false, GroupingReason = "synthetic" });
            database.CameraUploadAdmissions.AddRange(
                TestAdmission(oldTransferId, loginId, source.Id, source.Username, admissionTimestamp, oldAssetId, "/DCIM/OLD.JPG"),
                TestAdmission(currentTransferId, loginId, source.Id, source.Username, admissionTimestamp + 1, currentAssetId, "/DCIM/CURRENT.JPG"));
            await database.SaveChangesAsync(token);
        }

        IPreviewCache cache = fixture.Server.Services.GetRequiredService<IPreviewCache>();
        byte[] validPreview = Convert.FromBase64String("UklGRi4AAABXRUJQVlA4ICIAAABwAQCdASoCAAMAAUAmJZQCdAFAAAD+/DeBV/fU6D4r4AAA");
        await cache.WriteAsync(oldKey, validPreview, token);
        JsonElement oldReady = await ReadTransferAsync(oldTransferId);
        Assert.Equal("Ready", oldReady.GetProperty("phase").GetString());
        Assert.True(oldReady.GetProperty("previewReady").GetBoolean());

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            await database.CaptureAssets.Where(item => item.AssetId == oldAssetId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.IsPrimary, false), token);
            await database.CaptureAssets.Where(item => item.AssetId == currentAssetId)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.IsPrimary, true), token);
            database.PreviewPrewarms.Add(new PreviewPrewarmEntity
            {
                AssetId = currentAssetId, CaptureId = captureId, Profile = "Grid", State = "Pending",
            });
            await database.SaveChangesAsync(token);
        }

        JsonElement oldAfterReplacement = await ReadTransferAsync(oldTransferId);
        Assert.False(oldAfterReplacement.GetProperty("previewReady").GetBoolean());
        Assert.NotEqual("Ready", oldAfterReplacement.GetProperty("phase").GetString());

        foreach (string jobState in new[] { "Pending", "Processing" })
        {
            await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
            {
                PreviewPrewarmEntity job = await database.PreviewPrewarms.SingleAsync(item => item.AssetId == currentAssetId, token);
                job.State = jobState;
                if (jobState == "Processing")
                {
                    job.LeaseId = Guid.NewGuid();
                    job.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                await database.SaveChangesAsync(token);
            }
            JsonElement preparing = await ReadTransferAsync(currentTransferId);
            Assert.Equal("PreparingPreview", preparing.GetProperty("phase").GetString());
            Assert.True(preparing.GetProperty("observedAt").GetDateTimeOffset() > capturedAt.AddSeconds(60));
        }

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            PreviewPrewarmEntity job = await database.PreviewPrewarms.SingleAsync(item => item.AssetId == currentAssetId, token);
            job.State = "Invalid";
            await database.SaveChangesAsync(token);
        }
        JsonElement unavailable = await ReadTransferAsync(currentTransferId);
        Assert.Equal("FailedUnavailable", unavailable.GetProperty("phase").GetString());
        Assert.True(unavailable.GetProperty("accepted").GetBoolean());

        fixture.Server.Services.GetRequiredService<PreviewSettings>().PrewarmMode = PreviewPrewarmMode.Off;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            PreviewPrewarmEntity job = await database.PreviewPrewarms.SingleAsync(item => item.AssetId == currentAssetId, token);
            job.State = "Pending";
            await database.SaveChangesAsync(token);
        }
        JsonElement prewarmOff = await ReadTransferAsync(currentTransferId);
        Assert.Equal("AcceptedAwaitingGrouping", prewarmOff.GetProperty("phase").GetString());

        DateTimeOffset generationStartedAt = DateTimeOffset.UtcNow;
        var changes = fixture.Server.Services.GetRequiredService<ShutterSpool.Server.Api.Live.LiveChangeNotifier>();
        changes.Preparing(captureId, currentAssetId, PreviewProfile.Grid, generationStartedAt);
        JsonElement onDemandPreparing = await ReadTransferAsync(currentTransferId);
        Assert.Equal("PreparingPreview", onDemandPreparing.GetProperty("phase").GetString());
        Assert.Equal(generationStartedAt, onDemandPreparing.GetProperty("observedAt").GetDateTimeOffset());

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.PreviewPrewarms.Remove(await database.PreviewPrewarms.SingleAsync(item => item.AssetId == currentAssetId, token));
            await database.SaveChangesAsync(token);
        }
        await cache.WriteAsync(currentKey, validPreview, token);
        JsonElement onDemandReady = await ReadTransferAsync(currentTransferId);
        Assert.Equal("Ready", onDemandReady.GetProperty("phase").GetString());
        Assert.True(onDemandReady.GetProperty("previewReady").GetBoolean());
        changes.Finished(captureId, currentAssetId, PreviewProfile.Grid);

        string cachePath = Path.Combine(fixture.Database.SpoolRoot, "previews", currentKey.Replace('/', Path.DirectorySeparatorChar));
        File.Delete(cachePath);
        JsonElement lost = await ReadTransferAsync(currentTransferId);
        Assert.False(lost.GetProperty("previewReady").GetBoolean());
        Assert.NotEqual("Ready", lost.GetProperty("phase").GetString());

        async Task<JsonElement> ReadTransferAsync(Guid transferId)
        {
            using HttpResponseMessage response = await fixture.Client.GetAsync(
                $"/management/activity/transfers?sourceIds={source.Id}&limit=100", token);
            response.EnsureSuccessStatusCode();
            using JsonDocument document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            return document.RootElement.GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("transferId").GetGuid() == transferId).Clone();
        }

        static AssetEntity TestAsset(Guid id, Guid sourceId, string name, DateTimeOffset ingestedAt) => new()
        {
            Id = id, SourceId = sourceId, OriginalName = name, SpoolKey = $"spool/{id:N}", Size = 1,
            Sha256 = new string('a', 64), IngestedAt = ingestedAt, RecoveryCorrelation = Guid.NewGuid(), State = "Spooled",
        };

        static CameraUploadAdmissionEntity TestAdmission(Guid id, Guid loginId, Guid sourceId, string username,
            long timestamp, Guid assetId, string path) => new()
        {
            Id = id, LoginId = loginId, SourceId = sourceId, Username = username, SessionId = $"preview-{id:N}",
            VirtualPath = path, AdmissionTimestamp = timestamp, State = CameraTransferState.Completed, AssetId = assetId,
        };
    }

    [Fact]
    public async Task Source_authentication_reads_are_bounded_and_return_only_dated_source_outcomes()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        using HttpResponseMessage created = await fixture.Client.PostAsJsonAsync("/management/sources", CameraRequest("Auth camera"), token);
        CreatedSourceResponse source = (await created.Content.ReadFromJsonAsync<CreatedSourceResponse>(token))!;
        DateTimeOffset observedAt = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
        await using (AsyncServiceScope scope = fixture.Server.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ISourceAuthenticationObservationStore>().RecordAsync(
                new(source.Id, SourceAuthenticationOutcome.Rejected, observedAt), token);

        using HttpResponseMessage read = await fixture.Client.GetAsync(
            $"/management/sources/authentication-observations?sourceIds={source.Id}", token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        string body = await read.Content.ReadAsStringAsync(token);
        Assert.DoesNotContain(source.Username, body, StringComparison.Ordinal);
        Assert.DoesNotContain(source.Secret!, body, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement observation = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(source.Id, observation.GetProperty("sourceId").GetGuid());
        Assert.Equal("Rejected", observation.GetProperty("outcome").GetString());
        Assert.Equal(observedAt, observation.GetProperty("observedAt").GetDateTimeOffset());

        string ids = string.Join('&', Enumerable.Range(0, 101).Select(_ => $"sourceIds={Guid.NewGuid()}"));
        using HttpResponseMessage oversized = await fixture.Client.GetAsync($"/management/sources/authentication-observations?{ids}", token);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }

    [Fact]
    public async Task Source_mutations_return_safe_conflicts_and_missing_resources()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;

        using HttpResponseMessage created = await fixture.Client.PostAsJsonAsync("/management/sources", CameraRequest("Camera"), token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using HttpResponseMessage duplicate = await fixture.Client.PostAsJsonAsync("/management/sources", CameraRequest("Camera"), token);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("application/problem+json", duplicate.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("INSERT", await duplicate.Content.ReadAsStringAsync(token), StringComparison.Ordinal);

        using HttpResponseMessage missing = await fixture.Client.PostAsJsonAsync($"/management/sources/{Guid.NewGuid()}/regenerate-secret",
            new RegenerateSourceSecretRequest(0), token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Destinations_validate_optional_credentials_and_keep_secrets_separate()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;

        foreach (object invalid in new object[] { new { name = "Missing kind" }, new { name = "Unknown", kind = "s3" } })
        {
            using var response = await fixture.Client.PostAsJsonAsync("/management/destinations", invalid, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using HttpResponseMessage destinationResponse = await fixture.Client.PostAsJsonAsync("/management/destinations",
            new { name = "Local", kind = "local", secret = "synthetic-destination-secret" }, token);
        Assert.Equal(HttpStatusCode.Created, destinationResponse.StatusCode);
        DestinationResponse destination = (await destinationResponse.Content.ReadFromJsonAsync<DestinationResponse>(token))!;
        Assert.Equal("local", destination.Kind);
        string destinationBody = await destinationResponse.Content.ReadAsStringAsync(token);
        Assert.DoesNotContain("synthetic-destination-secret", destinationBody, StringComparison.Ordinal);
        using (JsonDocument parsed = JsonDocument.Parse(destinationBody))
            Assert.True(parsed.RootElement.GetProperty("secretConfigured").GetBoolean());
        await using (var scope = fixture.Server.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<ShutterSpool.Application.Security.ICredentialProtector>();
            DestinationCredentialEntity credential = await database.DestinationCredentials.SingleAsync(token);
            Assert.Equal(destination.Id, credential.DestinationId);
            Assert.DoesNotContain("synthetic-destination-secret", credential.EncryptedSecret, StringComparison.Ordinal);
            Assert.Equal("synthetic-destination-secret", protector.Unprotect(credential.EncryptedSecret));
        }

        using HttpResponseMessage noSecret = await fixture.Client.PostAsJsonAsync("/management/destinations", new { name = "No credential", kind = "local" }, token);
        Assert.Equal(HttpStatusCode.Created, noSecret.StatusCode);
    }

    [Fact]
    public async Task WebDav_credentials_are_write_only_and_collection_identity_locks_after_delivery_creation()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        using HttpResponseMessage created = await fixture.Client.PostAsJsonAsync("/management/destinations",
            new { name = "Private WebDAV", kind = "webdav" }, token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        DestinationResponse destination = (await created.Content.ReadFromJsonAsync<DestinationResponse>(token))!;

        using HttpResponseMessage unauthorized = await fixture.Server.CreateClient()
            .GetAsync($"/management/destinations/{destination.Id}/webdav", token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using HttpResponseMessage malformed = await fixture.Client.PutAsJsonAsync($"/management/destinations/{destination.Id}/webdav",
            new { expectedRevision = 0, collectionUrl = "https://operator:secret@dav.example.test/path/", username = "operator", password = "bad" }, token);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using HttpResponseMessage missingPassword = await fixture.Client.PutAsJsonAsync($"/management/destinations/{destination.Id}/webdav",
            new { expectedRevision = 0, collectionUrl = "https://dav.example.test/path/", username = "operator" }, token);
        Assert.Equal(HttpStatusCode.BadRequest, missingPassword.StatusCode);
        using HttpResponseMessage nullPassword = await fixture.Client.PutAsJsonAsync($"/management/destinations/{destination.Id}/webdav",
            new { expectedRevision = 0, collectionUrl = "https://dav.example.test/path/", username = "operator", password = (string?)null }, token);
        Assert.Equal(HttpStatusCode.BadRequest, nullPassword.StatusCode);

        var request = new { expectedRevision = 0, collectionUrl = "https://dav.example.test/remote.php/dav/files/operator/", username = "operator", password = "synthetic-app-password" };
        using HttpResponseMessage configured = await fixture.Client.PutAsJsonAsync($"/management/destinations/{destination.Id}/webdav", request, token);
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        Assert.DoesNotContain("synthetic-app-password", await configured.Content.ReadAsStringAsync(token), StringComparison.Ordinal);
        using HttpResponseMessage preserved = await fixture.Client.PutAsJsonAsync($"/management/destinations/{destination.Id}/webdav",
            new { expectedRevision = 1, request.collectionUrl, request.username }, token);
        Assert.Equal(HttpStatusCode.OK, preserved.StatusCode);
        Assert.Contains("\"configurationRevision\":2", await preserved.Content.ReadAsStringAsync(token), StringComparison.Ordinal);
        using HttpResponseMessage read = await fixture.Client.GetAsync($"/management/destinations/{destination.Id}/webdav", token);
        string readBody = await read.Content.ReadAsStringAsync(token);
        Assert.DoesNotContain("synthetic-app-password", readBody, StringComparison.Ordinal);
        Assert.Contains("operator", readBody, StringComparison.Ordinal);

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            DestinationCredentialEntity credential = await database.DestinationCredentials.SingleAsync(token);
            Assert.DoesNotContain("synthetic-app-password", credential.EncryptedSecret, StringComparison.Ordinal);
            Assert.Equal("synthetic-app-password", new SecretProtector(Microsoft.Extensions.Options.Options.Create(
                new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) })).Unprotect(credential.EncryptedSecret));

            Guid assetId = Guid.NewGuid(), captureId = Guid.NewGuid(), decisionId = Guid.NewGuid();
            database.Assets.Add(new AssetEntity { Id = assetId, OriginalName = "synthetic.raw", SpoolKey = Guid.NewGuid().ToString("N"),
                Size = 1, Sha256 = new string('0', 64), IngestedAt = DateTimeOffset.UtcNow, State = "Spooled" });
            database.Captures.Add(new CaptureEntity { Id = captureId, NormalizedStem = "synthetic", LastPrimaryIngestedAt = DateTimeOffset.UtcNow });
            database.RoutingDecisions.Add(new RoutingDecisionEntity { Id = decisionId, CaptureId = captureId, AssetId = assetId,
                DestinationId = destination.Id, Generation = 1, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched,
                Explanation = "test", CreatedAt = DateTimeOffset.UtcNow });
            database.Deliveries.Add(new DeliveryEntity { Id = Guid.NewGuid(), DecisionId = decisionId, AssetId = assetId,
                DestinationId = destination.Id, TargetPath = "synthetic.raw", CreatedAt = DateTimeOffset.UtcNow });
            await database.SaveChangesAsync(token);
        }

        var replacement = new { expectedRevision = 2, request.collectionUrl, request.username, password = "replacement-app-password" };
        using HttpResponseMessage passwordChanged = await fixture.Client.PutAsJsonAsync($"/management/destinations/{destination.Id}/webdav", replacement, token);
        Assert.Equal(HttpStatusCode.OK, passwordChanged.StatusCode);
        Assert.DoesNotContain("replacement-app-password", await passwordChanged.Content.ReadAsStringAsync(token), StringComparison.Ordinal);
        var identityChange = new { expectedRevision = 3, collectionUrl = "https://dav.example.test/other/", username = "other-user", password = "new-secret" };
        using HttpResponseMessage locked = await fixture.Client.PutAsJsonAsync($"/management/destinations/{destination.Id}/webdav", identityChange, token);
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        string problem = await locked.Content.ReadAsStringAsync(token);
        Assert.Contains("IDENTITY_LOCKED", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("new-secret", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_capture_curation_returns_the_current_revision()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        CreatedSourceResponse source = await CreateSourceAsync(fixture.Client, token);
        Guid captureId = Guid.NewGuid();
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.Captures.Add(new CaptureEntity { Id = captureId, SourceId = source.Id, NormalizedStem = "synthetic", Revision = 3, Rating = 2 });
            await database.SaveChangesAsync(token);
        }

        using HttpResponseMessage stale = await fixture.Client.PutAsJsonAsync($"/management/captures/{captureId}/curation",
            new UpdateCaptureCurationRequest(2, 5, null, []), token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        CaptureConflictProblemDetails conflict = (await stale.Content.ReadFromJsonAsync<CaptureConflictProblemDetails>(token))!;
        Assert.Equal("CAPTURE_REVISION_CONFLICT", conflict.ErrorCode);
        Assert.Equal(captureId, conflict.Current.Id);
        Assert.Equal(3, conflict.Current.Revision);
        Assert.Equal(2, conflict.Current.Rating);
    }

    [Fact]
    public async Task OpenApi_lists_configuration_and_capture_conflict_schemas()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        using JsonDocument openApi = JsonDocument.Parse(await fixture.Client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));
        var schemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");

        Assert.True(schemas.TryGetProperty(nameof(CaptureConflictProblemDetails), out _));
        Assert.True(schemas.TryGetProperty(nameof(DestinationResponse), out _));
        Assert.True(schemas.TryGetProperty(nameof(WebDavDestinationResponse), out _));
        JsonElement managementStatus = openApi.RootElement.GetProperty("paths").GetProperty("/management/status")
            .GetProperty("get").GetProperty("responses");
        Assert.True(managementStatus.TryGetProperty("401", out _));
        Assert.True(managementStatus.TryGetProperty("403", out _));
        Assert.True(managementStatus.TryGetProperty("503", out _));
    }

    private static async Task<CreatedSourceResponse> CreateSourceAsync(HttpClient client, CancellationToken token)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/management/sources", CameraRequest("Camera"), token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedSourceResponse>(token))!;
    }

    private sealed class ConfigurationApiFixture : IAsyncDisposable
    {
        private static readonly byte[] SigningKey = Encoding.UTF8.GetBytes("synthetic-api-test-signing-key-at-least-32-bytes");

        private ConfigurationApiFixture(ArchitectureTestDatabase database, WebApplicationFactory<Program> server, HttpClient client)
        {
            Database = database;
            Server = server;
            Client = client;
        }

        public ArchitectureTestDatabase Database { get; }
        public WebApplicationFactory<Program> Server { get; }
        public HttpClient Client { get; }

        public static async Task<ConfigurationApiFixture> CreateAsync(bool revokeBatchAuthorization = false,
            bool failBatchAuthorizationDependency = false, bool enableCameraIngress = false, bool invalidEncryptionKey = false,
            CameraIngressSettings? profileSettings = null, string? minimumPasswordLength = null,
            ICameraIngressDirectories? directoriesOverride = null)
        {
            ArchitectureTestDatabase database = await ArchitectureTestDatabase.CreateAsync();
            await using (var context = new ShutterSpoolDbContext(database.Options))
            {
                await new DatabaseInitializer(context).InitializeAsync(database.SpoolRoot, TestContext.Current.CancellationToken);
                await new EfAccessGrantStore(context).BootstrapAsync("https://issuer.test", "operator", TestContext.Current.CancellationToken);
            }
            CameraIngressSettings ingressSettings = profileSettings ?? new CameraIngressSettings
            {
                Enabled = true,
                CallbackKey = "synthetic-camera-callback-key-at-least-32",
                CallbackBaseUrl = "http://localhost:8081/internal/camera",
                InternalPort = 8081,
                Profiles = new CameraIngressProfiles
                {
                    ModernFtps = new() { Enabled = true, ControlPort = 2121 },
                    LegacyFtps = new() { Enabled = true, ControlPort = 2138 },
                    PlainFtpLan = new() { Enabled = true, ControlPort = 2155 },
                },
            };

            WebApplicationFactory<Program> server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ShutterSpool:Spool:RootPath"] = database.SpoolRoot,
                    ["ShutterSpool:Spool:ReservedBytes"] = "0",
                    ["ShutterSpool:CameraIngress:Enabled"] = enableCameraIngress ? "true" : "false",
                    ["ShutterSpool:CameraCredentials:MinimumPasswordLength"] = minimumPasswordLength,
                }));
                if (enableCameraIngress)
            {
                using var db = new ShutterSpoolDbContext(database.Options);
                string connectionString = db.Database.GetConnectionString()!;
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:shutterspool"] = connectionString,
                    ["ShutterSpool:Spool:RootPath"] = database.SpoolRoot,
                    ["ShutterSpool:Spool:ReservedBytes"] = "0",
                    ["ShutterSpool:CameraIngress:Enabled"] = "true",
                        ["ShutterSpool:CameraIngress:CallbackKey"] = "synthetic-camera-callback-key-at-least-32",
                        ["ShutterSpool:CameraIngress:CallbackBaseUrl"] = "http://localhost:8081/internal/camera",
                    ["ShutterSpool:CameraIngress:InternalPort"] = "8081",
                }));
            }
                builder.ConfigureTestServices(services =>
                {
                services.RemoveAll<DbContextOptions<ShutterSpoolDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<ShutterSpoolDbContext>>();
                services.AddSingleton(database.Options);
                using var connectionContext = new ShutterSpoolDbContext(database.Options);
                string testConnectionString = connectionContext.Database.GetConnectionString()!;
                services.RemoveAll<PostgresOriginalAssetLock>();
                services.AddSingleton(new PostgresOriginalAssetLock(testConnectionString));
                services.RemoveAll<ShutterSpool.Application.Retention.IRetentionControllerLock>();
                services.AddSingleton<ShutterSpool.Application.Retention.IRetentionControllerLock>(
                    new PostgresRetentionControllerLock(testConnectionString));
                services.RemoveAll<IProtectionAdmissionLock>();
                services.AddSingleton<IProtectionAdmissionLock>(provider => new PostgresProtectionAdmissionLock(
                    testConnectionString, provider.GetRequiredService<ShutterSpool.Application.Ingest.ISpool>()));
                services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new CredentialProtectionOptions
                { EncryptionKey = invalidEncryptionKey ? "synthetic-invalid-base64" : Convert.ToBase64String(new byte[32]) }));
                if (minimumPasswordLength is not null)
                {
                    services.RemoveAll<CameraCredentialPolicy>();
                    services.AddSingleton(new CameraCredentialPolicy
                    {
                        MinimumLength = int.Parse(minimumPasswordLength, System.Globalization.CultureInfo.InvariantCulture),
                    });
                }
                services.RemoveAll<CameraIngressSettings>();
                services.AddSingleton(ingressSettings);
                if (ingressSettings.Enabled)
                {
                    services.AddScoped<ICameraIngressStore, EfCameraIngressStore>();
                    services.AddSingleton<ICameraIngressDirectories>(directoriesOverride ??
                        new FileSystemCameraIngressDirectories(Path.Combine(database.SpoolRoot, ".camera-directories")));
                    services.AddScoped<CameraIngressService>();
                }
                if (revokeBatchAuthorization || failBatchAuthorizationDependency)
                    services.AddSingleton<IAuthorizationHandler>(new RevokeBatchAuthorizationHandler(failBatchAuthorizationDependency));
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = "https://issuer.test",
                        ValidAudience = "shutterspool",
                        IssuerSigningKey = new SymmetricSecurityKey(SigningKey),
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateIssuerSigningKey = true,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.Zero,
                    });
                });
            });
            var client = server.CreateClient();
            var token = new JwtSecurityToken(
                issuer: "https://issuer.test",
                audience: "shutterspool",
                claims: [new(ClaimTypes.NameIdentifier, "operator"), new(ClaimTypes.Role, "shutterspool-admin")],
                expires: DateTime.UtcNow.AddMinutes(5),
                signingCredentials: new(new SymmetricSecurityKey(SigningKey), SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
            return new(database, server, client);
        }

        private sealed class RevokeBatchAuthorizationHandler(bool failDependency) : AuthorizationHandler<LocalGrantRequirement>
        {
            private int _batchChecks;

            protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, LocalGrantRequirement requirement)
            {
                if (context.Resource is HttpContext http
                    && http.Request.Path.Value == "/management/captures/batch/curation")
                {
                    if (Interlocked.Increment(ref _batchChecks) == 3)
                    {
                        if (failDependency) throw new TimeoutException("synthetic authorization dependency outage");
                        context.Fail();
                    }
                }
                return Task.CompletedTask;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}
