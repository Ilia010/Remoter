namespace Remoter.App.OpenCode;

/// <summary>
/// Набор правил, который бот вешает на каждую сессию.
/// Чтение — свободно, любые изменения и команды — только с подтверждением в Telegram.
/// </summary>
public static class PermissionPolicy
{
    public static PermissionRule[] Default() =>
    [
        // Нельзя ставить "*" deny или "read" deny: OpenCode в этом случае падает
        // с ложной ошибкой "Free usage limit reached" ещё до вызова модели.
        // Чтение всегда разрешено, подтверждения нужны только на изменения.
        new("*", "*", "allow"),

        new("read", "*", "allow"),
        new("glob", "*", "allow"),
        new("grep", "*", "allow"),
        new("external_directory", "*", "allow"),

        new("edit", "*", "ask"),
        new("shell", "*", "ask"),
        new("webfetch", "*", "ask"),
        new("websearch", "*", "ask"),
        new("subagent", "*", "ask"),

        // Вопросы задаются обычным текстом: формы Telegram показать не может.
        new("question", "*", "deny"),

        // Секреты не читаем молча, но шаблон .env.example — не секрет.
        new("read", "*.env.example", "allow"),
        new("read", "*.env", "ask"),
        new("read", "*.env.*", "ask"),
    ];

    public static string Describe(string action) => action switch
    {
        "shell" => "🖥 Команда в терминале",
        "edit" => "✏️ Изменение файла",
        "webfetch" => "🌐 Запрос в интернет",
        "websearch" => "🔎 Поиск в интернете",
        "subagent" => "🤖 Запуск субагента",
        "read" => "📖 Чтение файла",
        "external_directory" => "📁 Доступ вне проекта",
        "skill" => "🧩 Навык",
        _ => $"⚙️ {action}",
    };
}
