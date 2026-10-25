using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradeDecisionService.Common.Abstractions;

namespace TradeDecisionService.Tests;

// NFR（試験の決定性）, FR-10, FR-19, IADR-0561 決定 3: 本番の Program.cs を起こす試験の時計を固定する。
//
// 本番の構成は閉場前の手仕舞いの常駐（PreCloseFlattenService）を常に登録し、起動直後から IClock の時刻で巡回する。
// 閉場の 60 分前（M の上限）以降の場中（日本株 JST 14:30〜15:30・米国株 ET 15:00〜16:00）では、巡回がサイジング文脈
// （GET /risk-controls/sizing-context・gRPC の GetSizingContext）を照会する。リスク管理の偽の提供側への**呼び出しの数・経路**を
// 数える試験は、実時刻がこの窓に入ると常駐の照会が 1 回混ざって赤になる（MSP#2070 の CI・2026-10-22T05:38Z で実測）。
// そこで、提供側の呼び出しを数える試験の組み立てでは、IClock を両市場とも休場の時刻へ固定する（常駐は登録のまま・窓の外なので照会しない）。
internal static class TestHostClock
{
    /// <summary>2026-10-24（土）12:00Z＝JST 21:00・ET 08:00。米国・日本の両市場とも休場（閉場前の手仕舞いの常駐は照会しない）。</summary>
    public static readonly DateTimeOffset OutsideMarketHours = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>本番の構成の IClock（SystemClock）を固定の時刻へ差し替える（ConfigureTestServices から呼ぶ）。</summary>
    public static IServiceCollection UseFixedClock(this IServiceCollection services, DateTimeOffset now)
    {
        services.RemoveAll<IClock>();
        services.AddSingleton<IClock>(new FixedClock(now));
        return services;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
