using ShutterSpool.Domain.Retention;

namespace ShutterSpool.Core.Tests;

public sealed class RetentionSettingsTests
{
    [Theory]
    [InlineData("Revision", -1)]
    [InlineData("TrashRecoveryWindow", 0)]
    [InlineData("TrashRecoveryWindow", 3651)]
    [InlineData("RestoreProtection", 0)]
    [InlineData("RestoreProtection", 3651)]
    [InlineData("GalleryMaximum", 0)]
    [InlineData("GalleryMaximum", 3651)]
    [InlineData("CompletedAttemptHistoryDays", 89)]
    [InlineData("CompletedPressureHistoryDays", 29)]
    public void Invalid_setting_identifies_its_own_property(string property, int value)
    {
        RetentionSettings defaults = new();
        RetentionSettings settings = property switch
        {
            "Revision" => defaults with { Revision = value },
            "TrashRecoveryWindow" => defaults with { TrashRecoveryWindow = TimeSpan.FromDays(value) },
            "RestoreProtection" => defaults with { RestoreProtection = TimeSpan.FromDays(value) },
            "GalleryMaximum" => defaults with { GalleryMaximum = TimeSpan.FromDays(value) },
            "CompletedAttemptHistoryDays" => defaults with { CompletedAttemptHistoryDays = value },
            "CompletedPressureHistoryDays" => defaults with { CompletedPressureHistoryDays = value },
            _ => throw new ArgumentException("Unknown retention setting.", nameof(property)),
        };

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);
        Assert.Equal(property, error.ParamName);
    }

    [Fact]
    public void Nonpositive_duration_takes_precedence_over_another_duration_above_the_maximum()
    {
        RetentionSettings settings = new() { TrashRecoveryWindow = TimeSpan.FromDays(3651), GalleryMaximum = TimeSpan.Zero };

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);
        Assert.Equal("GalleryMaximum", error.ParamName);
    }
}
