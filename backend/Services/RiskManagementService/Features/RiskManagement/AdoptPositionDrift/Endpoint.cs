using AiStockTrading.Shared.Contracts.Logging;
using AiStockTrading.Shared.Contracts.Trading;
using Wolverine;

namespace RiskManagementService.Features.RiskManagement.AdoptPositionDrift;

// ---- 乖離の取り込み（FR-10, FR-11, UC-06, ADR-0003, #849, IADR-0350）: 利用者のみ（OwnerOnly）。理由必須 ----
//
// システム外の売買（証券会社のアプリからの直接の売却など）で台帳とブローカーが乖離したとき、利用者が内容を確認して
// **観測されているブローカーの建玉へ台帳の数量を合わせる**。乖離の検知（IADR-0118）は是正しないため、
// これが無いと実在しない建玉が段階資金・保有建玉数の枠を占有し続け、新規建てが止まる（#849 の実測）。
//
// サービストークンには開かない（生成AI・自動処理が台帳を書き換えられないようにする＝FR-10「生成AIはこれらを
// 上書きできない」・ADR-0003。手仕舞い・kill switch と同じ権限管理）。
// **数量は受け取らない**——利用者が任意の数量で台帳を書き換える API にしない。
//
// FR-14, FR-11, ADR-0041 決定 4, #871, IADR-0240 決定11, IADR-0383, IADR-0423: **窓口は本 API と Discord Bot の両方**である。
// Bot は owner マップ機密クライアント（client_credentials）のトークンで本 API を呼び、多層認証で解決した利用者を
// 本文の OnBehalfOf で運ぶ。操作者は DelegatedActorResolver が決める（**信頼するクライアントのトークンに限って**
// OnBehalfOf を採る。利用者トークン直叩きでは無視＝他人の名前で取り込めない）。
// 🔴 **どちらの窓口でも記録の内容は同じ**（操作者・理由・取り込み前後の数量・観測値と観測時刻・実現損益が未記録であること）。
// 理由文は窓口で加工しない。窓口の違いはイベントの AuthorizedBy（認可の主体）にだけ現れる。
internal static class AdoptPositionDriftEndpoint
{
    public static void MapAdoptPositionDrift(this IEndpointRouteBuilder owner) =>
        owner.MapPost("/position-drift/adopt",
            async (PositionDriftAdoptionRequest req, PositionDriftAdoptionService svc, IMessageBus bus,
                DelegatedActorOptions delegated, ILoggerFactory loggerFactory, HttpContext http) =>
        {
            // market は Market?（非 nullable enum は省略時に暗黙 0＝日本市場へ束縛され、誤市場の建玉を対象にしてしまう）。
            if (string.IsNullOrWhiteSpace(req.Symbol))
                return Results.BadRequest(new { error = "symbol（銘柄コード）は必須です。" });
            if (req.Market is not { } market || !Enum.IsDefined(market))
                return Results.BadRequest(new { error = "market は有効な市場（Japan/UnitedStates）を指定してください。" });
            if (string.IsNullOrWhiteSpace(req.Reason))
            {
                return Results.BadRequest(new
                {
                    error = "reason（理由）は必須です。何が起きて台帳を合わせるのかを記録として残してください。",
                });
            }

            // FR-11, #871, IADR-0383, IADR-0423: 操作者の解決（代理は信頼するクライアントのトークンに限る）。
            var actor = DelegatedActorResolver.Resolve(http.User, req.OnBehalfOf, delegated.TrustedClientIds);
            var actorLogger = loggerFactory.CreateLogger("PositionDriftAdoptionActor");

            // 信頼クライアントの代理指定が値域外。**操作者を記録できない取り込みは行わない**（台帳にも触れない）。
            if (actor.Rejected)
            {
                actorLogger.LogWarning(
                    "乖離の取り込みの代理される利用者（OnBehalfOf）が値域外のため拒否しました（Symbol={Symbol}）。",
                    LogSanitizer.Sanitize(req.Symbol));
                return Results.BadRequest(new { error = "代理される利用者（onBehalfOf）の形式が不正です。" });
            }

            // 🔴 **操作者をまったく特定できない**（名前クレームも azp も無いトークン）。`unknown` を台帳へ残さない
            // （IADR-0383 決定 3 と同じ閂。7 年保持の台帳で「誰が台帳を書き換えたか」が失われる）。
            if (actor.IsUnidentified)
            {
                actorLogger.LogWarning("乖離の取り込みの操作者を特定できないため拒否しました。");
                return Results.BadRequest(new
                {
                    error = "操作者を特定できないため取り込みを行いません（トークンに利用者名がありません）。",
                });
            }

            // 代理指定を信じなかった（利用者トークン直叩き・一覧外のクライアント・azp 欠落・一覧未設定）。
            // 取り込みは通すが（操作者はトークンの主体）、なりすましの試行／設定漏れのどちらも見えるよう警告に残す。
            if (actor.IgnoredOnBehalfOf)
            {
                actorLogger.LogWarning(
                    "乖離の取り込みの OnBehalfOf を無視しました（信頼するクライアントのトークンではありません。操作者={Actor}）。",
                    LogSanitizer.Sanitize(actor.Actor));
            }

            var outcome = svc.Adopt(new PositionDriftAdoptionCommand(req.Symbol, market, req.Reason), actor.Actor);

            if (!outcome.Accepted)
            {
                return Results.UnprocessableEntity(
                    new PositionDriftAdoptionRejectionBody(DescribeRejection(outcome.Rejection), outcome.Rejection.ToString()));
            }

            // FR-11: 取り込み行（台帳）は既に永続しており、それが権威（fail-safe・IADR-0082 と同型）。
            // ここでは誰が・なぜ・何を観測して・どう変えたかを中央監査台帳と通知へ流す。
            // #871, IADR-0423: 代理（Discord Bot 経由）のときは認可の主体（クライアント ID）も運ぶ（利用者本人のトークンは null）。
            var adopted = outcome.Adopted! with { AuthorizedBy = actor.AuthorizedBy };
            await bus.PublishAsync(adopted);

            return Results.Ok(PositionDriftAdoptionResponse.From(adopted));
        });

    // **何が足りないか・どうすれば通るかを具体的に返す**（「受理できません」では次の一手が分からない）。
    private static string DescribeRejection(PositionDriftAdoptionRejection rejection) => rejection switch
    {
        PositionDriftAdoptionRejection.ReasonRequired => "reason（理由）は必須です。",
        PositionDriftAdoptionRejection.ObservationUnavailable =>
            "ブローカ建玉の観測がまだ届いていません（照会不能を含む）。観測できていない値へ台帳を合わせることはできません。",
        PositionDriftAdoptionRejection.ObservationStale =>
            "ブローカ建玉の最新の観測が古すぎます（60 分超）。建玉の照会が止まっていないか確認し、次の観測を待って再試行してください。",
        PositionDriftAdoptionRejection.NoDrift =>
            "当該銘柄に取り込む乖離がありません（最新の観測と台帳は一致しています。取り込み済みを含む）。",
        PositionDriftAdoptionRejection.DriftNotReported =>
            "その乖離はまだ報告されていません（同じ内容を連続して観測したときに報告されます）。"
            + "約定が台帳へ届く前の一過性のずれかもしれないため、乖離の通知を待って再試行してください。",
        PositionDriftAdoptionRejection.UnsupportedDirection =>
            "取り込めるのは台帳の建玉を減らす乖離だけです。台帳に無い建玉・数量の増加・方向の反転は、"
            + "取得単価も損切りも分からない建玉を台帳へ作ることになるため取り込みません。",
        PositionDriftAdoptionRejection.LedgerMovedAfterObservation =>
            "最新の観測より後に台帳の当該銘柄が動いています。次の観測を待って再試行してください。",
        PositionDriftAdoptionRejection.CloseInFlight =>
            "当該銘柄に処理中の決済（承認済み・未約定）があります。約定が後から届くと二重に減るため、"
            + "決済の約定または 30 分の経過を待って再試行してください。",
        _ => "取り込み要求を受理できません。",
    };
}

// #849, IADR-0350: 乖離の取り込み要求（理由必須）。**数量は含めない**（観測から決まる）。
// Market は nullable。非 nullable enum は本文省略時に既定値 0（＝Japan）へ暗黙束縛されるため、省略を 400 として弾く。
//
// FR-14, FR-11, #871, IADR-0383, IADR-0423: OnBehalfOf（任意・**末尾に追加**）は、呼び出し元が**代理している利用者**
// （Keycloak 利用者名）。Discord Bot が多層認証で解決した操作者を載せる。
// 🔴 **信頼するクライアントのトークン以外では無視される**（DelegatedActorResolver）。
// 公開型にしているのは、送り手（Discord Bot）の契約テスト（T-10-989）が本物の型で本文を読み戻すため（IADR-0423）。
public sealed record PositionDriftAdoptionRequest(string? Symbol, Market? Market, string? Reason, string? OnBehalfOf = null);

/// <summary>
/// FR-10, FR-11, #849, #871, IADR-0350, IADR-0408 決定3, IADR-0423: 取り込みの受理の応答（200）。
/// <para>
/// 以前は匿名型で、受け手（Discord Bot）の契約テストが項目名を送り手の型から得られなかった。**JSON は以前と同じ**
/// （web 既定・camelCase・列挙は数値）で、<see cref="Actor"/> だけを末尾へ足した（記録した操作者を窓口が確かめられる）。
/// </para>
/// </summary>
public sealed record PositionDriftAdoptionResponse(
    Guid AdoptionId,
    string Symbol,
    Market Market,
    int LedgerQuantityBefore,
    int LedgerQuantityAfter,
    int BrokerQuantity,
    DateTimeOffset ObservedAt,
    // **実現損益は記録していない**（不明）。推定は参考であり、台帳のどの数値にも入っていない。
    bool RealizedPnlRecorded,
    decimal? ReferencePrice,
    decimal? EstimatedPnlInBase,
    DateTimeOffset AdoptedAt,
    // #871, IADR-0423: 台帳へ記録した操作者（Discord Bot 経由なら代理される利用者）。
    string Actor)
{
    public static PositionDriftAdoptionResponse From(AiStockTrading.Shared.Contracts.Events.PositionDriftAdopted adopted)
    {
        ArgumentNullException.ThrowIfNull(adopted);
        return new(
            adopted.AdoptionId,
            adopted.Symbol,
            adopted.Market,
            adopted.LedgerQuantityBefore,
            adopted.LedgerQuantityAfter,
            adopted.BrokerQuantity,
            adopted.ObservedAt,
            adopted.RealizedPnlRecorded,
            adopted.ReferencePrice,
            adopted.EstimatedPnlInBase,
            adopted.AdoptedAt,
            adopted.Actor);
    }
}

/// <summary>
/// FR-10, #849, #871, IADR-0350, IADR-0423: 取り込みを受理できない応答（422）の本文。<b>いずれも台帳を書いていない。</b>
/// <see cref="Error"/> は利用者向けの文言（何が足りないか・どうすれば通るか）で、Discord Bot はこれをそのまま利用者へ返す。
/// <see cref="Code"/> は <see cref="PositionDriftAdoptionRejection"/> の名前。
/// </summary>
public sealed record PositionDriftAdoptionRejectionBody(string Error, string Code);
