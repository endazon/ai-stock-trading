using RabbitMQ.Client;

namespace AiStockTrading.IntegrationTests;

// NFR, ADR-0013, IADR-0129, #354（第 3 段階）: **実 RabbitMQ 上のトポロジ**を読むためのプローブ。
//
// なぜ要るのか: Wolverine 移行の中心的な受け入れ基準は「pub/sub の fan-out が competing consumer へ
// 退行していないこと」である（#258 の再発防止）。これはキュー名の導出規則（IADR-0129 決定 1）に依存するが、
// **規則が正しくてもブローカ上の実配線が期待どおりとは限らない**（AutoProvision の失敗・接続文字列の取り違え・
// 規約設定の迂回）。プロセス内の構成オブジェクトを読むだけでは、そこは確かめられない。
//
// そこで受動宣言（passive declare）でブローカに問い合わせる。受動宣言は「既存のキューを作らずに問い合わせる」
// AMQP の操作であり、存在しなければ 404 でチャネルが閉じる。よって
//   - キューが**存在すること**（AutoProvision が実際に宣言した）
//   - そのキューに**consumer が付いていること**（そのサービスが実際に購読している）
//   - サービスごとに**別々のキュー**であること（＝ 1 本を取り合っていない）
// を実ブローカ上で確かめられる。
//
// 注意: 本プローブは検査のたびに接続を開閉する（E2E の粒度では十分に軽い）。副作用として
// **キューを作らない**ことが重要である（能動宣言だと「無いキューを自分で作って緑にする」ため検査にならない）。
internal static class RabbitMqTopologyProbe
{
    /// <summary>受動宣言で得たキューの実状態。</summary>
    internal sealed record QueueState(string Name, uint MessageCount, uint ConsumerCount);

    /// <summary>
    /// キューを受動宣言して状態を返す。存在しなければ null（例外は投げない）。
    /// </summary>
    public static async Task<QueueState?> TryDescribeQueueAsync(string connectionString, string queueName)
    {
        var factory = new ConnectionFactory { Uri = new Uri(connectionString) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        try
        {
            var ok = await channel.QueueDeclarePassiveAsync(queueName);
            return new QueueState(queueName, ok.MessageCount, ok.ConsumerCount);
        }
        catch
        {
            // 存在しないキューの受動宣言はチャネルを閉じる（404）。呼び出し側は「未宣言」として扱う。
            return null;
        }
    }

    /// <summary>
    /// キューが宣言され、<paramref name="minConsumers"/> 以上の consumer が付くまで待つ。
    /// 期限までに満たされなければ最後に観測した状態（未宣言なら null）を返す。
    /// 購読の開始はホスト起動後に非同期で進むため、待ち合わせが要る。
    /// </summary>
    public static async Task<QueueState?> WaitForQueueAsync(
        string connectionString, string queueName, uint minConsumers, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        QueueState? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await TryDescribeQueueAsync(connectionString, queueName);
            if (last is not null && last.ConsumerCount >= minConsumers)
                return last;

            await Task.Delay(250);
        }

        return last;
    }
}
