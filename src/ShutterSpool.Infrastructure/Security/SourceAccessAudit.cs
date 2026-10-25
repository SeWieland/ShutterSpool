using Microsoft.Extensions.Logging;
using ShutterSpool.Application.Security;

namespace ShutterSpool.Infrastructure.Security;

internal sealed class SourceAccessAudit(ILogger<SourceAccessAudit> logger) : ISourceAccessAudit
{
    public void Succeeded(Guid sourceId) => logger.LogInformation(new EventId(2004, "SourceAuthenticationSucceeded"), "Source authentication succeeded for {SourceId}.", sourceId);
    public void Failed() => logger.LogWarning(new EventId(2005, "SourceAuthenticationFailed"), "Source authentication failed.");
}
