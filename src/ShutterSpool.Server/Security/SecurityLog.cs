namespace ShutterSpool.Server.Security;

internal static partial class SecurityLog
{
    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Security audit management authentication succeeded")]
    public static partial void ManagementAuthenticationSucceeded(this ILogger logger);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "Security audit management authentication failed")]
    public static partial void ManagementAuthenticationFailed(this ILogger logger);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Security audit management authorization failed")]
    public static partial void ManagementAuthorizationFailed(this ILogger logger);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Information, Message = "Security audit source authentication succeeded for {SourceId}")]
    public static partial void SourceAuthenticationSucceeded(this ILogger logger, Guid sourceId);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Information, Message = "Security audit source authentication failed")]
    public static partial void SourceAuthenticationFailed(this ILogger logger);
}
