using System.Text;
using System.Text.Json;

namespace Remoter.App.OpenCode;

/// <summary>
/// Разбирает поток SSE на события. Вынесено из сетевого кода, чтобы покрыть тестами
/// без поднятия HTTP-сервера.
/// </summary>
internal sealed class SseBuffer
{
    private readonly StringBuilder _data = new();

    public bool HasData => _data.Length > 0;

    /// <summary>
    /// Скармливает одну строку потока. Возвращает true, когда собрано событие.
    /// Событие может быть null, если JSON оказался битым — такие кадры просто пропускаем.
    /// </summary>
    public bool Feed(string line, out OpenCodeEvent? ev)
    {
        ev = null;

        if (line.Length == 0)
        {
            if (_data.Length == 0) return false;
            ev = Parse();
            return true;
        }

        if (line[0] == ':') return false; // комментарий/keep-alive

        if (line.StartsWith("data:", StringComparison.Ordinal))
        {
            var chunk = line[5..].TrimStart();
            if (_data.Length > 0) _data.Append('\n');
            _data.Append(chunk);
        }

        return false;
    }

    /// <summary>Отдаёт последнее недописанное событие при закрытии потока.</summary>
    public OpenCodeEvent? Flush() => _data.Length > 0 ? Parse() : null;

    private OpenCodeEvent? Parse()
    {
        var json = _data.ToString();
        _data.Clear();
        return ParseEvent(json);
    }

    internal static OpenCodeEvent? ParseEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(type)) return null;

            return new OpenCodeEvent
            {
                Id = root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                Type = type,
                Data = root.TryGetProperty("data", out var data) ? data.Clone() : default,
            };
        }
        catch (JsonException ex)
        {
            Log.Warn($"Некорректное событие SSE: {ex.Message}");
            return null;
        }
    }
}
