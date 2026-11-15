using Remoter.App.Telegram;

namespace Remoter.Tests;

public class CallbackDataTests
{
    [Theory]
    [InlineData(ApprovalDecision.Once, "once")]
    [InlineData(ApprovalDecision.Always, "always")]
    [InlineData(ApprovalDecision.Reject, "reject")]
    public void Approval_RoundTrips(ApprovalDecision decision, string token)
    {
        var data = CallbackData.Approval("a1b2c3d4", decision);

        Assert.True(CallbackData.TryParseApproval(data, out var shortId, out var parsed));
        Assert.Equal("a1b2c3d4", shortId);
        Assert.Equal(decision, parsed);
        Assert.Equal(token, CallbackData.Token(parsed));
    }

    [Fact]
    public void Approval_FitsTelegramCallbackLimit()
    {
        var data = CallbackData.Approval("ffffffff", ApprovalDecision.Always);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(data) <= 64);
    }

    [Theory]
    [InlineData("")]
    [InlineData("md:1")]
    [InlineData("ap:")]
    [InlineData("ap:abc")]
    [InlineData("ap::once")]
    [InlineData("ap:abc:once:extra")]
    [InlineData("sh:yes")]
    public void TryParseApproval_RejectsForeignOrBrokenData(string data)
    {
        Assert.False(CallbackData.TryParseApproval(data, out _, out _));
    }

    [Fact]
    public void TryParseApproval_UnknownDecisionFallsBackToOnce()
    {
        Assert.True(CallbackData.TryParseApproval("ap:abc:what", out _, out var decision));
        Assert.Equal(ApprovalDecision.Once, decision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(42)]
    public void Model_RoundTrips(int index)
    {
        var data = CallbackData.Model(index);

        Assert.True(CallbackData.TryParseModel(data, out var parsed));
        Assert.Equal(index, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("md:")]
    [InlineData("md:x")]
    [InlineData("ap:1")]
    [InlineData("sh:no")]
    public void TryParseModel_RejectsForeignOrBrokenData(string data)
    {
        Assert.False(CallbackData.TryParseModel(data, out _));
    }

    [Theory]
    [InlineData("main")]
    [InlineData("files")]
    [InlineData("new")]
    [InlineData("sessions")]
    [InlineData("model")]
    [InlineData("agent")]
    [InlineData("status")]
    [InlineData("screen")]
    [InlineData("diff")]
    [InlineData("cancel")]
    [InlineData("restart")]
    [InlineData("help")]
    [InlineData("shutdown")]
    public void Menu_RoundTrips(string action)
    {
        var data = CallbackData.Menu(action);

        Assert.True(CallbackData.TryParseMenu(data, out var parsed));
        Assert.Equal(action, parsed);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(data) <= 64);
    }

    [Theory]
    [InlineData("")]
    [InlineData("mn:")]
    [InlineData("fs:1:u")]
    [InlineData("ap:abc:once")]
    public void TryParseMenu_RejectsForeignOrBrokenData(string data)
    {
        Assert.False(CallbackData.TryParseMenu(data, out _));
    }

    [Theory]
    [InlineData(1, "u")]
    [InlineData(42, "p0")]
    [InlineData(123456789, "e99")]
    public void Files_RoundTrips(int generation, string token)
    {
        var data = CallbackData.FilesNav(generation, token);

        Assert.True(CallbackData.TryParseFiles(data, out var parsedGeneration, out var parsedToken));
        Assert.Equal(generation, parsedGeneration);
        Assert.Equal(token, parsedToken);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(data) <= 64);
    }

    [Fact]
    public void FilesEntry_FitsTelegramCallbackLimit()
    {
        var data = CallbackData.FilesEntry(int.MaxValue, 199);

        Assert.True(CallbackData.TryParseFiles(data, out var generation, out var token));
        Assert.Equal(int.MaxValue, generation);
        Assert.Equal("e199", token);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(data) <= 64);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fs:")]
    [InlineData("fs:1")]
    [InlineData("fs:x:u")]
    [InlineData("fs:1:")]
    [InlineData("fs:1:u:extra")]
    [InlineData("md:1")]
    public void TryParseFiles_RejectsForeignOrBrokenData(string data)
    {
        Assert.False(CallbackData.TryParseFiles(data, out _, out _));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(999, 14)]
    public void Session_RoundTrips(int token, int index)
    {
        var data = CallbackData.Session(token, index);

        Assert.True(CallbackData.TryParseSession(data, out var parsedToken, out var parsedIndex));
        Assert.Equal(token, parsedToken);
        Assert.Equal(index, parsedIndex);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(data) <= 64);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ss:")]
    [InlineData("ss:1")]
    [InlineData("ss:x:1")]
    [InlineData("ss:1:x")]
    [InlineData("ss:1:2:3")]
    [InlineData("fs:1:e0")]
    public void TryParseSession_RejectsForeignOrBrokenData(string data)
    {
        Assert.False(CallbackData.TryParseSession(data, out _, out _));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("plan")]
    public void Agent_RoundTrips(string agent)
    {
        var data = CallbackData.Agent(agent);

        Assert.True(CallbackData.TryParseAgent(data, out var parsed));
        Assert.Equal(agent, parsed);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(data) <= 64);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ag:")]
    [InlineData("mn:agent")]
    [InlineData("fs:1:e0")]
    public void TryParseAgent_RejectsForeignOrBrokenData(string data)
    {
        Assert.False(CallbackData.TryParseAgent(data, out _));
    }
}
