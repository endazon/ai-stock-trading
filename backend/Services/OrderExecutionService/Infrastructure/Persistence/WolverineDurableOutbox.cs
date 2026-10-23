using Microsoft.Extensions.Configuration;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ.Internal;

namespace OrderExecutionService.Infrastructure.Persistence;

// 🔴 NFR, FR-05, FR-10, ADR-0013（platform ADR-0027: イベント発行の原子性は Wolverine の PostgreSQL の Outbox を標準とする）,
// #1442, IADR-0573 決定1: 発注執行の**送信を耐久アウトボックスに載せる**（封筒を発注執行の専有 DB の `wolverine` スキーマへ保存してから送る）。
//
// なぜ要るか（実測。Wolverine 6.24.5・RabbitMQ 3.13）: 既定の RabbitMQ 送信（Inline）は、ブローカーが落ちている間も
// `PublishAsync` を**成功で返し**、送信の再試行を 3 回で打ち切ってメッセージを**黙って捨てる**（"Discarding message ... after 3 attempts"）。
// 送信の代理は閂（Latched）も立てないので、呼び出し側は失敗を知る手段が無い。約定（OrderExecuted）を失うと、
// リスク管理の台帳（建玉数・日次実現損益・当日取引銘柄）と S1 の評価対象がその約定を知らないままになる。
// 耐久アウトボックスでは `PublishAsync` が返った時点で封筒は DB にあり、ブローカーの復旧後に送られ、プロセスが落ちても
// 再起動の後に送られる（どちらも実測）。
//
// 単独ノード（Solo）にするのは、発注執行が replicas: 1 であり、再起動の直後に残った封筒をすぐ送るため
// （既定の Balanced では、落ちたノードの封筒を引き取るまで 1〜2 分かかった。実測）。入れ替えで旧 Pod と新 Pod が重なる間は
// 同じ封筒を両方が送り得る——同じ封筒 Id のまま届き、受け手は冪等である（IADR-0573 の母集合）。
//
// 🔴 `UseDurableOutboxOnAllSendingEndpoints` だけでは足りない（実測・Wolverine の実装を読んで確認）: この方針は送信先の endpoint が
// 「購読（Subscription）を持って」compile されるときだけ効き、規約ルーティングが起動時に購読を先に登録するのは**自分が処理する型だけ**である。
// 発注執行は OrderExecuted を処理しないので、再起動の直後に残った封筒を回収する経路が送信先を購読なしで作ると、その送信先は Inline で
// compile される——封筒は送られるが**保存先から消されず**（再起動のたびに送り直す）、**以後そのプロセスでその型の発行は保存されなくなる**。
// そこで RabbitMQ の exchange（本ユニットの送信先はすべて fan-out exchange。IADR-0129 決定 2）を、購読の有無に依らず Durable にする。
//
// 構成 `Messaging:DurableOutbox:Enabled`（既定 true）。false にすると従来どおり（保存せずに送る）。**配備で不具合が出たときの戻し口**であり、
// 常用しない（false では、ブローカー断の間に出た発注執行のイベントは失われ得る）。
public static class WolverineDurableOutbox
{
    /// <summary>構成キー（既定 true）。</summary>
    public const string EnabledKey = "Messaging:DurableOutbox:Enabled";

    /// <summary>封筒を置くスキーマ（発注執行の専有 DB の中。EF の表とは分ける）。</summary>
    public const string Schema = "wolverine";

    /// <summary>構成が耐久アウトボックスを求めているか（既定 true）。</summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue(EnabledKey, defaultValue: true);

    /// <summary>
    /// 送信を耐久アウトボックスに載せる（封筒の保存先＝<paramref name="connectionString"/> の <see cref="Schema"/>・全送信先〔RabbitMQ の exchange は
    /// 購読の有無に依らず〕・単独ノード）。
    /// 共通の RabbitMQ 配線（<c>UseAiStockTradingRabbitMq</c>）の<b>後に</b>呼ぶ。
    /// </summary>
    public static void UseOrderExecutionDurableOutbox(this WolverineOptions options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        options.PersistMessagesWithPostgresql(connectionString, Schema);
        options.Policies.UseDurableOutboxOnAllSendingEndpoints();
        options.Policies.Add(new LambdaEndpointPolicy<RabbitMqExchange>((exchange, _) =>
        {
            if (exchange.Role == EndpointRole.Application)
                exchange.Mode = EndpointMode.Durable;
        }));
        options.Durability.Mode = DurabilityMode.Solo;
    }
}
