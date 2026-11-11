using System.Text;
using System.Text.Json;
using Remoter.App.OpenCode;

namespace Remoter.App;

/// <summary>
/// Проверка всей цепочки без Telegram: сервис → сессия → промпт → события → финальный ответ.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(AppConfig config)
    {
        Out("Remoter self-test");
        Out($"  state: {config.StatePath}");
        Out($"  model: {config.Model.ProviderID}/{config.Model.ID}");
        Out($"  dir:   {config.DefaultDirectory}");
        Out("");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await using var client = new OpenCodeClient(config);

        try
        {
            var endpoint = await client.EnsureServiceAsync(cts.Token).ConfigureAwait(false);
            Out($"[ok] service {endpoint.Url} v{endpoint.Version} (pid {endpoint.Pid})");

            var directory = Directory.Exists(config.DefaultDirectory)
                ? config.DefaultDirectory
                : Path.GetTempPath();

            // Проверяем, что реальный набор правил (camelCase) принимается сервисом.
            var policySession = await client.CreateSessionAsync(
                directory,
                PermissionPolicy.For(config.AutoApprove),
                "build",
                new ModelRef { Id = config.Model.ID, ProviderID = config.Model.ProviderID },
                "remoter-policy-check",
                cts.Token).ConfigureAwait(false);

            Out($"[ok] permissions payload accepted: {policySession.Permissions?.Length ?? 0} rules");

            try
            {
                await client.DeleteSessionAsync(policySession.Id, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Out($"[warn] не удалось удалить policy-сессию: {ex.Message}");
            }

            // Важно: правила должны совпадать с боевыми. Если запретить read (или *),
            // OpenCode срывается в ложную ошибку "Free usage limit reached" ещё до
            // обращения к модели, и проверка показывает несуществующую проблему.
            var session = await client.CreateSessionAsync(
                directory,
                PermissionPolicy.For(config.AutoApprove),
                "build",
                new ModelRef { Id = config.Model.ID, ProviderID = config.Model.ProviderID },
                "remoter-selftest",
                cts.Token).ConfigureAwait(false);

            Out($"[ok] session {session.Id} in {session.Location?.Directory}");
            Out($"[ok] permissions on session: {session.Permissions?.Length ?? 0} rules");

            var finished = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamed = new StringBuilder();

            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var ev in client.SubscribeEventsAsync(cts.Token).ConfigureAwait(false))
                    {
                        var sessionId = GetString(ev.Data, "sessionID");
                        if (sessionId != session.Id) continue;

                        switch (ev.Type)
                        {
                            case "session.text.delta":
                            {
                                var delta = Json.Deserialize<TextDelta>(ev.Data.GetRawText());
                                if (delta is not null) streamed.Append(delta.Delta);
                                break;
                            }
                            case "session.execution.succeeded":
                                finished.TrySetResult("succeeded");
                                return;
                            case "session.execution.failed":
                                finished.TrySetResult("failed");
                                return;
                            case "session.execution.interrupted":
                                finished.TrySetResult("interrupted");
                                return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    finished.TrySetException(ex);
                }
            }, cts.Token);

            await client.PromptAsync(session.Id, "Ответь ровно одним словом: ок", cts.Token)
                .ConfigureAwait(false);

            var result = await finished.Task.ConfigureAwait(false);
            Out($"[ok] execution: {result}");

            if (result != "succeeded")
            {
                try
                {
                    var messages = await client.ListAssistantMessagesAsync(session.Id, 5, cts.Token)
                        .ConfigureAwait(false);
                    foreach (var message in messages)
                    {
                        Out($"[diag] message {message.Id} finish={message.Finish} raw={message.RawFinish}");
                        if (message.Error is { } error) Out($"[diag] error: {error.GetRawText()}");
                    }

                    var friendly = await client.GetLastAssistantErrorAsync(session.Id, cts.Token)
                        .ConfigureAwait(false);
                    Out($"[diag] friendly error: {friendly ?? "(нет)"}");
                }
                catch (Exception ex)
                {
                    Out($"[diag] не удалось прочитать сообщения: {ex.Message}");
                }
            }

            var finalText = await client.GetLastAssistantTextAsync(session.Id, cts.Token).ConfigureAwait(false);
            var answer = string.IsNullOrWhiteSpace(finalText) ? streamed.ToString().Trim() : finalText;
            Out($"[ok] answer: {answer}");

            try
            {
                await client.DeleteSessionAsync(session.Id, cts.Token).ConfigureAwait(false);
                Out("[ok] test session deleted");
            }
            catch (Exception ex)
            {
                Out($"[warn] не удалось удалить тестовую сессию: {ex.Message}");
            }

            return result == "succeeded" ? 0 : 2;
        }
        catch (Exception ex)
        {
            Err($"[fail] {ex}");
            return 1;
        }
    }

    private static void Out(string message)
    {
        try { Console.WriteLine(message); } catch { /* нет консоли */ }
        Log.Info("[selftest] " + message);
    }

    private static void Err(string message)
    {
        try { Console.Error.WriteLine(message); } catch { /* нет консоли */ }
        Log.Error("[selftest] " + message);
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
