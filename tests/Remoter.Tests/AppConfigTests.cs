using Remoter.App;

namespace Remoter.Tests;

public class AppConfigTests
{
    [Fact]
    public void ExpandPath_ExpandsTildeToUserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(home, AppConfig.ExpandPath("~"));
        Assert.Equal(Path.Combine(home, "docs"), AppConfig.ExpandPath("~/docs"));
        Assert.Equal(Path.Combine(home, "docs"), AppConfig.ExpandPath(@"~\docs"));
    }

    [Fact]
    public void ExpandPath_ExpandsEnvironmentVariables()
    {
        var expected = Path.GetFullPath(Environment.ExpandEnvironmentVariables("%TEMP%"));

        Assert.Equal(expected, AppConfig.ExpandPath("%TEMP%"));
    }

    [Fact]
    public void ExpandPath_KeepsAbsolutePath()
    {
        Assert.Equal(@"C:\work\demo", AppConfig.ExpandPath(@"C:\work\demo"));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData("yes", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    public void ApplySetting_ParsesPreventSleep(string value, bool expected)
    {
        var config = new AppConfig();

        config.ApplySetting("PREVENT_SLEEP", value);

        Assert.Equal(expected, config.PreventSleep);
    }

    [Fact]
    public void ApplySetting_PreventSleepAcceptsRemoterPrefix()
    {
        var config = new AppConfig();

        config.ApplySetting("REMOTER_PREVENT_SLEEP", "true");

        Assert.True(config.PreventSleep);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    public void ApplySetting_ParsesAutoApprove(string value, bool expected)
    {
        var config = new AppConfig();

        config.ApplySetting("AUTO_APPROVE", value);

        Assert.Equal(expected, config.AutoApprove);
    }

    [Fact]
    public void ApplySetting_AutoApproveHasNoApprovalAlias()
    {
        var config = new AppConfig();

        config.ApplySetting("NO_APPROVAL", "true");

        Assert.True(config.AutoApprove);
    }
}
