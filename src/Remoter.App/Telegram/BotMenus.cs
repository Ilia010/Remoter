using System.Text;
using Remoter.App.OpenCode;
using Remoter.App.Storage;
using Telegram.Bot.Types.ReplyMarkups;

namespace Remoter.App.Telegram;

/// <summary>Кнопки и тексты inline-интерфейса.</summary>
public static class BotMenus
{
    public static class Actions
    {
        public const string Main = "main";
        public const string Files = "files";
        public const string New = "new";
        public const string Sessions = "sessions";
        public const string Model = "model";
        public const string Agent = "agent";
        public const string Status = "status";
        public const string Screen = "screen";
        public const string Diff = "diff";
        public const string Cancel = "cancel";
        public const string Restart = "restart";
        public const string Help = "help";
        public const string Shutdown = "shutdown";
    }

    /// <summary>Подписи кнопок постоянной клавиатуры внизу чата.</summary>
    public static class Reply
    {
        public const string Files = "📂 Файлы";
        public const string New = "🆕 Новая";
        public const string Sessions = "🗂 Сессии";
        public const string Model = "🧠 Модель";
        public const string Agent = "🤖 Агент";
        public const string Status = "📊 Статус";
        public const string Screen = "🖥 Скриншот";
        public const string Diff = "📝 Diff";
        public const string Cancel = "⏹ Отмена";
        public const string Restart = "🔄 Рестарт";
        public const string Help = "❓ Помощь";
        public const string Menu = "⚙️ Меню";
    }

    /// <summary>
    /// Постоянная клавиатура под полем ввода. Кнопки приходят как обычный текст,
    /// поэтому <see cref="TryParseReply"/> превращает их в действия меню.
    /// </summary>
    public static ReplyKeyboardMarkup ReplyMenu()
    {
        var rows = new KeyboardButton[][]
        {
            [new KeyboardButton(Reply.Files), new KeyboardButton(Reply.New), new KeyboardButton(Reply.Sessions)],
            [new KeyboardButton(Reply.Model), new KeyboardButton(Reply.Agent), new KeyboardButton(Reply.Status)],
            [new KeyboardButton(Reply.Screen), new KeyboardButton(Reply.Diff), new KeyboardButton(Reply.Cancel)],
            [new KeyboardButton(Reply.Restart), new KeyboardButton(Reply.Help), new KeyboardButton(Reply.Menu)],
        };

        return new ReplyKeyboardMarkup(rows)
        {
            ResizeKeyboard = true,
            IsPersistent = true,
            InputFieldPlaceholder = "Напиши задачу…",
        };
    }

    public static bool TryParseReply(string text, out string action)
    {
        action = text.Trim() switch
        {
            Reply.Files => Actions.Files,
            Reply.New => Actions.New,
            Reply.Sessions => Actions.Sessions,
            Reply.Model => Actions.Model,
            Reply.Agent => Actions.Agent,
            Reply.Status => Actions.Status,
            Reply.Screen => Actions.Screen,
            Reply.Diff => Actions.Diff,
            Reply.Cancel => Actions.Cancel,
            Reply.Restart => Actions.Restart,
            Reply.Help => Actions.Help,
            Reply.Menu => Actions.Main,
            _ => "",
        };

        return action.Length > 0;
    }

    public static InlineKeyboardMarkup MainMenu() => new(
    [
        [Button("📂 Файлы", CallbackData.Menu(Actions.Files)), Button("🆕 Новая сессия", CallbackData.Menu(Actions.New))],
        [Button("🗂 Сессии", CallbackData.Menu(Actions.Sessions)), Button("🧠 Модель", CallbackData.Menu(Actions.Model))],
        [Button("🤖 Агент", CallbackData.Menu(Actions.Agent)), Button("📊 Статус", CallbackData.Menu(Actions.Status))],
        [Button("🖥 Скриншот", CallbackData.Menu(Actions.Screen)), Button("📝 Diff", CallbackData.Menu(Actions.Diff))],
        [Button("⏹ Отмена", CallbackData.Menu(Actions.Cancel)), Button("🔄 Рестарт", CallbackData.Menu(Actions.Restart))],
        [Button("❓ Помощь", CallbackData.Menu(Actions.Help)), Button("⏻ Выключить", CallbackData.Menu(Actions.Shutdown))],
    ]);

    public static InlineKeyboardButton[] MenuRow() =>
        [Button("☰ Меню", CallbackData.Menu(Actions.Main))];

    public static InlineKeyboardMarkup MenuOnly() => new([MenuRow()]);

    public static InlineKeyboardMarkup Agents(string current) => new(
    [
        [Button((current == "build" ? "✅ " : "") + "🧠 build — выполнять", CallbackData.Agent("build"))],
        [Button((current == "plan" ? "✅ " : "") + "📝 plan — только план", CallbackData.Agent("plan"))],
        MenuRow(),
    ]);

    public static InlineKeyboardMarkup ShutdownConfirm() => new(
    [
        [
            Button("Да, выключить", CallbackData.ShutdownYes),
            Button("Отмена", CallbackData.ShutdownNo),
        ],
    ]);

    public static string MainText(ChatState chat, AppConfig config, bool busy, int queued)
    {
        var sb = new StringBuilder("⚙️ <b>Меню Remoter</b>\n\n");
        sb.Append("📁 <code>").Append(TextUtil.EscapeHtml(chat.Directory ?? config.DefaultDirectory)).Append("</code>\n");
        sb.Append("🧠 <code>")
          .Append(TextUtil.EscapeHtml((chat.ModelProvider ?? config.Model.ProviderID) + "/" + (chat.ModelID ?? config.Model.ID)))
          .Append("</code>\n");
        sb.Append("🤖 <code>").Append(TextUtil.EscapeHtml(chat.Agent ?? config.Agent)).Append("</code>\n\n");

        sb.Append(busy ? "⚙️ Сейчас: <b>выполняет задачу</b>" : "💤 Сейчас: свободен");
        if (queued > 0) sb.Append($"\n📥 В очереди: {queued}");
        sb.Append("\n\nВыбери действие.");

        return sb.ToString();
    }

    public static string BrowserText(BrowserPage page, string? currentDirectory)
    {
        var sb = new StringBuilder();

        if (page.Drives)
        {
            sb.Append("💾 <b>Компьютер</b>\n\nВыбери диск.");
            return sb.ToString();
        }

        sb.Append("<code>").Append(TextUtil.EscapeHtml(page.Path ?? "")).Append("</code>");
        sb.Append("\n\n📁 ").Append(page.DirectoryCount).Append(" · 📄 ").Append(page.FileCount);
        if (page.Truncated) sb.Append("\n<i>Показаны не все элементы.</i>");

        if (page.PageCount > 1)
            sb.Append("\n<i>Страница ").Append(page.Page + 1).Append(" из ").Append(page.PageCount).Append("</i>");

        if (SameDirectory(page.Path, currentDirectory))
            sb.Append("\n\n📌 Это текущая рабочая папка.");

        sb.Append("\n\nНажми на папку, чтобы открыть, на файл — чтобы получить его.");

        return sb.ToString();
    }

    public static InlineKeyboardMarkup Browser(BrowserPage page, string? currentDirectory)
    {
        var rows = new List<InlineKeyboardButton[]>();
        var start = Math.Clamp(page.Page, 0, page.PageCount - 1) * BrowserPage.PageSize;
        var entries = page.CurrentEntries;

        for (var i = 0; i < entries.Count; i += 2)
        {
            var row = new List<InlineKeyboardButton>(2);
            for (var j = 0; j < 2 && i + j < entries.Count; j++)
            {
                var entry = entries[i + j];
                row.Add(Button(
                    $"{entry.Icon} {TextUtil.Truncate(entry.Name, 26)}",
                    CallbackData.FilesEntry(page.Generation, start + i + j)));
            }
            rows.Add(row.ToArray());
        }

        if (page.PageCount > 1)
        {
            rows.Add(
            [
                Button("⬅️", CallbackData.FilesNav(page.Generation, $"p{page.Page - 1}")),
                Button("➡️", CallbackData.FilesNav(page.Generation, $"p{page.Page + 1}")),
            ]);
        }

        if (!page.Drives)
        {
            rows.Add(
            [
                Button("⬆️ Наверх", CallbackData.FilesNav(page.Generation, "u")),
                Button("💾 Диски", CallbackData.FilesNav(page.Generation, "r")),
            ]);
        }

        rows.Add(
        [
            Button("🏠 Домой", CallbackData.FilesNav(page.Generation, "h")),
            Button("📁 Текущая", CallbackData.FilesNav(page.Generation, "c")),
        ]);

        if (!page.Drives)
            rows.Add([Button("🆕 Новая сессия здесь", CallbackData.FilesNav(page.Generation, "n"))]);

        rows.Add(
        [
            Button("🔄 Обновить", CallbackData.FilesNav(page.Generation, "R")),
            Button("☰ Меню", CallbackData.FilesNav(page.Generation, "m")),
        ]);

        return new InlineKeyboardMarkup(rows);
    }

    public static InlineKeyboardMarkup Models(IReadOnlyList<ModelInfo> models) => new(
        models
            .Select((model, index) => new[]
            {
                Button(
                    $"{index + 1}. {TextUtil.Truncate(string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name, 40)}",
                    CallbackData.Model(index)),
            })
            .Append(MenuRow())
            .ToArray());

    private static InlineKeyboardButton Button(string text, string data) =>
        InlineKeyboardButton.WithCallbackData(text, data);

    private static bool SameDirectory(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(left),
                Path.TrimEndingDirectorySeparator(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
