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
    public const string MenuPrefix = "mn:";
    public const string FilesPrefix = "fs:";
    public const string SessionPrefix = "ss:";
    public const string AgentPrefix = "ag:";

    public const string ShutdownYes = "sh:yes";
    public const string ShutdownNo = "sh:no";

    public static string Approval(string shortId, ApprovalDecision decision) =>
        $"{ApprovalPrefix}{shortId}:{Token(decision)}";

    public static string Model(int index) => $"{ModelPrefix}{index}";

    public static string Menu(string action) => $"{MenuPrefix}{action}";

    /// <summary>Навигация браузера: u — вверх, r — диски, h — домой, c — текущая, n — новая сессия, R — обновить, m — меню, pN — страница.</summary>
    public static string FilesNav(int generation, string code) => $"{FilesPrefix}{generation}:{code}";

    /// <summary>Кнопка элемента браузера (папка или файл) по индексу в списке.</summary>
    public static string FilesEntry(int generation, int index) => $"{FilesPrefix}{generation}:e{index}";

    public static string Session(int token, int index) => $"{SessionPrefix}{token}:{index}";

    public static string Agent(string id) => $"{AgentPrefix}{id}";

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

    public static bool TryParseMenu(string data, out string action)
    {
        action = "";
        if (!data.StartsWith(MenuPrefix, StringComparison.Ordinal)) return false;
        action = data[MenuPrefix.Length..];
        return action.Length > 0;
    }

    public static bool TryParseFiles(string data, out int generation, out string token)
    {
        generation = 0;
        token = "";
        if (!data.StartsWith(FilesPrefix, StringComparison.Ordinal)) return false;

        var parts = data.Split(':');
        if (parts.Length != 3 || parts[2].Length == 0 || !int.TryParse(parts[1], out generation))
            return false;

        token = parts[2];
        return true;
    }

    public static bool TryParseSession(string data, out int token, out int index)
    {
        token = 0;
        index = -1;
        if (!data.StartsWith(SessionPrefix, StringComparison.Ordinal)) return false;

        var parts = data.Split(':');
        return parts.Length == 3
               && int.TryParse(parts[1], out token)
               && int.TryParse(parts[2], out index);
    }

    public static bool TryParseAgent(string data, out string agent)
    {
        agent = "";
        if (!data.StartsWith(AgentPrefix, StringComparison.Ordinal)) return false;
        agent = data[AgentPrefix.Length..];
        return agent.Length > 0;
    }

    /// <summary>Значение, которое ждёт OpenCode в поле decision.</summary>
    public static string Token(ApprovalDecision decision) => decision switch
    {
        ApprovalDecision.Always => "always",
        ApprovalDecision.Reject => "reject",
        _ => "once",
    };
}
