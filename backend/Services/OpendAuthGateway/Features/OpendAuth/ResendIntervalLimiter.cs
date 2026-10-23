namespace OpendAuthGateway.Features.OpendAuth;

/// <summary>再送の取得の結果。</summary>
public enum ResendAdmission
{
    /// <summary>通した（② を記録し、① の枠を 1 件取った）。</summary>
    Accepted,

    /// <summary>② 再送だけの間隔が空いていない（① の枠は消費していない）。</summary>
    IntervalNotElapsed,

    /// <summary>① サービス全体のレート制限に掛かった（② は記録していない）。</summary>
    OverallLimited,
}

/// <summary>
/// #1456, SC-04, IADR-0579: SMS の<b>再送だけ</b>に課す間隔（既定 60 秒に 1 回。暫定値・実測待ち）。
/// <para>
/// ① サービス全体のレート制限（<see cref="SubmissionRateLimiter"/>・投入と再送の合算・IADR-0322 決定 4）は残し、
/// ② をその<b>内側に重ねる</b>。検証コードの投入（<c>/verify</c>）はここを通らない（① だけが効く）。
/// </para>
/// <para>
/// 🔴 <b>② の判定 → ① の取得 → ② の記録を 1 つの lock の内で行う。</b> 判定と記録を分けると、
/// 並行する 2 本の再送が両方とも「空いている」と読んで両方通る。
/// ② で拒否したときは ① の枠を消費しない（拒否された要求は資源を使わない）。
/// ① で拒否したときは ② を記録しない（送っていない再送で次の再送を 60 秒待たせない）。
/// </para>
/// <para>
/// 状態はプロセス内に持つ。サイドカーは OpenD Pod（<c>replicas: 1</c>・<c>Recreate</c>）に同居するため
/// 数える場所はクラスタに 1 つであり、再起動で窓が消えても失うのは高々 1 回ぶんの間隔である（IADR-0579）。
/// </para>
/// </summary>
public sealed class ResendIntervalLimiter(TimeProvider timeProvider, TimeSpan interval, SubmissionRateLimiter overall)
{
    private readonly Lock _gate = new();
    private DateTimeOffset? _lastAccepted;

    /// <summary>再送どうしの最小の間隔。</summary>
    public TimeSpan Interval { get; } = interval > TimeSpan.Zero
        ? interval
        : throw new ArgumentOutOfRangeException(nameof(interval), interval, "間隔は正の長さである必要がある。");

    /// <summary>
    /// 再送 1 件ぶんを取る。拒否したら <paramref name="retryAfter"/> に「① と ② の両方が空くまで」を入れる
    /// （② だけの残りを返すと、その時刻に再送しても ① で再び拒否され得る）。
    /// </summary>
    public ResendAdmission TryAcquire(out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (_lastAccepted is { } last && now - last < Interval)
            {
                var intervalWait = Interval - (now - last);
                var overallWait = overall.CurrentWait();
                retryAfter = intervalWait > overallWait ? intervalWait : overallWait;
                return ResendAdmission.IntervalNotElapsed;
            }

            if (!overall.TryAcquire(out retryAfter)) return ResendAdmission.OverallLimited;

            _lastAccepted = now;
            retryAfter = TimeSpan.Zero;
            return ResendAdmission.Accepted;
        }
    }
}
