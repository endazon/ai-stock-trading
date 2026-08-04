---
title: 取引ガード（FR-19）機能仕様書
type: functional-spec
status: draft
related_ids: [FR-19, FR-10, FR-20, UC-01, UC-02, UC-06, ADR-0007, ADR-0003, ADR-0009, ADR-0016, IADR-0132]
author: endazon (with Claude Code)
created: 2026-07-09
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
---

# 機能仕様書: 取引ガード（FR-19）

> 取引ガード（商品種別可否・市場別有効/無効・取引禁止銘柄・差金決済防止・相場操縦パターン禁止）を
> 発注前に決定的コード（`RiskEvaluator`）で強制する。ガード設定は利用者のみ変更でき、生成AIは上書きできない
> （ADR-0007）。本書は `RiskEvaluator` の**全違反理由の判定マトリクス**（FR-10/19/20 横断）も収録する。
>
> **2026-08-04 改訂（#332・[IADR-0132](../adr/IADR-0132_product-type-tri-state-and-guard-scope.md)）**:
> 商品種別を **3 値**（現物 / 信用買い / 空売り）へ改め（ADR-0016 決定1）、
> 商品種別ガードの適用を**新規建てのみ**に、差金決済防止ガードの適用を**日本株の現物取引のみ**に絞った。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-19（取引ガード）。横断: FR-10（リスク統制）、FR-20（段階ゲート）
- ユースケース（UC）: UC-01/UC-02（取引サイクル）、UC-06（設定変更）
- 業務フロー（04_workflows）: 取引サイクル（発注前判定）
- 計画書リンク: `05_trading-assumptions.md` §5、`06_daytrading-review.md` §2、ADR-0007、ADR-0016（決定1・8・13）

## 機能詳細（取引ガード項目）

| ガード | 入力 | 判定 | 拒否理由 | 適用範囲 |
| --- | --- | --- | --- | --- |
| 商品種別可否 | `EnabledProductTypes`（3 値） | **実効**商品種別が有効集合に無い | `ProductTypeDisabled` | **エントリーのみ**（#332） |
| 市場別有効/無効 | `EnabledMarkets` | 注文の Market が有効集合に無い | `MarketDisabled` | 全注文 |
| 取引禁止銘柄 | `BannedSymbols`（銘柄+市場） | (Symbol, Market) が禁止リストに一致 | `BannedSymbol` | 全注文 |
| 差金決済防止 | `PreventSameDayReentry`, `SymbolsTradedToday` | **日本株 × 現物**で同日に (Symbol, Market) を取引済み | `SameDayReentry` | エントリーのみ |
| 相場操縦パターン禁止 | `ProhibitManipulativeOrderPatterns`, 検出器 | 検出器が該当と判定（注入時のみ） | `ManipulativeOrderPattern` | 全注文 |

### 商品種別（3 値。ADR-0016 決定1・#332）

| 値 | 内容 | 損失の上限 | 既定 |
| --- | --- | --- | --- |
| `Cash`（現物） | 自己資金で買う | あり（投資額まで） | **有効** |
| `MarginLong`（信用買い） | 資金を借りて買う | あり（投資額まで） | 無効 |
| `ShortSell`（空売り） | 株を借りて売る | **なし（理論上無限）** | 無効 |

- **3 値はそれぞれ独立に有効・無効を設定でき、既定はいずれも「現物のみ有効」**（FR-19・§5）。
- **空売りの有効・無効はここが単一情報源**である（IADR-0132 決定2。専用フラグを別に持たない）。
  有効化しても空売り専用統制 8 規則（[FR-10 機能仕様](FR-10_risk-controls.md)）は上乗せで効く。
- **照合するのは実効商品種別**（IADR-0132 決定3）。新規売り建て（`Sell` × `Open`）は申告値によらず
  `ShortSell` として扱う。申告を信じると AI の自己申告でガードを解除できてしまう。
- 序数（0/1/2）は設定 JSON・画面と結合しているため**並べ替えない**（IADR-0132 決定1）。
- 段階別の可否（Stage 1＝3 種／Stage 2＝現物のみ／Stage 3＝条件付き解禁。ADR-0016 決定8）の**強制は
  FR-20（#333）の担当**であり、本ガードの有効集合と AND で効かせる（厳しい方が効く）。

### 差金決済防止の適用範囲（FR-19 本文・§5・#332）

**日本株の現物取引の新規建てにのみ**適用する。本ガードは日本の差金決済規制（金商法 161 条の 2・
06_daytrading-review §2.1）に対応するものであり、**米国株は信用口座（margin account）で運用するため
Good Faith Violation が発生しない**（§5「米国口座の種別・決済」）。米国株の回転数は
**1 日の発注金額上限（equity の 150%/日）と保有建玉数上限（3）**で管理する。
信用（信用買い・空売り）は同一保証金での同日無制限回転が可能なため対象外である。

- 禁止銘柄・差金決済は（Symbol, Market）で照合する（別市場の同一コードを区別。#26）。
- 禁止銘柄の銘柄コードは**前後空白・大文字小文字の差を吸収**して照合する（`BannedSymbol.Matches`。
  IADR-0132 決定6。表記差だけで禁止＝クラス C を迂回させない）。
- 計画が登録済みとする禁止銘柄は **6457 グローリー / 6902 デンソー / 6502 東芝（旧。再上場時に適用）**
  （いずれも日本株・理由と登録日つき。§5・INDEX 決定 20）。
- 相場操縦の拡張点（設定フラグ・理由コード・判定ポート `IManipulativeOrderPatternDetector`）は #28（IADR-0006）で用意。
  検知アルゴリズム本体（見せ玉・過剰訂正取消・自己レイヤリングを自口座の直近発注統計から検知）は #49（IADR-0040）で実装した
  （`ManipulationPatternAnalyzer`＋`ManipulativeOrderPatternDetector`）。検出器未注入時は判定をスキップする。本番 DI 登録は
  実注文履歴テレメトリ（発注・訂正・取消イベントの永続化 #13/#17）からの供給確定後（切り分け）。

## 判定マトリクス（違反理由 × エントリー/手仕舞い適用）

`RiskEvaluator.Evaluate` は違反を最初の1件で打ち切らず全件列挙する（FR-11 監査）。エントリー/手仕舞いは
建玉効果 `PositionEffect`（Open/Close）で判定する（売買方向ではない。#25。IADR-0004）。

| 違反理由 | 起点 | エントリー(Open) | 手仕舞い(Close) |
| --- | --- | --- | --- |
| KillSwitchActive | FR-10 | 適用 | 非適用（フェイルセーフ） |
| StageProhibitsLiveTrading | FR-20 | 適用（モードは効果非依存） | 適用（モードは効果非依存） |
| StageCapitalCapExceeded | FR-20 | 適用（累計投入額。#27） | 非適用 |
| ProductTypeDisabled | FR-19 | 適用 | **非適用**（#332・ADR-0009。無効な種別の建玉を手仕舞えなくしない） |
| MarketDisabled | FR-19 | 適用 | 適用 |
| BannedSymbol | FR-19 | 適用 | 適用 |
| SameDayReentry | FR-19 | 適用（**日本株 × 現物のみ**。#332） | 非適用 |
| PerOrderAmountExceeded | FR-10 | 適用 | 非適用 |
| DailyOrderAmountExceeded | FR-10 | 適用 | 非適用 |
| MaxPositionsExceeded | FR-10 | 適用 | 非適用 |
| DailyLossLimitReached | FR-10 | 適用（実現+含み損の合算。#31） | 非適用 |
| MaxDrawdownReached | FR-10 | 適用 | 非適用 |
| ManipulativeOrderPattern | FR-19 | 適用 | 適用 |

- **フェイルセーフの原則**（NFR / ADR-0003 / ADR-0009）: 新規建て（Open）は止めるが、保有ポジションの
  手仕舞い（Close）はブロックしない。損切り監視は最後まで維持する。
  **商品種別は 3 値化（#332）に伴い Close 非適用へ改めた**——既定では空売りが無効であり、
  Close にも適用すると**空売り建玉の買い戻し**（`Buy` × `Close` × `ShortSell`）が拒否され、
  損失に上限が無い建玉を閉じられなくなるためである（IADR-0132 決定4）。
  モード（Paper/Live）・市場・禁止銘柄・相場操縦は建玉効果に依存しない性質のため Close にも適用する
  （禁止銘柄・市場の Close 適用は計画側の裁定待ちの論点。作業仕様書 #332 の未決事項 1）。
- FR-10 の各上限の詳細は [FR-10 機能仕様](FR-10_risk-controls.md) を参照。

## 処理フロー

```mermaid
flowchart TD
  A[OrderIntent] --> B[kill switch]
  B --> C[段階ゲート モード/資金上限]
  C --> D[取引ガード 商品/市場/禁止/差金決済/相場操縦]
  D --> E[リスク上限 金額/保有数/日次損失/DD]
  E --> F{違反あり?}
  F -- はい --> G[Reject 理由を全件列挙]
  F -- いいえ --> H[Approve 承認数量]
```

## 例外・エラー処理

| 条件 | 振る舞い | 記録 |
| --- | --- | --- |
| いずれかのガードに違反 | 該当理由を列挙して Reject | `OrderRejected` イベント（監査 FR-11・通知 FR-09） |
| ガード設定の変更 | 利用者のみ可・変更履歴を記録 | 設定ストア（#12/#19） |

## 受け入れ基準

- [x] 禁止銘柄・無効商品種別・無効市場・差金決済該当の注文が拒否され理由が記録される
- [x] 差金決済・禁止銘柄は（銘柄, 市場）で照合し、別市場の同一コードを誤拒否しない
- [x] 相場操縦ガードは設定・理由コード・判定ポートを持ち、無効化時／検出器未注入時はスキップする
- [x] 手仕舞い（Close）はエントリー専用ガードの対象外（フェイルセーフ）
- [x] **商品種別が 3 値であり、それぞれ独立に有効・無効を設定できる**（既定＝現物のみ有効）
- [x] **差金決済防止ガードが日本株の現物取引にのみ適用され、米国株には適用されない**
- [x] **禁止銘柄が理由と登録日を伴って登録され、表記差・商品種別の変更で迂回できない**
- [ ] 段階別の商品種別強制（Stage 2 は現物のみ）… **FR-20 / #333**

## 関連仕様

- 機能仕様書: [FR-10 リスク統制](FR-10_risk-controls.md)、[FR-20 段階ゲート](FR-20_staged-gates.md)
- データ仕様書: [リスク管理ドメインの集約](../data/risk-management-aggregates.md)
- テスト仕様書: [FR-19 取引ガード（再実装）](../tests/FR-19_trading-guards-tests.md)、[FR-10 リスクガードコア](../tests/FR-10_risk-guard-core-tests.md)、[FR-19 相場操縦パターン検知](../tests/FR-19_manipulation-detection-tests.md)
- 実装ADR: [IADR-0132](../adr/IADR-0132_product-type-tri-state-and-guard-scope.md)（商品種別 3 値化・ガードの適用範囲）、[IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)（空売り統制）、[IADR-0004](../adr/IADR-0004_position-effect-entry-scoping.md)（建玉効果）、[IADR-0006](../adr/IADR-0006_manipulation-guard-extension-point.md)（相場操縦拡張点）、[IADR-0038](../adr/IADR-0038_order-decomposition-position-effect.md)（ドテン/部分決済の注文分解）、[IADR-0040](../adr/IADR-0040_manipulation-detection-algorithm.md)（相場操縦検知アルゴリズム）
- 作業仕様書: [20260804_332_trading-guards](../specs/20260804_332_trading-guards.md)（#332）、[20260711_manipulation-detector](../specs/20260711_manipulation-detector.md)（#49）

## 未決事項

- **禁止銘柄・市場ガードの手仕舞い適用**（#332 未決事項 1）: 禁止銘柄へ登録した瞬間に既存建玉を
  手仕舞えなくなる。商品種別と違い「登録は利用者の明示的な意思」であり、ADR-0007 が
  「登録されたものを確実に強制する」と定めているため実装判断では緩めない。計画側の裁定を要する。
- **信用買い（`MarginLong`）の建玉表現**: 3 値化は有効・無効の制御までであり、信用金利・必要証拠金・
  建玉の区別は未実装（実弾解禁は Stage 3。#332 未決事項 3）。
- 相場操縦検知の具体閾値（IADR-0040 の初期値）は運用データで較正する（フォローアップ）。本番 DI 登録は実注文履歴テレメトリ（#13/#17）確定後。
- 回転売買・ドテン/部分決済の注文分解方針は [IADR-0038](../adr/IADR-0038_order-decomposition-position-effect.md)（符号付きポジションのゼロ跨ぎで Close+Open に分解）で確定済み。分解ロジックの実装結線は信用有効化スライスで行う（後続）。
