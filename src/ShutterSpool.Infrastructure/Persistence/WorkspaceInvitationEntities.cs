using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Security;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class WorkspaceInvitationRecipientEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Issuer { get; set; }
    public required string Subject { get; set; }
    public required string Label { get; set; }
}

internal sealed class WorkspaceInvitationEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RecipientId { get; set; }
    public Guid InviterId { get; set; }
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
        modelBuilder.Entity<WorkspaceInvitationRecipientEntity>(entity =>
        {
            entity.ToTable("workspace_invitation_recipients");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Issuer).HasMaxLength(2048);
            entity.Property(item => item.Subject).HasMaxLength(512);
            entity.Property(item => item.Label).HasMaxLength(256);
            entity.HasIndex(item => new { item.Issuer, item.Subject }).IsUnique();
        });
        modelBuilder.Entity<WorkspaceInvitationEntity>(entity =>
        {
            entity.ToTable("workspace_invitations");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.RecipientId).IsUnique();
            entity.HasOne<WorkspaceInvitationRecipientEntity>().WithOne().HasForeignKey<WorkspaceInvitationEntity>(item => item.RecipientId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AuthorizationGrant>().WithMany().HasForeignKey(item => item.InviterId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(item => item.Role).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Status).HasMaxLength(32);
        });
    }
}
