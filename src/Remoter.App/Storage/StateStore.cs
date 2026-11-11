using System.Text.Json;
using System.Text.Json.Serialization;

namespace Remoter.App.Storage;

public sealed class ChatState
{
    public string? SessionID { get; set; }
    public string? Directory { get; set; }
    public string? ModelID { get; set; }
    public string? ModelProvider { get; set; }
    public string? Agent { get; set; }
    public long Updated { get; set; }
}

public sealed class PersistedState
{
    public Dictionary<string, ChatState> Chats { get; set; } = new();
    public string? LastActiveSessionID { get; set; }
}

public sealed class StateStore
{
    private readonly string _path;
    private readonly object _sync = new();
    private PersistedState _state;

    public StateStore(string path)
    {
        _path = path;
        _state = Load(path);
    }

    public ChatState Get(long chatId)
    {
        lock (_sync)
        {
            var key = chatId.ToString();
            if (!_state.Chats.TryGetValue(key, out var chat))
            {
                chat = new ChatState();
                _state.Chats[key] = chat;
            }
            return chat;
        }
    }

    public void Update(long chatId, Action<ChatState> mutate)
    {
        lock (_sync)
        {
            var key = chatId.ToString();
            if (!_state.Chats.TryGetValue(key, out var chat))
            {
                chat = new ChatState();
                _state.Chats[key] = chat;
            }

            mutate(chat);
            chat.Updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Save();
        }
    }

    private static PersistedState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new PersistedState();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PersistedState>(json, JsonOptions) ?? new PersistedState();
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось прочитать state.json: {ex.Message}");
            return new PersistedState();
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось сохранить state.json", ex);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
