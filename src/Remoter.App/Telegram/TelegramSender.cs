using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Message = Telegram.Bot.Types.Message;

namespace Remoter.App.Telegram;

/// <summary>Отправка сообщений с учётом лимитов Telegram и запасным plain-текстом.</summary>
public sealed class TelegramSender
{
    private readonly ITelegramBotClient _bot;
    private readonly AppConfig _config;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _editGate = new(1, 1);
    private readonly Dictionary<long, DateTime> _lastEdit = new();
    private readonly object _sync = new();
    private DateTime _lastSend = DateTime.MinValue;

    public TelegramSender(ITelegramBotClient bot, AppConfig config)
    {
        _bot = bot;
        _config = config;
    }

    public async Task<Message?> SendAsync(
        ChatId chatId, string markdown, ReplyMarkup? markup, CancellationToken ct)
    {
        var chunks = TextUtil.SplitForTelegram(markdown, _config.MaxMessageChars);
        Message? last = null;

        for (var i = 0; i < chunks.Count; i++)
        {
            var html = TextUtil.ToTelegramHtml(chunks[i]);
            var chunkMarkup = i == chunks.Count - 1 ? markup : null;
            last = await SendChunkAsync(chatId, html, chunks[i], chunkMarkup, ct).ConfigureAwait(false);
            if (i < chunks.Count - 1) await Task.Delay(120, ct).ConfigureAwait(false);
        }

        return last;
    }

    /// <summary>Отправляет уже готовый HTML (команды, статусы, подтверждения).</summary>
    public async Task<Message?> SendHtmlAsync(
        ChatId chatId, string html, ReplyMarkup? markup, CancellationToken ct)
    {
        var chunks = TextUtil.SplitByLines(html, _config.MaxMessageChars);
        if (chunks is null)
        {
            // Строку с тегами резать нельзя — уходим в обычный текст.
            return await SendPlainAsync(chatId, TextUtil.HtmlToPlain(html), markup, ct).ConfigureAwait(false);
        }

        Message? last = null;
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunkMarkup = i == chunks.Count - 1 ? markup : null;
            last = await SendChunkAsync(
                chatId, chunks[i], TextUtil.HtmlToPlain(chunks[i]), chunkMarkup, ct).ConfigureAwait(false);
            if (i < chunks.Count - 1) await Task.Delay(120, ct).ConfigureAwait(false);
        }

        return last;
    }

    public async Task<Message?> SendPlainAsync(
        ChatId chatId, string text, ReplyMarkup? markup, CancellationToken ct)
    {
        var chunks = TextUtil.SplitForTelegram(text, _config.MaxMessageChars);
        Message? last = null;

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunkMarkup = i == chunks.Count - 1 ? markup : null;
            last = await SendChunkAsync(
                chatId, TextUtil.EscapeHtml(chunks[i]), chunks[i], chunkMarkup, ct).ConfigureAwait(false);
            if (i < chunks.Count - 1) await Task.Delay(120, ct).ConfigureAwait(false);
        }

        return last;
    }

    private async Task<Message?> SendChunkAsync(
        ChatId chatId, string html, string plain, ReplyMarkup? markup, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = 250 - (DateTime.UtcNow - _lastSend).TotalMilliseconds;
            if (wait > 0) await Task.Delay((int)wait, ct).ConfigureAwait(false);
            _lastSend = DateTime.UtcNow;

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await _bot.SendMessage(
                        chatId,
                        html,
                        parseMode: ParseMode.Html,
                        replyMarkup: markup,
                        linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                        cancellationToken: ct).ConfigureAwait(false);
                }
                catch (ApiRequestException ex) when (IsParseError(ex) && attempt == 0)
                {
                    Log.Warn($"HTML не разобрался, отправляю как текст: {ex.Message}");
                    return await _bot.SendMessage(
                        chatId,
                        plain,
                        replyMarkup: markup,
                        linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                        cancellationToken: ct).ConfigureAwait(false);
                }
                catch (ApiRequestException ex) when (TryGetRetryAfter(ex, out var retryAfter) && attempt < 3)
                {
                    await Task.Delay(retryAfter, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public Task EditAsync(
        ChatId chatId, int messageId, string markdown, InlineKeyboardMarkup? markup, CancellationToken ct, bool force = false) =>
        EditCoreAsync(chatId, messageId, TextUtil.ToTelegramHtml(markdown), markdown, markup, ct, force);

    /// <summary>Правит сообщение, содержимое которого уже является HTML.</summary>
    public Task EditHtmlAsync(
        ChatId chatId, int messageId, string html, InlineKeyboardMarkup? markup, CancellationToken ct, bool force = false) =>
        EditCoreAsync(chatId, messageId, html, TextUtil.HtmlToPlain(html), markup, ct, force);

    private async Task EditCoreAsync(
        ChatId chatId, int messageId, string html, string plain, InlineKeyboardMarkup? markup, CancellationToken ct, bool force)
    {
        if (messageId <= 0) return;

        await _editGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var chatKey = chatId.Identifier ?? 0;
            lock (_sync)
            {
                if (!force && _lastEdit.TryGetValue(chatKey, out var last) &&
                    (DateTime.UtcNow - last).TotalMilliseconds < 900)
                {
                    // Telegram не любит частые правки — пропускаем промежуточный кадр.
                    return;
                }
                _lastEdit[chatKey] = DateTime.UtcNow;
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await _bot.EditMessageText(
                        chatId,
                        messageId,
                        html,
                        parseMode: ParseMode.Html,
                        replyMarkup: markup,
                        linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                        cancellationToken: ct).ConfigureAwait(false);
                    return;
                }
                catch (ApiRequestException ex) when (IsNotModified(ex))
                {
                    return;
                }
                catch (ApiRequestException ex) when (IsParseError(ex) && attempt == 0)
                {
                    await _bot.EditMessageText(
                        chatId,
                        messageId,
                        TextUtil.EscapeHtml(plain),
                        parseMode: ParseMode.Html,
                        replyMarkup: markup,
                        cancellationToken: ct).ConfigureAwait(false);
                    return;
                }
                catch (ApiRequestException ex) when (TryGetRetryAfter(ex, out var retryAfter) && attempt < 3)
                {
                    await Task.Delay(retryAfter, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось изменить сообщение: {ex.Message}");
        }
        finally
        {
            _editGate.Release();
        }
    }

    public async Task DeleteAsync(ChatId chatId, int messageId, CancellationToken ct)
    {
        if (messageId <= 0) return;
        try
        {
            await _bot.DeleteMessage(chatId, messageId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось удалить сообщение: {ex.Message}");
        }
    }

    public async Task SendPhotoAsync(ChatId chatId, string path, string caption, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var stream = File.OpenRead(path);
            await _bot.SendPhoto(
                chatId,
                InputFile.FromStream(stream, Path.GetFileName(path)),
                caption: TextUtil.EscapeHtml(caption),
                parseMode: ParseMode.Html,
                cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task SendDocumentAsync(ChatId chatId, string path, string caption, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var stream = File.OpenRead(path);
            await _bot.SendDocument(
                chatId,
                InputFile.FromStream(stream, Path.GetFileName(path)),
                caption: TextUtil.EscapeHtml(caption),
                parseMode: ParseMode.Html,
                cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task AnswerCallbackAsync(string callbackId, string text, bool alert, CancellationToken ct)
    {
        try
        {
            await _bot.AnswerCallbackQuery(callbackId, text, showAlert: alert, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"AnswerCallbackQuery: {ex.Message}");
        }
    }

    private static bool IsParseError(ApiRequestException ex) =>
        ex.ErrorCode == 400 &&
        (ex.Message.Contains("parse entities", StringComparison.OrdinalIgnoreCase)
         || ex.Message.Contains("can't parse", StringComparison.OrdinalIgnoreCase)
         || ex.Message.Contains("unsupported start tag", StringComparison.OrdinalIgnoreCase));

    private static bool IsNotModified(ApiRequestException ex) =>
        ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetRetryAfter(ApiRequestException ex, out TimeSpan delay)
    {
        var seconds = ex.Parameters?.RetryAfter;
        if (seconds is > 0)
        {
            delay = TimeSpan.FromSeconds(seconds.Value + 1);
            return true;
        }
        delay = TimeSpan.Zero;
        return false;
    }
}
