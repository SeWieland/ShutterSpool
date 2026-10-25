using Microsoft.EntityFrameworkCore;
using ShutterSpool.Infrastructure.Persistence;
using Npgsql;
using ShutterSpool.Server.Api.Sources;
using System.Net.Http.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Theory]
    [InlineData(1, 201, 2, 1)]
    [InlineData(4, 201, 5, 1)]
    [InlineData(99, 409, 5, 0)]
    public async Task Generated_username_collision_retries_atomically_and_stops_after_five_attempts(
        int failuresToInject, int expectedStatus, long expectedAttempts, int expectedSourceCount)
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
            name = "Generated username collision",
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
