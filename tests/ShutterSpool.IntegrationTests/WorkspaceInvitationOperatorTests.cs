using Microsoft.EntityFrameworkCore;
using ShutterSpool.Infrastructure.Composition;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Workspaces;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class WorkspaceTenancyPostgresTests
{
    [Fact]
    public async Task Workspace_invitation_operator_handoff_is_workspace_scoped_audited_idempotent_and_never_grants_membership()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.ProvisionAsync();
        using var writer = new WorkspaceWriterLease();
        writer.Acquire((await fixture.RegistryAsync())[0]);
        string path = Path.Combine(Path.GetTempPath(), "shutterspool-recipient-" + Guid.NewGuid().ToString("N") + ".json");
        Guid operation = Guid.NewGuid();
        var handoff = new WorkspaceRecipientHandoff("https://identity.example", "synthetic-recipient", "Synthetic intended tester", "synthetic-host-operator");
        string[] command = ["--workspace-recipient", fixture.WorkspaceName(0), operation.ToString("D"), path];
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(handoff), token);
            await WorkspaceOperator.RunAsync(command, fixture.Configuration, token);
            await WorkspaceOperator.RunAsync([command[0], command[1].ToUpperInvariant(), command[2], command[3]], fixture.Configuration, token);
            await using var database = fixture.MigrationDatabase(0);
            WorkspaceInvitationRecipientEntity recipient = await database.Set<WorkspaceInvitationRecipientEntity>().SingleAsync(token);
            Assert.Equal(handoff.Issuer, recipient.Issuer);
            Assert.Equal(handoff.Subject, recipient.Subject);
            Assert.Single(await database.AuthorizationGrants.ToArrayAsync(token));
            Assert.Empty(await database.Set<WorkspaceInvitationEntity>().ToArrayAsync(token));
            WorkspaceAccessOperationEntity audit = await database.Set<WorkspaceAccessOperationEntity>().SingleAsync(item => item.Id == operation, token);
            Assert.Equal("operator-recipient", audit.Kind);
            Assert.Equal(handoff.OperatorIdentity, audit.ActorSubject);
            Assert.Equal(recipient.Id, audit.TargetId);
            await using var other = fixture.MigrationDatabase(1);
            Assert.Empty(await other.Set<WorkspaceInvitationRecipientEntity>().ToArrayAsync(token));
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(handoff with { Subject = "different-subject" }), token);
            await Assert.ThrowsAsync<ArgumentException>(() => WorkspaceOperator.RunAsync(command, fixture.Configuration, token));
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(handoff with { Issuer = "https://wrong-issuer.example" }), token);
            await Assert.ThrowsAsync<ArgumentException>(() => WorkspaceOperator.RunAsync(withOperation(Guid.NewGuid()), fixture.Configuration, token));
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(handoff with { OperatorIdentity = "" }), token);
            await Assert.ThrowsAsync<ArgumentException>(() => WorkspaceOperator.RunAsync(withOperation(Guid.NewGuid()), fixture.Configuration, token));
            await File.WriteAllTextAsync(path, new string('x', 16_385), token);
            await Assert.ThrowsAsync<ArgumentException>(() => WorkspaceOperator.RunAsync(withOperation(Guid.NewGuid()), fixture.Configuration, token));
            database.ChangeTracker.Clear();
            Assert.Equal(handoff.Subject, (await database.Set<WorkspaceInvitationRecipientEntity>().SingleAsync(token)).Subject);
        }
        finally { File.Delete(path); }

        string[] withOperation(Guid next) => [command[0], command[1], next.ToString("D"), command[3]];
    }
}
