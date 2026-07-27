---
title: リスク統制（FR-10）機能仕様書
type: functional-spec
status: draft
related_ids: [FR-10, FR-11, FR-17, UC-01, UC-02, UC-06, ADR-0003, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-09
updated: 2026-07-27
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 機能仕様書: リスク統制（FR-10）

> 機能（FR）単位の仕様。本書はまず **日次損失上限の判定基準**（Issue #31）を確定するために作成した。
> リスク統制全体（kill switch・各金額上限・保有数上限・最大DD・連敗縮小・ポジションサイジング等）の網羅は
> Issue #33 で拡充する。ここに未記載の項目は作業仕様書 [20260709_risk-eval-core-fixes](../specs/20260709_risk-eval-core-fixes.md)
> と各 IADR（0002/0003/0004/0005/0008）を一次情報とする。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制）、FR-11（監査ログ）
- ユースケース（UC）: UC-01/UC-02（取引サイクルの検証段）、UC-06（設定変更・緊急停止）
- 業務フロー（04_workflows）: 取引サイクル（発注前の決定的判定）
- 計画書リンク: `05_trading-assumptions.md` §5（リスク統制・取引ガードの既定値）

## 概要

リスク管理サービスは、生成AIの判断がどうであれ制約違反の注文が発注執行へ到達しないよう、発注前に決定的コード
（`RiskEvaluator`）で注文意図を検証する。判定はエントリー専用制約（新規建て＝`PositionEffect.Open` のみ）と、
手仕舞い（Close）をブロックしないフェイルセーフ（NFR / ADR-0003）で構成する。

## 機能詳細（日次損失上限）

| 項目 | 内容 |
| --- | --- |
| 入力 | `PortfolioSnapshot.Capital`（当日開始時運用資金・固定基準）、`DailyRealizedPnl`（当日実現損益）、`UnrealizedPnl`（含み損益・日次終値評価）、`RiskLimitSettings.DailyLossLimitRatio`（既定 0.02） |
| 処理 | 日次損失 = `DailyRealizedPnl + UnrealizedPnl`。`日次損失 <= -(Capital × DailyLossLimitRatio)` なら到達と判定 |
| 出力 | 到達時、新規建て（Open）注文を `RejectionReason.DailyLossLimitReached` で拒否。手仕舞い（Close）は対象外 |
| 業務ルール | 資金の 2% 到達で当日全停止・翌営業日までロックアウト（§5）。判定基準は**実現損益＋含み損益の合算**（A 案。IADR-0008） |

### 金額判定の通貨（#257 / IADR-0106）

統制の金額（1 注文金額上限・日次発注累計・段階資金上限・日次損失・最大DD）は**基準通貨（円）**で判定する
（計画 06_technical/05_trading-assumptions §3「基準通貨 = JPY」）。

| 項目 | 内容 |
| --- | --- |
| 入力 | `OrderIntent.Price`（**ローカル通貨**＝銘柄の市場の通貨）、`OrderIntent.FxRateToBase`（ローカル通貨 1 単位あたりの円。取引判断が意図生成時に確定・既定 1） |
| 処理 | 金額の突き合わせは `NotionalInBase = Quantity × Price × FxRateToBase`。台帳（取得額・当日発注累計・実現/含み損益・エクイティ）も同レートで基準通貨に積む |
| 出力 | 円換算後の金額が上限を超えれば該当の `RejectionReason` で拒否する |
| 業務ルール | 換算レートは判断境界の 1 点でのみ解決し、下流は同伴レートを使う（評価時点で判定が変わらない）。**非基準通貨でレートが解決できない場合、取引判断は新規建てを作らない**（古い/無いレートで発注しない） |

`Price` を円換算しないのは、発注執行がこの値をブローカーの注文価格として送るため（円換算すると実発注価格が壊れる）。
建玉の平均取得単価・損切り価格も現在値と同一通貨で比較するためローカル通貨のまま保持する。

### 判定基準の確定（Issue #31 / IADR-0008）

- **含み損の扱い**: 実現損益のみでは含み損の大きいポジションを抱えたまま検知が遅れるため、**実現損益と含み損益の
  合算**で判定する（利用者決定 2026-07-09・A 案）。含み益は実現損を相殺する。
- **評価基準**: 含み損益は日次終値（全体前提条件 §5 の「評価損益 = 日次終値」）で算出する。集計はリスク管理ホスト（#12）。
- **固定基準資金**: しきい値の基準 `Capital` は当日開始時点の固定値とし、当日の損益で自己参照的に縮小させない。

## 処理フロー / 状態遷移

```mermaid
flowchart TD
  A[注文意図] --> B{新規建て Open?}
  B -- いいえ 手仕舞い Close --> Z[日次損失上限は適用しない]
  B -- はい --> C[日次損失 = 実現損益 + 含み損益]
  C --> D{日次損失 <= -(資金 × 2%)?}
  D -- はい --> E[DailyLossLimitReached で拒否]
  D -- いいえ --> F[他の統制へ]
```

## 例外・エラー処理

| 条件 | 振る舞い | 記録 |
| --- | --- | --- |
| 日次損失上限に到達（合算） | 新規建てを拒否。手仕舞いは許可 | `RejectionReason.DailyLossLimitReached`（監査ログ FR-11・通知 FR-09） |
| ロックアウト（翌営業日まで） | ホスト（#12）が当日ロックとして保持・翌営業日に解除 | ホスト側の状態管理（`RiskEvaluator` はステートレス） |

## 受け入れ基準

- [x] 実現損益のみで 2% 到達する場合に新規建てを拒否する
- [x] 実現ゼロでも含み損の合算で 2% 到達する場合に新規建てを拒否する
- [x] 実現＋含み損の合算が上限未満なら日次損失上限で拒否しない（含み益は相殺する）
- [x] 含み損で上限到達中でも手仕舞い（Close）は承認する
- [ ] ロックアウト（翌営業日まで）の状態管理をリスク管理ホスト（#12）で実装する

## 関連仕様

- 実装ADR: [IADR-0008](../adr/IADR-0008_daily-loss-limit-basis.md)（日次損失の判定基準）、[IADR-0004](../adr/IADR-0004_position-effect-entry-scoping.md)（エントリー判定）
- 作業仕様書: [20260709_risk-eval-core-fixes](../specs/20260709_risk-eval-core-fixes.md)
- テスト仕様書: Issue #36 で作成
- データ仕様書: Issue #35 で作成

## 未決事項

- リスク統制の他項目（各金額上限・保有数上限・最大DD・連敗縮小・サイジング）の機能仕様は Issue #33 で拡充する。
- ロックアウトの具体的な状態保持・解除タイミングはリスク管理ホスト（#12）で確定する。
