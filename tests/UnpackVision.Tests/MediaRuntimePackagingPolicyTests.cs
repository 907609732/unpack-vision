namespace UnpackVision.Tests;

public sealed class MediaRuntimePackagingPolicyTests
{
    private const string GStreamerSha256 =
        "51ee5eaec33008e8409d8cf6f6884457f22aa3bd515f8856f993a3eaab903530";

    [Fact]
    public void StageScriptPinsOfficialGStreamerAndAuditsAnExplicitDependencyClosure()
    {
        var script = Read("stage-media-runtimes.ps1");

        Assert.Contains(
            "https://gstreamer.freedesktop.org/data/pkg/windows/$gstreamerVersion/msvc/$installerName",
            script,
            StringComparison.Ordinal);
        Assert.Contains(GStreamerSha256, script, StringComparison.Ordinal);
        Assert.Contains("/TYPE=runtime", script, StringComparison.Ordinal);
        Assert.Contains("$approvedBinaryPaths", script, StringComparison.Ordinal);
        Assert.Contains("Runtime binary is not on the audited license allowlist", script, StringComparison.Ordinal);
        Assert.Contains("$allowedPluginLicenses", script, StringComparison.Ordinal);
        Assert.Contains("Get-PluginMetadata", script, StringComparison.Ordinal);
        Assert.Contains("share\\licenses", script, StringComparison.Ordinal);
    }

    [Fact]
    public void FfmpegGateRequiresExactVersionAndRejectsGplAndNonfreeFlags()
    {
        var script = Read("test-ffmpeg-redistribution.ps1");

        Assert.Contains("$expectedVersion = '8.1.2'", script, StringComparison.Ordinal);
        Assert.Contains("--enable-gpl", script, StringComparison.Ordinal);
        Assert.Contains("--enable-nonfree", script, StringComparison.Ordinal);
        Assert.Contains("FFmpeg redistribution refused", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishAndPackageBothEnforceTheRuntimeVerifier()
    {
        var publish = Read("publish.ps1");
        var package = Read("package-release.ps1");

        Assert.Contains("stage-media-runtimes.ps1", publish, StringComparison.Ordinal);
        Assert.Contains("verify-media-runtime-package.ps1", publish, StringComparison.Ordinal);
        Assert.Contains("verify-media-runtime-package.ps1", package, StringComparison.Ordinal);
        Assert.Contains("media-runtime-manifest.json", package, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflowRunsRuntimeVerificationBeforePackaging()
    {
        var workflow = Read("release.yml");
        var verify = workflow.IndexOf("Verify audited media runtime package", StringComparison.Ordinal);
        var package = workflow.IndexOf("Build Windows installer and release files", StringComparison.Ordinal);

        Assert.True(verify >= 0);
        Assert.True(package > verify);
        Assert.Contains("runtimes\\gstreamer\\1.28.5", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeVerifierChecksPinnedManifestHashesAndForbiddenBinaryNames()
    {
        var script = Read("verify-media-runtime-package.ps1");

        Assert.Contains(GStreamerSha256, script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", script, StringComparison.Ordinal);
        Assert.Contains("x264|x265|avcodec", script, StringComparison.Ordinal);
        Assert.Contains("--enable-gpl", script, StringComparison.Ordinal);
        Assert.Contains("--enable-nonfree", script, StringComparison.Ordinal);
    }

    private static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));
}
