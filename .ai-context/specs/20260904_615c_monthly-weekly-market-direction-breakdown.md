---
title: 月報 §2 週別・市場別・建玉方向別の内訳を、同じ帰属（1 回の畳み込み）から描く
type: spec
status: review
related_ids: [FR-06, FR-07, FR-16, FR-17, UC-05, ADR-0030, IADR-0025, IADR-0033, IADR-0269, IADR-0291, IADR-0301, IADR-0305]
author: endazon (with Claude Code)
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0030_report-section-numbering-is-plan-canonical.md
---

# 仕様書: 月報 §2 週別・市場別・建玉方向別の内訳を、同じ帰属（1 回の畳み込み）から描く

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-06（報告サイクル）／FR-07（報告書の階層管理）／FR-16（報告書テンプレート）／FR-17（全体前提条件）
- ユースケース（UC）: UC-05（報告書の確定）
- 画面（SC）: なし
- 関連 ADR: 計画 ADR-0030（節番号・節順は計画が正・未実装の節を詰めない）
- 関連 IADR: [IADR-0301](../adr/IADR-0301_fill-level-pnl-attribution-single-fold.md)（約定単位の損益帰属＝内訳の単一情報源）／
  [IADR-0305](../adr/IADR-0305_weekly-risk-cost-review-breakdown-and-ratio.md)（費用の内訳・未供給の描き方）／
  [IADR-0291](../adr/IADR-0291_report-sections-follow-plan-numbering.md)（未実装節の見出し出力）／
  [IADR-0269](../adr/IADR-0269_trade-history-wiring-and-record-based-supply.md)（未供給の規律・出口での結線固定）／
  [IADR-0033](../adr/IADR-0033_shared-inventory-fold.md)（符号付き在庫の単一情報源）
- 関連する作業仕様書: [20260904_615a_weekly-daily-progression-and-highlights](./20260904_615a_weekly-daily-progression-and-highlights.md)／
  [20260904_615b_weekly-risk-cost-review](./20260904_615b_weekly-risk-cost-review.md)
- 計画書リンク: `project-planning/projects/ai-stock-trading/06_technical/04_report-templates.md` §月報テンプレート §2

## 目的・背景

[#615](https://github.com/endazon/ai-stock-trading/issues/615) のスライス c（最後）。月報 §2 は IADR-0291 以来
「本節は未実装です（#615 で実装予定）」のままである。計画の節は**表 3 つ**である。

```
## 2. 週別・市場別の内訳
| 週 | 実現損益 | 取引数 | 備考 |

| 市場 | 実現損益 | 費用 | 主要銘柄（損益上位/下位） |
| 日本株 |  |  |  |
| 米国株 |  |  |  |

| 建玉の方向 | 実現損益 | 取引数 | 勝率 | 費用（うち借株料） |
| ロング（現物・信用買い） |  |  |  |  |
| ショート（空売り） |  |  |  |  |
```

## 対象範囲

- 対象:
  - 週別・市場別・方向別の集計を作る Domain 純関数を新設する（**`FillPnlAttribution` へ列を足さない**）。
  - `ReportDraftService` の帰属の供給を**月報へも広げる**（現在は週報のみ）。
  - `ReportRenderer` の月報 §2 を `AppendNotImplemented` から実体の描画へ置き換える。
  - ゴールデン 2 本（`Tests/Domain/Golden/monthly-{supplied,unsupplied}.md`）の更新と焦点テストの追加。
- 対象外:
  - **月報 §3 税金レビュー**（別の理由定数。IADR-0272 決定3。#615 の対象外）。
  - **日報 §6 振り返り**（別の理由定数。#615 の対象外）。
  - **月報 §1 の「費用合計 / 費用率」**（`（データ連携後）` のまま）。分母の裁定（planning#535）待ちであり、
    §1 を動かすとゴールデンの差分が「新設 1 節の中身だけ」でなくなる。
  - **借株料を費用合計へ算入すること**（後述。§1 の費用合計と内訳の和が一致しなくなる）。
  - 新しい HTTP 端点・イベント・設定。**1 つも要らない**。
  - 節番号の繰り上げ。**既存の §3〜§8 は計画正のまま動かさない**（ADR-0030 決定1・決定2）。

## 🔴 設計の中心: 帰属へ列を 1 つも足さない

**期間・区分を切って `PnlAggregator.Aggregate` を呼び直さない**（IADR-0301 決定1）。3 表とも
`FillPnlAttribution`（期間全体を 1 回だけ畳み込んだ帰属）を**数え直すだけ**で作る。

| 軸 | 集計キー | 帰属の既存フィールドで足りるか |
| --- | --- | --- |
| 週別 | `SessionDateJst` の **ISO 週**（`ReportPeriod.Label(Weekly, date)`） | **足りる** |
| 市場別 | `Market` | **足りる** |
| 建玉方向別 | 下記の導出 | **足りる**（IADR-0301 決定1 の明文） |

### 建玉方向の導出（実測で airtight）

`SignedInventory.Apply` は **在庫 0 か同符号なら `Reduced=false`／反対符号のときだけ `Reduced=true`** を返す
（実測: `Shared.Contracts/Trading/SignedInventory.cs`）。したがって:

- `Realizing == true` ⇒ 直前の在庫は約定と**反対符号** ⇒ 決済された建玉は **Sell なら ロング／Buy なら ショート**。
- `Realizing == false` ⇒ 直前の在庫は 0 か**同符号** ⇒ 建てられた建玉は **Buy なら ロング／Sell なら ショート**。

まとめると **ロング ⇔ `Realizing ? Side == Sell : Side == Buy`** である。**列を増やす必要が無い。**

> 反転（ロング +5 に対して Sell 10）は 1 約定で「ロングの全決済＋ショートの新規建て」を兼ねるが、
> `Realizing=true` かつ Sell なので**ロング側に数える**。IADR-0301 決定1 と同じ扱いであり、
> **1 約定を 2 行へ割らない**（割ると取引数の合計が §1 と合わなくなる）。

## 表ごとの描画仕様

### 表 1: 週別

| 列 | 値 |
| --- | --- |
| 週 | ISO 週ラベル（`2026-W35`）。**年跨ぎは ISO の規則どおり**（1 月の日付が前年の W52/W53 に入り得る。ラベルが年を持つので取り違えない） |
| 実現損益 | **税引前・費用込み**（当週の決済損益 − 当週の約定に掛かる概算費用）。週報 §2 の日別行と同じ基準 |
| 取引数 | **約定件数**（新規建てを含む） |
| 備考 | 当週の**寄与最大の決済**（機械的な事実）。決済が無い週は「決済なし（新規建てのみ）」 |

- **約定が 1 件も無い週は行を出さない**（IADR-0301 決定2 と同じ。営業日カレンダーを持たないため）。
- 週の並びは ISO 週ラベルの昇順（年 → 週の順で決定的）。

### 表 2: 市場別

| 列 | 値 |
| --- | --- |
| 市場 | **日本株 / 米国株**（計画の表本文がこの語で行を持っている） |
| 実現損益 | **税引前・費用前**（同じ行に費用列があるため。週報 §3 と同じ基準） |
| 費用 | 当該市場の約定に掛かる概算費用 |
| 主要銘柄（損益上位/下位） | 当該市場の**決済**を銘柄で集計した最上位・最下位 |

- 🔴 **行は常に 2 行出す**（計画が行を固定している）。約定が 1 件も無い市場は
  **「（当月の約定なし）」と明記**し、数値の `0` を「取引して収支が 0 だった」と読ませない。
  日別行と違い、**市場に休場日カレンダーの曖昧さは無い**（「その市場で 1 度も約定しなかった」は確定した事実である）。

### 表 3: 建玉の方向別

| 列 | 値 |
| --- | --- |
| 建玉の方向 | **ロング（現物・信用買い） / ショート（空売り）**（計画の表本文どおり） |
| 実現損益 | **税引前・費用前** |
| 取引数 | **約定件数**（新規建てを含む） |
| 勝率 | 当該方向の勝ち決済 / 決済件数。決済 0 は `-（0/0）`（§1 の勝率と同じ書式） |
| 費用（うち借株料） | 概算費用。**借株料は別掲**（下記） |

- 🔴 **借株料を概算費用へ足さない。** 実装の費用合計は `手数料 + 為替スプレッド` であり（IADR-0305 決定3）、
  借株料はそこに入っていない。足すと**内訳の和が §1 の費用合計と一致しなくなる**。
  ショート行には `+X.XX USD（借株料 +Y.YY USD は別掲・§6.1）` と書き、**計画の「うち」が成り立っていないことを
  凡例で明示する**。ロング行の借株料は `—`（借株料はショートにのみ発生する）。
- 借株料が未供給（`BorrowFees` が `null`）なら**未供給の標識**で描く（0 と書かない）。

## 影響範囲

| ファイル | 変更 |
| --- | --- |
| `Domain/PeriodBreakdown.cs` | **新規**（週別・市場別・方向別の純関数） |
| `Domain/ReportRenderer.cs` | 月報 §2 を実体の描画へ置き換える（§3 の未実装文言は残す） |
| `Features/Reports/ReportDraftService.cs` | 帰属の供給を月報へも広げる（費用レビューは週報のまま） |
| `Tests/Domain/Golden/monthly-supplied.md` / `monthly-unsupplied.md` | 更新（差分は**新設 1 節の中身だけ**） |
| `Tests/Domain/PeriodBreakdownTests.cs` | **新規**（内訳の和・方向の導出・年跨ぎ） |
| `Tests/Domain/ReportRendererMonthlyBreakdownTests.cs` | **新規**（出口の焦点テスト） |
| `Tests/Features/Reports/ReportDraftWeeklyBreakdownTests.cs` | 月報の結線テストを足す（既存の「月報には出さない」検査を是正する） |

## テスト方針（受け入れ基準の写像）

1. 🔴 **3 表それぞれの和が §1 サマリと一致する**（同じ約定列から `PnlAggregator.Aggregate` と突き合わせる）。
   - 週別: Σ（実現損益・費用込み）= `RealizedPnlGross − TotalCost`／Σ 取引数 = `TradeCount`
   - 市場別: Σ 実現損益 = `RealizedPnlGross`／Σ 費用 = `TotalCost`
   - 方向別: Σ 実現損益 = `RealizedPnlGross`／Σ 取引数 = `TradeCount`／Σ 決済 = `RealizingTradeCount`／
     Σ 勝ち決済 = `WinningTradeCount`
2. **持ち越し建玉が月内の週をまたぐ列**を含める（「週で切って呼び直す」実装との差が出る唯一の場所）。
3. **方向の導出**: ロングの建て→決済／ショートの建て→決済／**反転**（1 約定が両方に見える形）。
4. **年跨ぎ**（1 月初の日付が前年 ISO 週に入る）で週ラベルが前年を指すこと。
5. 出口の結線（`ReportDraftService` 経由で約定列から月報 §2 が出る）。
6. ゴールデン 2 本で全文を固定する。**差分は §2 の中身だけ**であること。
7. 節番号の維持（§3〜§8 が繰り上がっていないこと）。

## 母集合の取り方（是正・追随の対象）

- 未実装の理由定数の走査: `grep -n "PendingIssueReason" Domain/ReportRenderer.cs` → 宣言 1 ＋ 呼び出し 1（月報 §2）。
  **本 PR で最後の呼び出しが消えるため、定数の宣言ごと削除する**（残すと未使用で警告になる）。
- 「#615 で実装予定」を含むゴールデンの走査: `grep -rln "615 で実装予定" Tests/Domain/Golden` →
  `monthly-supplied.md` / `monthly-unsupplied.md` の 2 本。**本 PR で 0 本になる。**
- `ReportRendererTests.未実装の節は…` の InlineData: 月報 §2 の行を外す（残る 2 行は月報 §3・日報 §6）。
- 除外したものと理由:
  - `docs/functional/` `docs/tests/`: **必須範囲外**（`docs/README.md` の網羅裁定は安全・統制の中核 FR に限る）。
  - `docs/api/openapi.yaml`: HTTP 契約は変わらない。

## 受け入れ基準

- [ ] 月報ゴールデン 2 本の「## 2. 週別・市場別の内訳」に**中身**（3 表）が出る。
- [ ] 3 表それぞれの和が §1 サマリと一致することがテストで固定されている。
- [ ] 建玉方向が**帰属の既存フィールドだけ**から導かれている（列を足していない）。
- [ ] 約定の無い市場が「（当月の約定なし）」と明記され、`0` を「収支 0」と読ませない。
- [ ] 借株料が概算費用へ**足されていない**ことと、その理由が凡例にある。
- [ ] **既存の §3〜§8 の番号が変わらない**。月報 §3・日報 §6 は未実装のまま。
- [ ] `dotnet build` 警告 0・`dotnet format --verify-no-changes` 緑・文書系検査が緑。

## 残課題（本 PR では解かない）

- **月報 §3 税金レビュー**（IADR-0272 決定3。前提整備が無い）・**日報 §6 振り返り**（週次目標の参照値が無い）。
- **月報 §1 の「費用合計 / 費用率」**（planning#535 の裁定待ち）。
- **借株料を費用合計へ算入するか**（計画の §数値の定義は費用合計に借株料を含めていないが、
  月報 §2 の表 3 は「費用（うち借株料）」と書いている。planning#535 へ追記して確認する）。
