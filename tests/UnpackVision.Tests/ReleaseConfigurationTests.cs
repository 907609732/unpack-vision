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

    [Fact]
    public void ReleaseWorkflow_SignsApplicationBeforePackagingAndInstallerAfterPackaging()
    {
        var workflow = ReadTestData("release.yml");

        var applicationSigning = workflow.IndexOf(
            "Sign Windows application with SignPath",
            StringComparison.Ordinal);
        var packageBuild = workflow.IndexOf(
            "Build Windows installer and release files",
            StringComparison.Ordinal);
        var installerSigning = workflow.IndexOf(
            "Sign Windows installers with SignPath",
            StringComparison.Ordinal);
        var checksumRefresh = workflow.IndexOf(
            "Refresh release checksums",
            StringComparison.Ordinal);

        Assert.True(applicationSigning >= 0);
        Assert.True(packageBuild > applicationSigning);
        Assert.True(installerSigning > packageBuild);
        Assert.True(checksumRefresh > installerSigning);
        Assert.Contains("-SkipPublish", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflow_PinsAndGatesSignPathRequests()
    {
        var workflow = ReadTestData("release.yml");
        const string pinnedAction =
            "SignPath/github-action-submit-signing-request@b9d91eadd323de506c0c81cf0c7fe7438f3360fd";

        Assert.Equal(2, CountOccurrences(workflow, pinnedAction));
        Assert.Contains(
            "if: ${{ vars.WINDOWS_STABLE_SIGNING_READY == 'true' }}",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("SIGNPATH_API_TOKEN", workflow, StringComparison.Ordinal);
        Assert.Contains("SIGNPATH_APPLICATION_ARTIFACT_CONFIGURATION_SLUG", workflow, StringComparison.Ordinal);
        Assert.Contains("SIGNPATH_INSTALLER_ARTIFACT_CONFIGURATION_SLUG", workflow, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var startIndex = 0;
        while ((startIndex = value.IndexOf(search, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += search.Length;
        }

        return count;
    }

    private static string ReadTestData(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));
}
