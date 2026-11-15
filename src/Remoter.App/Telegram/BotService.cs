using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Remoter.App.Features;
using Remoter.App.OpenCode;
using Remoter.App.Storage;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Message = Telegram.Bot.Types.Message;

namespace Remoter.App.Telegram;

public sealed class ChatRuntime
{
    private readonly Dictionary<int, List<SessionInfo>> _sessionLists = new();
    private readonly object _sessionSync = new();

    // Токены живут только в памяти; случайный старт не даёт старым кнопкам
    // после перезапуска совпасть с новым списком сессий.
    private int _sessionToken = Random.Shared.Next(1, 1_000_000);

    public ChatRuntime(long chatId) => ChatId = chatId;

    public long ChatId { get; }
    public long UserId { get; set; }
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public object QueueSync { get; } = new();
    public Queue<string> Queue { get; } = new();
    public ProgressTracker? Progress;
    public int Busy;
    public string? SessionId;
    public List<SessionInfo>? LastSessions;
    public List<ModelInfo>? LastModels;
    public FileBrowser Browser { get; } = new();

    public int StoreSessions(List<SessionInfo> sessions)
    {
        lock (_sessionSync)
        {
            var token = ++_sessionToken;
            _sessionLists[token] = sessions;
            while (_sessionLists.Count > 5)
                _sessionLists.Remove(_sessionLists.Keys.Min());
            return token;
        }
    }

    public bool TryGetSessions(int token, out List<SessionInfo> sessions)
    {
        lock (_sessionSync) return _sessionLists.TryGetValue(token, out sessions!);
    }
}

public sealed class BotService
{
    private readonly AppConfig _config;
    private readonly StateStore _state;
    private readonly OpenCodeClient _client;
    private readonly Action _shutdown;

    private readonly ConcurrentDictionary<long, ChatRuntime> _chats = new();
    private readonly ConcurrentDictionary<string, long> _sessionChats = new();
    private readonly ConcurrentDictionary<string, byte> _seenPermissions = new();
    private readonly ConcurrentDictionary<long, ChatInbox> _inboxes = new();

    private ITelegramBotClient? _bot;
    private TelegramSender? _sender;
    private ApprovalService? _approvals;
    private User? _me;

    public BotService(AppConfig config, StateStore state, OpenCodeClient client, Action shutdown)
    {
        _config = config;
        _state = state;
        _client = client;
        _shutdown = shutdown;
    }

    public string? BotUsername => _me?.Username;

    public async Task RunAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.TelegramBotToken))
            throw new InvalidOperationException(
                "Не задан TELEGRAM_BOT_TOKEN. Добавь токен в .env рядом с Remoter.exe и перезапусти.");

        _bot = new TelegramBotClient(_config.TelegramBotToken);
        _sender = new TelegramSender(_bot, _config);
        _approvals = new ApprovalService();

        _me = await _bot.GetMe(ct).ConfigureAwait(false);
        Log.Info($"Бот @{_me.Username} (id {_me.Id}) запущен");

        try
        {
            await _bot.DeleteWebhook(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"DeleteWebhook: {ex.Message}");
        }

        await RegisterCommandsAsync(ct).ConfigureAwait(false);

        _ = Task.Run(() => EventLoopAsync(ct), ct);
        _ = Task.Run(() => StartupNoticeAsync(ct), ct);
        _ = Task.Run(() => WarmUpAsync(ct), ct);

        var receiver = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery],
        };

        _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiver, ct);

        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // штатная остановка
        }
    }

    private async Task StartupNoticeAsync(CancellationToken ct)
    {
        if (_config.AllowedUserIds.Length == 0) return;
        try
        {
            await Task.Delay(1500, ct).ConfigureAwait(false);
            var text = "🟢 <b>Remoter запущен</b>\n" +
                       $"📁 {TextUtil.EscapeHtml(_config.DefaultDirectory)}\n" +
                       $"🧠 {TextUtil.EscapeHtml(_config.Model.ProviderID + "/" + _config.Model.ID)}\n" +
                       (_config.AutoApprove ? "🔓 Подтверждения отключены\n" : "") +
                       "\nПиши задачу обычным текстом или выбирай действия кнопками.";
            await _sender!
                .SendHtmlAsync(Cid(_config.AllowedUserIds[0]), text, BotMenus.ReplyMenu(), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось отправить приветствие: {ex.Message}");
        }
    }

    private async Task RegisterCommandsAsync(CancellationToken ct)
    {
        try
        {
            await _bot!.SetMyCommands(
            [
                new BotCommand { Command = "start", Description = "Приветствие и меню" },
                new BotCommand { Command = "menu", Description = "Меню с кнопками" },
                new BotCommand { Command = "new", Description = "Новая сессия и выбор папки" },
                new BotCommand { Command = "ls", Description = "Файлы и папки" },
                new BotCommand { Command = "sessions", Description = "Последние сессии" },
                new BotCommand { Command = "use", Description = "Переключить сессию по номеру" },
                new BotCommand { Command = "model", Description = "Выбрать модель" },
                new BotCommand { Command = "agent", Description = "Режим агента" },
                new BotCommand { Command = "status", Description = "Статус и очередь" },
                new BotCommand { Command = "cancel", Description = "Прервать задачу" },
                new BotCommand { Command = "screen", Description = "Скриншот экрана" },
                new BotCommand { Command = "file", Description = "Прислать файл по пути" },
                new BotCommand { Command = "diff", Description = "Изменения сессии" },
                new BotCommand { Command = "restart", Description = "Перезапустить OpenCode" },
                new BotCommand { Command = "id", Description = "Мой Telegram ID" },
                new BotCommand { Command = "help", Description = "Справка" },
            ], cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось обновить список команд: {ex.Message}");
        }
    }

    /// <summary>Заранее поднимаем OpenCode, чтобы первый запрос не ждал холодного старта.</summary>
    private async Task WarmUpAsync(CancellationToken ct)
    {
        try
        {
            await _client.EnsureServiceAsync(ct).ConfigureAwait(false);
            Log.Info("OpenCode прогрет и готов принимать запросы");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Прогрев OpenCode не удался: {ex.Message}");
        }
    }

    // ---------- updates ----------

    /// <summary>
    /// Колбэк Telegram не должен ждать выполнения команды: иначе одна долгая
    /// операция блокирует приём всех следующих сообщений. Раскладываем апдейты
    /// по очередям чатов, а сами очереди разбираем параллельно.
    /// </summary>
    private Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id ?? 0;
        if (chatId == 0) return Task.CompletedTask;

        var inbox = _inboxes.GetOrAdd(chatId, _ => new ChatInbox());
        if (Interlocked.Exchange(ref inbox.Started, 1) == 0)
            _ = Task.Run(() => ConsumeUpdatesAsync(inbox, ct), CancellationToken.None);

        inbox.Writer.TryWrite(update);
        return Task.CompletedTask;
    }

    private async Task ConsumeUpdatesAsync(ChatInbox inbox, CancellationToken ct)
    {
        try
        {
            await foreach (var update in inbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await ProcessUpdateAsync(update, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    await ReportUpdateErrorAsync(update, ex, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            // Если цикл завершился не из-за остановки приложения, следующий апдейт
            // поднимет нового потребителя и очередь не зависнет.
            Interlocked.Exchange(ref inbox.Started, 0);
        }
    }

    private async Task ProcessUpdateAsync(Update update, CancellationToken ct)
    {
        if (update.Type == UpdateType.Message && update.Message is { } message)
            await OnMessageAsync(message, ct).ConfigureAwait(false);
        else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } callback)
            await OnCallbackAsync(callback, ct).ConfigureAwait(false);
    }

    private async Task ReportUpdateErrorAsync(Update update, Exception ex, CancellationToken ct)
    {
        Log.Error($"Ошибка обработки {update.Type}", ex);
        var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id;
        if (chatId is null || _sender is null) return;

        try
        {
            await _sender.SendPlainAsync(Cid(chatId.Value), $"❌ {ex.Message}", null, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // ничего
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        Log.Error("Ошибка polling", ex);
        return Task.CompletedTask;
    }

    private bool IsAllowed(long userId) =>
        AccessPolicy.IsAllowed(userId, _config.AllowedUserIds);

    private async Task OnMessageAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var userId = message.From?.Id ?? 0;

        if (!IsAllowed(userId))
        {
            if (_config.AllowedUserIds.Length == 0 && _sender is not null)
            {
                await _sender.SendPlainAsync(
                    Cid(chatId),
                    $"Ваш Telegram ID: {userId}\n\n" +
                    "Бот ещё не настроен. Добавьте в .env строку:\n" +
                    $"ALLOWED_USER_IDS={userId}\n\n" +
                    "и перезапустите Remoter.",
                    null,
                    ct).ConfigureAwait(false);
            }
            else
            {
                Log.Warn($"Игнорирую сообщение от пользователя {userId}");
            }
            return;
        }

        var text = message.Text ?? message.Caption;
        if (string.IsNullOrWhiteSpace(text))
        {
            await _sender!.SendPlainAsync(Cid(chatId), "Поддерживается только текст.", null, ct)
                .ConfigureAwait(false);
            return;
        }

        if (message.Chat.Type is ChatType.Group or ChatType.Supergroup)
        {
            var mentioned = IsMentioned(text, message);
            if (!mentioned) return;
            text = StripMention(text);
        }

        // «Печатает…» появляется сразу, не дожидаясь ответа команды или OpenCode.
        _ = _sender!.SendTypingAsync(Cid(chatId), ChatAction.Typing, ct);

        var runtime = GetRuntime(chatId);
        runtime.UserId = userId;

        if (text.StartsWith('/'))
        {
            await HandleCommandAsync(runtime, text.Trim(), ct).ConfigureAwait(false);
            return;
        }

        // Кнопки постоянной клавиатуры приходят обычным текстом.
        if (BotMenus.TryParseReply(text, out var menuAction))
        {
            await ExecuteMenuActionAsync(runtime, menuAction, 0, ct).ConfigureAwait(false);
            return;
        }

        await HandlePromptAsync(runtime, text.Trim(), ct).ConfigureAwait(false);
    }

    private bool IsMentioned(string text, Message message)
    {
        var username = _me?.Username;
        if (string.IsNullOrEmpty(username)) return true;
        if (text.Contains("@" + username, StringComparison.OrdinalIgnoreCase)) return true;
        return message.ReplyToMessage?.From?.Id == _me!.Id;
    }

    private string StripMention(string text)
    {
        var username = _me?.Username;
        if (string.IsNullOrEmpty(username)) return text;
        return text.Replace("@" + username, "", StringComparison.OrdinalIgnoreCase).Trim();
    }

    // ---------- prompt ----------

    private async Task HandlePromptAsync(ChatRuntime runtime, string text, CancellationToken ct)
    {
        await runtime.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref runtime.Busy) == 1)
            {
                int position;
                lock (runtime.QueueSync)
                {
                    runtime.Queue.Enqueue(text);
                    position = runtime.Queue.Count;
                }

                await _sender!.SendHtmlAsync(
                    Cid(runtime.ChatId),
                    $"📥 Задача в очереди (позиция {position}).",
                    null,
                    ct).ConfigureAwait(false);
                return;
            }

            var sessionId = await EnsureSessionAsync(runtime, ct).ConfigureAwait(false);

            var progress = new ProgressTracker(_sender!, _config, Cid(runtime.ChatId));
            runtime.Progress = progress;
            Volatile.Write(ref runtime.Busy, 1);

            await progress.StartAsync(Preview(text), ct).ConfigureAwait(false);

            try
            {
                await _client.PromptAsync(sessionId, text, ct).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Exchange(ref runtime.Busy, 0);
                runtime.Progress = null;
                await progress.FailAsync("Не удалось отправить задачу в OpenCode.", ct).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            runtime.Gate.Release();
        }
    }

    private async Task StartNextQueuedAsync(ChatRuntime runtime, CancellationToken ct)
    {
        string? next;
        lock (runtime.QueueSync)
        {
            next = runtime.Queue.Count > 0 ? runtime.Queue.Dequeue() : null;
        }

        if (next is not null)
            await HandlePromptAsync(runtime, next, ct).ConfigureAwait(false);
    }

    private static string Preview(string text)
    {
        var oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return TextUtil.Truncate(oneLine, 120);
    }

    private async Task<string> EnsureSessionAsync(ChatRuntime runtime, CancellationToken ct)
    {
        var chat = _state.Get(runtime.ChatId);

        if (!string.IsNullOrWhiteSpace(chat.SessionID))
        {
            try
            {
                var existing = await _client.GetSessionAsync(chat.SessionID, ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    // Настройки могли измениться (например, включили AUTO_APPROVE) —
                    // применяем актуальные права к уже существующей сессии.
                    var desired = PermissionPolicy.For(_config.AutoApprove);
                    if (!PermissionPolicy.AreEqual(existing.Permissions, desired))
                    {
                        try
                        {
                            await _client.UpdatePermissionsAsync(existing.Id, desired, ct).ConfigureAwait(false);
                            Log.Info($"Права сессии {existing.Id} обновлены: {desired.Length} правил");
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"Не удалось обновить права сессии {existing.Id}: {ex.Message}");
                        }
                    }

                    runtime.SessionId = existing.Id;
                    MapSession(existing.Id, runtime.ChatId);
                    return existing.Id;
                }
            }
            catch (OpenCodeException ex) when (ex.StatusCode == 404)
            {
                Log.Warn($"Сессия {chat.SessionID} больше не существует, создаю новую.");
            }
        }

        var directory = chat.Directory ?? _config.DefaultDirectory;
        if (!Directory.Exists(directory)) directory = _config.DefaultDirectory;
        if (!Directory.Exists(directory))
            throw new InvalidOperationException($"Рабочая папка не существует: {directory}");

        var model = new ModelRef
        {
            Id = chat.ModelID ?? _config.Model.ID,
            ProviderID = chat.ModelProvider ?? _config.Model.ProviderID,
        };

        var agent = chat.Agent ?? _config.Agent;
        var session = await _client
            .CreateSessionAsync(directory, PermissionPolicy.For(_config.AutoApprove), agent, model, "Telegram", ct)
            .ConfigureAwait(false);

        _state.Update(runtime.ChatId, s =>
        {
            s.SessionID = session.Id;
            s.Directory = directory;
            s.ModelID = model.Id;
            s.ModelProvider = model.ProviderID;
            s.Agent = agent;
        });

        runtime.SessionId = session.Id;
        MapSession(session.Id, runtime.ChatId);
        Log.Info($"Создана сессия {session.Id} в {directory}");

        return session.Id;
    }

    private void MapSession(string sessionId, long chatId)
    {
        _sessionChats[sessionId] = chatId;
        _chats.TryGetValue(chatId, out var runtime);
        if (runtime is not null) runtime.SessionId = sessionId;
    }

    // ---------- events ----------

    private async Task EventLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var ev in _client.SubscribeEventsAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        await DispatchAsync(ev, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Ошибка события {ev.Type}", ex);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Поток событий упал, перезапускаю через 3 с", ex);
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchAsync(OpenCodeEvent ev, CancellationToken ct)
    {
        if (ev.Type == "server.connected")
        {
            await ResyncPermissionsAsync(ct).ConfigureAwait(false);
            return;
        }

        var sessionId = TryGetString(ev.Data, "sessionID");
        if (sessionId is null) return;
        if (!_sessionChats.TryGetValue(sessionId, out var chatId)) return;

        var runtime = GetRuntime(chatId);

        switch (ev.Type)
        {
            case "session.text.delta":
            {
                var delta = Json.Deserialize<TextDelta>(ev.Data.GetRawText());
                if (delta is not null) runtime.Progress?.AppendText(delta.Delta);
                break;
            }
            case "session.tool.input.started":
            {
                var id = TryGetString(ev.Data, "id");
                var name = TryGetString(ev.Data, "name");
                if (id is not null && name is not null) runtime.Progress?.ToolStarted(id, name);
                break;
            }
            case "session.tool.called":
            {
                var id = TryGetString(ev.Data, "id");
                if (id is not null && ev.Data.TryGetProperty("input", out var input))
                    runtime.Progress?.ToolCalled(id, input);
                break;
            }
            case "session.execution.succeeded":
                await CompleteAsync(runtime, true, null, ct).ConfigureAwait(false);
                break;
            case "session.execution.failed":
                await CompleteAsync(runtime, false, "Агент завершил работу с ошибкой.", ct).ConfigureAwait(false);
                break;
            case "session.execution.interrupted":
                await CompleteAsync(runtime, false, "Выполнение прервано.", ct).ConfigureAwait(false);
                break;
            case "session.status":
                if (ev.Data.TryGetProperty("status", out var status)
                    && status.TryGetProperty("type", out var statusType)
                    && statusType.GetString() == "retry")
                {
                    runtime.Progress?.Note("🔁 Повтор запроса к модели…");
                }
                break;
            case "permission.asked":
            {
                var request = Json.Deserialize<PermissionRequest>(ev.Data.GetRawText());
                if (request is not null)
                    await OnPermissionAskedAsync(chatId, request, ct).ConfigureAwait(false);
                break;
            }
        }
    }

    private async Task CompleteAsync(ChatRuntime runtime, bool success, string? error, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref runtime.Busy, 0) == 0) return;

        var progress = runtime.Progress;
        runtime.Progress = null;

        if (progress is not null)
        {
            string? text = null;
            try
            {
                if (runtime.SessionId is not null)
                    text = await _client.GetLastAssistantTextAsync(runtime.SessionId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось получить финальный текст: {ex.Message}");
            }

            if (!success && runtime.SessionId is not null)
            {
                try
                {
                    var detail = await _client.GetLastAssistantErrorAsync(runtime.SessionId, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(detail)) error = detail;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Не удалось получить причину ошибки: {ex.Message}");
                }
            }

            await progress.FinishAsync(text, success, error, ct).ConfigureAwait(false);
        }

        await StartNextQueuedAsync(runtime, ct).ConfigureAwait(false);
    }

    private async Task ResyncPermissionsAsync(CancellationToken ct)
    {
        foreach (var (sessionId, chatId) in _sessionChats.ToArray())
        {
            try
            {
                var pending = await _client.ListPermissionsAsync(sessionId, ct).ConfigureAwait(false);
                foreach (var request in pending)
                {
                    if (_approvals!.Contains(request.Id) || _seenPermissions.ContainsKey(request.Id)) continue;
                    await OnPermissionAskedAsync(chatId, request, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось синхронизировать разрешения для {sessionId}: {ex.Message}");
            }
        }
    }

    // ---------- approvals ----------

    private async Task OnPermissionAskedAsync(long chatId, PermissionRequest request, CancellationToken ct)
    {
        if (!_seenPermissions.TryAdd(request.Id, 0)) return;
        if (_seenPermissions.Count > 2000) _seenPermissions.Clear();

        var pending = _approvals!.Register(chatId, request);
        var text = BuildPermissionText(request);

        var keyboard = new InlineKeyboardMarkup(
        [
            [
                InlineKeyboardButton.WithCallbackData("✅ Разрешить", CallbackData.Approval(pending.ShortId, ApprovalDecision.Once)),
                InlineKeyboardButton.WithCallbackData("🔁 Всегда", CallbackData.Approval(pending.ShortId, ApprovalDecision.Always)),
            ],
            [InlineKeyboardButton.WithCallbackData("❌ Отклонить", CallbackData.Approval(pending.ShortId, ApprovalDecision.Reject))],
        ]);

        var message = await _sender!.SendHtmlAsync(Cid(chatId), text, keyboard, ct).ConfigureAwait(false);
        pending.MessageId = message?.Id ?? 0;

        _ = Task.Run(() => ApprovalTimeoutAsync(pending), CancellationToken.None);
    }

    private async Task ApprovalTimeoutAsync(PendingApproval pending)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_config.ApprovalTimeoutSeconds)).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (!_approvals!.TryRemove(pending.ShortId, out var removed)) return;

        try
        {
            await _client.ReplyPermissionAsync(
                removed.SessionId, removed.RequestId, "reject", "Таймаут подтверждения", CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось отклонить по таймауту: {ex.Message}");
        }

        await _sender!.EditHtmlAsync(
            Cid(removed.ChatId),
            removed.MessageId,
            "⌛ <b>Время вышло — действие отклонено.</b>",
            null,
            CancellationToken.None,
            force: true).ConfigureAwait(false);
    }

    private string BuildPermissionText(PermissionRequest request)
    {
        var sb = new StringBuilder();
        sb.Append("<b>").Append(PermissionPolicy.Describe(request.Action)).Append("</b>\n");

        if (!string.IsNullOrWhiteSpace(request.Message))
            sb.Append(TextUtil.EscapeHtml(request.Message)).Append('\n');

        foreach (var resource in request.Resources.Take(8))
        {
            var value = TextUtil.EscapeHtml(TextUtil.Truncate(resource, 1500));
            sb.Append(request.Action is "shell" or "edit" ? "\n<pre>" : "\n<code>")
              .Append(value)
              .Append(request.Action is "shell" or "edit" ? "</pre>" : "</code>");
        }

        if (request.Resources.Length > 8)
            sb.Append($"\n… и ещё {request.Resources.Length - 8}");

        var diff = ExtractDiff(request.Metadata);
        if (!string.IsNullOrWhiteSpace(diff))
        {
            sb.Append("\n<pre>")
              .Append(TextUtil.EscapeHtml(TextUtil.Truncate(diff, 2600)))
              .Append("</pre>");
        }

        var minutes = Math.Max(1, _config.ApprovalTimeoutSeconds / 60);
        sb.Append($"\n\n<i>Подтверждение действует {minutes} мин.</i>");
        return sb.ToString();
    }

    private static string? ExtractDiff(JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } obj) return null;

        foreach (var key in new[] { "diff", "patch", "changes", "preview" })
        {
            if (obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    // ---------- callbacks ----------

    private async Task OnCallbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        var userId = callback.From.Id;
        if (!IsAllowed(userId))
        {
            await _sender!.AnswerCallbackAsync(callback.Id, "Нет доступа", true, ct).ConfigureAwait(false);
            return;
        }

        var data = callback.Data ?? "";
        var chatId = callback.Message?.Chat.Id ?? 0;
        if (chatId == 0) return;

        if (CallbackData.TryParseApproval(data, out var shortId, out var decision))
        {
            await HandleApprovalCallbackAsync(callback, shortId, decision, chatId, ct).ConfigureAwait(false);
            return;
        }

        if (CallbackData.TryParseModel(data, out var modelIndex))
        {
            await HandleModelCallbackAsync(callback, modelIndex, chatId, ct).ConfigureAwait(false);
            return;
        }

        if (data.StartsWith(CallbackData.ShutdownPrefix, StringComparison.Ordinal))
        {
            if (data == CallbackData.ShutdownYes)
            {
                await _sender!.AnswerCallbackAsync(callback.Id, "Выключаюсь", false, ct).ConfigureAwait(false);
                await _sender.SendHtmlAsync(Cid(chatId), "👋 Remoter выключен.", null, ct).ConfigureAwait(false);
                _shutdown();
            }
            else
            {
                await _sender!.AnswerCallbackAsync(callback.Id, "Отменено", false, ct).ConfigureAwait(false);
                await _sender.EditHtmlAsync(Cid(chatId), callback.Message!.Id, "Отменено.", null, ct, force: true)
                    .ConfigureAwait(false);
            }
            return;
        }

        if (CallbackData.TryParseMenu(data, out var menuAction))
        {
            await HandleMenuCallbackAsync(callback, menuAction, chatId, ct).ConfigureAwait(false);
            return;
        }

        if (CallbackData.TryParseFiles(data, out var generation, out var filesToken))
        {
            await HandleFilesCallbackAsync(callback, generation, filesToken, chatId, ct).ConfigureAwait(false);
            return;
        }

        if (CallbackData.TryParseSession(data, out var sessionToken, out var sessionIndex))
        {
            await HandleSessionCallbackAsync(callback, sessionToken, sessionIndex, chatId, ct).ConfigureAwait(false);
            return;
        }

        if (CallbackData.TryParseAgent(data, out var agent))
        {
            await HandleAgentCallbackAsync(callback, agent, chatId, ct).ConfigureAwait(false);
            return;
        }

        await _sender!.AnswerCallbackAsync(callback.Id, "Кнопка устарела", true, ct).ConfigureAwait(false);
    }

    private async Task HandleMenuCallbackAsync(
        CallbackQuery callback, string action, long chatId, CancellationToken ct)
    {
        var runtime = GetRuntime(chatId);
        var messageId = callback.Message?.Id ?? 0;

        var title = MenuActionTitle(action);
        if (title is null)
        {
            await _sender!.AnswerCallbackAsync(callback.Id, "Кнопка устарела", true, ct).ConfigureAwait(false);
            return;
        }

        await _sender!.AnswerCallbackAsync(callback.Id, title, false, ct).ConfigureAwait(false);
        await ExecuteMenuActionAsync(runtime, action, messageId, ct).ConfigureAwait(false);
    }

    /// <summary>Общая обработка действий меню: и для inline-кнопок, и для нижней клавиатуры.</summary>
    private async Task ExecuteMenuActionAsync(
        ChatRuntime runtime, string action, int messageId, CancellationToken ct)
    {
        switch (action)
        {
            case BotMenus.Actions.Main:
                await CmdMenuAsync(runtime, messageId, ct).ConfigureAwait(false);
                break;

            case BotMenus.Actions.Files:
                await OpenBrowserAsync(
                    runtime,
                    _state.Get(runtime.ChatId).Directory ?? _config.DefaultDirectory,
                    "📂 <b>Файлы</b>\nВыбери папку или файл.",
                    messageId,
                    ct).ConfigureAwait(false);
                break;

            case BotMenus.Actions.New:
                await OpenBrowserAsync(
                    runtime,
                    _state.Get(runtime.ChatId).Directory ?? _config.DefaultDirectory,
                    "🆕 <b>Новая сессия</b>\nВыбери папку кнопками и нажми «Новая сессия здесь».",
                    messageId,
                    ct).ConfigureAwait(false);
                break;

            case BotMenus.Actions.Sessions:
                await ShowSessionsAsync(runtime, messageId, ct).ConfigureAwait(false);
                break;

            case BotMenus.Actions.Model:
                await ShowModelsAsync(runtime, messageId, ct).ConfigureAwait(false);
                break;

            case BotMenus.Actions.Agent:
                await ShowAgentsAsync(runtime, messageId, ct).ConfigureAwait(false);
                break;

            case BotMenus.Actions.Status:
                await CmdStatusAsync(runtime, ct).ConfigureAwait(false);
                break;

            case BotMenus.Actions.Screen:
                await CmdScreenAsync(runtime, ct).ConfigureAwait(false);
                break;

            case BotMenus.Actions.Diff:
                await CmdDiffAsync(runtime, ct).ConfigureAwait(false);
                break;

            case BotMenus.Actions.Cancel:
                await CmdCancelAsync(runtime, ct).ConfigureAwait(false);
                break;

            case BotMenus.Actions.Restart:
                await CmdRestartAsync(runtime, ct).ConfigureAwait(false);
                break;

            case BotMenus.Actions.Help:
                await CmdHelpAsync(runtime, ct).ConfigureAwait(false);
                break;

            case BotMenus.Actions.Shutdown:
                await CmdShutdownAsync(runtime, ct).ConfigureAwait(false);
                break;
        }
    }

    private static string? MenuActionTitle(string action) => action switch
    {
        BotMenus.Actions.Main => "Меню",
        BotMenus.Actions.Files => "Файлы",
        BotMenus.Actions.New => "Выбор папки",
        BotMenus.Actions.Sessions => "Сессии",
        BotMenus.Actions.Model => "Модели",
        BotMenus.Actions.Agent => "Агент",
        BotMenus.Actions.Status => "Статус",
        BotMenus.Actions.Screen => "Скриншот",
        BotMenus.Actions.Diff => "Diff",
        BotMenus.Actions.Cancel => "Отмена",
        BotMenus.Actions.Restart => "Перезапуск",
        BotMenus.Actions.Help => "Справка",
        BotMenus.Actions.Shutdown => "Выключение",
        _ => null,
    };

    private async Task HandleFilesCallbackAsync(
        CallbackQuery callback, int generation, string token, long chatId, CancellationToken ct)
    {
        var runtime = GetRuntime(chatId);
        var messageId = callback.Message?.Id ?? 0;

        if (!runtime.Browser.TryGet(generation, out var page))
        {
            await _sender!.AnswerCallbackAsync(callback.Id, "Список устарел — открой /ls заново", true, ct)
                .ConfigureAwait(false);
            return;
        }

        if (token is "m")
        {
            await _sender!.AnswerCallbackAsync(callback.Id, "Меню", false, ct).ConfigureAwait(false);
            await CmdMenuAsync(runtime, messageId, ct).ConfigureAwait(false);
            return;
        }

        if (token is "n")
        {
            if (page.Drives || string.IsNullOrWhiteSpace(page.Path))
            {
                await _sender!.AnswerCallbackAsync(callback.Id, "Сначала выбери папку", true, ct).ConfigureAwait(false);
                return;
            }

            await _sender!.AnswerCallbackAsync(callback.Id, "Создаю сессию…", false, ct).ConfigureAwait(false);
            await CreateSessionAtAsync(runtime, page.Path, messageId, ct).ConfigureAwait(false);
            return;
        }

        if (token.Length > 1 && token[0] == 'p' && int.TryParse(token.AsSpan(1), out var pageNumber))
        {
            if (pageNumber < 0 || pageNumber >= page.PageCount)
            {
                await _sender!.AnswerCallbackAsync(callback.Id, "Это крайняя страница", false, ct).ConfigureAwait(false);
                return;
            }

            FileBrowser.SetPage(page, pageNumber);
            await _sender!.AnswerCallbackAsync(callback.Id, $"Страница {pageNumber + 1}", false, ct)
                .ConfigureAwait(false);
            await ShowBrowserPageAsync(
                runtime,
                page,
                "",
                messageId,
                ct).ConfigureAwait(false);
            return;
        }

        if (token.Length > 1 && token[0] == 'e' && int.TryParse(token.AsSpan(1), out var entryIndex))
        {
            if (entryIndex < 0 || entryIndex >= page.Entries.Count)
            {
                await _sender!.AnswerCallbackAsync(callback.Id, "Список устарел — открой /ls заново", true, ct)
                    .ConfigureAwait(false);
                return;
            }

            var entry = page.Entries[entryIndex];
            if (entry.IsDirectory)
            {
                await _sender!.AnswerCallbackAsync(callback.Id, entry.Name, false, ct).ConfigureAwait(false);
                try
                {
                    await OpenBrowserAsync(
                        runtime,
                        entry.FullPath,
                        "",
                        messageId,
                        ct).ConfigureAwait(false);
                }
                catch (DirectoryNotFoundException ex)
                {
                    await _sender.SendPlainAsync(Cid(chatId), $"❌ {ex.Message}", null, ct).ConfigureAwait(false);
                }
                return;
            }

            await _sender!.AnswerCallbackAsync(callback.Id, "Отправляю файл…", false, ct).ConfigureAwait(false);
            _ = Task.Run(() => SendFileByPathAsync(chatId, entry.FullPath, ct), CancellationToken.None);
            return;
        }

        await _sender!.AnswerCallbackAsync(callback.Id, "Обновляю", false, ct).ConfigureAwait(false);

        var target = token switch
        {
            "u" => FileBrowser.ParentOf(page.Path),
            "r" => null,
            "h" => FileBrowser.HomeDirectory,
            "c" => _state.Get(chatId).Directory ?? _config.DefaultDirectory,
            "R" => page.Drives ? null : page.Path,
            _ => page.Drives ? null : page.Path,
        };

        await OpenBrowserAsync(
            runtime,
            target,
            "",
            messageId,
            ct).ConfigureAwait(false);
    }

    private async Task HandleSessionCallbackAsync(
        CallbackQuery callback, int token, int index, long chatId, CancellationToken ct)
    {
        var runtime = GetRuntime(chatId);
        if (!runtime.TryGetSessions(token, out var sessions) || index < 0 || index >= sessions.Count)
        {
            await _sender!.AnswerCallbackAsync(callback.Id, "Список устарел, открой /sessions", true, ct)
                .ConfigureAwait(false);
            return;
        }

        var chosen = sessions[index];
        await _sender!.AnswerCallbackAsync(callback.Id, "Переключаюсь…", false, ct).ConfigureAwait(false);

        var session = await _client.GetSessionAsync(chosen.Id, ct).ConfigureAwait(false);
        if (session is null)
        {
            await _sender.SendPlainAsync(Cid(chatId), "Сессия не найдена.", null, ct).ConfigureAwait(false);
            return;
        }

        var messageId = callback.Message?.Id ?? 0;
        await SwitchSessionAsync(runtime, session, messageId, ct).ConfigureAwait(false);
    }

    private async Task HandleAgentCallbackAsync(
        CallbackQuery callback, string agent, long chatId, CancellationToken ct)
    {
        if (agent is not ("build" or "plan"))
        {
            await _sender!.AnswerCallbackAsync(callback.Id, "Такой агент недоступен", true, ct).ConfigureAwait(false);
            return;
        }

        var runtime = GetRuntime(chatId);
        await _sender!.AnswerCallbackAsync(callback.Id, $"Агент: {agent}", false, ct).ConfigureAwait(false);
        await ApplyAgentAsync(runtime, agent, callback.Message?.Id ?? 0, ct).ConfigureAwait(false);
    }

    private async Task HandleApprovalCallbackAsync(
        CallbackQuery callback, string shortId, ApprovalDecision choice, long chatId, CancellationToken ct)
    {
        if (!_approvals!.TryRemove(shortId, out var pending))
        {
            await _sender!.AnswerCallbackAsync(callback.Id, "Устарело или уже обработано", true, ct)
                .ConfigureAwait(false);
            return;
        }

        var decision = CallbackData.Token(choice);

        try
        {
            await _client.ReplyPermissionAsync(
                pending.SessionId,
                pending.RequestId,
                decision,
                decision == "reject" ? "Отклонено пользователем" : null,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось ответить на запрос разрешения", ex);
        }

        var verdict = decision switch
        {
            "always" => "🔁 Разрешено всегда",
            "reject" => "❌ Отклонено",
            _ => "✅ Разрешено",
        };

        var original = callback.Message?.Text ?? "Запрос разрешения";
        await _sender!.EditHtmlAsync(
            Cid(chatId),
            pending.MessageId,
            TextUtil.EscapeHtml(original) + "\n\n<b>" + verdict + "</b>",
            null,
            ct,
            force: true).ConfigureAwait(false);

        await _sender.AnswerCallbackAsync(callback.Id, verdict, false, ct).ConfigureAwait(false);
    }

    private async Task HandleModelCallbackAsync(
        CallbackQuery callback, int index, long chatId, CancellationToken ct)
    {
        var runtime = GetRuntime(chatId);
        if (runtime.LastModels is null
            || index < 0
            || index >= runtime.LastModels.Count)
        {
            await _sender!.AnswerCallbackAsync(callback.Id, "Список устарел, вызови /model", true, ct)
                .ConfigureAwait(false);
            return;
        }

        var model = runtime.LastModels[index];
        await _sender!.AnswerCallbackAsync(callback.Id, $"Модель: {model.Name}", false, ct).ConfigureAwait(false);

        await ApplyModelAsync(runtime, model, ct).ConfigureAwait(false);
        await _sender.EditHtmlAsync(
            Cid(chatId),
            callback.Message!.Id,
            $"🧠 Модель переключена: <code>{TextUtil.EscapeHtml(model.ProviderID + "/" + model.Id)}</code>",
            BotMenus.MenuOnly(),
            ct,
            force: true).ConfigureAwait(false);
    }

    // ---------- commands ----------

    private async Task HandleCommandAsync(ChatRuntime runtime, string text, CancellationToken ct)
    {
        var space = text.IndexOf(' ');
        var command = (space < 0 ? text : text[..space]).Trim();
        var arg = space < 0 ? "" : text[(space + 1)..].Trim();

        var at = command.IndexOf('@');
        if (at > 0) command = command[..at];

        switch (command.ToLowerInvariant())
        {
            case "/start":
                await CmdStartAsync(runtime, ct).ConfigureAwait(false);
                break;
            case "/menu":
                await CmdMenuAsync(runtime, 0, ct).ConfigureAwait(false);
                break;
            case "/help":
                await CmdHelpAsync(runtime, ct).ConfigureAwait(false);
                break;
            case "/id":
                await _sender!.SendHtmlAsync(
                    Cid(runtime.ChatId),
                    $"👤 User ID: <code>{runtime.UserId}</code>\n💬 Chat ID: <code>{runtime.ChatId}</code>",
                    null,
                    ct).ConfigureAwait(false);
                break;
            case "/new":
                await CmdNewAsync(runtime, arg, ct).ConfigureAwait(false);
                break;
            case "/sessions":
                await CmdSessionsAsync(runtime, ct).ConfigureAwait(false);
                break;
            case "/use":
                await CmdUseAsync(runtime, arg, ct).ConfigureAwait(false);
                break;
            case "/status":
                await CmdStatusAsync(runtime, ct).ConfigureAwait(false);
                break;
            case "/cancel":
                await CmdCancelAsync(runtime, ct).ConfigureAwait(false);
                break;
            case "/agent":
                await CmdAgentAsync(runtime, arg, ct).ConfigureAwait(false);
                break;
            case "/model":
                await CmdModelAsync(runtime, arg, ct).ConfigureAwait(false);
                break;
            case "/screen":
                await CmdScreenAsync(runtime, ct).ConfigureAwait(false);
                break;
            case "/file":
                await CmdFileAsync(runtime, arg, ct).ConfigureAwait(false);
                break;
            case "/ls":
                await CmdListAsync(runtime, arg, ct).ConfigureAwait(false);
                break;
            case "/diff":
                await CmdDiffAsync(runtime, ct).ConfigureAwait(false);
                break;
            case "/restart":
                await CmdRestartAsync(runtime, ct).ConfigureAwait(false);
                break;
            case "/shutdown":
                await CmdShutdownAsync(runtime, ct).ConfigureAwait(false);
                break;
            default:
                await _sender!.SendPlainAsync(
                    Cid(runtime.ChatId), $"Неизвестная команда {command}. /help — список.", null, ct)
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task CmdStartAsync(ChatRuntime runtime, CancellationToken ct)
    {
        var chat = _state.Get(runtime.ChatId);
        var text =
            "🟢 <b>Remoter на связи</b>\n\n" +
            $"👤 Твой ID: <code>{runtime.UserId}</code>\n" +
            $"📁 Папка: <code>{TextUtil.EscapeHtml(chat.Directory ?? _config.DefaultDirectory)}</code>\n" +
            $"🧠 Модель: <code>{TextUtil.EscapeHtml((chat.ModelProvider ?? _config.Model.ProviderID) + "/" + (chat.ModelID ?? _config.Model.ID))}</code>\n" +
            $"🤖 Агент: <code>{TextUtil.EscapeHtml(chat.Agent ?? _config.Agent)}</code>\n\n" +
            "Кнопки меню теперь всегда внизу — писать команды не нужно.\n" +
            "Напиши задачу обычным текстом или выбери действие кнопкой. " +
            (_config.AutoApprove
                ? "Подтверждения отключены — всё выполняется сразу.\n"
                : "Опасные действия придут с кнопками.\n");

        await _sender!.SendHtmlAsync(Cid(runtime.ChatId), text, BotMenus.ReplyMenu(), ct).ConfigureAwait(false);
    }

    private async Task CmdMenuAsync(ChatRuntime runtime, int messageId, CancellationToken ct)
    {
        var chat = _state.Get(runtime.ChatId);
        var busy = Volatile.Read(ref runtime.Busy) == 1;
        int queued;
        lock (runtime.QueueSync) queued = runtime.Queue.Count;

        var text = BotMenus.MainText(chat, _config, busy, queued);
        if (messageId > 0)
        {
            await _sender!.EditHtmlAsync(
                Cid(runtime.ChatId), messageId, text, BotMenus.MainMenu(), ct, force: true).ConfigureAwait(false);
        }
        else
        {
            await _sender!.SendHtmlAsync(Cid(runtime.ChatId), text, BotMenus.MainMenu(), ct).ConfigureAwait(false);
        }
    }

    private async Task CmdHelpAsync(ChatRuntime runtime, CancellationToken ct)
    {
        var approvalLine = _config.AutoApprove
            ? "Подтверждения отключены: правки, команды и сеть выполняются сразу."
            : "Правки файлов, команды в терминале и сеть требуют подтверждения кнопкой.";

        var footer = _config.AutoApprove
            ? "<i>Чтение и изменения выполняются без спроса. Бот слушает только твой ID.</i>"
            : "<i>Чтение файлов разрешено без спроса. Всё, что меняет систему, — только с кнопкой.</i>";

        var text =
            "<b>Remoter</b> — мост к OpenCode на этом ПК.\n\n" +
            "Просто напиши задачу: агент прочитает код, что-то поменяет и запустит.\n" +
            "Или пользуйся кнопками — писать команды с телефона не обязательно.\n" +
            approvalLine + "\n\n" +
            "<b>Команды</b>\n" +
            "/menu — меню с кнопками\n" +
            "/new [путь] — новая сессия (без пути — выбор папки кнопками)\n" +
            "/sessions — последние сессии\n" +
            "/use N — переключиться на сессию\n" +
            "/status — что происходит сейчас\n" +
            "/cancel — прервать выполнение\n" +
            "/agent [build|plan] — режим агента\n" +
            "/model [имя] — выбор модели\n" +
            "/screen — скриншот экрана\n" +
            "/file путь — прислать файл\n" +
            "/ls [путь] — файлы и папки кнопками\n" +
            "/diff — изменения текущей сессии\n" +
            "/restart — перезапуск сервиса OpenCode\n" +
            "/shutdown — выключить бота\n\n" + footer;

        await _sender!.SendHtmlAsync(Cid(runtime.ChatId), text, BotMenus.MainMenu(), ct).ConfigureAwait(false);
    }

    private async Task CmdNewAsync(ChatRuntime runtime, string arg, CancellationToken ct)
    {
        var chat = _state.Get(runtime.ChatId);

        if (string.IsNullOrWhiteSpace(arg))
        {
            await OpenBrowserAsync(
                runtime,
                chat.Directory ?? _config.DefaultDirectory,
                "🆕 <b>Новая сессия</b>\nВыбери папку кнопками и нажми «Новая сессия здесь».",
                0,
                ct).ConfigureAwait(false);
            return;
        }

        var directory = AppConfig.ExpandPath(arg.Trim('"'));
        await CreateSessionAtAsync(runtime, directory, 0, ct).ConfigureAwait(false);
    }

    private async Task CreateSessionAtAsync(
        ChatRuntime runtime, string directory, int messageId, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            await _sender!.SendPlainAsync(
                Cid(runtime.ChatId), $"❌ Папка не найдена: {directory}", null, ct).ConfigureAwait(false);
            return;
        }

        var chat = _state.Get(runtime.ChatId);
        var model = new ModelRef
        {
            Id = chat.ModelID ?? _config.Model.ID,
            ProviderID = chat.ModelProvider ?? _config.Model.ProviderID,
        };
        var agent = chat.Agent ?? _config.Agent;

        var session = await _client
            .CreateSessionAsync(directory, PermissionPolicy.For(_config.AutoApprove), agent, model, "Telegram", ct)
            .ConfigureAwait(false);

        _state.Update(runtime.ChatId, s =>
        {
            s.SessionID = session.Id;
            s.Directory = directory;
            s.ModelID = model.Id;
            s.ModelProvider = model.ProviderID;
            s.Agent = agent;
        });

        MapSession(session.Id, runtime.ChatId);

        var text =
            $"🆕 <b>Новая сессия</b>\n" +
            $"📁 <code>{TextUtil.EscapeHtml(directory)}</code>\n" +
            $"🧠 <code>{TextUtil.EscapeHtml(model.ProviderID + "/" + model.Id)}</code>\n" +
            $"🤖 <code>{TextUtil.EscapeHtml(agent)}</code>\n" +
            $"🆔 <code>{session.Id}</code>";

        if (messageId > 0)
        {
            await _sender!.EditHtmlAsync(
                Cid(runtime.ChatId), messageId, text, BotMenus.MenuOnly(), ct, force: true).ConfigureAwait(false);
        }
        else
        {
            await _sender!.SendHtmlAsync(
                Cid(runtime.ChatId), text, BotMenus.MenuOnly(), ct).ConfigureAwait(false);
        }
    }

    private async Task CmdSessionsAsync(ChatRuntime runtime, CancellationToken ct) =>
        await ShowSessionsAsync(runtime, 0, ct).ConfigureAwait(false);

    private async Task ShowSessionsAsync(ChatRuntime runtime, int messageId, CancellationToken ct)
    {
        var list = (await _client.ListSessionsAsync(15, ct).ConfigureAwait(false)).ToList();
        var token = runtime.StoreSessions(list);
        runtime.LastSessions = list;

        var chat = _state.Get(runtime.ChatId);
        var current = chat.SessionID;

        if (list.Count == 0)
        {
            if (messageId > 0)
            {
                await _sender!.EditHtmlAsync(
                    Cid(runtime.ChatId),
                    messageId,
                    "🗂 Сессий пока нет. Напиши задачу — сессия создастся сама.",
                    BotMenus.MenuOnly(),
                    ct,
                    force: true).ConfigureAwait(false);
            }
            else
            {
                await _sender!.SendPlainAsync(
                    Cid(runtime.ChatId), "Сессий пока нет.", BotMenus.MenuOnly(), ct).ConfigureAwait(false);
            }
            return;
        }

        var header = new StringBuilder("🗂 <b>Последние сессии</b>\n");
        var currentTitle = list.FirstOrDefault(s => s.Id == current)?.Title;
        if (!string.IsNullOrWhiteSpace(currentTitle))
            header.Append("Сейчас: <b>").Append(TextUtil.EscapeHtml(TextUtil.Truncate(currentTitle!, 60))).Append("</b>\n");
        header.Append("\nНажми на кнопку, чтобы переключиться.");

        var rows = new List<InlineKeyboardButton[]>(list.Count + 1);
        for (var i = 0; i < list.Count; i++)
        {
            var session = list[i];
            var title = string.IsNullOrWhiteSpace(session.Title)
                ? Path.GetFileName(session.Location?.Directory?.TrimEnd('\\', '/') ?? "")
                : session.Title!;
            if (string.IsNullOrWhiteSpace(title)) title = "без названия";

            var marker = session.Id == current ? "✅ " : "";
            rows.Add(
            [
                InlineKeyboardButton.WithCallbackData(
                    $"{marker}{i + 1}. {TextUtil.Truncate(title, 48)}",
                    CallbackData.Session(token, i)),
            ]);
        }
        rows.Add(BotMenus.MenuRow());

        if (messageId > 0)
        {
            await _sender!.EditHtmlAsync(
                Cid(runtime.ChatId),
                messageId,
                header.ToString(),
                new InlineKeyboardMarkup(rows),
                ct,
                force: true).ConfigureAwait(false);
        }
        else
        {
            await _sender!.SendHtmlAsync(
                Cid(runtime.ChatId),
                header.ToString(),
                new InlineKeyboardMarkup(rows),
                ct).ConfigureAwait(false);
        }
    }

    private async Task CmdUseAsync(ChatRuntime runtime, string arg, CancellationToken ct)
    {
        if (!int.TryParse(arg, out var number)
            || runtime.LastSessions is null
            || number < 1
            || number > runtime.LastSessions.Count)
        {
            await _sender!.SendPlainAsync(
                Cid(runtime.ChatId), "Укажи номер из /sessions, например /use 2", null, ct).ConfigureAwait(false);
            return;
        }

        var chosen = runtime.LastSessions[number - 1];
        var session = await _client.GetSessionAsync(chosen.Id, ct).ConfigureAwait(false);
        if (session is null)
        {
            await _sender!.SendPlainAsync(Cid(runtime.ChatId), "Сессия не найдена.", null, ct).ConfigureAwait(false);
            return;
        }

        await SwitchSessionAsync(runtime, session, 0, ct).ConfigureAwait(false);
    }

    private async Task SwitchSessionAsync(
        ChatRuntime runtime, SessionInfo session, int messageId, CancellationToken ct)
    {
        _state.Update(runtime.ChatId, s =>
        {
            s.SessionID = session.Id;
            s.Directory = session.Location?.Directory;
            s.ModelID = session.Model?.Id;
            s.ModelProvider = session.Model?.ProviderID;
            s.Agent = session.Agent;
        });

        MapSession(session.Id, runtime.ChatId);

        var text =
            $"🔀 Переключился\n" +
            $"📁 <code>{TextUtil.EscapeHtml(session.Location?.Directory ?? "")}</code>\n" +
            $"🧠 <code>{TextUtil.EscapeHtml((session.Model?.ProviderID ?? "") + "/" + (session.Model?.Id ?? ""))}</code>";

        if (messageId > 0)
        {
            await _sender!.EditHtmlAsync(
                Cid(runtime.ChatId), messageId, text, BotMenus.MenuOnly(), ct, force: true).ConfigureAwait(false);
        }
        else
        {
            await _sender!.SendHtmlAsync(
                Cid(runtime.ChatId), text, BotMenus.MenuOnly(), ct).ConfigureAwait(false);
        }
    }

    private async Task CmdStatusAsync(ChatRuntime runtime, CancellationToken ct)
    {
        var chat = _state.Get(runtime.ChatId);
        if (string.IsNullOrWhiteSpace(chat.SessionID))
        {
            await _sender!.SendPlainAsync(
                Cid(runtime.ChatId), "Активной сессии нет. Напиши задачу или /new.", null, ct).ConfigureAwait(false);
            return;
        }

        SessionInfo? info = null;
        try
        {
            info = await _client.GetSessionAsync(chat.SessionID, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось получить сессию: {ex.Message}");
        }

        var busy = Volatile.Read(ref runtime.Busy) == 1;
        int queue;
        lock (runtime.QueueSync) queue = runtime.Queue.Count;

        var sb = new StringBuilder("📊 <b>Статус</b>\n\n");
        sb.Append($"📁 <code>{TextUtil.EscapeHtml(chat.Directory ?? "")}</code>\n");
        sb.Append($"🧠 <code>{TextUtil.EscapeHtml((chat.ModelProvider ?? "") + "/" + (chat.ModelID ?? ""))}</code>\n");
        sb.Append($"🤖 <code>{TextUtil.EscapeHtml(chat.Agent ?? _config.Agent)}</code>\n");
        sb.Append($"🆔 <code>{TextUtil.EscapeHtml(chat.SessionID)}</code>\n");

        if (info is not null && !string.IsNullOrWhiteSpace(info.Title))
            sb.Append($"📝 {TextUtil.EscapeHtml(TextUtil.Truncate(info.Title!, 80))}\n");

        sb.Append(busy ? "\n⚙️ Сейчас: <b>выполняет задачу</b>" : "\n💤 Сейчас: свободен");
        if (queue > 0) sb.Append($"\n📥 В очереди: {queue}");

        await _sender!.SendHtmlAsync(Cid(runtime.ChatId), sb.ToString(), null, ct).ConfigureAwait(false);
    }

    private async Task CmdCancelAsync(ChatRuntime runtime, CancellationToken ct)
    {
        var chat = _state.Get(runtime.ChatId);
        if (string.IsNullOrWhiteSpace(chat.SessionID))
        {
            await _sender!.SendPlainAsync(Cid(runtime.ChatId), "Нечего отменять.", null, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await _client.InterruptAsync(chat.SessionID, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Interrupt: {ex.Message}");
        }

        var progress = runtime.Progress;
        runtime.Progress = null;
        Interlocked.Exchange(ref runtime.Busy, 0);

        int cleared;
        lock (runtime.QueueSync)
        {
            cleared = runtime.Queue.Count;
            runtime.Queue.Clear();
        }

        if (progress is not null)
        {
            await progress.FailAsync("Отменено пользователем.", ct).ConfigureAwait(false);
        }
        else
        {
            await _sender!.SendPlainAsync(Cid(runtime.ChatId), "⏹ Отменено.", null, ct).ConfigureAwait(false);
        }

        if (cleared > 0)
            await _sender!.SendPlainAsync(
                Cid(runtime.ChatId), $"Очередь очищена ({cleared}).", null, ct).ConfigureAwait(false);
    }

    private async Task CmdAgentAsync(ChatRuntime runtime, string arg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await ShowAgentsAsync(runtime, 0, ct).ConfigureAwait(false);
            return;
        }

        var name = arg.Trim().ToLowerInvariant();
        if (name is not ("build" or "plan"))
        {
            await _sender!.SendPlainAsync(
                Cid(runtime.ChatId), "Доступны только build и plan.", null, ct).ConfigureAwait(false);
            return;
        }

        await ApplyAgentAsync(runtime, name, 0, ct).ConfigureAwait(false);
    }

    private async Task ShowAgentsAsync(ChatRuntime runtime, int messageId, CancellationToken ct)
    {
        var chat = _state.Get(runtime.ChatId);
        var current = chat.Agent ?? _config.Agent;
        var text = $"🤖 <b>Режим агента</b>\n\nСейчас: <code>{TextUtil.EscapeHtml(current)}</code>\n\n" +
                   "build — выполнять задачи и менять файлы\n" +
                   "plan — только планировать, без изменений";

        if (messageId > 0)
        {
            await _sender!.EditHtmlAsync(
                Cid(runtime.ChatId), messageId, text, BotMenus.Agents(current), ct, force: true)
                .ConfigureAwait(false);
        }
        else
        {
            await _sender!.SendHtmlAsync(
                Cid(runtime.ChatId), text, BotMenus.Agents(current), ct).ConfigureAwait(false);
        }
    }

    private async Task ApplyAgentAsync(ChatRuntime runtime, string name, int messageId, CancellationToken ct)
    {
        var chat = _state.Get(runtime.ChatId);

        if (!string.IsNullOrWhiteSpace(chat.SessionID))
        {
            try
            {
                await _client.SwitchAgentAsync(chat.SessionID, name, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn($"SwitchAgent: {ex.Message}");
            }
        }

        _state.Update(runtime.ChatId, s => s.Agent = name);

        var text = $"🤖 Агент: <code>{TextUtil.EscapeHtml(name)}</code>";
        if (messageId > 0)
        {
            await _sender!.EditHtmlAsync(
                Cid(runtime.ChatId), messageId, text, BotMenus.MenuOnly(), ct, force: true)
                .ConfigureAwait(false);
        }
        else
        {
            await _sender!.SendHtmlAsync(
                Cid(runtime.ChatId), text, BotMenus.MenuOnly(), ct).ConfigureAwait(false);
        }
    }

    private async Task CmdModelAsync(ChatRuntime runtime, string arg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await ShowModelsAsync(runtime, 0, ct).ConfigureAwait(false);
            return;
        }

        var models = await _client.ListModelsAsync(ct).ConfigureAwait(false);
        var query = arg.Trim();
        var match = models.FirstOrDefault(m => $"{m.ProviderID}/{m.Id}".Equals(query, StringComparison.OrdinalIgnoreCase))
                    ?? models.FirstOrDefault(m => m.Id.Equals(query, StringComparison.OrdinalIgnoreCase))
                    ?? models.FirstOrDefault(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    ?? models.FirstOrDefault(m => m.Id.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            await _sender!.SendPlainAsync(
                Cid(runtime.ChatId), $"Не нашёл модель «{query}». Попробуй /model без аргументов.", null, ct)
                .ConfigureAwait(false);
            return;
        }

        await ApplyModelAsync(runtime, match, ct).ConfigureAwait(false);
        await _sender!.SendHtmlAsync(
            Cid(runtime.ChatId),
            $"🧠 Модель: <code>{TextUtil.EscapeHtml(match.ProviderID + "/" + match.Id)}</code>",
            BotMenus.MenuOnly(),
            ct).ConfigureAwait(false);
    }

    private async Task ShowModelsAsync(ChatRuntime runtime, int messageId, CancellationToken ct)
    {
        var chat = _state.Get(runtime.ChatId);
        var current = (chat.ModelProvider ?? _config.Model.ProviderID) + "/" + (chat.ModelID ?? _config.Model.ID);

        var all = await _client.ListModelsAsync(ct).ConfigureAwait(false);
        var list = all
            .Where(m => string.IsNullOrEmpty(m.Status) || m.Status == "active")
            .Take(12)
            .ToList();

        runtime.LastModels = list;

        if (list.Count == 0)
        {
            await _sender!.SendPlainAsync(Cid(runtime.ChatId), "Модели не найдены.", null, ct)
                .ConfigureAwait(false);
            return;
        }

        var text = $"🧠 <b>Модель</b>\n\nСейчас: <code>{TextUtil.EscapeHtml(current)}</code>\n\nВыбери модель:";

        if (messageId > 0)
        {
            await _sender!.EditHtmlAsync(
                Cid(runtime.ChatId), messageId, text, BotMenus.Models(list), ct, force: true)
                .ConfigureAwait(false);
        }
        else
        {
            await _sender!.SendHtmlAsync(
                Cid(runtime.ChatId), text, BotMenus.Models(list), ct).ConfigureAwait(false);
        }
    }

    private async Task ApplyModelAsync(ChatRuntime runtime, ModelInfo model, CancellationToken ct)
    {
        var chat = _state.Get(runtime.ChatId);
        var sessionId = chat.SessionID ?? await EnsureSessionAsync(runtime, ct).ConfigureAwait(false);

        await _client
            .SwitchModelAsync(sessionId, new ModelRef { Id = model.Id, ProviderID = model.ProviderID }, ct)
            .ConfigureAwait(false);

        _state.Update(runtime.ChatId, s =>
        {
            s.ModelID = model.Id;
            s.ModelProvider = model.ProviderID;
        });
    }

    private Task CmdScreenAsync(ChatRuntime runtime, CancellationToken ct)
    {
        _ = Task.Run(() => CaptureAndSendScreenAsync(runtime.ChatId, ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task CaptureAndSendScreenAsync(long chatId, CancellationToken ct)
    {
        string? path = null;
        try
        {
            var shot = await ScreenshotService.CaptureAsync(ct).ConfigureAwait(false);
            path = shot.Path;
            await _sender!.SendPhotoAsync(
                Cid(chatId), path, $"🖥 {shot.Width}×{shot.Height}", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            try
            {
                await _sender!.SendPlainAsync(Cid(chatId), $"❌ {ex.Message}", null, ct).ConfigureAwait(false);
            }
            catch
            {
                // ничего
            }
        }
        finally
        {
            if (path is not null)
            {
                try { File.Delete(path); } catch { /* ничего */ }
            }
        }
    }

    private async Task CmdFileAsync(ChatRuntime runtime, string arg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            await _sender!.SendPlainAsync(
                Cid(runtime.ChatId), "Укажи путь: /file G:\\c#\\remoter\\README.md", null, ct).ConfigureAwait(false);
            return;
        }

        var path = AppConfig.ExpandPath(arg.Trim('"'));
        if (!File.Exists(path))
        {
            await _sender!.SendPlainAsync(Cid(runtime.ChatId), $"❌ Файл не найден: {path}", null, ct)
                .ConfigureAwait(false);
            return;
        }

        var info = new FileInfo(path);
        if (info.Length > _config.MaxTelegramDocumentBytes)
        {
            await _sender!.SendPlainAsync(
                Cid(runtime.ChatId),
                $"❌ Файл слишком большой: {TextUtil.HumanBytes(info.Length)} (лимит 50 МБ).",
                null,
                ct).ConfigureAwait(false);
            return;
        }

        // Отправка файла может занять десятки секунд — не держим очередь команд.
        _ = Task.Run(() => SendFileByPathAsync(runtime.ChatId, path, ct), CancellationToken.None);
    }

    private async Task SendFileByPathAsync(long chatId, string path, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                await _sender!.SendPlainAsync(Cid(chatId), $"❌ Файл не найден: {path}", null, ct)
                    .ConfigureAwait(false);
                return;
            }

            if (info.Length > _config.MaxTelegramDocumentBytes)
            {
                await _sender!.SendPlainAsync(
                    Cid(chatId),
                    $"❌ Файл слишком большой: {TextUtil.HumanBytes(info.Length)}.",
                    null,
                    ct).ConfigureAwait(false);
                return;
            }

            var extension = info.Extension.ToLowerInvariant();
            var isImage = extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
            var caption = $"{info.Name} — {TextUtil.HumanBytes(info.Length)}";

            if (isImage)
                await _sender!.SendPhotoAsync(Cid(chatId), path, caption, ct).ConfigureAwait(false);
            else
                await _sender!.SendDocumentAsync(Cid(chatId), path, caption, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось отправить файл {path}: {ex.Message}");
            try
            {
                await _sender!.SendPlainAsync(Cid(chatId), $"❌ {ex.Message}", null, ct).ConfigureAwait(false);
            }
            catch
            {
                // ничего
            }
        }
    }

    private async Task CmdListAsync(ChatRuntime runtime, string arg, CancellationToken ct)
    {
        var chat = _state.Get(runtime.ChatId);
        var path = string.IsNullOrWhiteSpace(arg)
            ? chat.Directory ?? _config.DefaultDirectory
            : AppConfig.ExpandPath(arg.Trim('"'));

        if (File.Exists(path))
        {
            // Отправка файла не должна задерживать следующие команды чата.
            _ = Task.Run(() => SendFileByPathAsync(runtime.ChatId, path, ct), CancellationToken.None);
            return;
        }

        await OpenBrowserAsync(
            runtime,
            path,
            "📂 <b>Файлы</b>\nВыбери папку или файл.",
            0,
            ct).ConfigureAwait(false);
    }

    private async Task OpenBrowserAsync(
        ChatRuntime runtime, string? path, string intro, int messageId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(path) && !Directory.Exists(path))
        {
            await _sender!.SendPlainAsync(
                Cid(runtime.ChatId), $"❌ Папка не найдена: {path}", null, ct).ConfigureAwait(false);
            return;
        }

        var page = runtime.Browser.Open(string.IsNullOrWhiteSpace(path) ? null : path);
        await ShowBrowserPageAsync(runtime, page, intro, messageId, ct).ConfigureAwait(false);
    }

    private async Task ShowBrowserPageAsync(
        ChatRuntime runtime, BrowserPage page, string intro, int messageId, CancellationToken ct)
    {
        var chat = _state.Get(runtime.ChatId);
        var browserText = BotMenus.BrowserText(page, chat.Directory);
        var text = string.IsNullOrWhiteSpace(intro) ? browserText : intro + "\n\n" + browserText;
        var keyboard = BotMenus.Browser(page, chat.Directory);

        if (messageId > 0)
        {
            await _sender!.EditHtmlAsync(
                Cid(runtime.ChatId), messageId, text, keyboard, ct, force: true).ConfigureAwait(false);
        }
        else
        {
            await _sender!.SendHtmlAsync(Cid(runtime.ChatId), text, keyboard, ct).ConfigureAwait(false);
        }
    }

    private async Task CmdDiffAsync(ChatRuntime runtime, CancellationToken ct)
    {
        var chat = _state.Get(runtime.ChatId);
        if (string.IsNullOrWhiteSpace(chat.SessionID))
        {
            await _sender!.SendPlainAsync(Cid(runtime.ChatId), "Активной сессии нет.", null, ct).ConfigureAwait(false);
            return;
        }

        var diffs = await _client.GetDiffAsync(chat.SessionID, ct).ConfigureAwait(false);
        if (diffs.Count == 0)
        {
            await _sender!.SendPlainAsync(Cid(runtime.ChatId), "В текущей сессии изменений нет.", null, ct)
                .ConfigureAwait(false);
            return;
        }

        var sb = new StringBuilder("📝 <b>Изменения сессии</b>\n\n");
        foreach (var diff in diffs)
        {
            sb.Append($"<b>{TextUtil.EscapeHtml(diff.File)}</b> ")
              .Append($"<i>+{diff.Additions} −{diff.Deletions}</i>\n")
              .Append("<pre>").Append(TextUtil.EscapeHtml(TextUtil.Truncate(diff.Patch, 2500))).Append("</pre>\n\n");
        }

        var text = sb.ToString();
        if (text.Length <= _config.MaxMessageChars)
        {
            await _sender!.SendHtmlAsync(Cid(runtime.ChatId), text, null, ct).ConfigureAwait(false);
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), $"remoter-diff-{DateTime.Now:HHmmss}.md");
        await File.WriteAllTextAsync(temp, TextUtil.HtmlToPlain(text), ct).ConfigureAwait(false);
        try
        {
            await _sender!.SendDocumentAsync(Cid(runtime.ChatId), temp, "📝 diff", ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* ничего */ }
        }
    }

    private async Task CmdRestartAsync(ChatRuntime runtime, CancellationToken ct)
    {
        await _sender!.SendPlainAsync(Cid(runtime.ChatId), "🔄 Перезапускаю сервис OpenCode…", null, ct)
            .ConfigureAwait(false);

        _ = Task.Run(() => RestartServiceAsync(runtime.ChatId, ct), CancellationToken.None);
    }

    private async Task RestartServiceAsync(long chatId, CancellationToken ct)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{_config.OpenCodeCommand} service restart\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _config.DefaultDirectory,
            };

            using var process = Process.Start(info)
                ?? throw new InvalidOperationException("Не удалось запустить процесс.");

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            _client.Reconnect();

            var text = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
            await _sender!.SendHtmlAsync(
                Cid(chatId),
                "✅ Сервис перезапущен\n<pre>" + TextUtil.EscapeHtml(TextUtil.Truncate(text, 500)) + "</pre>",
                BotMenus.MenuOnly(),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось перезапустить OpenCode: {ex.Message}");
            try
            {
                await _sender!.SendPlainAsync(Cid(chatId), $"❌ {ex.Message}", null, ct).ConfigureAwait(false);
            }
            catch
            {
                // ничего
            }
        }
    }

    private async Task CmdShutdownAsync(ChatRuntime runtime, CancellationToken ct)
    {
        await _sender!.SendHtmlAsync(
            Cid(runtime.ChatId),
            "Выключить Remoter? Бот перестанет отвечать до следующего запуска.",
            BotMenus.ShutdownConfirm(),
            ct).ConfigureAwait(false);
    }

    // ---------- helpers ----------

    private ChatRuntime GetRuntime(long chatId) =>
        _chats.GetOrAdd(chatId, id => new ChatRuntime(id));

    private static ChatId Cid(long chatId) => new(chatId);

    private static string? TryGetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>Очередь апдейтов одного чата: команды обрабатываются по порядку, но не блокируют polling.</summary>
    private sealed class ChatInbox
    {
        public Channel<Update> Updates { get; } = Channel.CreateUnbounded<Update>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public ChannelReader<Update> Reader => Updates.Reader;
        public ChannelWriter<Update> Writer => Updates.Writer;
        public int Started;
    }
}
