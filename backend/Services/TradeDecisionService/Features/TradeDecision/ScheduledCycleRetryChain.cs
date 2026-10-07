using System.Globalization;

namespace TradeDecisionService.Features.TradeDecision;

// 🔴 FR-04, FR-02, NFR-02, NFR-13, #1194, IADR-0505: 定時サイクル（InformationCollected）の**再試行の連鎖全体**を、
// ブローカ（RabbitMQ）の consumer_timeout に収める。
//
// なぜ要るのか: 共通の失敗方針（全例外を 2 秒・10 秒・30 秒で再試行）は**同じ配信の中で**再試行する（Inline の受信。ack は最後の試行の後）。
// IADR-0490 は 1 回の試行（サイクルの上限 T）だけを consumer_timeout と比べていたため、サイクルの打ち切りや銘柄の catch の外へ漏れた例外では
// 再試行 1 回ごとに最大 T（＋待ち）が足され、consumer_timeout を超え得た（チャネルが閉じられ、先読みした配送ごと再配送 → LLM の費用が二重）。
//
// 1 通が配信を握る時間の上限（握り）:
//   握り(n) ＝ 起点の待ち（QueueWaitAllowance） ＋ n × T ＋ 最初の n − 1 回の再試行の待ちの和
// 起点の待ち: T が巡回間隔より長いと、次の起点は先読みされたまま待ち、鮮度の上限以内なら判断される（IADR-0490 の 2026-10-07 追記）。
// 定時サイクルの試行の上限 n は、共通の上限（初回＋再試行 3 回＝4）以下で**握り(n) ＜ consumer_timeout** となる最大の n とする。
// n ＝ 1 でも収まらない構成では**起動を止める**（MSP の IADR-0478 決定 7 `EnsureRetryChainFits` と同じ形。等しいときも止める）。
// 既定（T 960 秒）・経路B（T 1,140 秒）はいずれも n ＝ 1（定時サイクルは再試行しない。失敗は _error へ）。
public sealed record ScheduledCycleRetryChain
{
    /// <summary>ブローカの <c>consumer_timeout</c> として仮定する値の構成キー（MSP の同名キーにそろえる）。</summary>
    public const string BrokerConsumerTimeoutKey = "Messaging:BrokerConsumerTimeoutSeconds";

    /// <summary>RabbitMQ 3.13 の既定 30 分。配備（chart・MSP の platform-infra）は上書きしていない。変えたら合わせる。</summary>
    public const int DefaultBrokerConsumerTimeoutSeconds = 1800;

    /// <summary>
    /// 起点が先読みされたまま待つ時間の見込み。鮮度の上限（巡回間隔 300 秒の宣言 600 秒・宣言なしの既定 600 秒）より長く待った起点は
    /// 判断されずに捨てられるので、判断される起点の待ちはこれ以下である（巡回間隔を変えたら本値の前提を引き直す）。
    /// </summary>
    public static TimeSpan QueueWaitAllowance => ScheduledCycleBudget.DefaultStaleness;

    private ScheduledCycleRetryChain(int attempts, IReadOnlyList<TimeSpan> cooldowns, TimeSpan hold, TimeSpan brokerConsumerTimeout)
    {
        Attempts = attempts;
        Cooldowns = cooldowns;
        Hold = hold;
        BrokerConsumerTimeout = brokerConsumerTimeout;
    }

    /// <summary>定時サイクルの 1 回の配信で行う試行の上限（初回を含む。1 なら再試行しない）。</summary>
    public int Attempts { get; }

    /// <summary>定時サイクルの再試行の待ち（要素数 ＝ <see cref="Attempts"/> − 1）。共通の間隔の先頭から取る。</summary>
    public IReadOnlyList<TimeSpan> Cooldowns { get; }

    /// <summary>1 通が配信を握る時間の上限（起点の待ち ＋ 試行 × T ＋ 再試行の待ちの和）。<see cref="BrokerConsumerTimeout"/> より短い。</summary>
    public TimeSpan Hold { get; }

    /// <summary>比べた consumer_timeout。</summary>
    public TimeSpan BrokerConsumerTimeout { get; }

    /// <summary>
    /// 試行の上限を導く。n ＝ 1 でも <paramref name="brokerConsumerTimeout"/> に収まらなければ <see cref="InvalidOperationException"/>（起動を止める）。
    /// </summary>
    /// <param name="handlerTimeout">定時サイクルのハンドラの上限 T（Wolverine に設定する整数秒）。</param>
    /// <param name="sharedCooldowns">共通の再試行の間隔（要素数が共通の再試行の回数）。</param>
    /// <param name="queueWait">起点が先読みされたまま待つ時間の見込み（<see cref="QueueWaitAllowance"/>）。</param>
    /// <param name="brokerConsumerTimeout">ブローカの consumer_timeout。</param>
    public static ScheduledCycleRetryChain Derive(
        TimeSpan handlerTimeout, IReadOnlyList<TimeSpan> sharedCooldowns, TimeSpan queueWait, TimeSpan brokerConsumerTimeout)
    {
        ArgumentNullException.ThrowIfNull(sharedCooldowns);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(handlerTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueWait, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(brokerConsumerTimeout, TimeSpan.Zero);

        for (var attempts = sharedCooldowns.Count + 1; attempts >= 1; attempts--)
        {
            var cooldowns = sharedCooldowns.Take(attempts - 1).ToArray();
            var hold = HoldOf(handlerTimeout, cooldowns, queueWait, attempts);
            if (hold < brokerConsumerTimeout)
                return new ScheduledCycleRetryChain(attempts, Array.AsReadOnly(cooldowns), hold, brokerConsumerTimeout);
        }

        var single = HoldOf(handlerTimeout, [], queueWait, 1);
        throw new InvalidOperationException(
            $"定時サイクル（InformationCollected）の 1 回の配信の握り（起点の待ち {queueWait.TotalSeconds} 秒 ＋ 試行 1 回 × "
            + $"ハンドラの上限 {handlerTimeout.TotalSeconds} 秒 ＝ {single.TotalSeconds} 秒）は、ブローカの consumer_timeout"
            + $"（{BrokerConsumerTimeoutKey}＝{brokerConsumerTimeout.TotalSeconds} 秒）より短くなければならない。"
            + $" 超えるとブローカが処理中のサイクルを取り上げて再配送し、LLM の費用が二重になる。{ScheduledCycleBudget.MaxWatchedSymbolsKey}"
            + "（監視銘柄数の前提）を下げるか、LLM の timeout・票数（1 銘柄の締め切り）を見直すこと。");
    }

    /// <summary>
    /// consumer_timeout を構成から読む。未設定・不正・非正値は既定（<see cref="DefaultBrokerConsumerTimeoutSeconds"/>）へ倒す
    /// （<see cref="ScheduledCycleBudget.ParseMaxWatchedSymbols"/> と同じ作法）。
    /// </summary>
    public static TimeSpan ParseBrokerConsumerTimeout(string? value) =>
        TimeSpan.FromSeconds(
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? seconds
                : DefaultBrokerConsumerTimeoutSeconds);

    private static TimeSpan HoldOf(TimeSpan handlerTimeout, IReadOnlyList<TimeSpan> cooldowns, TimeSpan queueWait, int attempts) =>
        cooldowns.Aggregate(queueWait + handlerTimeout * attempts, (sum, cooldown) => sum + cooldown);
}
