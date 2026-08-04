---
title: 空売りの「逆指値の同時発注必須」と「強制買戻し 30 日禁止」に対応する拒否理由コードが ADR-0016 決定10 に無い
type: plan-feedback
status: open
category: 要求の不足
related_ids: [FR-10, UC-06, ADR-0016]
source_repo: ai-stock-trading
source_ref: feat/FR-10-risk-control-core / docs/specs/20260804_329_short-selling-controls.md / IADR-0131
author: endazon (with Claude Code)
created: 2026-08-04
---

# フィードバック: 空売りの拒否理由 7 種に、実装すべき規則 2 つに対応するコードが無い

## 種別

要求の不足（拒否理由コードの列挙漏れ）

## 起点となる計画書

- 機能要求（FR）: FR-10（リスク統制。空売り専用統制 8 項目・拒否理由 7 種）
- ユースケース（UC）: UC-06
- 関連 ADR: [ADR-0016](../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md) 決定2(b)・決定4・決定10
- 計画書リンク: `02_requirements/01_requirements.md` FR-10 ／ `06_technical/05_trading-assumptions.md` §5

## 現状（計画書の記述 / As-Is）

ADR-0016 は空売りに 8 つの統制を課し、決定10 で拒否理由を **7 種**列挙している。

| 統制 | 決定 | 対応する拒否理由（決定10） |
| --- | --- | --- |
| (1) 1 銘柄あたり equity の 10% | 決定2(a) | `ShortExposureExceeded` |
| **(2) 逆指値（ストップ注文）の同時発注必須** | 決定2(b) | **無し** |
| (3) 借株料 年率 20% / 照会不可なら空売りしない | 決定3 | `BorrowCostExceeded` / `BorrowUnavailable` |
| (4) 維持率 40% と規制要求の厳しい方 | 決定7 | `MaintenanceMarginBreach` |
| (5) 株価 $5.00 未満は対象外 | 決定7 | `ShortPriceFloorBreach` |
| (6) 空売り比率 50% | 決定9 | `ShortExposureExceeded` |
| (7) 権利確定日前日の新規空売り禁止 | 決定5 | `DividendRecordDateNear` |
| **(8) 強制買戻し検知 → 30 日禁止リスト自動追加** | 決定4 | **無し**（`BannedSymbol` は使えない） |
| （空売りが無効に設定されている） | 決定1 | `ShortSellDisabled` |

(2) と (8) には対応するコードが無い。とくに (8) は決定4 が「**禁止銘柄リストへ自動追加する**」と書いて
いるが、決定10 は「$5 未満の除外を `BannedSymbol` で表現してはならない——市況由来の事象を『AI が禁止事項を
犯そうとした件数』（クラス C）に混入させると、段階昇格ゲートが機能しなくなる」と明記している。
**強制買戻しも市況（借株需給の逼迫）由来**であるため、同じ理由で `BannedSymbol`（クラス C）を使えない。

## 問題点 / あるべき姿（To-Be）

拒否理由コードが無い規則は、実装すると次のいずれかになる。

1. **規則を実装しない** → 逆指値なしの空売り（＝損失に上限の無い建玉を損切り機構なしで持つ）が素通りする
2. **既存の 7 種で代用する** → 監査ログ（FR-11）の理由が実態と食い違い、原因究明が壊れる
3. **実装側でコードを新設する** → 規則は塞がるが、計画と実装で拒否理由の集合が食い違う

いずれも望ましくない。**計画側で 2 つのコードを追認するか、既存コードへの写像を明示すべきである。**

## 実装で判明した経緯

[#329](https://github.com/endazon/ai-stock-trading/issues/329) 第 2 段階（空売り統制 8 規則の実装）で、
8 規則を拒否理由へ写像する際に判明した。実装は上記 3 のうち「新設し、クラス A とし、計画へ環流する」を
選び、[IADR-0131](../docs/adr/IADR-0131_short-selling-controls-fail-closed.md) 決定3 に記録した。

- (2) → **`StopOrderRequired`**（新設・クラス A）。`OrderIntent.StopLossPrice` の有無で判定する
- (8) → **`BorrowUnavailable`** へ写像（借株需給の逼迫による借株不可として扱う）。禁止期間は
  `ShortSellingLimits.BuyInBanDurationDays = 30` から算出する

## 提案（計画への反映案）

- 反映先候補: **ADR-0016 決定10 の追補**（部分改定の形。表へ 1〜2 行追加）＋ FR-10 本文の「7 種」の更新
- 提案内容:
  1. 決定10 の表へ **`StopOrderRequired`（逆指値を建玉と同時に発注できない。由来 決定2(b)・クラス A）**を
     追加し、「7 種」を「8 種」へ改める。実装側の名称と揃えるか、計画側で別名を与える場合はその名称を示す
  2. 決定4 の「30 日間の禁止銘柄リストへ自動追加する」に、**クラス C の禁止銘柄リスト
     （`BannedSymbol`）とは別の空売り専用リストである**ことと、当該リストによる拒否が
     `BorrowUnavailable`（クラス A）で記録されることを明記する
  3. 上記が受け入れられない場合は、(2)・(8) をどの既存コードへ写像するかを決定10 に明示する

## 影響範囲

- **計画**: ADR-0016 決定4・決定10、FR-10 本文（拒否理由の種類数）、05_trading-assumptions §5 の注記
  （「拒否理由 7 種」の記述）、00_vision の KPI 注記（「空売り 7 種」への言及）
- **実装**: `RejectionReason`（実装済み・名称の追認待ち）、`ShortSellEvaluator`、
  クラス分類 `RejectionReasonClassification`、計画適合検査 `PlanRiskDefaults`
  （`RejectionReason.ShortSellReasons` の期待値は現在 7 種の列挙であり、8 種へ改める場合は同時に更新が要る）
- **段階ゲート**: クラス分類が変われば「統制違反 0 件」の計上対象が変わる（#333）
