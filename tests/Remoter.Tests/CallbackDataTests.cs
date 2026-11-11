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
}
