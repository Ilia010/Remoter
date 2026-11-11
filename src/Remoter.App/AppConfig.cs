using System.Text.Json;
using System.Text.Json.Serialization;

namespace Remoter.App;

public sealed class ModelSettings
{
    public string ProviderID { get; set; } = "opencode";
    public string ID { get; set; } = "deepseek-v4.1-flash-free";
}

public sealed class AppConfig
{
    public string TelegramBotToken { get; set; } = "";
    public long[] AllowedUserIds { get; set; } = [];

    public string DefaultDirectory { get; set; } = "~";
    public ModelSettings Model { get; set; } = new();
    public string Agent { get; set; } = "build";

    public int ApprovalTimeoutSeconds { get; set; } = 300;
    public int ProgressEditIntervalMs { get; set; } = 1800;
    public int MaxMessageChars { get; set; } = 3800;
    public int MaxTelegramDocumentBytes { get; set; } = 49 * 1024 * 1024;

    public bool AutoStartOpenCodeService { get; set; } = true;
    public bool PreventSleep { get; set; }

    /// <summary>Не спрашивать подтверждений: все действия выполняются сразу.</summary>
    public bool AutoApprove { get; set; }
    public string OpenCodeCommand { get; set; } = "opencode.cmd";
    public string ServiceStatePath { get; set; } = "";
    public int OpenCodeStartTimeoutSeconds { get; set; } = 90;

    public bool RequireMentionInGroups { get; set; } = true;

    public string DataDirectory { get; set; } = "";
    public string StatePath { get; set; } = "";
    public string LogDirectory { get; set; } = "";

    [JsonIgnore]
    public string? LoadedEnvFile { get; set; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(TelegramBotToken) && AllowedUserIds.Length > 0;

    public static AppConfig Load(string[] args)
    {
        var baseDir = AppContext.BaseDirectory;
        var config = new AppConfig();

        foreach (var candidate in ConfigCandidates(baseDir, "appsettings.json"))
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(
                    File.ReadAllText(candidate),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });
                if (loaded is not null)
                {
                    config = loaded;
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Не удалось прочитать {candidate}: {ex.Message}");
            }
        }

        foreach (var candidate in ConfigCandidates(baseDir, ".env"))
        {
            if (!File.Exists(candidate)) continue;
            config.ApplyEnvFile(candidate);
            config.LoadedEnvFile = candidate;
            break;
        }

        config.ApplyEnvironment();

        var dataDir = string.IsNullOrWhiteSpace(config.DataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Remoter")
            : ExpandPath(config.DataDirectory);

        config.DataDirectory = dataDir;
        if (string.IsNullOrWhiteSpace(config.StatePath))
            config.StatePath = Path.Combine(dataDir, "state.json");
        if (string.IsNullOrWhiteSpace(config.LogDirectory))
            config.LogDirectory = Path.Combine(dataDir, "logs");
        if (string.IsNullOrWhiteSpace(config.ServiceStatePath))
            config.ServiceStatePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "state", "opencode", "service.json");

        config.DefaultDirectory = ExpandPath(config.DefaultDirectory);

        return config;
    }

    private static IEnumerable<string> ConfigCandidates(string baseDir, string fileName)
    {
        yield return Path.Combine(baseDir, fileName);

        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 4 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (!string.Equals(candidate, Path.Combine(baseDir, fileName), StringComparison.OrdinalIgnoreCase))
                yield return candidate;
        }
    }

    private void ApplyEnvFile(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];

            ApplySetting(key, value);
        }
    }

    private void ApplyEnvironment()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            if (string.IsNullOrWhiteSpace(key)) continue;
            if (!key.StartsWith("TELEGRAM_", StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith("REMOTER_", StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith("ALLOWED_", StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith("DEFAULT_", StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith("MODEL_", StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith("AGENT", StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith("APPROVAL_", StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith("AUTO_START", StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith("PREVENT_", StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith("AUTO_APPROVE", StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith("NO_APPROVAL", StringComparison.OrdinalIgnoreCase)
                && !key.StartsWith("OPENCODE_", StringComparison.OrdinalIgnoreCase))
                continue;

            ApplySetting(key, entry.Value?.ToString() ?? "");
        }
    }

    internal void ApplySetting(string key, string value)
    {
        var name = key.StartsWith("REMOTER_", StringComparison.OrdinalIgnoreCase) ? key[8..] : key;

        switch (name.ToUpperInvariant())
        {
            case "TELEGRAM_BOT_TOKEN":
            case "BOT_TOKEN":
                TelegramBotToken = value;
                break;
            case "ALLOWED_USER_IDS":
            case "ALLOWED_USERS":
                AllowedUserIds = value
                    .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => long.TryParse(s, out var id) ? id : (long?)null)
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .ToArray();
                break;
            case "DEFAULT_DIRECTORY":
            case "WORKDIR":
                DefaultDirectory = value;
                break;
            case "MODEL_ID":
                Model.ID = value;
                break;
            case "MODEL_PROVIDER":
                Model.ProviderID = value;
                break;
            case "AGENT":
                Agent = value;
                break;
            case "APPROVAL_TIMEOUT_SECONDS":
                if (int.TryParse(value, out var timeout)) ApprovalTimeoutSeconds = Math.Max(15, timeout);
                break;
            case "PROGRESS_EDIT_INTERVAL_MS":
                if (int.TryParse(value, out var interval)) ProgressEditIntervalMs = Math.Max(500, interval);
                break;
            case "MAX_MESSAGE_CHARS":
                if (int.TryParse(value, out var maxChars)) MaxMessageChars = Math.Clamp(maxChars, 500, 4096);
                break;
            case "AUTO_START_OPENCODE":
            case "AUTO_START_OPENCODE_SERVICE":
                AutoStartOpenCodeService = IsTrue(value);
                break;
            case "PREVENT_SLEEP":
            case "KEEP_AWAKE":
                PreventSleep = IsTrue(value);
                break;
            case "AUTO_APPROVE":
            case "NO_APPROVAL":
                AutoApprove = IsTrue(value);
                break;
            case "OPENCODE_COMMAND":
                OpenCodeCommand = value;
                break;
            case "OPENCODE_STATE_PATH":
            case "SERVICE_STATE_PATH":
                ServiceStatePath = value;
                break;
            case "DATA_DIRECTORY":
                DataDirectory = value;
                break;
            case "STATE_PATH":
                StatePath = value;
                break;
            case "LOG_DIRECTORY":
                LogDirectory = value;
                break;
        }
    }

    private static bool IsTrue(string value) =>
        value.Equals("1", StringComparison.OrdinalIgnoreCase)
        || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || value.Equals("on", StringComparison.OrdinalIgnoreCase);

    public static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded == "~" || expanded.StartsWith("~/") || expanded.StartsWith("~\\"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = expanded.Length <= 2 ? home : Path.Combine(home, expanded[2..]);
        }
        return Path.GetFullPath(expanded);
    }
}
