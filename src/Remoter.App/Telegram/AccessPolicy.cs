namespace Remoter.App.Telegram;

/// <summary>
/// Белый список пользователей. Пустой список означает режим настройки:
/// бот отвечает только своим ID и никого не пускает к задачам.
/// </summary>
public static class AccessPolicy
{
    public static bool IsAllowed(long userId, IReadOnlyCollection<long> allowedUserIds) =>
        allowedUserIds.Count > 0 && allowedUserIds.Contains(userId);

    public static bool IsSetupMode(IReadOnlyCollection<long> allowedUserIds) =>
        allowedUserIds.Count == 0;
}
