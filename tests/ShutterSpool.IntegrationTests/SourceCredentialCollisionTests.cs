using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShutterSpool.Application.Sources;
using ShutterSpool.Infrastructure.Persistence;
using Npgsql;
using ShutterSpool.Server.Api.Sources;
using System.Net.Http.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Theory]
    [InlineData(null, 1, 201, 2, 1)]
    [InlineData(null, 4, 201, 5, 1)]
    [InlineData(null, 99, 409, 5, 0)]
    [InlineData("supplied-camera", 1, 409, 1, 0)]
    public async Task Username_collision_retries_only_generated_usernames_and_stops_after_five_attempts(
        string? username, int failuresToInject, int expectedStatus, long expectedAttempts, int expectedSourceCount)
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        string sequence = $"credential_collision_{Guid.NewGuid():N}";
        string function = $"credential_collision_{Guid.NewGuid():N}";
        string trigger = $"credential_collision_{Guid.NewGuid():N}";
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            string connectionString = database.Database.GetConnectionString()!;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using (var command = new NpgsqlCommand($"CREATE SEQUENCE \"{sequence}\" START WITH 1", connection))
                await command.ExecuteNonQueryAsync(token);
            await using (var grant = new NpgsqlCommand($"GRANT USAGE,SELECT ON SEQUENCE \"{sequence}\" TO \"{fixture.RuntimeRole}\"", connection))
                await grant.ExecuteNonQueryAsync(token);
            string createFunction = $"""
                CREATE FUNCTION "{function}"() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF nextval('"{sequence}"') <= {failuresToInject} THEN
                        RAISE unique_violation USING CONSTRAINT = 'IX_source_credentials_Username';
                    END IF;
                    RETURN NEW;
                END
                $$;
                """;
            await using (var command = new NpgsqlCommand(createFunction, connection))
                await command.ExecuteNonQueryAsync(token);
            await using (var command = new NpgsqlCommand(
                $"CREATE TRIGGER \"{trigger}\" BEFORE INSERT ON source_credentials FOR EACH ROW EXECUTE FUNCTION \"{function}\"()",
                connection))
                await command.ExecuteNonQueryAsync(token);
        }

        using HttpResponseMessage response = await fixture.Client.PostAsJsonAsync("/management/sources", new
        {
            name = "Camera username collision",
            username,
            sourceType = "CameraFtpUpload",
            connectionProfile = "ModernFtps",
        }, token);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        if (expectedStatus == 201)
        {
            CreatedSourceResponse created = (await response.Content.ReadFromJsonAsync<CreatedSourceResponse>(token))!;
            Assert.Matches("^[a-z]{6,8}$", created.Username);
            Assert.Equal(20, created.Secret!.Length);
            Assert.All(created.Secret, character => Assert.Contains(character, "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"));
            await using var scope = fixture.Scope();
            var verifier = scope.ServiceProvider.GetRequiredService<SourceCredentialVerifier>();
            Assert.Equal(created.Id, (await verifier.VerifyAsync(created.Username, created.Secret, cancellationToken: token))?.SourceId);
        }
        else
        {
            string body = await response.Content.ReadAsStringAsync(token);
            Assert.Contains("username", body, StringComparison.OrdinalIgnoreCase);
        }

        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Equal(expectedSourceCount, await verify.Sources.CountAsync(token));
        Assert.Equal(expectedSourceCount, await verify.SourceCredentials.CountAsync(token));
        string verifyConnectionString = verify.Database.GetConnectionString()!;
        await using var verifyConnection = new NpgsqlConnection(verifyConnectionString);
        await verifyConnection.OpenAsync(token);
        await using var attempts = new NpgsqlCommand($"SELECT last_value FROM \"{sequence}\"", verifyConnection);
        Assert.Equal(expectedAttempts, (long)(await attempts.ExecuteScalarAsync(token))!);
    }
}
