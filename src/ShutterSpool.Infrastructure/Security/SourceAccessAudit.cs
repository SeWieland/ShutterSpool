using Microsoft.Extensions.Logging;
using ShutterSpool.Application.Security;

namespace ShutterSpool.Infrastructure.Security;

internal sealed partial class SourceAccessAudit(ILogger<SourceAccessAudit> logger) : ISourceAccessAudit
{
    [LoggerMessage(EventId = 2004, EventName = "SourceAuthenticationSucceeded", Level = LogLevel.Information,
        Message = "Source authentication succeeded for {SourceId}.")]
    public partial void Succeeded(Guid sourceId);

    public void Failed() => logger.LogWarning(new EventId(2005, "SourceAuthenticationFailed"), "Source authentication failed.");
}
