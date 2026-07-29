using UnpackVision.Core;

namespace UnpackVision.Tests;

public sealed class PrivacyAndTelemetryTests
{
    [Fact]
    public void ConsentState_RequiresBothCurrentDocumentVersionsAndAcceptanceTime()
    {
        var state = new ConsentState
        {
            TermsVersion = "2026-07-29",
            PrivacyPolicyVersion = "2026-07-29",
            AcceptedAt = new DateTimeOffset(2026, 7, 29, 10, 0, 0, TimeSpan.FromHours(8))
        };

        Assert.True(state.IsCurrent("2026-07-29", "2026-07-29"));
        Assert.False(state.IsCurrent("2026-07-30", "2026-07-29"));
        Assert.False(new ConsentState().IsCurrent("2026-07-29", "2026-07-29"));
    }

    [Fact]
    public async Task NoOpUsageTelemetry_DoesNotFailOrRequireNetworkConfiguration()
    {
        IUsageTelemetry telemetry = new NoOpUsageTelemetry();

        await telemetry.TrackAsync(
            "application.started",
            new Dictionary<string, string> { ["version"] = "2.2.0" });
    }

    [Fact]
    public void DonationProfile_DefaultsToUnconfiguredQrCodes()
    {
        var profile = new DonationProfile();

        Assert.False(profile.IsConfigured);
        Assert.Equal("五成", profile.DeveloperName);
        Assert.True(string.IsNullOrWhiteSpace(profile.AlipayQrAsset));
        Assert.True(string.IsNullOrWhiteSpace(profile.WeChatQrAsset));
    }
}
