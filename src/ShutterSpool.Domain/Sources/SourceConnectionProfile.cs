namespace ShutterSpool.Domain.Sources;

public enum SourceType
{
    CameraFtpUpload,
}

public enum SourceConnectionProfile
{
    ModernFtps,
    PlainFtpLan,
    LegacyFtps,
}
