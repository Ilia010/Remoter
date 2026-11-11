using System.Text;
using System.Text.Json;
using Telegram.Bot.Types;

namespace Remoter.App.Telegram;

/// <summary>
/// Одно живое сообщение в чате: статус, текущий инструмент и поток ответа.
/// </summary>
public sealed class ProgressTracker
{
    private readonly TelegramSender _sender;
    private readonly AppConfig _config;
    private readonly ChatId _chatId;
    private readonly DateTime _started = DateTime.UtcNow;

    private readonly object _sync = new();
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly StringBuilder _text = new();
    private readonly Dictionary<string, string> _toolNames = new();

    private string? _tool;
    private string _promptPreview = "";
    private int _messageId;
    private DateTime _lastUpdate = DateTime.MinValue;
    private int _updating;
    private int _dirty;
    private int _finished;

    public ProgressTracker(TelegramSender sender, AppConfig config, ChatId chatId)
    {
        _sender = sender;
        _config = config;
        _chatId = chatId;
    }

    public bool IsFinished => Volatile.Read(ref _finished) == 1;

    public async Task StartAsync(string promptPreview, CancellationToken ct)
    {
        _promptPreview = promptPreview;
        var message = await _sender.SendHtmlAsync(_chatId, ComposeRunning(), null, ct).ConfigureAwait(false);
        _messageId = message?.Id ?? 0;
        _lastUpdate = DateTime.UtcNow;
    }

    public void AppendText(string delta)
    {
        if (string.IsNullOrEmpty(delta) || IsFinished) return;
        lock (_sync) _text.Append(delta);
        ScheduleUpdate();
    }

    public void ToolStarted(string id, string name)
    {
        lock (_sync) _toolNames[id] = name;
        SetTool(FormatTool(name, null), force: true);
    }

    public void ToolCalled(string id, JsonElement input)
    {
        string name;
        lock (_sync) name = _toolNames.TryGetValue(id, out var stored) ? stored : "tool";
        SetTool(FormatTool(name, input), force: true);
    }

    public void Note(string text) => SetTool(TextUtil.EscapeHtml(text), force: true);

    private void SetTool(string tool, bool force)
    {
        if (IsFinished) return;
        _tool = tool;
        ScheduleUpdate(force);
    }

    public async Task FinishAsync(string? finalText, bool success, string? error, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _finished, 1) == 1) return;

        var body = !string.IsNullOrWhiteSpace(finalText) ? finalText!.Trim() : SnapshotText();
        if (string.IsNullOrWhiteSpace(body))
            body = success ? "✅ Готово." : "⚠️ Завершено без ответа.";

        if (!string.IsNullOrWhiteSpace(error))
            body += $"\n\n⚠️ {error.Trim()}";

        // Дожидаемся текущего обновления, чтобы не перезаписать финал.
        await _updateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_messageId > 0 && body.Length <= _config.MaxMessageChars)
            {
                await _sender.EditAsync(_chatId, _messageId, body, null, ct, force: true).ConfigureAwait(false);
            }
            else
            {
                if (_messageId > 0)
                    await _sender.DeleteAsync(_chatId, _messageId, ct).ConfigureAwait(false);

                await _sender.SendAsync(_chatId, body, null, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task FailAsync(string error, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _finished, 1) == 1) return;

        var body = SnapshotText();
        var message = string.IsNullOrWhiteSpace(body)
            ? $"❌ {error}"
            : $"{body}\n\n❌ {error}";

        await _updateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_messageId > 0 && message.Length <= _config.MaxMessageChars)
                await _sender.EditAsync(_chatId, _messageId, message, null, ct, force: true).ConfigureAwait(false);
            else
            {
                if (_messageId > 0)
                    await _sender.DeleteAsync(_chatId, _messageId, ct).ConfigureAwait(false);
                await _sender.SendAsync(_chatId, message, null, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private void ScheduleUpdate(bool force = false)
    {
        if (IsFinished) return;
        if (!force && (DateTime.UtcNow - _lastUpdate).TotalMilliseconds < _config.ProgressEditIntervalMs)
            return;

        Interlocked.Exchange(ref _dirty, 1);
        if (Interlocked.Exchange(ref _updating, 1) == 1) return;
        _ = Task.Run(UpdateLoopAsync);
    }

    private async Task UpdateLoopAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _dirty, 0) == 1 && !IsFinished)
            {
                await _updateGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (IsFinished) return;
                    _lastUpdate = DateTime.UtcNow;
                    await _sender.EditHtmlAsync(_chatId, _messageId, ComposeRunning(), null, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _updateGate.Release();
                }

                await Task.Delay(250).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось обновить прогресс: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _updating, 0);
        }
    }

    private string ComposeRunning()
    {
        var sb = new StringBuilder();
        sb.Append("<b>⏳ Работаю…</b>");
        sb.Append(" <i>").Append(Elapsed()).Append("</i>");

        if (!string.IsNullOrEmpty(_promptPreview))
            sb.Append("\n<i>").Append(TextUtil.EscapeHtml(_promptPreview)).Append("</i>");

        if (!string.IsNullOrEmpty(_tool))
            sb.Append('\n').Append(_tool);

        var tail = TextTail(1800);
        if (tail.Length > 0)
            sb.Append("\n\n").Append(TextUtil.ToTelegramHtml(tail));

        return sb.ToString();
    }

    private string Elapsed()
    {
        var span = DateTime.UtcNow - _started;
        if (span.TotalMinutes < 1) return $"{span.TotalSeconds:0} с";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} мин {span.Seconds} с";
        return $"{(int)span.TotalHours} ч {span.Minutes} мин";
    }

    private string TextTail(int maxChars)
    {
        string text;
        lock (_sync) text = _text.ToString();
        if (text.Length == 0) return "";

        if (text.Length <= maxChars)
            return text.TrimStart();

        var tail = text[^maxChars..];
        var newline = tail.IndexOf('\n');
        if (newline > 0) tail = tail[(newline + 1)..];
        return "…" + tail.TrimStart();
    }

    private string SnapshotText()
    {
        lock (_sync) return _text.ToString().Trim();
    }

    private static string FormatTool(string name, JsonElement? input)
    {
        var label = name.ToLowerInvariant() switch
        {
            "shell" or "bash" => "🔧 Команда",
            "edit" or "write" or "patch" => "✏️ Правка",
            "read" => "📖 Чтение",
            "grep" => "🔎 Поиск",
            "glob" => "📂 Поиск файлов",
            "webfetch" => "🌐 Запрос",
            "websearch" => "🔎 Веб-поиск",
            "task" or "subagent" => "🤖 Субагент",
            _ => $"🔧 {name}",
        };

        if (input is not { ValueKind: JsonValueKind.Object } obj) return TextUtil.EscapeHtml(label);

        string? detail = null;
        foreach (var key in new[] { "command", "filePath", "path", "pattern", "url", "query", "description" })
        {
            if (obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                detail = value.GetString();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(detail)) return TextUtil.EscapeHtml(label);

        detail = detail.Replace("\r", " ").Replace("\n", " ");
        return $"{TextUtil.EscapeHtml(label)}: <code>{TextUtil.EscapeHtml(TextUtil.Truncate(detail, 160))}</code>";
    }
}
