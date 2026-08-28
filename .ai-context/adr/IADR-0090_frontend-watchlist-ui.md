---
title: IADR-0090 SC-02 の監視銘柄（watchlist）変更 UI は MarketMonitor `/monitor/watchlist` を個別操作で消費し、削除は明示確認で fail-safe にする（別サービスとして独立ロード・実 BFF プロキシは MSP 後続）
type: impl-adr
status: Accepted
related_ids: [FR-13, FR-03, FR-11, UC-06, SC-02, IADR-0080, IADR-0084, IADR-0086, IADR-0088]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
---

# IADR-0090: SC-02 の監視銘柄（watchlist）変更 UI は MarketMonitor `/monitor/watchlist` を個別操作で消費し、削除は明示確認で fail-safe にする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-13**（利用者が設定を変更できる）、**FR-03**（監視）、**FR-11**（変更履歴）、**UC-06**（設定変更）
- 対象 Issue: [#196](https://github.com/endazon/ai-stock-trading/issues/196)（フロント監視銘柄変更 UI・`Refs #191 #188 #106`）
- 関連する実装仕様書: [作業仕様](../specs/20260718_196_frontend-watchlist-ui.md)、画面 [SC-02 リスク設定](../../docs/screens/20260718_SC-02_risk-settings.md)
- 前段: [IADR-0088](IADR-0088_watchlist-settings-api.md)（監視銘柄設定ストア API＝本 UI が消費する権威データ源）、
  [IADR-0086](IADR-0086_frontend-guard-edit-ui.md)（SC-02 ガード変更 UI・理由必須/危険操作の明示確認）、
  [IADR-0084](IADR-0084_frontend-risk-settings-and-control-status.md)（SC-02/SC-03 新設・数値 enum 写像）、
  [IADR-0080](IADR-0080_frontend-settings-screen.md)（frontend 新設・単独リポの @foundation スタブ＋vitest）
- 前提（develop マージ済み）: MarketMonitor `GET/POST/DELETE /monitor/watchlist`・`GET /monitor/watchlist/history`
  （OwnerOnly・[MonitorSettingsEndpoints.cs](../../backend/Services/MarketMonitorService/Features/MarketMonitor/MonitorSettingsEndpoints.cs)・PR #195）
- **採番について（0089 の欠番は意図的）**: 本 IADR は **0090** を用いる。**0089 は並行作業の #164 に先着で割り当て済み**
  （ユーザー調整による）で、本ブランチで 0089 を使うと衝突する。番号衝突の扱いは「先着尊重」（[README の採番手順](README.md)・
  前例 [20260717_iadr-0059-number-collision-fix.md](../specs/20260717_iadr-0059-number-collision-fix.md)）に従い当面 0089 を空けて
  0090 とする。develop への #164 マージ時に 0089 が埋まり欠番は解消する（履歴不変・番号の再割り当てはしない）。

## 背景・課題

#188（[IADR-0086](IADR-0086_frontend-guard-edit-ui.md)）は SC-02 にリスク上限・ガードの変更 UI を実装したが、**監視銘柄 UI** は
「設定ストア側の変更 API が未整備」として分離した。#191（PR #195）でその API が整備されたため、本作業は消費側 UI を実装する。
着手時の制約:

1. **監視銘柄 API は別サービスの権威データ源**: watchlist は RiskManagementService ではなく **MarketMonitorService** が所有する
   （[IADR-0088](IADR-0088_watchlist-settings-api.md)）。SC-02（リスク設定画面）は既に Risk `/risk-controls/*` を消費しており、
   監視銘柄を載せると **1 画面 2 サービス**になる。
2. **API は個別操作（全置換ではない）**: `POST /monitor/watchlist`（1 件追加）・`DELETE /monitor/watchlist`（1 件削除・body に理由）で、
   いずれも**理由必須**。ガードの全置換 PUT（IADR-0086）とは形が異なる。
3. **削除は破壊的**: 監視から外すと当該銘柄は変動検知・取引サイクルの対象外になる（誤操作で「見ていない」状態になる）。
4. **enum は数値表現**: MarketMonitor Worker も `JsonStringEnumConverter` 非設定のため、`market`・`changeType` は数値で往復する。
5. **実 BFF プロキシは別リポ**: フロントの apiFetch は `/bff` 前置で BFF を叩くが、`/monitor/*` を MarketMonitor へ中継する
   合成点は microservices-platform（MSP）側にあり、`risk-controls` の合成点（MSP#287）と同様に本リポでは触れない。
6. **計画の画面帰属**: 計画 `05_screens/01_screens.md` は監視銘柄を **SC-01（設定画面）の運用パラメータ**節に置くが、frontend 実装は
   IADR-0084 で「所有サービス単位に画面を分ける」方針を採り、リスク統制系を SC-02（`settings/risk`）へ独立させている。#196 は
   SC-02 への追加を明示指定する。

## 決定

### 1. 既存 `sc02-risk-settings` feature に「監視銘柄」セクションを追加し、別サービスとして**独立ロード/縮退**する

新しい feature/route/nav は増やさず、`RiskSettingsPage` のページ末尾に自己完結コンポーネント `WatchlistForm` を置く。
`WatchlistForm` は自前で `GET /monitor/watchlist`（一覧）と `GET /monitor/watchlist/history`（履歴）を読み、`status`・`historyStatus`
を**独立**に持つ。`RiskSettingsPage` はリスク設定の `status` に**連動させず無条件に**レンダリングする。根拠: 監視銘柄は Risk とは
別サービス（MarketMonitor）が所有し、片方の取得可否・BFF 未結線がもう片方を巻き込むべきでない（fail-safe な疎結合）。計画は監視銘柄を
SC-01 に置くが、#196 の明示指定と IADR-0084 の「所有サービス単位で画面を分ける」方針に従い SC-02（owner 限定の運用設定画面）へ載せる
（画面帰属の差異は計画へ環流する）。

### 2. 追加/削除は**個別操作 API**を消費する（全置換しない）。理由必須・400/409 は #186/#188 の作法を踏襲する

`POST /monitor/watchlist`（`{ symbol, market, reason }`）で 1 件追加、`DELETE /monitor/watchlist`（body `{ symbol, market, reason }`）で
1 件削除する。クライアント側で一覧をマージして全置換 PUT する方式は採らない（API 形と乖離し、並行更新を握り潰す）。検証(400)・競合(409)・
権限(403) はメッセージへ写像し、**破壊的な自動再試行はしない**（安全既定）。成功後は一覧・履歴を再取得して最新化する。重複追加・不在削除の
実効検証はサーバ 400 に委ね（#191/IADR-0088）、クライアントは空欄（symbol/理由）でのボタン無効化という明白な無効のみ抑止する（#186 と同方針）。
`market` は必須指定（未指定はサーバ 400。#191 で `Market?` として明示指定を必須化済み）。

### 3. 削除は「明示確認」を必須にする（fail-safe）。追加は 1 段でよい

監視からの削除は破壊的（対象が検知・取引の対象外になる）ため、行の「削除」を押すとインライン確認（**削除理由**入力＋「監視から削除」
確認ボタン＋キャンセル）を開き、理由が入るまで確定不可とする。**自動再試行なし**。一方、追加は監視対象を**増やす**方向（統制の厳格化に近い）で
誤操作の被害が小さいため 1 段（銘柄コード＋市場＋理由が揃えば追加可）とする。根拠: 統制の網を狭める（＝守りを外す）操作にだけ二段の意思確認を
課す非対称設計（IADR-0086 決定 3 と同趣旨）。

### 4. 数値 enum ↔ ラベルの写像はフロントに閉じ、未知値は安全側フォールバックにする（IADR-0084 決定 4 踏襲）

`market`（0=日本/1=米国）は `risk/contracts` の `marketLabel`/`MARKET_OPTIONS` を**再利用**する（共有 `Trading.Market` enum で同一のため
重複写像を作らない）。履歴の `changeType`（`MonitorSettingsChangeType`＝Risk の `SettingsChangeType` とは別 enum・0=追加/1=削除）は
`monitor/contracts` に新規写像を置く。いずれも未知値は「不明(N)」へフォールバックし画面を壊さない。バックエンドの JSON 表現は変えない。

### 5. 実 BFF の `/monitor/*` プロキシ結線は MSP 後続へ分離し、フロントは論理パスを消費・E2E で契約検証する

フロントは `/monitor/watchlist`（apiFetch が `/bff` 前置）を叩く。実プロキシ（BFF→MarketMonitor 中継）は MSP 側の合成点であり、
`risk-controls`（MSP#287）と同様に別リポの後続作業として分離する。E2E（Playwright）は `page.route('**/bff/**')` のモックで
`GET/POST/DELETE /monitor/watchlist` のパス/メソッドを契約として追認する（実クラスタ疎通に依存しない）。MSP 側フォローアップ issue を
優先度ラベル付きで起票する。

## 影響・トレードオフ

- **利点**: バックエンド・foundation とも無改修で FR-13 の監視銘柄変更を充足。破壊的な削除にだけ確認を課し誤操作面を最小化。別サービスを
  独立ロードにすることで片方の障害・未結線を巻き込まない。数値 enum の変化にフォールバックで頑健。既存 feature へ最小追加で画面粒度を保つ。
- **代償**: SC-02 が 2 サービス（Risk＋MarketMonitor）を消費する（画面責務がやや広がる）。計画は監視銘柄を SC-01 に置くため画面帰属に
  差異が残る（環流で調整）。実 BFF `/monitor/*` プロキシ未結線の間は一覧が 404 縮退する（「利用できません」表示＝安全側）。
- **却下案**: (a) 一覧をクライアントでマージし全置換 PUT → API 形と乖離し並行更新を握り潰すため却下（個別操作を消費）。(b) 監視銘柄を
  SC-01 に載せる（計画準拠）→ #196 の明示指定と所有サービス単位の画面分割方針（IADR-0084）に反するため SC-02 とし、差異は環流。
  (c) Risk 設定の status に監視銘柄ロードを従属させる → 別サービス障害を相互に巻き込むため独立ロードにする。(d) 削除も 1 段 → 破壊的操作の
  誤爆面が大きいため明示確認を課す。

## 検証

`cd frontend && npm ci && npm run typecheck && npm run lint && npm run test`（Vitest）が緑。実ブラウザ E2E（Playwright）は CI の
`frontend-e2e` ジョブ（モック BFF）で実行。実 BFF/`/monitor/*` プロキシ疎通は実基盤依存として MSP 後続へ分離。CI は既存ジョブで実行
（`ci.yml` は追加のみ・既存保持）。
