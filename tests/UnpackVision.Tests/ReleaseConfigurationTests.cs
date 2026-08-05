namespace UnpackVision.Tests;

public sealed class ReleaseConfigurationTests
{
    [Fact]
    public void ReleaseWorkflow_InjectsThePublicTelemetryEndpointIntoAndroid()
    {
        var workflow = ReadTestData("release.yml");
        var endpoint = ReadTestData("telemetry-endpoint.txt").Trim();

        Assert.True(Uri.TryCreate(endpoint, UriKind.Absolute, out var uri));
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.Contains("UNPACKVISION_TELEMETRY_ENDPOINT", workflow, StringComparison.Ordinal);
        Assert.Contains("telemetry-endpoint.txt", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflow_RefusesUnsignedStableWindowsArtifacts()
    {
        var workflow = ReadTestData("release.yml");

        Assert.Contains("WINDOWS_STABLE_SIGNING_READY", workflow, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", workflow, StringComparison.Ordinal);
        Assert.Contains("SignatureStatus]::Valid", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "prerelease: ${{ steps.release_channel.outputs.prerelease }}",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("prerelease: true", workflow, StringComparison.Ordinal);
    }

    private static string ReadTestData(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));
}
