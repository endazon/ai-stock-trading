using Microsoft.Extensions.Configuration;

namespace OrderExecutionService.Infrastructure.ExternalServices.RealReadOnly;

// FR-10, UC-06, ADR-0016 決定3（2026-08-06 追記）, #1000, IADR-0482 決定3: 実弾口座の読み取り専用の照会を使うかの構成。
//
// 🔴 **既定は無効**（未設定・空は false）。無効なら借株可否は従来どおり発注と同じ SIMULATE 口座のヘッダで照会し
// （IADR-0425 決定2。SIMULATE では失敗して「分からない」＝空売りは拒否）、本系のコードは実弾のヘッダを 1 度も作らない。
// 明示して true にしたときだけ、照会を実弾口座（Real × Margin）のヘッダへ切り替える。**発注は SIMULATE のまま変わらない。**
// 未知の値（`yes` 等）は既定へ黙って倒さず起動時に停止する（「有効にしたつもりで無効」「無効にしたつもりで有効」を作らない）。
public sealed record RealMarginQueryOptions(bool Enabled)
{
    public const string EnabledKey = "Broker:Moomoo:RealMarginQuery:Enabled";

    public static RealMarginQueryOptions Disabled { get; } = new(false);

    public static RealMarginQueryOptions FromConfiguration(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var configured = config[EnabledKey];
        if (string.IsNullOrWhiteSpace(configured))
            return Disabled;
        return configured.Trim().ToLowerInvariant() switch
        {
            "false" => Disabled,
            "true" => new RealMarginQueryOptions(true),
            _ => throw new InvalidOperationException(
                $"{EnabledKey} '{configured}' は受理しません。'true' または 'false'（既定）を指定してください"
                + "（実弾口座のヘッダでの読み取り専用の照会。#1000 / IADR-0482）。"),
        };
    }
}
