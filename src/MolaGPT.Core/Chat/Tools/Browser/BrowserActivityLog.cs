using System.Text.Json;

namespace MolaGPT.Core.Chat.Tools.Browser;

/// <summary>
/// 浏览器做过什么的流水账。
///
/// 存在的理由不是排障，是信任：这个工具带着用户的真实登录态在他自己的浏览器里动手，
/// 而动作发生在一个我们不拥有的界面上——标签组能显示「现在在哪」，显示不了「刚才做过
/// 什么」。一份按时间排的记录加一个可撤销的授权列表，是在出事之前就降低焦虑的东西；
/// 等出事了再去翻，已经晚了。
///
/// 只记元数据：站点、动作、成败、时间。页面内容、填入的值一律不记——那正是这套架构
/// 承诺不外流的东西，没有理由让它在我们自己的库里留一份。
/// </summary>
public sealed class BrowserActivityLog
{
    public const int Capacity = 200;

    private readonly object _gate = new();
    private readonly LinkedList<BrowserActivityEntry> _entries = new();
    private readonly Action<string>? _save;

    /// <summary>新增一条时触发。App 层据此刷新设置页，并在桥断开时发通知。</summary>
    public event EventHandler<BrowserActivityEntry>? Recorded;

    public BrowserActivityLog(Func<string?>? load = null, Action<string>? save = null)
    {
        _save = save;
        if (load?.Invoke() is { Length: > 0 } raw) Restore(raw);
    }

    public IReadOnlyList<BrowserActivityEntry> Recent()
    {
        lock (_gate) return _entries.ToArray();
    }

    public void Record(BrowserActivityEntry entry)
    {
        lock (_gate)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > Capacity) _entries.RemoveLast();
            Persist();
        }
        Recorded?.Invoke(this, entry);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Persist();
        }
        Recorded?.Invoke(this, BrowserActivityEntry.Empty);
    }

    private void Persist()
    {
        if (_save is null) return;
        try { _save(JsonSerializer.Serialize(_entries)); }
        catch { /* 记录坏了不该拖垮一次浏览器调用 */ }
    }

    private void Restore(string raw)
    {
        try
        {
            var restored = JsonSerializer.Deserialize<List<BrowserActivityEntry>>(raw);
            if (restored is null) return;
            foreach (var entry in restored.Take(Capacity)) _entries.AddLast(entry);
        }
        catch (JsonException)
        {
            // 读不回来就当没有，不要因为一条坏记录让整页打不开。
        }
    }
}

/// <param name="Host">解析不出站点时为 null——比如 click 发生在一个已经关掉的会话里。</param>
/// <param name="Note">失败原因或受保护动作的理由。不含页面内容。</param>
public sealed record BrowserActivityEntry(
    DateTimeOffset At,
    string Action,
    string? Host = null,
    bool Success = true,
    string? Note = null)
{
    public static readonly BrowserActivityEntry Empty = new(default, string.Empty);

    /// <summary>桥/扩展不可用导致的失败——App 层据此决定要不要发通知。</summary>
    public bool IsBridgeFailure =>
        !Success && Note is { } note
        && (note.Contains("扩展未连接", StringComparison.Ordinal)
            || note.Contains("本机服务", StringComparison.Ordinal));
}
