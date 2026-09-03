---
title: BFF に /risk-controls/short-selling のパススルーを追加する
type: spec
status: draft
related_ids: [SC-03, FR-10, FR-19, IADR-0071, IADR-0091, IADR-0182]
author: claude
created: 2026-09-03
updated: 2026-09-03
plan_refs: [SC-03, FR-10, FR-19]
---

# 仕様書: BFF に /risk-controls/short-selling のパススルーを追加する（#640）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制）, FR-19（取引ガード）
- ユースケース（UC）: UC-06
- 画面（SC）: SC-03（統制状態参照）
- 関連 ADR: ADR-0016 決定15（維持率を最上位に置く）
- 計画書リンク: `projects/ai-stock-trading/05_screens/01_screens.md`（SC-03。隣接クローン参照）

## 目的・背景

issue #640（由来: #204 実装監査 更新版 2026-09-02）が検出した不具合を直す。

SC-03「統制状態参照」画面の「空売りの現況」節は、フロントが
`apiFetch<ShortSellingStatusView>('/risk-controls/short-selling')`（`frontend/src/lib/risk/queries.ts:86`）
を呼ぶが、BFF（`backend/Bff/AiStockTrading.Bff.Endpoints/RiskControlsBffEndpoints.cs`）には
この経路のパススルー登録が無い。既存 8 経路（`/settings` `/settings/history` `/settings/limits`
`/settings/guard` `/settings/broker-provider` `/settings/stage1-minimum-trade-count` `/status`
`/stage-gate`）はすべて登録されているが `short-selling` だけが漏れている。

後段（`backend/Services/RiskManagementService/Features/RiskManagement/GetShortSellingStatus/Endpoint.cs`）
は実装済みで `owner.MapGetShortSellingStatus()` により `GET /risk-controls/short-selling` として
`RiskControlEndpoints.cs` の OwnerOnly グループへ既に登録されている。欠けているのは BFF 側の
パススルー 1 経路のみである。

画面は「供給が無い値」を安全側（「取得できていません」）に描く規約（05_screens §供給が無い値の
表示規約）に従っているため、経路欠落があっても画面が壊れて見えず、フロントの単体テスト 5 本が
この経路をモックしているため見逃されていた（issue 本文より）。

なお `IMaintenanceMarginSnapshotSource` が「供給なし」固定である（#634）ため、本 issue の対応後も
維持率そのものの値は出ない。本 issue が直すのは経路の欠落のみであり、値の供給は #634 / #342 / #331
の解決を待つ。

## 対象範囲

- 対象:
  - `backend/Bff/AiStockTrading.Bff.Endpoints/RiskControlsBffEndpoints.cs` へ
    `GET /bff/risk-controls/short-selling` のパススルーを追加する（既存 8 経路と同型）
  - `backend/Bff/AiStockTrading.Bff.Endpoints.Tests/BffPassThroughTests.cs` の
    `AllRoutes`（完全一致テストの母集合）へ追加する
  - 否定形テスト（後段 5xx を正常値化しない）を 1 本追加する
- 対象外:
  - `RiskManagementService` 側（後段）の実装変更（既に実装済み・変更不要）
  - フロント側の変更（既に `apiFetch` 済み・変更不要）
  - 維持率（`IMaintenanceMarginSnapshotSource`）の値供給（#634 / #342 / #331 の範囲）
  - 「BFF ルートとフロントの `apiFetch` パスの対応を検査する検査器」の新設
    （issue 本文が「検討する」としている項目。下記「検査器を追加しない判断」を参照）

## 設計

`RiskControlsBffEndpoints.cs` の `/status`（`MapGet`、後段 `/risk-controls/status`）を雛形に
`GET /short-selling`（後段 `/risk-controls/short-selling`）を追加する。

- 登録方法・後段 URL の組み立て・`ProxyAsync` の呼び出し方（`IHttpClientFactory` → 名前付き
  クライアント → `HttpMethod.Get` → 後段パス）は既存 8 経路と完全に同じ形にする。
- 認可: `MapRiskControlsBffEndpoints` のグループは `RequireAuthorization()`（認証必須・匿名 401）
  のみを課し、owner 判定は後段（`RiskManagementService` 側の OwnerOnly）に委ねる方式が既存 8 経路
  全ての方式である。後段の `GetShortSellingStatus` エンドポイントは既に
  `RiskControlEndpoints.cs` の `owner`（`AiStockTradingAuthPolicies.OwnerOnly`）グループに
  登録済みであるため、本 BFF 経路もこれに合わせて追加登録のみで認可要件を満たす
  （BFF 側で認可ロジックを重複させない＝既存方式の踏襲。IADR-0071 のコメントにある「認可は
  後段が強制する」方針どおり）。
- 登録順序: 後段 `RiskControlEndpoints.cs` の `owner` グループでの登録順は
  `MapGetKillSwitch → ... → MapGetRiskStatus → MapGetShortSellingStatus → MapGetRiskSettings → ...`
  であり、`GetShortSellingStatus` は「表示専用（`GetRiskStatus`）」の直後・「設定
  （`GetRiskSettings` 以降）」の直前に位置する。BFF 側は SC-03 表示専用グループ
  （`/status` `/stage-gate`）の直後に追加し、後段の並び（表示専用のまとまり）と揃える。
- エラー処理・応答透過: `ProxyAsync` を共通利用するため、後段 5xx / タイムアウトの扱い
  （5xx はそのまま透過・後段不達/タイムアウトは 502 へ縮退）は自動的に既存 8 経路と同一になる。
  BFF 層で「取得不能時に 0 や既定値を作って返す」ロジックは元から存在しない
  （`ProxyAsync` は本文を素通しするだけで DTO に結合しない）ため、本追加によって
  受け入れ基準の否定形（5xx/タイムアウトを正常値化しない）が新たに壊れる余地は無い。
  これはテストで固定する（後述）。

追加後のコード（差分イメージ）:

```csharp
// SC-03 統制状態参照（表示専用）: 空売りの現況（維持率・空売り比率・保有建玉方向・借株料累計・
// 自動縮小の現況）。ADR-0016 決定15（維持率を最上位に置く）。後段 OwnerOnly。
// AST #640: 欠落していたパススルーを追加（後段は実装済み・IADR-0154）。
g.MapGet("/short-selling", (IHttpClientFactory httpFactory, HttpContext http, CancellationToken ct) =>
    ProxyAsync(httpFactory, http, HttpMethod.Get, "/risk-controls/short-selling", ct))
    .WithName("BffRiskControlsShortSelling");
```

ファイル冒頭のコメント（「登録経路は SC-02/03 が実消費する 7 本のみ」等、経路数の記述）も
実態（登録本数）に合わせて更新する。

## 受け入れ基準

- [ ] SC-03 の空売り現況節が、`RiskManagementService` の応答を表示できる
      （BFF に `GET /risk-controls/short-selling` パススルーが存在し、200 のとき後段の JSON を
      そのまま透過する）
- [ ] **否定形**: 後段が 5xx / タイムアウトのとき、0 や「—」で正常値のように見せない
      （5xx はステータスをそのまま透過、後段不達/タイムアウトは 502 へ縮退することをテストで固定する）
- [ ] BFF の当該ルートに既存 8 経路と同じ認可（`RequireAuthorization()` ＋ 後段 OwnerOnly）が
      掛かっていること（匿名 401 をテストで固定する）
- [ ] 起点 ID コメント（SC-03 / FR-10 / FR-19）付きのテストを添える

## テスト方針

`backend/Bff/AiStockTrading.Bff.Endpoints.Tests/BffPassThroughTests.cs` に追加する。

1. `AllRoutes`（完全一致テスト `登録されている_BFF_ルートは_AllRoutes_と完全一致する` の母集合）へ
   `["GET", "/bff/risk-controls/short-selling"]` を追加する。これにより：
   - `Anonymous_request_is_rejected_with_401`（Theory）が当該ルートにも自動的に適用され、
     匿名 401 を固定する（受け入れ基準3）。
   - 完全一致テストが新ルートを検知し続ける（意図しない経路の増減の回帰ガード。ADR-0028 決定3 と
     同型の防御だが本経路は表示専用の GET であり破壊的操作ではないため対象外）。
2. 否定形テストを 1 本追加する: 後段が 500 を返したとき、BFF が 200 や既定値へ縮退させず
   500 をそのまま透過することを固定する（`Downstream_4xx_is_passed_through_unchanged` は
   400/403/404/409 のみを対象にしており 5xx を持たないため、本 PR で
   `Downstream_5xx_for_short_selling_is_not_disguised_as_success` を追加する）。
   既存の `Downstream_unreachable_degrades_to_502`（後段不達→502）は他経路
   （`/bff/risk-controls/status`）で既に固定されており、`ProxyAsync` 共通処理のため
   `/short-selling` でも同じ経路を通ることは自明だが、issue の受け入れ基準が
   「短絡的な安全側表示（0 や「—」）にしない」ことを名指ししているため、
   `/short-selling` 自身に対する固定を独立に持つ。

## 検査器を追加しない判断

issue #640 は「BFF ルートとフロントの `apiFetch` パスの対応を検査するテスト」を**検討する**と
書いているが、本リポジトリの規約（CLAUDE.md「検査器・規約の追加は同型事故 2 回から」）に従い、
**本 PR では追加しない**。

- 本件（BFF 経路の登録漏れ）が確認できた事故はこの #640 が **1 件目** である。
  過去に類似の「BFF 経路の登録漏れ」として記録されているのは #340（`/monitor/settings` 未結線・
  IADR-0164）だが、これは「フロントが新しい経路を叩き始めたのに BFF が未結線」という**追随漏れ**
  であり、本件（後段は元から実装済みで BFF 登録だけを 1 本忘れた）とは発生機序が異なる。
  同型と数えても 2 件に届かず、規約の閾値（2 回）に達しない。
- 既存の `登録されている_BFF_ルートは_AllRoutes_と完全一致する`（`BffPassThroughTests.cs`）は
  「BFF に生えている経路」の増減は検知できるが、「フロントが叩くが BFF に無い経路」
  （今回のような**登録漏れ**）は検知できない——ホワイトリスト自体を更新しない限り増減を検知する
  設計であり、本件のような「そもそも足りない」状態を横断的に検査するには、フロントの
  `apiFetch` 呼び出し全件と BFF 登録済みルート全件を静的に突合する専用の検査器が要る。
  これは相応の設計判断（フロント側の走査方法・許可リストの持ち方・誤検知の扱い）を要し、
  1 件目の事故で足すには時期尚早と判断する。
- 次に同型の事故（フロントが呼ぶ経路が BFF に登録されていない）が起きたら 2 件目として
  検査器の新設を検討する。

## 計画書との差異

- 差異: なし（計画書どおりの経路を、既存の実装パターンに揃えて追加するのみ）

## 未決事項

- なし
