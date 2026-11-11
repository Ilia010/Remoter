namespace Remoter.App.Telegram;

public enum ApprovalDecision
{
    Once,
    Always,
    Reject,
}

/// <summary>
/// Формат callback_data кнопок. Telegram ограничивает значение 64 байтами,
/// поэтому префиксы короткие, а вместо GUID используем локальный shortId.
/// </summary>
public static class CallbackData
{
    public const string ApprovalPrefix = "ap:";
    public const string ModelPrefix = "md:";
    public const string ShutdownPrefix = "sh:";

    public const string ShutdownYes = "sh:yes";
    public const string ShutdownNo = "sh:no";

    public static string Approval(string shortId, ApprovalDecision decision) =>
        $"{ApprovalPrefix}{shortId}:{Token(decision)}";

    public static string Model(int index) => $"{ModelPrefix}{index}";

    public static bool TryParseApproval(string data, out string shortId, out ApprovalDecision decision)
    {
        shortId = "";
        decision = ApprovalDecision.Once;

        if (!data.StartsWith(ApprovalPrefix, StringComparison.Ordinal)) return false;

        var parts = data.Split(':');
        if (parts.Length != 3 || parts[1].Length == 0) return false;

        shortId = parts[1];
        decision = parts[2] switch
        {
            "always" => ApprovalDecision.Always,
            "reject" => ApprovalDecision.Reject,
            _ => ApprovalDecision.Once,
        };

        return true;
    }

    public static bool TryParseModel(string data, out int index)
    {
        index = -1;
        return data.StartsWith(ModelPrefix, StringComparison.Ordinal)
               && int.TryParse(data.AsSpan(ModelPrefix.Length), out index);
    }

    /// <summary>Значение, которое ждёт OpenCode в поле decision.</summary>
    public static string Token(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Always => "always",
        ApprovalDecision.Reject => "reject",
        _ => "once",
    };
}
