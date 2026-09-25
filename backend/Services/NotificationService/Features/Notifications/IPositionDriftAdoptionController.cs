using AiStockTrading.Shared.Contracts.Trading;

namespace NotificationService.Features.Notifications;

// FR-10, FR-11, FR-14, UC-06, ADR-0041 決定 4, #871, IADR-0350, IADR-0423:
// 台帳とブローカーの乖離の取り込み（リスク管理の OwnerOnly エンドポイント）を呼ぶだけのポート。
// 通知サービスは台帳も観測も持たない（権威はリスク管理）。kill switch / GFV 解除 / 段階ゲートと同型。
//
// 返り値は**整形済み表示テキスト**（Message）を持つ。リスク管理の JSON 表現への結合は実装アダプタに閉じる
// （IADR-0081 決定1 と同じ representation-agnostic）。
public interface IPositionDriftAdoptionController
{
    // 取り込みを要求する。**数量は渡さない**（目標は最新の観測が決める）。理由は必須（リスク管理が空を 400 で拒否する）。
    //
    // FR-11, #871, IADR-0240 決定11, IADR-0383: **onBehalfOf は多層認証が解決した操作者**（Keycloak 利用者名）。
    // Bot のトークンは owner マップ機密クライアントのもので人を表さないため、渡さないと台帳・監査の操作者が
    // `unknown`／`client:<azp>` になる。**省略できない引数**にして渡し忘れを型で止める。
    Task<PositionDriftAdoptionResult> AdoptAsync(
        string symbol, Market market, string reason, string onBehalfOf, CancellationToken cancellationToken = default);
}

// FR-10, FR-11, UC-06, #871: 取り込み要求の結果。
//
// **Succeeded=false はリスク管理の呼び出し自体が失敗した、または要求が不備（400）であったこと**を意味し、
// **Adopted=false（受理されなかった＝観測が古い・乖離が無い・数量の増加など。422）とは区別する** ——
// どちらも台帳は変わっていない。「失敗を成功に見せない」ことが安全側になる（GFV 解除・段階ゲートと同じ方針）。
public sealed record PositionDriftAdoptionResult(bool Succeeded, bool Adopted, string Message);
