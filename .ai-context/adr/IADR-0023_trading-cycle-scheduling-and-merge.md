---
title: IADR-0023 定時/イベント駆動サイクルは取引判断で合流させ、開場日ゲートは市場カレンダーで行う
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-03, FR-04, ADR-0003, ADR-0006]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/ai-stock-trading/04_workflows/01_scheduled-trading-cycle.md
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# IADR-0023: 定時/イベント駆動サイクルは取引判断で合流させ、開場日ゲートは市場カレンダーで行う

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-02（取引サイクル）、FR-03（価格変動）、FR-04（判断）、ADR-0003、ADR-0006（Hetzner・スケジューラ実現）
- 対象 Issue: [#21](https://github.com/endazon/ai-stock-trading/issues/21)（Slice A）
- 関連する実装仕様書: [20260710_trading-cycle-wiring](../specs/20260710_trading-cycle-wiring.md)
- 関連 IADR: [IADR-0013](IADR-0013_platform-foundation-testsupport-shim.md)（standalone 配線は test-only 足場）、[IADR-0022](IADR-0022_information-collection-safe-sourcing.md)（`InformationCollected` 発行元）

## コンテキストと課題

FR-02 は定時（情報収集→判断→発注）とイベント駆動（価格変動→判断）の2系統を同一パイプラインへ合流させることを要求する。
現状、価格変動系統は結線済みだが定時系統が未結線。また (1) 両系統をどこで合流させるか、(2) 休場日ゲート（祝日含む）をどこで
行うか、(3) スケジューラ方式（K8s CronJob / Quartz.NET 常駐）を決める必要がある（計画の未決事項）。

## 検討した選択肢

**合流点**:
1. 各系統が別々に判断ロジックを持つ — 重複・不整合。
2. **取引判断サービスで合流**（採用） — ワークフロー（COL→TRD 収集完了イベント）どおり、両トリガーを取引判断が受けて同一
   `DecideAsync` に流す。トリガーを `DecisionTrigger`（種別＋銘柄・市場＋任意の価格文脈）に一般化する。

**スケジューラ方式**:
1. **K8s CronJob** — 本番の定時実行に適するが、ローカル dev/CI では動かせず、platform 統合（#22）前提。
2. **Quartz.NET 常駐** — アプリ内スケジュールだが、現状の各サービスは既に in-process の `PeriodicTimer` BackgroundService で
   定時ポーリングしており（市場監視=価格、情報収集=定時）、追加依存の利得が薄い。
3. **現段階は in-process ポーリング（採用）** — 既存パターンを踏襲し、本番の CronJob 化は platform 統合（#22・ADR-0006）で確定。

## 決定

- **合流は取引判断サービスで行う**。`DecisionTrigger`（`Scheduled`/`PriceMovement`）で起点を一般化し、`InformationCollected`
  （定時）と `PriceMovementDetected`（イベント）の両 Consumer が同一 `DecideAsync` に合流する。定時系統は監視銘柄
  （`IWatchlistProvider`）を巡回して判断する。
- **開場日ゲートは市場カレンダー（`IMarketCalendar`）で行う**。市場ローカル TZ（日本=JST、米国=US Eastern）で「取引日か
  （週末・休場日でない）」を判定し、両 Consumer が発注前に開場判定する。祝日は市場別に構成注入する（既定は空＝週末のみ。
  祝日データ源の取り込みは後続）。これにより市場監視の平日判定を超えて祝日ガードを効かせる。
- **スケジューラは現段階 in-process ポーリング**（BackgroundService）とし、本番の K8s CronJob 化・市場ローカル時刻スケジュールは
  platform 統合（#22・ADR-0006）で確定する。
- **暫定 watchlist は構成ベース**（`TradeCycle:Watchlist`）。実 watchlist（市場監視 #10 の監視銘柄）連携は後続。

## 理由

- 取引判断での合流はワークフロー（COL→TRD）に忠実で、両系統が同一の判断・サイジング・監査経路を共有できる。
- 市場カレンダーを合流点に置くことで、平日判定しか持たない市場監視を超えて祝日ガードを一元的に効かせられる。
- 既存の in-process ポーリングを踏襲することで CI 緑を保ちつつ、本番スケジューラは platform 統合に委ねられる。

## 結果

- 良い影響: 定時・イベント両系統が取引判断で合流し、休場日はサイクルが起動しない構造になる（FR-02 の骨格を満たす）。
- 悪い影響・トレードオフ: 実 watchlist・祝日データ源・本番 CronJob・場中時刻ガードは後続。暫定 watchlist が空なら定時系統は
  実質起動しない（構成で銘柄を与えるまで）。ペーパー E2E（全サービス結線）は実コンテナ前提で CI 既定では実行しない。
- フォローアップ: 本番スケジューラ（#22・ADR-0006）、実 watchlist 連携（#10）、祝日データ源、場中時刻ガード、日報確定・kill
  switch の前提チェック統合。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0022](IADR-0022_information-collection-safe-sourcing.md)（`InformationCollected` 発行）
