using System.Diagnostics;
using System.Text.Json;

namespace Remoter.App.OpenCode;

/// <summary>
/// Находит запущенный сервис OpenCode (service.json) и при необходимости стартует его.
/// </summary>
public sealed class ServiceLocator
{
    private readonly AppConfig _config;
    private readonly object _sync = new();
    private ServiceEndpoint? _endpoint;
    private DateTime _lastStartAttempt = DateTime.MinValue;

    public ServiceLocator(AppConfig config)
    {
        _config = config;
    }

    public async Task<ServiceEndpoint> ResolveAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_endpoint is not null && IsProcessAlive(_endpoint.Pid))
                return _endpoint;
        }

        var endpoint = await TryReadEndpointAsync(ct).ConfigureAwait(false);
        if (endpoint is not null && await PingAsync(endpoint, ct).ConfigureAwait(false))
        {
            Set(endpoint);
            return endpoint;
        }

        if (!_config.AutoStartOpenCodeService)
            throw new OpenCodeException("Сервис OpenCode не запущен, а автозапуск отключён в конфиге.");

        await StartServiceAsync(ct).ConfigureAwait(false);

        var deadline = DateTime.UtcNow.AddSeconds(_config.OpenCodeStartTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            endpoint = await TryReadEndpointAsync(ct).ConfigureAwait(false);
            if (endpoint is not null && await PingAsync(endpoint, ct).ConfigureAwait(false))
            {
                Set(endpoint);
                Log.Info($"OpenCode service готов: {endpoint.Url} (pid {endpoint.Pid}, v{endpoint.Version})");
                return endpoint;
            }
            await Task.Delay(700, ct).ConfigureAwait(false);
        }

        throw new OpenCodeException("Не удалось дождаться запуска сервиса OpenCode. Проверь `opencode service status`.");
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _endpoint = null;
        }
    }

    private void Set(ServiceEndpoint endpoint)
    {
        lock (_sync)
        {
            _endpoint = endpoint;
        }
    }

    private async Task<ServiceEndpoint?> TryReadEndpointAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(_config.ServiceStatePath)) return null;

                await using var stream = new FileStream(
                    _config.ServiceStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

                var root = doc.RootElement;
                var url = root.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
                var password = root.TryGetProperty("password", out var passProp) ? passProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(password)) return null;

                return new ServiceEndpoint
                {
                    Url = url.TrimEnd('/'),
                    Password = password,
                    Version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                    Pid = root.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pid) ? pid : 0,
                };
            }
            catch (IOException)
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static async Task<bool> PingAsync(ServiceEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Authorization = BasicAuth(endpoint.Password);

            using var response = await http.GetAsync($"{endpoint.Url}/api/info", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task StartServiceAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastStartAttempt < TimeSpan.FromSeconds(10))
            return;
        _lastStartAttempt = DateTime.UtcNow;

        Log.Info("Стартую сервис OpenCode...");
        try
        {
            var command = _config.OpenCodeCommand;
            var info = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{command} service start\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _config.DefaultDirectory,
            };

            using var process = Process.Start(info);
            if (process is null)
                throw new OpenCodeException("Не удалось запустить процесс opencode.");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var output = (await stdout.ConfigureAwait(false)).Trim();
            var error = (await stderr.ConfigureAwait(false)).Trim();
            if (!string.IsNullOrWhiteSpace(output)) Log.Info($"opencode service start: {output}");
            if (!string.IsNullOrWhiteSpace(error)) Log.Warn($"opencode service start (stderr): {error}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OpenCodeException($"Не удалось запустить сервис OpenCode: {ex.Message}", 0, ex);
        }
    }

    public static System.Net.Http.Headers.AuthenticationHeaderValue BasicAuth(string password) =>
        new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"opencode:{password}")));

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
