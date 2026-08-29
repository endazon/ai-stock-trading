using System.Reflection;
using System.Runtime.CompilerServices;
using JasperFx.CodeGeneration.Model;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;

namespace AiStockTrading.TestSupport.PlatformShim.Foundation.Extensions;

// NFR, ADR-0013, IADR-0129, #354: 非同期メッセージング（Wolverine + RabbitMQ）の共通配線。
// platform ADR-0027（Wolverine 移行）/ ADR-0028（RabbitMQ 継続）に追随する。
//
// **本ファイルはブローカのトポロジの単一の出所である。** サービス側の Program.cs は
// UseAiStockTradingRabbitMq を 1 行呼ぶだけにし、キュー名・ルーティング・再試行・DLQ の選択肢を持たない
// （IADR-0129 決定 4）。素の UseConventionalRouting / ListenToRabbitQueue / PrefixIdentifiers を
// サービス側で直接呼ぶことは scripts/check-consumer-endpoint-names.js が CI で止める。
//
// なぜ既定に任せないのか（実測に基づく。IADR-0129 §コンテキスト）:
//   1. Wolverine の conventional routing は**リスニングキュー名をメッセージ型名だけ**から導く
//      （NamingSource.FromMessageType）。ハンドラのクラス名は一切関与しない。したがって同じイベントを
//      購読する別サービスは**必ず**同一キューを共有し、pub/sub が competing consumer へ退行する
//      （#258 と同じ事故が、偶然ではなく構造的に起きる）。IADR-0106 の「クラス名を一意にする」対策は効かない。
//   2. Wolverine の既定は、発行しようとした型に**自プロセス内のハンドラがある**と、発行先をローカルキューに
//      閉じる。RiskManagementService は OrderApproved を発行しつつ購読しているため、既定のままでは
//      承認済みの発注が OrderExecutionService へ一通も届かない（発注が執行されない）。
//   3. Wolverine 6 の既定は、ハンドラの生成コードが **service location を要する構成を拒否する**
//      （ServiceLocationPolicy.NotAllowed）。本ユニットの永続アダプタは `internal sealed`（25 型）であり
//      生成コードから直接 new できないため、既定のままでは**1 通目の受信時に**例外になる。
//      しかもこれは起動時ではなくハンドラ生成が走る初回受信時に起きるため、起動もヘルスチェックも
//      キュー宣言も consumer 接続も成功したまま、メッセージだけが無言で処理されない（IADR-0129 決定 11）。
public static class WolverineExtensions
{
    // 再試行間隔（MassTransit 時代の UseAiStockTradingRetry と同値）。
    // 一時的失敗（保存失敗・外部サービスの一時エラー等）は間隔を空けて再試行し、
    // 使い切った継続失敗は <queue>_error へ退避する（メッセージ喪失なく回復性を確保する）。
    private static readonly TimeSpan[] RetryIntervals =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    // 既定の RabbitMQ 接続文字列（dev/test/CI のローカル単体実行用。IADR-0013）。
    public const string DefaultRabbitMqConnectionString = "amqp://guest:guest@rabbitmq:5672";

    /// <summary>
    /// IADR-0129 決定 1: リスニングキューの名前。<c>&lt;ServiceName&gt;.&lt;メッセージ型名&gt;</c>。
    /// キュー名の一意性を「人間が付けるクラス名」ではなく、既に一意な <paramref name="serviceName"/>
    /// （OpenTelemetry / Serilog と同じサービス識別子）に帰着させる。
    /// </summary>
    public static string QueueNameFor(string serviceName, Type messageType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(messageType);
        return $"{serviceName}.{messageType.Name}";
    }

    /// <summary>
    /// IADR-0129 決定 5: デッドレターキューの名前。MassTransit 時代と同じ <c>&lt;queue&gt;_error</c> を保つ
    /// （Wolverine の既定は全キュー共有の <c>wolverine-dead-letter-queue</c> であり、どのキューで失敗したかが
    /// キュー一覧から読めなくなるため採らない）。
    /// </summary>
    public static string DeadLetterQueueNameFor(string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        return $"{queueName}_error";
    }

    /// <summary>
    /// 本ユニット共通の RabbitMQ 配線。全サービスがこれだけを呼ぶ（IADR-0129 決定 4）。
    /// </summary>
    /// <param name="options">Wolverine の構成。</param>
    /// <param name="serviceName">サービス識別子（例 <c>ai-stock-trading.cost-control-service</c>）。キュー名の接頭辞になる。</param>
    /// <param name="connectionString">RabbitMQ の接続文字列。未指定なら <see cref="DefaultRabbitMqConnectionString"/>。</param>
    /// <param name="handlerAssemblies">
    /// 自アセンブリ**以外**にハンドラを置いている場合の追加アセンブリ。自分自身は常に走査されるため渡す必要はない
    /// （IADR-0268 で application assembly を呼び出し元へ固定した。渡しても重複は無害）。
    /// </param>
    // IADR-0268: GetCallingAssembly を使うため、インライン化を禁止する（インライン化されると
    // 呼び出し元がひとつ上へずれ、固定するアセンブリを取り違える）。
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void UseAiStockTradingRabbitMq(
        this WolverineOptions options,
        string serviceName,
        string? connectionString = null,
        params Assembly[] handlerAssemblies)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        options.ServiceName = serviceName;

        // 🔴 IADR-0268 決定 1: **ハンドラ探索の範囲を呼び出し元サービスのアセンブリへ固定する。**
        //
        // Wolverine の既定はハンドラを「application assembly」から走査する。application assembly は
        // 明示しなければ実行時に推論され、**同一プロセスで 2 つ目以降に起動したホストは、先に起動した
        // ホストが確定させたものを引き継ぐ**。1 サービス 1 プロセスの本番では表に出ないが、
        // 複数サービスの Worker を同居させる実基盤 E2E（WebApplicationFactory）では、
        // **後から起動したサービスが他サービスのハンドラを自分のハンドラグラフへ取り込む**。
        //
        // 取り込んだ側には相手の依存が DI に無いため、チェーンの組み立てが
        // NotSupportedException（"does not have a suitable, public constructor ... or is missing
        // registered dependencies"）で失敗する。これは**起動時ではなく 1 通目の受信時**に起きるため、
        // 起動・ヘルスチェック・キュー宣言・consumer 接続がすべて成功したまま、
        // **メッセージだけが無言で処理されない**（IADR-0129 決定 11 と同じ失敗様式）。
        //
        // 実測（#600 の VSA 移送後）: リスク管理ホストを先に起動したプロセスで発注執行ホストが
        // RiskManagementService のハンドラを拾い、OrderApproved が一通も執行されなくなった。
        // 移送前は相手の application assembly（`*.Api`）がハンドラを含まなかったため無害だっただけで、
        // **配線の欠陥は移送前から存在した**。
        options.ApplicationAssembly = Assembly.GetCallingAssembly();

        // IADR-0129 決定 11: ハンドラの生成コードに service location を許可する。
        // 本ユニットの永続アダプタ（EfExecutedOrderStore 等）は `internal sealed` であり、生成コードは
        // これを直接 new できず IServiceProvider から解決する（＝service location）しかない。
        // Wolverine 6 の既定 NotAllowed のままだと、**キュー宣言も consumer 接続も起動も成功したうえで**、
        // 1 通目の受信時のハンドラ生成が InvalidServiceLocationException で失敗し、メッセージが無言で
        // 処理されない（実測: 実 RabbitMQ の E2E で発注が一件も執行されなかった）。
        // 代替（全アダプタを public 化）は本ユニットの内部実装隠蔽（`internal sealed`・25 型）を壊すため採らない。
        options.ServiceLocationPolicy = ServiceLocationPolicy.AlwaysAllowed;

        // IADR-0129 決定 3: 発行がプロセス内へ閉じるのを止める。
        // これが無いと、発行元サービス自身が同じ型を購読している経路（OrderApproved / StopLossTriggered）で
        // 他サービスへ一通も届かなくなる。ビルドもユニットテストも緑のまま起こる退行である。
        options.Policies.DisableConventionalLocalRouting();

        // IADR-0129 決定 5: 再試行（2s/10s/30s の 3 回）→ 使い切ったら <queue>_error へ。
        options.OnAnyException()
            .RetryWithCooldown(RetryIntervals)
            .Then.MoveToErrorQueue();

        foreach (var assembly in handlerAssemblies)
        {
            options.Discovery.IncludeAssembly(assembly);
        }

        options.UseRabbitMq(new Uri(connectionString ?? DefaultRabbitMqConnectionString))
            .AutoProvision()
            .UseConventionalRouting(convention => convention
                // IADR-0129 決定 1: サービス名を前置してキューを分離する。
                .QueueNameForListener(messageType => QueueNameFor(serviceName, messageType))
                // IADR-0129 決定 5: DLQ はキュー単位（<queue>_error）。
                .ConfigureListeners((listener, _) =>
                    listener.DeadLetterQueueing(new DeadLetterQueue(DeadLetterQueueNameFor(listener.QueueName)))));

        // IADR-0129 決定 2: 送信側の exchange 名はメッセージ型名（Wolverine 既定）のままにする。
        // fan-out はこの共有 fanout exchange の上で成立するため、exchange にサービス名を混ぜてはならない
        // （PrefixIdentifiers で exchange まで前置すると、発行側と購読側が永久に出会わない）。
    }
}
