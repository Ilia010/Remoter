using System.Text.Json;
using Remoter.App.OpenCode;

namespace Remoter.Tests;

public class JsonOptionsTests
{
    [Fact]
    public void PermissionRules_SerializeAsCamelCase()
    {
        var json = JsonSerializer.Serialize(new[] { new PermissionRule("edit", "*", "ask") }, Json.Options);

        Assert.Contains("\"action\"", json);
        Assert.Contains("\"resource\"", json);
        Assert.Contains("\"effect\"", json);
        Assert.DoesNotContain("\"Action\"", json);
    }

    [Fact]
    public void SessionMessage_ParsesProviderError()
    {
        const string json =
            "{\"id\":\"m1\",\"type\":\"assistant\",\"error\":{\"type\":\"provider.quota\",\"message\":\"no money\",\"status\":402}}";

        var message = Json.Deserialize<SessionMessage>(json);

        Assert.NotNull(message);
        Assert.NotNull(message!.Error);
        Assert.Equal(JsonValueKind.Object, message.Error!.Value.ValueKind);
        Assert.Equal("provider.quota", message.Error.Value.GetProperty("type").GetString());
        Assert.Equal(402, message.Error.Value.GetProperty("status").GetInt32());
    }

    [Fact]
    public void SessionInfo_ParsesNestedLocationAndModel()
    {
        const string json =
            "{\"id\":\"ses_1\",\"agent\":\"build\",\"location\":{\"directory\":\"C:\\\\work\\\\demo\"},\"model\":{\"id\":\"m\",\"providerID\":\"opencode\"}}";

        var session = Json.Deserialize<SessionInfo>(json);

        Assert.NotNull(session);
        Assert.Equal("ses_1", session!.Id);
        Assert.Equal(@"C:\work\demo", session.Location!.Directory);
        Assert.Equal("opencode", session.Model!.ProviderID);
        Assert.Equal("opencode/m", session.Model.ToString());
    }
}
