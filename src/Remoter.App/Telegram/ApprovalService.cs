using System.Collections.Concurrent;
using Remoter.App.OpenCode;

namespace Remoter.App.Telegram;

public sealed class PendingApproval
{
    public string ShortId { get; init; } = "";
    public long ChatId { get; init; }
    public string SessionId { get; init; } = "";
    public string RequestId { get; init; } = "";
    public PermissionRequest Request { get; init; } = new();
    public int MessageId { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>Хранит запросы разрешений, ожидающие нажатия кнопки.</summary>
public sealed class ApprovalService
{
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new();
    private int _counter;

    public PendingApproval Register(long chatId, PermissionRequest request)
    {
        var shortId = Interlocked.Increment(ref _counter).ToString("x8");
        var pending = new PendingApproval
        {
            ShortId = shortId,
            ChatId = chatId,
            SessionId = request.SessionID,
            RequestId = request.Id,
            Request = request,
        };

        _pending[shortId] = pending;
        return pending;
    }

    public bool TryRemove(string shortId, out PendingApproval pending) =>
        _pending.TryRemove(shortId, out pending!);

    public IReadOnlyCollection<PendingApproval> Snapshot() => _pending.Values.ToArray();

    public bool Contains(string requestId) => _pending.Values.Any(p => p.RequestId == requestId);
}
