using ShutterSpool.Application.Workspaces;
using ShutterSpool.Server.Security;
using Microsoft.AspNetCore.Http;

namespace ShutterSpool.IntegrationTests;

public sealed class WorkspaceScopeTests
{
    [Fact]
    public void Workspace_scope_cannot_be_rebound_or_read_without_selection()
    {
        var context = new WorkspaceContext(true);
        Assert.Throws<InvalidOperationException>(() => context.Required);
        var workspace = new WorkspaceDefinition(Guid.NewGuid(), "Studio", "volume-a", "/workspace-a", 100, "tenant-a");
        context.Bind(workspace);
        Assert.Same(workspace, context.Required);
        Assert.Throws<InvalidOperationException>(() => context.Bind(workspace with { Id = Guid.NewGuid() }));
    }

    [Theory]
    [InlineData("/management/captures", false)]
    [InlineData("/media/previews/x", false)]
    [InlineData("/live/changes", true)]
    [InlineData("/live/changes/negotiate", true)]
    [InlineData("/live/status", true)]
    public void Query_selection_is_limited_to_live_transports(string path, bool accepted)
    {
        var request = new DefaultHttpContext().Request;
        request.Path = path;
        Guid id = Guid.NewGuid();
        request.QueryString = new QueryString($"?workspace={id:D}");
        Assert.Equal(accepted, WorkspaceSelection.TryRead(request, out Guid selected));
        if (accepted) Assert.Equal(id, selected);
    }

    [Fact]
    public void Missing_duplicate_and_conflicting_selectors_fail_closed()
    {
        var request = new DefaultHttpContext().Request;
        request.Path = "/live/changes";
        Assert.False(WorkspaceSelection.TryRead(request, out _));
        Guid id = Guid.NewGuid();
        request.Headers[WorkspaceSelection.Header] = id.ToString("D");
        request.QueryString = new QueryString($"?workspace={Guid.NewGuid():D}");
        Assert.False(WorkspaceSelection.TryRead(request, out _));
        request.QueryString = new QueryString($"?workspace={id:D}");
        Assert.True(WorkspaceSelection.TryRead(request, out _));
        request.Headers.Append(WorkspaceSelection.Header, id.ToString("D"));
        Assert.False(WorkspaceSelection.TryRead(request, out _));
    }
}
