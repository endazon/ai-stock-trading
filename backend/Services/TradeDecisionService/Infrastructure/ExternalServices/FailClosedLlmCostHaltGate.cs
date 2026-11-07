using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// 🔴 NFR-13, ADR-0074 決定2, IADR-0582, IADR-0639: 月次 LLM 費用の停止を**読めないとき**の扱いの段。
// 基盤の LLM ゲートウェイの呼び出しの上限（Llm:CallLimits:Mode。当日の費用の枠を読めない cost_state は段 2 で送らない）と同じ考え方で、
// 判定は常に行い、止めるのは Enforce のときだけにする。
public enum LlmCostHaltUnavailableMode
{
    /// <summary>従来の挙動。読めなければ「停止していない」（LLM を呼ぶ。IADR-0031 / IADR-0582）。既定・ローカル。</summary>
    Off = 0,

    /// <summary>倒す向きは Off と同じ。猶予を過ぎても読めなければ、Enforce なら停止として扱うことをログに出す。
    /// 止めないのは読めない場合に限る。読めた停止の値（猶予の内の最後に読めた値を含む）には従う。</summary>
    Report = 1,

    /// <summary>猶予を過ぎても読めなければ停止として扱う（LLM を呼ばない。fail-closed）。クラウド。</summary>
    Enforce = 2,
}

// NFR-13, IADR-0639: 段と猶予。構成から起動時に読み、読めない値は起動で止める（既定へ黙って倒さない）。
public sealed record LlmCostHaltQueryOptions(LlmCostHaltUnavailableMode Mode, TimeSpan Grace)
{
    public const string ModeKey = "CostControl:HaltQuery:UnavailableMode";
    public const string GraceSecondsKey = "CostControl:HaltQuery:GraceSeconds";

    /// <summary>猶予の既定（15 分）。月次の予算は緩変であり、短い不達で判断を止めない。</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(15);

    /// <summary>猶予の上限（1 日）。長くすると fail-closed が名ばかりになる。</summary>
    public static readonly TimeSpan MaxGrace = TimeSpan.FromDays(1);

    public static LlmCostHaltQueryOptions FromConfiguration(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var rawMode = config[ModeKey];
        var mode = LlmCostHaltUnavailableMode.Off;
        if (!string.IsNullOrWhiteSpace(rawMode))
        {
            // 名前だけを受ける（Enum.TryParse は "1" のような数も受けるので、名前の一覧と突き合わせる）。
            var name = Enum.GetNames<LlmCostHaltUnavailableMode>()
                .FirstOrDefault(n => string.Equals(n, rawMode.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{ModeKey} は Off / Report / Enforce のどれかです（{rawMode}。IADR-0639）。");
            mode = Enum.Parse<LlmCostHaltUnavailableMode>(name);
        }

        var grace = DefaultGrace;
        var rawGrace = config[GraceSecondsKey];
        if (!string.IsNullOrWhiteSpace(rawGrace))
        {
            if (!int.TryParse(rawGrace.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                || TimeSpan.FromSeconds(seconds) > MaxGrace)
            {
                throw new InvalidOperationException(
                    $"{GraceSecondsKey} は 0 以上 {(int)MaxGrace.TotalSeconds} 以下の整数（秒）です（{rawGrace}。IADR-0639）。");
            }

            grace = TimeSpan.FromSeconds(seconds);
        }

        return new LlmCostHaltQueryOptions(mode, grace);
    }
}

// NFR-13, IADR-0639: 最後に読めた費用統制の停止の値と時刻。プロセスで 1 つ（関所は判断ごとの scope で作られるため、状態は singleton に置く）。
public sealed class LlmCostHaltConfirmation
{
    private readonly object _gate = new();
    private (bool Halted, DateTimeOffset At)? _last;

    public void Record(bool halted, DateTimeOffset at)
    {
        lock (_gate)
            _last = (halted, at);
    }

    public (bool Halted, DateTimeOffset At)? Last
    {
        get
        {
            lock (_gate)
                return _last;
        }
    }
}

// 🔴 NFR-13, ADR-0074 決定2, IADR-0639: 段 Report / Enforce の関所。照会（ILlmCostHaltReader）を包み、読めないときの扱いを決める。
//   - 読めた: その値を返し、時刻と一緒に覚える。
//   - 読めない・最後に読めた時から猶予の内: 最後に読めた値を返す（短い不達で判断を止めない）。
//   - 読めない・猶予の外（起動から一度も読めていないときを含む）: Enforce は停止（true）、Report は停止していない（false）とログ。
// LLM を使わない保護（損切り・利用者の手仕舞い・閉場前の手仕舞い・維持率割れの自動縮小）はこの関所を通らないので止まらない。
public sealed class FailClosedLlmCostHaltGate(
    ILlmCostHaltReader reader,
    LlmCostHaltConfirmation confirmation,
    LlmCostHaltQueryOptions options,
    TimeProvider clock,
    ILogger<FailClosedLlmCostHaltGate> logger) : ILlmCostHaltGate
{
    public async Task<bool> IsHaltedAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        if (await reader.ReadHaltedAsync(cancellationToken).ConfigureAwait(false) is { } halted)
        {
            confirmation.Record(halted, now);
            return halted;
        }

        if (confirmation.Last is { } last && now - last.At <= options.Grace)
        {
            logger.LogWarning(
                "費用統制の停止を読めません。最後に読めた値（停止={Halted}・{At:o}）を猶予（{Grace}）の内として使います。",
                last.Halted, last.At, options.Grace);
            return last.Halted;
        }

        if (options.Mode == LlmCostHaltUnavailableMode.Enforce)
        {
            logger.LogError(
                "費用統制の停止を猶予（{Grace}）を過ぎても読めません。停止として扱い、LLM を呼ぶ前に見送ります（CostControl:HaltQuery:UnavailableMode=Enforce）。最後に読めた時刻: {At}",
                options.Grace, confirmation.Last?.At);
            return true;
        }

        logger.LogError(
            "費用統制の停止を猶予（{Grace}）を過ぎても読めません。Enforce なら停止として扱います（今は Report のため判断を続けます）。最後に読めた時刻: {At}",
            options.Grace, confirmation.Last?.At);
        return false;
    }
}
