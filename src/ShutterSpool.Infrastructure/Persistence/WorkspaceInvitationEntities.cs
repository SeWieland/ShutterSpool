using ShutterSpool.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class WorkspaceInvitationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string RecipientIssuer { get; set; }
    public required string RecipientSubject { get; set; }
    public required string RecipientLabel { get; set; }
    public WorkspaceRole Role { get; set; }
    public required string Status { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed partial class ShutterSpoolDbContext
{
    private static void ConfigureInvitations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkspaceInvitationEntity>(entity =>
        {
            entity.ToTable("workspace_invitations");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.RecipientIssuer).HasMaxLength(2048);
            entity.Property(item => item.RecipientSubject).HasMaxLength(512);
            entity.Property(item => item.RecipientLabel).HasMaxLength(256);
            entity.HasIndex(item => new { item.RecipientIssuer, item.RecipientSubject }).IsUnique();
            entity.Property(item => item.Role).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Status).HasMaxLength(32);
        });
    }
}
