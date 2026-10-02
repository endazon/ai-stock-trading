using System.Collections.Concurrent;

namespace ReportService.Features.Reports;

// FR-06, #840, IADR-0352 決定 3・4: 「依存先が一過性に落ちているので今回は見送る」を**何回まで**許すか。
//
// 再試行そのものは常駐の巡回が担う（冪等の根拠は PeriodKey の不在・IADR-0115 決定3 のまま）。
// 本クラスが持つのは期間ごとの見送り回数だけであり、**上限を超えたら見送らせない**
// ＝縮退した報告書を従来どおり生成・提示させる（依存先が戻らないまま報告書が永久に出ない状態を作らない）。
//
// 状態はプロセス内に持つ（singleton）。再起動で回数は 0 へ戻るが、再起動こそが一過性の失敗の主因であり、
// 戻って困る向きではない（見送りが増えるだけで、上限は再び効く）。多重レプリカでは各々が数える。
public sealed class ReportGenerationDeferralTracker(ReportDeferralSettings settings)
{
    private readonly ConcurrentDictionary<string, int> _deferrals = new(StringComparer.Ordinal);

    /// <summary>上限（回）。0 は「見送らない」＝本変更前と同じく即座に縮退した報告書を出す。</summary>
    public int MaxDeferrals => settings.MaxDeferrals;

    /// <summary>
    /// FR-06, #1156, IADR-0480 決定 3: 見送りの上限（回）。<paramref name="core"/> が真（中核の入力が一過性に欠けている）なら
    /// 中核の上限、偽なら通常の上限。
    /// </summary>
    public int LimitFor(bool core) => settings.LimitFor(core);

    /// <summary>
    /// **次に見送るとしたら待つことになる時間**（回数は増やさない）。上限に達していれば <c>null</c>。
    /// <para>
    /// #866: 呼び出し側は「その待ち時間の後もこの期間がまだ生成対象か」を確かめてから見送る。
    /// 回数を消費してから取り消すと、取り消し漏れが「上限だけ減る」側の事故になるため**先読みで分ける**。
    /// </para>
    /// </summary>
    public TimeSpan? NextDelay(string periodKey, bool core = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

        var used = _deferrals.GetValueOrDefault(periodKey);
        return used >= settings.LimitFor(core) ? null : settings.DelayFor(used + 1);
    }

    /// <summary>
    /// 見送りを 1 回ぶん数える。上限内なら見送りの内容（何回目か・次に試すまでの待ち時間）を返し、
    /// **上限に達していれば <c>null</c>**（＝これ以上は見送らない）。
    /// <para>
    /// #1156, IADR-0480 決定 3: 回数は期間ごとに 1 本で数え、上限だけを <paramref name="core"/> で選ぶ
    /// （中核でない入力の待ちで使った回数も中核の上限に数える＝合計の待ちは中核の上限で頭打ちになる）。
    /// </para>
    /// </summary>
    public ReportDeferralTicket? TryDefer(string periodKey, bool core = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(periodKey);

        var limit = settings.LimitFor(core);
        var used = _deferrals.GetOrAdd(periodKey, 0);
        if (used >= limit)
            return null;

        var attempt = _deferrals.AddOrUpdate(periodKey, 1, (_, current) => current + 1);
        return new ReportDeferralTicket(attempt, limit, settings.DelayFor(attempt));
    }

    /// <summary>これまでに見送った回数（生成できた後は 0）。</summary>
    public int DeferralsOf(string periodKey) => _deferrals.GetValueOrDefault(periodKey);

    /// <summary>生成できた（縮退の有無を問わない）期間の記録を捨てる。</summary>
    public void Clear(string periodKey) => _deferrals.TryRemove(periodKey, out _);

    /// <summary>
    /// #866: **いま生成対象である期間だけを残す。** 生成窓が閉じて対象から外れた期間（月報＝当月を過ぎた・
    /// 週報＝ ISO 週が変わった）は二度と <c>Due</c> に現れず、生成による解放（<see cref="Clear"/>）も
    /// 起きないため、捨てないとプロセス内に永久に残る。
    /// </summary>
    public void RetainOnly(IReadOnlyCollection<string> periodKeys)
    {
        ArgumentNullException.ThrowIfNull(periodKeys);

        var keep = new HashSet<string>(periodKeys, StringComparer.Ordinal);
        foreach (var tracked in _deferrals.Keys)
        {
            if (!keep.Contains(tracked))
                _deferrals.TryRemove(tracked, out _);
        }
    }
}

// 見送り 1 回の内容。Attempt は 1 始まり。
public sealed record ReportDeferralTicket(int Attempt, int MaxDeferrals, TimeSpan RetryAfter);

// FR-06, #840, IADR-0352 決定 3: 見送りの上限と待ち時間。
// 待ち時間は基準値からの倍々（30 秒 → 60 → 120 → …）で、**通常の巡回間隔を超えない**
// （超えると「見送ったせいで通常より遅くなる」向きへ倒れる）。
//
// #1156, IADR-0480 決定 3: **中核の入力（約定・建玉・手動売買の取り込み）が一過性に欠けているときは、上限を別に持つ**
// （既定 12 回＝ 30+60+120+240+300×8 秒 ≒ 47.5 分）。全 Pod が同時に起動する再起動では Keycloak の準備が
// 通常の上限（≒ 12.5 分）に間に合わないことがあり、中核が欠けた報告書は主張そのもの（損益・保有の有無）が崩れる。
// 中核でない入力は従来の上限のまま（各節が「照会できませんでした」と描けば主張は崩れない）。
public sealed record ReportDeferralSettings
{
    public const int DefaultMaxDeferrals = 5;

    public const int DefaultCoreMaxDeferrals = 12;

    public static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(300);

    public int MaxDeferrals { get; init; } = DefaultMaxDeferrals;

    /// <summary>
    /// 中核の入力が一過性に欠けているときの上限（回）。<b>通常の上限より小さければ通常の上限を使う</b>
    /// （中核のほうが早く諦める、を作らない）。
    /// </summary>
    public int CoreMaxDeferrals { get; init; } = DefaultCoreMaxDeferrals;

    /// <summary>
    /// 見送りの上限（回）。中核なら <see cref="CoreMaxDeferrals"/> と <see cref="MaxDeferrals"/> の大きいほう。
    /// 🔴 <b><see cref="MaxDeferrals"/> が 0（＝「見送らない」を明示した構成）なら中核も 0</b>
    /// （#840 から「0 は見送らない」と約束している。中核だけ黙って待ち始めると、その約束が破れる）。
    /// </summary>
    public int LimitFor(bool core) =>
        core && MaxDeferrals > 0 ? Math.Max(CoreMaxDeferrals, MaxDeferrals) : MaxDeferrals;

    public TimeSpan BaseDelay { get; init; } = DefaultBaseDelay;

    public TimeSpan MaxDelay { get; init; } = DefaultMaxDelay;

    /// <summary>attempt 回目（1 始まり）の見送りの後、次に試すまでの待ち時間。</summary>
    public TimeSpan DelayFor(int attempt)
    {
        // 2^(attempt-1) 倍。桁あふれを避けるため指数を抑える（30 回も倍にすれば必ず上限に当たる）。
        var factor = Math.Pow(2, Math.Clamp(attempt - 1, 0, 30));
        var delay = TimeSpan.FromTicks((long)Math.Min(BaseDelay.Ticks * factor, MaxDelay.Ticks));
        return delay < MaxDelay ? delay : MaxDelay;
    }
}
