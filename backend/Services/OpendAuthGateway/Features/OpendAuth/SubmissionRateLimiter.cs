namespace OpendAuthGateway.Features.OpendAuth;

/// <summary>
/// #722, IADR-0320 決定 4: 投入（<c>POST /opend-auth/verify</c>）の流量制限。
/// <para>
/// 🔴 <b>呼び出し元ごとではなく、サービス全体で数える。</b> 守っている資源が全体で 1 つしか
/// 無いためである —— moomoo の SMS 送信枠（<c>resend</c> が消費する）と、OpenD のコンソール
/// （投入した行は次のプロンプトが消費するので、束ねて投げると順序が壊れる）。
/// 呼び出し元ごとに数えると、呼び出し元を増やすだけで全体の上限が上がってしまう。
/// </para>
/// <para>
/// 固定窓（fixed window）で足りる。窓の境界で瞬間的に上限の 2 倍まで通り得るのは承知のうえで、
/// ここで防ぎたいのは「総当たり」と「SMS 枠の焼き切り」であり、桁で効けばよい。
/// </para>
/// <para>
/// <b>時刻は <see cref="TimeProvider"/> から取る</b>（試験が実時間を待たずに窓を跨げるようにするため）。
/// </para>
/// </summary>
public sealed class SubmissionRateLimiter(TimeProvider timeProvider, int maxSubmissions, TimeSpan window)
{
    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _accepted = new();

    /// <summary>窓あたりの上限件数。</summary>
    public int MaxSubmissions { get; } = maxSubmissions > 0
        ? maxSubmissions
        : throw new ArgumentOutOfRangeException(nameof(maxSubmissions), maxSubmissions, "上限は 1 以上である必要がある。");

    /// <summary>窓の長さ。</summary>
    public TimeSpan Window { get; } = window > TimeSpan.Zero
        ? window
        : throw new ArgumentOutOfRangeException(nameof(window), window, "窓は正の長さである必要がある。");

    /// <summary>
    /// 1 件ぶんの枠を取る。取れなければ <c>false</c> を返し、<paramref name="retryAfter"/> に
    /// 「最も古い 1 件が窓から外れるまで」を入れる。
    /// </summary>
    public bool TryAcquire(out TimeSpan retryAfter)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            while (_accepted.Count > 0 && now - _accepted.Peek() >= Window) _accepted.Dequeue();

            if (_accepted.Count >= MaxSubmissions)
            {
                var wait = Window - (now - _accepted.Peek());
                retryAfter = wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
                return false;
            }

            _accepted.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}
