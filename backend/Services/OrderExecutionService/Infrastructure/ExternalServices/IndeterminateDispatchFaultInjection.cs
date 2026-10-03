using System.Globalization;
using AiStockTrading.Shared.Contracts.Logging;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderExecutionService.Infrastructure.ExternalServices.RealReadOnly;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// 🔴 FR-10, FR-05, NFR-09, ADR-0045 決定1・決定2, #856, IADR-0488: 送信結果を確認できない発注（届いたか不明）を
// **SIMULATE で意図的に作る**故障注入の形。利用者裁定 2026-10-03「NotPlaced の実機検証に使う事例は、SIMULATE で意図的に作る」。
//   - AfterSend: 実際に送信した後で結果を不明にする → 突合は「発注済み」になるはず（肯定形）。
//   - BeforeSend: 送信せずに結果を不明にする → 突合は「未発注」になるはず（否定形。誤判定は二重発注に直結する本丸）。
public enum IndeterminateDispatchFaultMode
{
    None,
    AfterSend,
    BeforeSend,
}

// 🔴 FR-10, #856, IADR-0488 決定1・2: 故障注入の構成。**既定は無効**（未設定・空は None）。
//
// 有効（None 以外）にするときは、許可する銘柄（`*` 単独で全銘柄）と期限（ISO-8601）が必須。
// 未知の値・空の銘柄・`*` と銘柄の混在・読めない期限・起動時刻から 24 時間より先の期限は**起動時に止める**
// （「有効のつもりで無効」「切り忘れ」を作らない。RealMarginQueryOptions と同じ流儀）。
// **期限を過ぎた構成は起動を止めない**（注入しないだけ）——切り忘れた構成で発注執行が再起動のたびに落ちると、
// 発注と保護逆指値ガードがまとめて止まる（IADR-0444 決定4 と同じ判断）。
public sealed record IndeterminateDispatchFaultInjectionOptions(
    IndeterminateDispatchFaultMode Mode,
    IReadOnlyList<string> Symbols,
    DateTimeOffset? ExpiresAt)
{
    public const string ModeKey = "FaultInjection:IndeterminateDispatch:Mode";
    public const string SymbolsKey = "FaultInjection:IndeterminateDispatch:Symbols";
    public const string ExpiresAtKey = "FaultInjection:IndeterminateDispatch:ExpiresAtUtc";

    /// <summary>許可する銘柄の「全銘柄」。単独でだけ受理する。</summary>
    public const string AnySymbol = "*";

    /// <summary>期限の上限（起動時刻からの長さ）。これより先の期限は切り忘れの温床なので受理しない。</summary>
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);

    public static IndeterminateDispatchFaultInjectionOptions Disabled { get; } =
        new(IndeterminateDispatchFaultMode.None, [], null);

    public bool Enabled => Mode != IndeterminateDispatchFaultMode.None;

    public bool AllowsAnySymbol => Symbols is [AnySymbol];

    public static IndeterminateDispatchFaultInjectionOptions FromConfiguration(IConfiguration config, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(config);
        var mode = ParseMode(config[ModeKey]);
        if (mode == IndeterminateDispatchFaultMode.None)
            return Disabled;

        return new IndeterminateDispatchFaultInjectionOptions(
            mode, ParseSymbols(config[SymbolsKey]), ParseExpiresAt(config[ExpiresAtKey], now));
    }

    // 🔴 FR-10, NFR-09, #856, IADR-0488 決定2: **SIMULATE 限定。** 有効なら、実弾に近づく構成・意味の無い構成で起動を止める。
    // 引数で受けるのは試験のためであり、本番は Program.cs が合成起点で 1 回だけ呼ぶ
    // （liveTradingReleased には LiveTradingGate.LiveTradingReleased を渡す。定数を直接読むと分岐が到達不能になる＝CS0162）。
    public void EnsureAllowed(
        BrokerSelection selection,
        bool liveTradingReleased,
        string? configuredTrdEnv,
        bool realAccountQueryEnabled)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!Enabled)
            return;

        // 内蔵 paper（包む OpenD クライアントが無い＝「有効のつもり」になる）と live 階層（実弾）の両方をこの 1 つで止める。
        if (selection.ToBrokerProvider() != BrokerProvider.MoomooSimulate)
        {
            throw Refuse($"発注先 '{selection.Tier}' は moomoo SIMULATE ではありません（moomoo SIMULATE の OpenD クライアントを包む故障注入です）");
        }
        if (liveTradingReleased)
        {
            throw Refuse("実弾が解禁された版（LiveTradingGate.LiveTradingReleased）では受理しません");
        }
        if (!string.IsNullOrWhiteSpace(configuredTrdEnv)
            && !string.Equals(configuredTrdEnv.Trim(), MoomooBrokerOptions.SimulateTrdEnv, StringComparison.OrdinalIgnoreCase))
        {
            throw Refuse($"Broker:Moomoo:TrdEnv '{configuredTrdEnv}' は SIMULATE ではありません");
        }
        if (realAccountQueryEnabled)
        {
            throw Refuse($"実弾口座の読み取り専用の照会（{RealMarginQueryOptions.EnabledKey}=true）と同じプロセスでは受理しません");
        }
    }

    private InvalidOperationException Refuse(string reason) => new(
        $"{ModeKey}='{Mode}'（送信結果を確認できない発注の故障注入）は SIMULATE 限定です。{reason}。"
        + $"故障注入を外す（{ModeKey} を未設定か 'None' にする）か、broker.tier=moomoo-sim の構成で使ってください（#856 / IADR-0488）。");

    private static IndeterminateDispatchFaultMode ParseMode(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return IndeterminateDispatchFaultMode.None;
        return configured.Trim().ToLowerInvariant() switch
        {
            "none" => IndeterminateDispatchFaultMode.None,
            "aftersend" => IndeterminateDispatchFaultMode.AfterSend,
            "beforesend" => IndeterminateDispatchFaultMode.BeforeSend,
            _ => throw new InvalidOperationException(
                $"{ModeKey} '{configured}' は受理しません。'None'（既定）/ 'AfterSend' / 'BeforeSend' を指定してください"
                + "（送信結果を確認できない発注の故障注入。SIMULATE 限定。#856 / IADR-0488）。"),
        };
    }

    private static IReadOnlyList<string> ParseSymbols(string? configured)
    {
        var symbols = (configured ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (symbols.Count == 0)
        {
            throw new InvalidOperationException(
                $"{SymbolsKey} が空です。故障注入を当てる銘柄をカンマ区切りで指定してください"
                + $"（全銘柄なら '{AnySymbol}' 単独。#856 / IADR-0488）。");
        }
        if (symbols.Count > 1 && symbols.Contains(AnySymbol))
        {
            throw new InvalidOperationException(
                $"{SymbolsKey} '{configured}' は受理しません。'{AnySymbol}'（全銘柄）は単独で指定してください（#856 / IADR-0488）。");
        }
        return symbols;
    }

    private static DateTimeOffset ParseExpiresAt(string? configured, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{ExpiresAtKey} が空です。故障注入の期限を ISO-8601（例 2026-10-05T20:00:00Z）で指定してください"
                + $"（起動から {MaximumLifetime.TotalHours:0} 時間以内。切り忘れを期限で無害にするため。#856 / IADR-0488）。");
        }
        if (!DateTimeOffset.TryParse(configured.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiresAt))
        {
            throw new InvalidOperationException(
                $"{ExpiresAtKey} '{configured}' を時刻として読めません。ISO-8601（例 2026-10-05T20:00:00Z）で指定してください（#856 / IADR-0488）。");
        }
        if (expiresAt > now + MaximumLifetime)
        {
            throw new InvalidOperationException(
                $"{ExpiresAtKey} '{configured}' は起動時刻から {MaximumLifetime.TotalHours:0} 時間より先です。"
                + "故障注入の期限は PoC の 1 回分に絞ってください（#856 / IADR-0488）。");
        }
        return expiresAt;
    }
}

// 🔴 FR-10, #856, IADR-0488 決定4: 故障注入が作った「届いたか不明」。アダプタ（MoomooBrokerAdapter）は確認できた失敗
// （retType -1）以外の例外を BrokerDispatchIndeterminateException に包む——**本型はその既存の経路をそのまま通るための入力**であり、
// 発注執行・予約・突合のコードは本型を知らない。型名と本文に「故障注入」を出し、自然発生と取り違えないようにする。
public sealed class IndeterminateDispatchFaultInjectedException(string message) : Exception(message);

// 🔴 FR-10, FR-05, #856, IADR-0488 決定3〜6: 発注アダプタへ渡す OpenD クライアントを包み、**新規建ての発注 1 本だけ**を
// 「届いたか不明」にする故障注入のデコレータ（SIMULATE 限定・既定無効。有効でなければ Program.cs は包まない）。
//
// 注入の対象（すべて満たすときだけ。どれかが外れたら素通しで、1 回分も消費しない）:
//   - 要求の効果が新規建て（PositionEffect.Open）かつ指値（Limit）かつ remark（DecisionId）がある
//     ——保護レグ（S0 / S3）・成行の手仕舞い・指値の手仕舞いはすべて Close なので構造上対象外。BeforeSend を保護レグへ当てると、
//       解放の門が閉じたまま建玉が無保護・未決済で据え置かれる。remark が無いと突合できず、検証にならない。
//   - 銘柄が許可されている（`*` は全銘柄）・期限の前・このプロセスでまだ注入していない（1 プロセス 1 回）。
// 1 回分は**送る前に**取る（前の端だけ。後の端で数えると BeforeSend は数えられず毎回注入になる。作業仕様書の窓の表）。
// 発注以外の口（照会・取消・建玉・口座・remark 突合）は素通しにする。
public sealed class IndeterminateDispatchFaultInjectingClient : IMoomooTradeClient
{
    private readonly IMoomooTradeClient _inner;
    private readonly IndeterminateDispatchFaultInjectionOptions _options;
    private readonly ILogger<IndeterminateDispatchFaultInjectingClient> _logger;
    private readonly TimeProvider _time;
    private int _fired;

    public IndeterminateDispatchFaultInjectingClient(
        IMoomooTradeClient inner,
        IndeterminateDispatchFaultInjectionOptions options,
        ILogger<IndeterminateDispatchFaultInjectingClient>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<IndeterminateDispatchFaultInjectingClient>.Instance;
        _time = timeProvider ?? TimeProvider.System;

        var expired = _options.ExpiresAt is not { } expiresAt || _time.GetUtcNow() >= expiresAt;
        _logger.LogWarning(
            "故障注入（送信結果を確認できない発注）が構成されています: 形={Mode} 銘柄={Symbols} 期限={ExpiresAt}{Expired}。"
                + "SIMULATE の新規建てにだけ、このプロセスで 1 回だけ当てます（#856）。PoC が済んだら構成から外してください。",
            _options.Mode,
            LogSanitizer.Sanitize(string.Join(",", _options.Symbols)),
            _options.ExpiresAt,
            expired ? "（期限切れのため注入しません）" : string.Empty);
    }

    /// <summary>このプロセスで注入を発火したか（1 回だけ）。</summary>
    public bool HasFired => Volatile.Read(ref _fired) != 0;

    public async Task<MoomooOrderResult> PlaceOrderAsync(
        MoomooOrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsTarget(request) || Interlocked.CompareExchange(ref _fired, 1, 0) != 0)
        {
            return await _inner.PlaceOrderAsync(request, cancellationToken).ConfigureAwait(false);
        }

        if (_options.Mode == IndeterminateDispatchFaultMode.BeforeSend)
        {
            LogFired(request, orderId: null);
            throw Injected(request, "証券会社へは送信していません");
        }

        MoomooOrderResult result;
        try
        {
            result = await _inner.PlaceOrderAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 実際の送信が先に失敗した。注入はせず、実際の例外をそのまま伝播させる（分類はアダプタの既存の経路）。
            // 1 回分は使い切った（戻すと「実は届いた送信」の後に 2 本目を注入し得る）。
            _logger.LogWarning(ex,
                "故障注入（形={Mode}）の対象の発注が、注入の前に実際の送信で失敗しました（DecisionId={DecisionId} 銘柄={Symbol}）。"
                    + "注入は行っていません。このプロセスの 1 回分は使い切りました（#856）。",
                _options.Mode, LogSanitizer.Sanitize(request.Remark), LogSanitizer.Sanitize(request.Symbol));
            throw;
        }

        LogFired(request, result.OrderId);
        throw Injected(request, $"証券会社へは送信済みです（注文ID={result.OrderId}）");
    }

    private bool IsTarget(MoomooOrderRequest request) =>
        _options.Enabled
        && request is { PositionEffect: PositionEffect.Open, Kind: MoomooOrderKind.Limit }
        && !string.IsNullOrEmpty(request.Remark)
        && (_options.AllowsAnySymbol
            || _options.Symbols.Contains(request.Symbol?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        && _options.ExpiresAt is { } expiresAt
        && _time.GetUtcNow() < expiresAt;

    private void LogFired(MoomooOrderRequest request, string? orderId) =>
        _logger.LogWarning(
            "故障注入: 発注の結果を確認できなかった状態を意図的に作りました（形={Mode} DecisionId={DecisionId} 銘柄={Symbol} "
                + "数量={Quantity} 注文ID={OrderId}）。予約は Reserved のまま据え置かれ、突合が判定します（#856）。",
            _options.Mode,
            LogSanitizer.Sanitize(request.Remark),
            LogSanitizer.Sanitize(request.Symbol),
            request.Quantity,
            LogSanitizer.Sanitize(orderId ?? "（送信していない）"));

    private IndeterminateDispatchFaultInjectedException Injected(MoomooOrderRequest request, string detail) => new(
        $"故障注入（{_options.Mode}）: 発注の結果を確認できなかったことにしました（{detail}。DecisionId={request.Remark}）。");

    public Task<MoomooOrderResult?> QueryOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
        _inner.QueryOrderAsync(orderId, cancellationToken);

    public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) =>
        _inner.CancelOrderAsync(orderId, cancellationToken);

    public Task<MoomooOrderSnapshot?> FindOrderByClientIdAsync(
        string clientOrderId, DateTimeOffset reservedAtUtc, CancellationToken cancellationToken = default) =>
        _inner.FindOrderByClientIdAsync(clientOrderId, reservedAtUtc, cancellationToken);

    public Task<IReadOnlyList<MoomooPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default) =>
        _inner.GetPositionsAsync(cancellationToken);

    public Task<MoomooAccountType?> GetAccountTypeAsync(CancellationToken cancellationToken = default) =>
        _inner.GetAccountTypeAsync(cancellationToken);

    public Task<decimal?> GetAccountEquityInBaseAsync(CancellationToken cancellationToken = default) =>
        _inner.GetAccountEquityInBaseAsync(cancellationToken);
}
