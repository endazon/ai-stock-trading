using AiStockTrading.Shared.Contracts.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NotificationService.Infrastructure.ExternalServices;

/// <summary>
/// 🔴 NFR, FR-14, IADR-0062, IADR-0645 決定 3: Discord ボットの接続状態（オーナーの帯域内の停止〔kill switch・一時停止〕の経路があるか）。
/// 状態は計器 <c>ast.notification.discord_bot_status</c>（今の状態だけ 1）にも出す。警報 <c>AstDiscordBotUnavailable</c> が引く。
/// </summary>
public sealed class DiscordBotHealth(BusinessMetrics? metrics = null)
{
    private volatile string _state = BusinessMetrics.DiscordBotDisabled;

    /// <summary>今の状態（<see cref="BusinessMetrics.DiscordBotStates"/> のどれか）。</summary>
    public string State => _state;

    /// <summary>状態を記録する（計器へも出す）。</summary>
    public void Set(string state)
    {
        metrics?.RecordDiscordBotState(state);
        _state = state;
    }
}

/// <summary>NFR, FR-14, IADR-0645 決定 3: 設定の欠けを readiness に反映するかの段（構成 <c>Notifications:Discord:Bot:MissingSettingsMode</c>）。</summary>
public enum DiscordBotMissingSettingsMode
{
    /// <summary>既定: 計器と警報だけ（readiness は落とさない）。</summary>
    Report = 0,

    /// <summary>有効なのに設定が欠けていれば readiness を Unhealthy にする（入れ替えを進めない・配備の失敗として見せる）。</summary>
    Enforce = 1,
}

/// <summary>
/// 🔴 NFR, FR-14, IADR-0645 決定 3: readiness の確かめ（"ready" タグ）。段 Enforce で、ボットが有効なのに設定が欠けて接続しないときだけ Unhealthy。
/// 切断（Discord 側の障害・再接続の間）では落とさない（揺れて入れ替えを止めない。警報が見る）。無効（Enabled=false）は明示の選択なので Healthy。
/// </summary>
public sealed class DiscordBotReadinessCheck(DiscordBotHealth health, DiscordBotMissingSettingsMode mode) : IHealthCheck
{
    public const string ModeKey = "Notifications:Discord:Bot:MissingSettingsMode";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (mode == DiscordBotMissingSettingsMode.Enforce
            && string.Equals(health.State, BusinessMetrics.DiscordBotMisconfigured, StringComparison.Ordinal))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Discord ボットは有効ですが設定が欠けて接続していません（オーナーの帯域内の停止の経路がありません。起動ログの不足の一覧を見ること）。"));
        }

        return Task.FromResult(HealthCheckResult.Healthy());
    }

    /// <summary>構成から段を読む（未設定は Report。Report・Enforce 以外は起動で止める）。</summary>
    public static DiscordBotMissingSettingsMode ReadMode(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var text = configuration[ModeKey];
        if (string.IsNullOrWhiteSpace(text))
            return DiscordBotMissingSettingsMode.Report;
        if (Enum.TryParse<DiscordBotMissingSettingsMode>(text.Trim(), ignoreCase: false, out var mode) && Enum.IsDefined(mode))
            return mode;
        throw new InvalidOperationException($"{ModeKey} は Report か Enforce です（値: {text}。IADR-0645）。");
    }
}
