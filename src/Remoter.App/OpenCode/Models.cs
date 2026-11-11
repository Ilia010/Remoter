using System.Text.Json;
using System.Text.Json.Serialization;

namespace Remoter.App.OpenCode;

public sealed class ServiceEndpoint
{
    public string Url { get; set; } = "";
    public string Password { get; set; } = "";
    public string Version { get; set; } = "";
    public int Pid { get; set; }
}

public sealed class ModelRef
{
    public string Id { get; set; } = "";
    public string ProviderID { get; set; } = "";
    public string? Variant { get; set; }

    public override string ToString() => $"{ProviderID}/{Id}";
}

public sealed class ModelInfo
{
    public string Id { get; set; } = "";
    public string ProviderID { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Family { get; set; }
    public string? Status { get; set; }
}

public sealed class SessionLocation
{
    public string Directory { get; set; } = "";
}

public sealed class SessionTime
{
    public double Created { get; set; }
    public double Updated { get; set; }
    public double? Busy { get; set; }
    public double? Idle { get; set; }
}

public sealed class SessionInfo
{
    public string Id { get; set; } = "";
    public string? ParentID { get; set; }
    public string? Title { get; set; }
    public string? Agent { get; set; }
    public ModelRef? Model { get; set; }
    public SessionLocation? Location { get; set; }
    public SessionTime? Time { get; set; }
    public string? Outcome { get; set; }
    public PermissionRule[]? Permissions { get; set; }
}

public sealed class PermissionRule
{
    public string Action { get; set; } = "";
    public string Resource { get; set; } = "";
    public string Effect { get; set; } = "";

    public PermissionRule() { }

    public PermissionRule(string action, string resource, string effect)
    {
        Action = action;
        Resource = resource;
        Effect = effect;
    }
}

public sealed class PermissionRequest
{
    public string Id { get; set; } = "";
    public string SessionID { get; set; } = "";
    public string Action { get; set; } = "";
    public string[] Resources { get; set; } = [];
    public string[]? Save { get; set; }
    public JsonElement? Metadata { get; set; }
    public string? Message { get; set; }
}

public sealed class OpenCodeEvent
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public JsonElement Data { get; set; }
}

public sealed class TextDelta
{
    public string SessionID { get; set; } = "";
    public string AssistantMessageID { get; set; } = "";
    public int Ordinal { get; set; }
    public string Delta { get; set; } = "";
}

public sealed class ToolCall
{
    public string SessionID { get; set; } = "";
    public string Id { get; set; } = "";
    public JsonElement Input { get; set; }
}

public sealed class SessionMessage
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Text { get; set; }
    public string? Finish { get; set; }
    public string? RawFinish { get; set; }
    public JsonElement? Error { get; set; }
    public SessionMessageContent[]? Content { get; set; }
}

public sealed class SessionMessageContent
{
    public string Type { get; set; } = "";
    public string? Text { get; set; }
}

public sealed class AgentInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "";
    public string? Description { get; set; }
}

public sealed class FileDiffInfo
{
    public string File { get; set; } = "";
    public string Patch { get; set; } = "";
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public string Status { get; set; } = "";
}

public sealed class OpenCodeException : Exception
{
    public int StatusCode { get; }

    public OpenCodeException(string message, int statusCode = 0, Exception? inner = null) : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static JsonElement Clone(JsonElement element) => element.Clone();
}
