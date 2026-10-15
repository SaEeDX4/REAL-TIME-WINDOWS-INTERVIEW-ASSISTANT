using InterviewAssistant.Client;

namespace InterviewAssistant.Tests;

public class UpdatePolicyTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-07T09:00:00Z");

    [Fact]
    public void NeverChecksDuringALiveSession()
    {
        Assert.False(UpdatePolicy.ShouldCheck(sessionActive: true, null, Start.AddHours(10), Start));
        Assert.True(UpdatePolicy.ShouldCheck(sessionActive: false, null, Start.AddHours(10), Start));
    }

    [Fact]
    public void WaitsAfterStartupAndBetweenChecks()
    {
        Assert.False(UpdatePolicy.ShouldCheck(false, null, Start.AddSeconds(10), Start));        // app just opened
        Assert.False(UpdatePolicy.ShouldCheck(false, Start.AddHours(1), Start.AddHours(2), Start));
        Assert.True(UpdatePolicy.ShouldCheck(false, Start.AddHours(1), Start.AddHours(8), Start));
    }

    [Fact]
    public void AppliesOnlyWhenIdleAndDownloaded()
    {
        Assert.False(UpdatePolicy.CanApplyNow(sessionActive: true, updateDownloaded: true));
        Assert.False(UpdatePolicy.CanApplyNow(false, false));
        Assert.True(UpdatePolicy.CanApplyNow(false, true));
    }

    [Theory]
    [InlineData("beta", "beta")] [InlineData("BETA", "beta")] [InlineData("stable", "stable")] [InlineData(null, "stable")] [InlineData("nightly", "stable")]
    public void ChannelsAreRestricted(string? input, string expected) => Assert.Equal(expected, UpdatePolicy.NormalizeChannel(input));
}

public class InstallLayoutTests
{
    [Fact]
    public void InstalledVelopackLayout_IsInstalled_PortableIsNot()
    {
        var installed = Path.Combine("C:", "Users", "u", "AppData", "Local", "InterviewAssistant.Desktop", "current", "InterviewAssistant.exe");
        var update = Path.Combine("C:", "Users", "u", "AppData", "Local", "InterviewAssistant.Desktop", "Update.exe");
        Assert.True(InterviewAssistant.Core.Diagnostics.InstallLayout.IsInstalled(installed, p => p == update));
        Assert.False(InterviewAssistant.Core.Diagnostics.InstallLayout.IsInstalled(installed, _ => false));          // "current" folder alone is not enough
        Assert.False(InterviewAssistant.Core.Diagnostics.InstallLayout.IsInstalled(Path.Combine("D:", "Downloads", "InterviewAssistant", "InterviewAssistant.exe"), _ => true));
        Assert.False(InterviewAssistant.Core.Diagnostics.InstallLayout.IsInstalled(null, _ => true));
    }
}
