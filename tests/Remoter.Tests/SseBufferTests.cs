using Remoter.App.OpenCode;

namespace Remoter.Tests;

public class SseBufferTests
{
    [Fact]
    public void Feed_AssemblesEventOnBlankLine()
    {
        var buffer = new SseBuffer();

        Assert.False(buffer.Feed(": heartbeat", out _));
        Assert.False(buffer.Feed(
            "data: {\"id\":\"evt_1\",\"type\":\"session.text.delta\",\"data\":{\"delta\":\"hi\"}}", out _));
        Assert.True(buffer.Feed("", out var ev));

        Assert.NotNull(ev);
        Assert.Equal("evt_1", ev!.Id);
        Assert.Equal("session.text.delta", ev.Type);
        Assert.Equal("hi", ev.Data.GetProperty("delta").GetString());
    }

    [Fact]
    public void Feed_JoinsMultipleDataLines()
    {
        var buffer = new SseBuffer();

        buffer.Feed("data: {\"type\":\"t\",", out _);
        buffer.Feed("data: \"data\":{}}", out _);
        Assert.True(buffer.Feed("", out var ev));

        Assert.NotNull(ev);
        Assert.Equal("t", ev!.Type);
    }

    [Fact]
    public void Feed_BrokenJsonCompletesFrameWithNullEvent()
    {
        var buffer = new SseBuffer();

        buffer.Feed("data: not-json", out _);
        Assert.True(buffer.Feed("", out var ev));
        Assert.Null(ev);
        Assert.False(buffer.HasData);
    }

    [Fact]
    public void Feed_IgnoresUnknownFieldsAndBlankLinesWithoutData()
    {
        var buffer = new SseBuffer();

        Assert.False(buffer.Feed("event: message", out _));
        Assert.False(buffer.Feed("", out _));
        Assert.False(buffer.HasData);
    }

    [Fact]
    public void Flush_ParsesPendingEventAtEndOfStream()
    {
        var buffer = new SseBuffer();
        buffer.Feed("data: {\"type\":\"server.connected\"}", out _);

        var ev = buffer.Flush();

        Assert.NotNull(ev);
        Assert.Equal("server.connected", ev!.Type);
    }

    [Theory]
    [InlineData("{\"id\":\"x\"}")]
    [InlineData("[]")]
    [InlineData("\"just a string\"")]
    public void ParseEvent_ReturnsNullForNonEvents(string json)
    {
        Assert.Null(SseBuffer.ParseEvent(json));
    }

    [Fact]
    public void ParseEvent_KeepsNestedData()
    {
        var ev = SseBuffer.ParseEvent(
            "{\"type\":\"permission.asked\",\"data\":{\"id\":\"per_1\",\"action\":\"shell\"}}");

        Assert.NotNull(ev);
        Assert.Equal("permission.asked", ev!.Type);
        Assert.Equal("shell", ev.Data.GetProperty("action").GetString());
    }
}
