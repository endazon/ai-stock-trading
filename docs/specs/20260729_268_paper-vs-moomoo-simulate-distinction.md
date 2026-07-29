---
title: paper（内蔵擬似約定）と moomoo SIMULATE（OpenD 経由）の区別を運用・検証資料へ明記する
type: spec
status: draft
related_ids:
  - FR-05
  - FR-12
  - ADR-0002
  - IADR-0016
  - IADR-0056
  - IADR-0060
  - IADR-0111
author: endazon (with Claude Code)
created: 2026-07-29
updated: 2026-07-29
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md"
---

# 仕様書: paper と moomoo SIMULATE の区別の明文化（#268）

> 本作業は**ドキュメントのみ**である。コード・既定値・Helm values に一切触れない。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-05（発注執行）/ FR-12（ペーパートレード）
- ユースケース（UC）: 該当なし（運用・検証手順の明文化）
- 画面（SC）: 該当なし
- 関連 ADR: [ADR-0002](../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md)（証券会社連携）/
  [IADR-0016](../adr/IADR-0016_safe-broker-execution.md)（安全既定のブローカ執行）/
  [IADR-0056](../adr/IADR-0056_moomoo-simulate-poc-complete-real-gated.md)（SIMULATE PoC 完了・実弾はゲート）/
  [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)（OpenD 本番切替ゲート）/
  [IADR-0111](../adr/IADR-0111_broker-tier-selection.md)（ブローカ階層）
- 計画書リンク: [ADR-0002](../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md)

## 目的・背景

検証中に「SIMULATE で発注したのに moomoo 側に何も届かない」という取り違えが起きた（[#268](https://github.com/endazon/ai-stock-trading/issues/268)）。

- `Broker__Provider=paper`（既定・`broker.tier=paper`）は **プロセス内蔵の `PaperBrokerAdapter`** による擬似約定であり、
  OpenD へは 1 リクエストも出さない。参照価格で即時全量 `Filled` になる。
- `Broker__Provider=moomoo`（`broker.tier=moomoo-sim`）は **OpenD 経由で moomoo の模擬口座へ実発注**する経路であり、
  発注直後は `Accepted`（未約定）である。

どちらも「実弾ではない」が**約定の主体・残高・注文履歴の所在がまったく別**で、資料はこの区別を明示していなかった。
結果、paper の擬似約定を moomoo 模擬口座の履歴で探して見つからない、という混乱が生じ得る。

## 対象範囲

- 対象: `docs/operations/` の運用資料・検証 runbook、`deploy/helm/ai-stock-trading/README.md`、
  `docs/how-to/local-run.md` への区別・識別方法の追記。
- 対象外:
  - コード変更・既定値変更・Helm values 変更（**一切行わない**）。
  - ブローカ階層の切替設計そのもの（[#269](https://github.com/endazon/ai-stock-trading/issues/269) /
    [IADR-0111](../adr/IADR-0111_broker-tier-selection.md) で決着済み。本書は参照するのみ）。
  - moomoo 経路で約定が台帳へ反映されない不具合（[#270](https://github.com/endazon/ai-stock-trading/issues/270)）の是正。
    本書は**既知の制約として明記し相互参照する**にとどめる。

## 設計

### 文書の配置（単一情報源と参照の方向）

| 文書 | 役割 | 本作業での変更 |
| --- | --- | --- |
| `docs/operations/broker-execution-paths-runbook.md`（新規） | **区別と識別方法の単一情報源**。2 経路の対比表・識別手順・moomoo 有効化の前提・実弾が拒否される仕組みへの参照 | 新規作成 |
| `docs/operations/operations.md` | 運用仕様書。デプロイ表から新 runbook へ導線を張る | 行追加 |
| `docs/operations/live-trading-cutover-runbook.md` | 実弾解禁 runbook。**閂の詳細はこちらが単一情報源**で、新 runbook からは参照のみ（重複管理しない） | 参照節へ 1 行追加 |
| `deploy/helm/ai-stock-trading/README.md` | 経路B（ローカル SIMULATE）の検証手順。`broker.tier` 節に「どちらで約定したか」の識別を追記 | 節内へ追記 |
| `docs/how-to/local-run.md` | compose のローカル実行手順。既定 paper＝擬似約定であることを明示 | 注記追加 |

### 記載内容（実コードで裏取りした事実のみを書く）

| 論点 | 根拠（実装箇所） |
| --- | --- |
| paper は即時 `Filled`・`OrderId` は 32 桁 hex（`Guid.ToString("N")`） | `PaperBrokerAdapter.PlaceOrderAsync` |
| moomoo は発注直後 `Accepted`（`MoomooOrderState.Submitted` → `OrderStatus.Accepted`）・`OrderId` は moomoo 採番の数値 | `MMApiMoomooTradeClient.PlaceOrderAsync` / `MoomooBrokerAdapter.MapState` |
| moomoo 経路でも**発注不達・不正注文は 32 桁 hex の `Rejected`** になる（＝形だけでは判定しきれない） | `MoomooBrokerAdapter.Terminal` |
| 起動ログ `OpenD 接続完了・SIMULATE 口座 accId=` / 発注ログ `moomoo SIMULATE 発注成功 orderId=` | `MMApiMoomooTradeClient` |
| moomoo 注文の備考（remark）に `DecisionId`（ハイフン無し 32 桁）が載る | `MoomooClientOrderId.From` / `MoomooBrokerAdapter.PlaceOrderAsync(intent, decisionId, ...)` |
| 稼働中の階層は `GET /internal/introspection` の `broker` ポートが自己申告（`paper` / `moomoo-sim`） | `OrderExecutionService.Worker/Program.cs` |
| 実弾に行かない 4 層＋外周（閂 0〜4・Helm 描画 fail） | `LiveTradingGate` / `BrokerFactory` / `MMApiMoomooTradeClient.BuildHeader`・`FetchSimulateAccIdAsync` / `MoomooBrokerOptions.EnsureSimulate` / chart `deployment.yaml` |

## 受け入れ基準

（[#268](https://github.com/endazon/ai-stock-trading/issues/268) の受け入れ基準を転記）

- [ ] 運用/検証資料に paper と moomoo SIMULATE の違いが表形式で明記されている（約定主体・残高・履歴の確認先）
- [ ] 設定キーと現在の既定（paper）が記載されている
- [ ] moomoo SIMULATE を使う場合の前提（OpenD・資格情報）と、実弾が拒否される仕組みへの参照がある
- [ ] コード変更は伴わない（ドキュメントのみ）

本作業で追加する条件:

- [ ] どちらの経路で約定したかを**事後に識別する手順**（`OrderId` の形・ログ・remark・moomoo アプリ目視）が記載されている
- [ ] [#269](https://github.com/endazon/ai-stock-trading/issues/269)（ブローカ階層）と
      [#270](https://github.com/endazon/ai-stock-trading/issues/270)（moomoo 台帳未反映）への相互参照がある

## テスト方針

コードを変更しないため xUnit の追加は無い。検証は次で行う。

- `git diff --stat` が `docs/` と `deploy/helm/ai-stock-trading/README.md` のみであること（コード無改修の証明）。
- 相対リンクの実在確認（`check-doc-links` CI）。planning 配下への参照は既存資料と同じ書式に揃える。
- 記載事実は上表の実装箇所を都度参照して裏取りする（推測を書かない）。

## 計画書との差異

- 差異: なし（ADR-0002 の「moomoo を第一候補・SIMULATE で検証」という方針に沿った運用文書化である）。

## 未決事項

- なし。[#270](https://github.com/endazon/ai-stock-trading/issues/270)（moomoo 経路で約定が台帳へ反映されない）は
  本書の範囲外だが、識別手順に影響する既知の制約として runbook に明記する。
