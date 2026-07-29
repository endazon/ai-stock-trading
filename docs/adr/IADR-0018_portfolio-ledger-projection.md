---
title: IADR-0018 ポートフォリオ状態は追記専用取引台帳からの純射影で供給する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-05, FR-11, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0018: ポートフォリオ状態は追記専用取引台帳からの純射影で供給する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-10（保有・損益依存の統制）、FR-05（`OrderExecuted` 消費）、FR-11（監査台帳）、ADR-0003
- 対象 Issue: [#12](https://github.com/endazon/ai-stock-trading/issues/12)（実データ化スライス）
- 関連する実装仕様書: [20260710_portfolio-projection](../specs/20260710_portfolio-projection.md)
- 関連 IADR: [IADR-0005](IADR-0005_stage-capital-cap-definition.md)、[IADR-0008](IADR-0008_daily-loss-limit-basis.md)、
  [IADR-0004](IADR-0004_position-effect-entry-scoping.md)、[IADR-0012](IADR-0012_risk-settings-persistence.md)

## コンテキストと課題

リスク管理の `IPortfolioStateProvider` は `PlaceholderPortfolioStateProvider`（`Capital=初期資金`・他ゼロ）で暫定運用しており、
保有・損益・当日発注累計・連敗・当日取引銘柄に依存する統制が「過小適用」だった。これを実データ化する必要がある。

制約: `OrderExecuted`（FR-05）は `DecisionId, OrderId, Status, FilledQuantity, AveragePrice, ExecutedAt` のみで
**銘柄・売買方向・建玉効果を持たない**。一方、アーキ概要では損益集計の最終所有は報告書サービス（#14・未着手）にある。
現段階でリスク管理の判定入力に必要な read model を、過剰な前倒し設計なしにどう供給するかが論点。

## 検討した選択肢

1. **`OrderExecuted` に銘柄・方向を追加**（契約拡張）— 消費側は楽だが、確定済みイベント契約の後方互換を崩し、
   発注執行の責務外情報を運ばせることになる。
2. **各消費サービス（市場監視・取引判断・リスク管理）が個別に read model を持つ** — Database per Service には忠実だが、
   3 サービスで射影ロジックが重複し不整合リスク。今スライスの目的（リスク管理の安全ギャップ即時解消）に対し過大。
3. **リスク管理が自サービス内に追記専用台帳を持ち、純関数で射影する（採用）** — リスク管理自身が発行する
   `OrderApproved(DecisionId, Intent)` を `DecisionId` で相関し `OrderExecuted` を銘柄・方向に補完。新規契約不要。
   台帳は追記専用で監査（FR-11）とも整合。射影は DB・Clock 非依存の純関数で全面テスト可能。

## 決定

**選択肢 3** を採用する。

- **台帳**: `OrderApproved` を購読して承認済み `Intent` を `DecisionId` で記録（`ApprovedOrderRow`）、`OrderExecuted`
  （`Status==Filled` かつ `FilledQuantity>0`）を `OrderId` で記録（`TradeFillRow`）。`TradeFillRow.DecisionId` は相関・
  時系列畳み込みのため**インデックス**を張るが、DB 強制の外部キー制約（FK）は張らない。参照整合性はアプリ層で担保する
  （`EfPortfolioLedgerStore.AppendFill` が事前に承認 Intent の存在を確認し、無ければ記録せず false を返す）。いずれも追記専用・
  リスク管理専有 DB（ADR-0001・IADR-0012 のパターン）。MassTransit 再送に対し `DecisionId`/`OrderId` で冪等。
- **射影**: `PortfolioProjection.Project(fills, today, initialCapital)` を純関数とし、**符号付き在庫・平均取得単価法**で
  建玉・実現損益を畳み込む（銘柄ごとに 1 ネットポジション。現物ネッティング口座の会計として経済的に正しい）。通常経路・
  損切り機械執行の両方が `OrderApproved` を出すため統一的に扱える。
- **`PositionEffect` と IADR-0004 の関係**: IADR-0004 は発注前スクリーニング（`RiskEvaluator.isEntry`）で Open/Close を
  売買方向から分離する決定であり、本射影の損益会計とは別関心。現物のみ有効な現段階ではショートエントリー（Sell×Open）が
  発生せず、符号推論と `PositionEffect` は一致するため会計に差は出ない。`PositionEffect` は監査・将来の両建て（ロング/ショート
  別ロット）会計のため `LedgerFill`／台帳に保持する。信用有効化後の別ロット会計は margin フォローアップ（ADR-0007／#50）で対応する。
- **`Capital`（当日開始運用資金・固定基準）** = `initialCapital + 当日より前の Close 実現損益`。当日実現・含みは含めない（当日中不変）。
  `initialCapital` は `TradingDefaults.InitialCapital`（既存プレースホルダと同一基準）。
- **`DailyOrderedAmount` は約定ベース**（当日約定代金合計）とする。発注（承認）ベースではなく、実際に資本が投下された額を採る。
  理由: 台帳の権威データは約定であり、承認後未約定・取消の二重計上を避けられ、資本拘束の honest な指標になる。
- **`UnrealizedPnl`・`DrawdownRatio` は本スライス対象外**（`0` を返す）。日次終値マーク（市場データ連携）が必要で、
  IADR-0008 が #12 後続と明記済み。実現損益ベースの統制は本スライスで実データ化される。
  **→ 算出ロジックは [IADR-0036](IADR-0036_unrealized-pnl-valuation.md)（現在値/ピーク入力の純関数）で追加済み（#81）。現在値・ピークの実供給は #22/#82 後続。**
- **取引日境界**は単一取引日タイムゾーン（既定 JST）で解釈する。市場別（日本株/米国株）境界は後続。

## 理由

- 新規契約を追加せず、リスク管理が既に扱う情報（自ら発行する `OrderApproved` の `Intent`）だけで補完できる。
- 追記専用台帳は監査（FR-11）と親和し、射影が純関数のため決定的・全面テスト可能。
- 報告書サービス（#14）が未着手でも、リスク管理の安全ギャップを最小の中間 read model で即時に塞げる。

## 結果

- 良い影響: 段階資金上限（取得額累計）・当日発注累計・保有数・日次実現損益・連敗・当日取引銘柄が実データで判定される。
- 悪い影響・トレードオフ: 射影は毎回の判定で台帳全量を畳み込む（個人利用・単一ユーザー規模では許容）。含み損益・DD は
  市場データ連携まで `0` のまま（日次損失は実現ベースで判定され、含み分は過小のまま＝IADR-0008 の残タスク）。
- フォローアップ: 含み損益・DD の日次終値マーク（市場データ連携）、市場監視 `IPositionStore`・取引判断
  `ISizingContextProvider` の実データ化、損益集計の報告書サービス（#14）への集約、市場別取引日境界、部分決済/ドテン（#50）。

## 関連

- Supersedes: なし（`PlaceholderPortfolioStateProvider` を置き換え）
- Superseded by: [IADR-0112](IADR-0112_moomoo-fill-polling.md)（**約定の受け口条件と `trade_fills` の追記専用性のみ**。
  受け口は `Status == Filled` から「約定があること（`FilledQuantity > 0`）」へ広がり、`AppendFill` は `OrderId` 単位の
  単調 upsert＝累積約定数が増えたときだけ既存行を更新する。相関キー・射影・冪等の方針は本 IADR を維持）
- 関連: [IADR-0005](IADR-0005_stage-capital-cap-definition.md)（InvestedCapital=取得額累計）、
  [IADR-0008](IADR-0008_daily-loss-limit-basis.md)（含み損益は後続）、[IADR-0004](IADR-0004_position-effect-entry-scoping.md)（建玉効果）
