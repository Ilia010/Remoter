using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Remoter.App.OpenCode;

public sealed class OpenCodeClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private readonly AppConfig _config;
    private readonly ServiceLocator _locator;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private HttpClient? _http;
    private ServiceEndpoint? _endpoint;

    public OpenCodeClient(AppConfig config)
    {
        _config = config;
        _locator = new ServiceLocator(config);
    }

    public async Task<ServiceEndpoint> EnsureServiceAsync(CancellationToken ct)
    {
        await GetHttpAsync(ct).ConfigureAwait(false);
        return _endpoint!;
    }

    private async Task<HttpClient> GetHttpAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_http is not null && _endpoint is not null) return _http;

            var endpoint = await _locator.ResolveAsync(ct).ConfigureAwait(false);
            var handler = new HttpClientHandler { UseProxy = false };
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Authorization = ServiceLocator.BasicAuth(endpoint.Password);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Remoter/1.0");

            _endpoint = endpoint;
            _http = http;
            return http;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ResetConnection()
    {
        _gate.Wait();
        try
        {
            _http?.Dispose();
            _http = null;
            _endpoint = null;
            _locator.Invalidate();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var http = await GetHttpAsync(ct).ConfigureAwait(false);
            var baseUrl = _endpoint!.Url;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(RequestTimeout);

            try
            {
                using var request = new HttpRequestMessage(method, baseUrl + path);
                if (body is not null)
                    request.Content = JsonContent.Create(body, options: Json.Options);

                using var response = await http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    Log.Warn("OpenCode вернул 401, перечитываю service.json.");
                    ResetConnection();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var snippet = text.Length > 600 ? text[..600] : text;
                    throw new OpenCodeException(
                        $"OpenCode {method} {path} -> {(int)response.StatusCode}: {snippet}",
                        (int)response.StatusCode);
                }

                return text;
            }
            catch (HttpRequestException ex) when (attempt == 0)
            {
                Log.Warn($"Соединение с OpenCode потеряно ({ex.Message}), переподключаюсь.");
                ResetConnection();
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new OpenCodeException($"Таймаут запроса к OpenCode: {method} {path}");
            }
        }
    }

    private static JsonElement Unwrap(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("data", out var data)
            ? data.Clone()
            : doc.RootElement.Clone();
    }

    private static T? Unwrap<T>(string json)
    {
        var data = Unwrap(json);
        return Json.Deserialize<T>(data.GetRawText());
    }

    // ---------- sessions ----------

    public async Task<SessionInfo> CreateSessionAsync(
        string directory,
        IEnumerable<PermissionRule> permissions,
        string? agent,
        ModelRef? model,
        string? title,
        CancellationToken ct)
    {
        var body = new
        {
            location = new { directory },
            permissions = permissions.ToArray(),
            agent,
            model = model is null ? null : new { id = model.Id, providerID = model.ProviderID },
            title,
        };

        var json = await SendAsync(HttpMethod.Post, "/api/session", body, ct).ConfigureAwait(false);
        var session = Unwrap<SessionInfo>(json);
        return session ?? throw new OpenCodeException("OpenCode вернул пустую сессию.");
    }

    public async Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"/api/session/{Uri.EscapeDataString(sessionId)}", null, ct)
            .ConfigureAwait(false);
        return Unwrap<SessionInfo>(json);
    }

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(int limit, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"/api/session?limit={limit}&order=desc", null, ct)
            .ConfigureAwait(false);
        return Unwrap<List<SessionInfo>>(json) ?? [];
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken ct)
    {
        await SendAsync(HttpMethod.Delete, $"/api/session/{Uri.EscapeDataString(sessionId)}", null, ct)
            .ConfigureAwait(false);
    }

    public async Task PromptAsync(string sessionId, string text, CancellationToken ct)
    {
        await SendAsync(
            HttpMethod.Post,
            $"/api/session/{Uri.EscapeDataString(sessionId)}/prompt",
            new { text },
            ct).ConfigureAwait(false);
    }

    public async Task InterruptAsync(string sessionId, CancellationToken ct)
    {
        await SendAsync(
            HttpMethod.Post,
            $"/api/session/{Uri.EscapeDataString(sessionId)}/interrupt",
            null,
            ct).ConfigureAwait(false);
    }

    public async Task SwitchModelAsync(string sessionId, ModelRef model, CancellationToken ct)
    {
        await SendAsync(
            HttpMethod.Post,
            $"/api/session/{Uri.EscapeDataString(sessionId)}/model",
            new { model = new { id = model.Id, providerID = model.ProviderID } },
            ct).ConfigureAwait(false);
    }

    public async Task SwitchAgentAsync(string sessionId, string agent, CancellationToken ct)
    {
        await SendAsync(
            HttpMethod.Post,
            $"/api/session/{Uri.EscapeDataString(sessionId)}/agent",
            new { agent },
            ct).ConfigureAwait(false);
    }

    // ---------- permissions ----------

    public async Task<IReadOnlyList<PermissionRequest>> ListPermissionsAsync(string sessionId, CancellationToken ct)
    {
        var json = await SendAsync(
            HttpMethod.Get,
            $"/api/session/{Uri.EscapeDataString(sessionId)}/permission",
            null,
            ct).ConfigureAwait(false);
        return Unwrap<List<PermissionRequest>>(json) ?? [];
    }

    public async Task ReplyPermissionAsync(
        string sessionId, string requestId, string decision, string? message, CancellationToken ct)
    {
        await SendAsync(
            HttpMethod.Post,
            $"/api/session/{Uri.EscapeDataString(sessionId)}/permission/{Uri.EscapeDataString(requestId)}/reply",
            new { decision, message },
            ct).ConfigureAwait(false);
    }

    // ---------- messages / diff / models ----------

    public async Task<IReadOnlyList<SessionMessage>> ListAssistantMessagesAsync(
        string sessionId, int limit, CancellationToken ct)
    {
        var json = await SendAsync(
            HttpMethod.Get,
            $"/api/session/{Uri.EscapeDataString(sessionId)}/message?limit={limit}&order=desc&type=assistant",
            null,
            ct).ConfigureAwait(false);
        return Unwrap<List<SessionMessage>>(json) ?? [];
    }

    public async Task<string?> GetLastAssistantTextAsync(string sessionId, CancellationToken ct)
    {
        var messages = await ListAssistantMessagesAsync(sessionId, 25, ct).ConfigureAwait(false);
        foreach (var message in messages)
        {
            var text = message.Content?
                .Where(part => part.Type == "text" && !string.IsNullOrWhiteSpace(part.Text))
                .Select(part => part.Text!.Trim())
                .Where(part => part.Length > 0)
                .Aggregate("", (acc, part) => acc.Length == 0 ? part : acc + "\n\n" + part);

            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return null;
    }

    /// <summary>
    /// Достаёт из последних сообщений ошибку провайдера и превращает её в понятную строку.
    /// Нужна, чтобы вместо «агент завершил работу с ошибкой» показать причину (лимит, баланс, авторизация).
    /// </summary>
    public async Task<string?> GetLastAssistantErrorAsync(string sessionId, CancellationToken ct)
    {
        IReadOnlyList<SessionMessage> messages;
        try
        {
            messages = await ListAssistantMessagesAsync(sessionId, 5, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось прочитать ошибку выполнения: {ex.Message}");
            return null;
        }

        foreach (var message in messages)
        {
            if (message.Error is not { ValueKind: JsonValueKind.Object } error) continue;

            var type = TryString(error, "type");
            var detail = TryString(error, "message");
            var status = error.TryGetProperty("status", out var statusElement)
                         && statusElement.TryGetInt32(out var code)
                ? code
                : 0;
            var retryAfterMs = error.TryGetProperty("retryAfterMs", out var retryElement)
                               && retryElement.TryGetInt64(out var retryMs)
                ? retryMs
                : 0;

            var sb = new StringBuilder();
            sb.Append(string.IsNullOrWhiteSpace(detail)
                ? type ?? "Провайдер модели вернул ошибку."
                : detail);

            if (status > 0) sb.Append($" (HTTP {status})");

            if (retryAfterMs > 0)
                sb.Append("\n\nЛимит сбросится примерно через ").Append(HumanSpan(TimeSpan.FromMilliseconds(retryAfterMs))).Append('.');

            if (type is "provider.free-limit" or "provider.quota" or "provider.auth")
                sb.Append("\n\nМожно выбрать другую модель: /model");

            return sb.ToString();
        }

        return null;
    }

    private static string? TryString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string HumanSpan(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return $"{Math.Max(1, (int)span.TotalSeconds)} с";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} мин";
        return $"{(int)span.TotalHours} ч {span.Minutes} мин";
    }

    public async Task<IReadOnlyList<FileDiffInfo>> GetDiffAsync(string sessionId, CancellationToken ct)
    {
        var json = await SendAsync(
            HttpMethod.Get,
            $"/api/session/{Uri.EscapeDataString(sessionId)}/diff",
            null,
            ct).ConfigureAwait(false);
        return Unwrap<List<FileDiffInfo>>(json) ?? [];
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "/api/model", null, ct).ConfigureAwait(false);
        return Unwrap<List<ModelInfo>>(json) ?? [];
    }

    public async Task<IReadOnlyList<AgentInfo>> ListAgentsAsync(CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, "/api/agent", null, ct).ConfigureAwait(false);
        return Unwrap<List<AgentInfo>>(json) ?? [];
    }

    public void Reconnect() => ResetConnection();

    // ---------- events ----------

    public async IAsyncEnumerable<OpenCodeEvent> SubscribeEventsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);

        while (!ct.IsCancellationRequested)
        {
            SseConnection? connection = null;
            try
            {
                var http = await GetHttpAsync(ct).ConfigureAwait(false);
                var url = _endpoint!.Url + "/api/event";
                connection = await SseConnection.OpenAsync(http, url, ct).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось подписаться на события OpenCode: {ex.Message}");
            }

            if (connection is null)
            {
                ResetConnection();
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 15));
                continue;
            }

            await using (connection.ConfigureAwait(false))
            {
                while (!ct.IsCancellationRequested)
                {
                    OpenCodeEvent? ev;
                    try
                    {
                        ev = await connection.ReadAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        yield break;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Поток событий оборвался: {ex.Message}");
                        break;
                    }

                    if (ev is null) break;
                    yield return ev;
                }
            }

            ResetConnection();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _http?.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class SseConnection : IAsyncDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly StreamReader _reader;
        private readonly SseBuffer _buffer = new();

        private SseConnection(HttpResponseMessage response, StreamReader reader)
        {
            _response = response;
            _reader = reader;
        }

        public static async Task<SseConnection> OpenAsync(HttpClient http, string url, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

            var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                response.Dispose();
                throw new OpenCodeException(
                    $"SSE /api/event -> {(int)response.StatusCode}: {text}",
                    (int)response.StatusCode);
            }

            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new SseConnection(response, new StreamReader(stream, Encoding.UTF8));
        }

        public async Task<OpenCodeEvent?> ReadAsync(CancellationToken ct)
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) return _buffer.Flush();

                if (_buffer.Feed(line, out var ev)) return ev;
            }
        }

        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            _response.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
