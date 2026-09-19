---
title: IADR-0360 取引記録の「由来」を専用の列挙で表し、取り込み行は約定と別の wire で報告書へ渡して §2-b に別掲し、在庫だけを畳む
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-11, FR-10, FR-16, SC-03, UC-06, ADR-0041, IADR-0033, IADR-0107, IADR-0115, IADR-0246, IADR-0269, IADR-0271, IADR-0286, IADR-0301, IADR-0305, IADR-0350, IADR-0352]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md (決定 1)
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (日報 §2-b)
  - planning:docs/glossary.md (「由来（取引記録の）」)
---

# IADR-0360: 取引記録の「由来」を専用の列挙で表し、取り込み行は約定と別の wire で報告書へ渡して §2-b に別掲し、在庫だけを畳む

- 状態: Accepted
- 日付: 2026-09-19
- 決定者: endazon（利用者裁定 ADR-0041 決定 1 の実装）

## 起点・関連

- 関連する計画書 ID: FR-06 / FR-11 / FR-10 / FR-16 / SC-03 / UC-06 / **ADR-0041 決定 1**
- 関連する実装仕様書: `.ai-context/specs/20260919_870_manual-trade-origin-and-report-section-2b.md`
- 先行: [IADR-0350](IADR-0350_owner-approved-ledger-drift-adoption.md)（取り込みそのもの。**実現損益を記録しない**方針を含む）
- issue: #870（正本）・#859（先行分離。報告書側の在庫への数量反映を持つ）

## コンテキストと課題

IADR-0350 で取り込み（`position_drift_adoptions`・`POST /risk-controls/position-drift/adopt`）は実装したが、
**その事実が報告書のどこにも現れない**。加えて、取り込み行は `GET /risk-controls/fills` へ返していないため、
報告書側の在庫の畳み込みには入らず、**当該建玉は「期間内に決済されなかった建玉」として畳まれ続け、
実在しない建玉の評価損益（参考値）が出る**。SC-03 からも「基準資金・当日損益が実際の口座とずれている」ことが読めない。

ADR-0041 決定 1 は 3 点を求める ——（1）取引記録に**由来**のラベル、（2）報告書へ「手動売買（損益不明）」の欄、
（3）SC-03 へ当期の取り込み件数。🔴 **由来は経費区分とは別の軸である**（区分は「何の費用か」、由来は「誰が約定させたか」）。

## 決定

### 決定 1: 由来は `TradeOrigin` という専用の列挙で表し、経費区分と混ぜない

`AiStockTrading.Shared.Contracts.Trading.TradeOrigin`（`System = 0` / `ManualAdoption = 1`）を新設し、
`LedgerFill.Origin` として台帳の読み口に載せる。既存の `bool IsDriftAdoption` は **`Origin == ManualAdoption` の
導出プロパティ**として残す（読み手の分岐を書き換えないため）。

- 🔴 **`TradeExpenseCategory`（`Realized` / `BorrowFee` / …）へ値を足す案は採らない。** 別の軸であり、
  混ぜると「手動売買」という費用区分が生まれる。planning `docs/glossary.md` が両者を別の語として登録している。
- **wire にも出す**（`origin`）。由来は「後から記録上で区別できる」ことが目的であり、隠すと監査で読めない。
  `GET /risk-controls/fills` は取り込み行を返さないため常に `0` だが、**列が有ること自体が軸の表明**である。
- 序数は `TradeExpenseCategory` と同じ規律で固定する（永続化・wire を跨ぐため）。回帰テストで留める。

### 決定 2: 報告書へは `fills` に混ぜず、新しいエンドポイントと**別の型**で渡す

`GET /risk-controls/drift-adoptions?from&to`（読み取り群＝`OwnerOrService`。`fills` と同じ認可・同じ取引日境界）を新設し、
報告書は `PeriodDriftAdoption`（`PeriodTradeFill` とは別の型）として受ける。

- 🔴 **`fills` に `isDriftAdoption` を足す案は採らない。** 消費側（`PnlAggregator` / `FillPnlAttributionBuilder` /
  `TradeHistoryViewBuilder` / `ThreeWayComparisonAggregator` / `FxTranslationBuilder` / `PeriodCostReviewBuilder` /
  `ResolveCurrentPricesAsync`）のどれか 1 つで除外を書き落とすと、**「平均取得単価で売った損益 0 の決済」が確定値として
  集計される** —— **失敗が開く側へ倒れる**。別の型なら、実現損益・勝率・費用・三者比較へ渡すことが**型として不可能**になる。
- 供給の未達は `ReportInput.DriftAdoptions` として記録・提示する（#840 / IADR-0352 の縮退の枠組みに乗せる）。
  **`null`＝照会できていない／空列＝該当なし**を潰さない。
- 取り込み行は「約定 0 件でも起き得る事実」であるため、**すべての種別（日報・週報・月報）で取りに行く** ——
  在庫の畳み込みは種別に依らず要る（描画だけが日報に閉じる）。

### 決定 3: §2-b は日報 §2 の子節とし、`TradeHistoryView` が供給元を持つ

計画テンプレート（`04_report-templates` 日報）が `### 2-b. 手動売買（損益不明）` を `## 2. 取引履歴（全明細）` の下に
置いており、**節番号と並び順は計画が正**（ADR-0030）。したがって描画は `TradeHistoryRenderer`、入力は
`TradeHistoryView.DriftAdoptions` に置く（`ReportView` の直下には置かない）。

- 🔴 **実現損益の列を置き、値は常に `不明`。列ごと落とさず、空欄にもしない。**
  定数であり、推定値を入れる経路そのものを作らない（FR-16）。
- 🔴 **§1 の合計へ算入しない。** 構造的な保証は決定 2 の「別の型」である。
- **該当が無い日は「該当なし」**（欄は出す）。**照会できていない日は「照会できませんでした」**
  ——`不明`・`未供給`・`該当なし` を混ぜない（計画の語彙分割に従う）。
- 週報・月報には出さない（計画の粒度対応表が手動売買の行を持たない）。

### 決定 4: 在庫の畳み込みへは**数量だけ**反映する。規則はリスク管理の `PortfolioProjection.ApplyToLot` と同じ

その時点の**平均取得単価**で在庫を減らす（決済単価に取得単価そのものを渡せば実現損益は**丸め誤差なしに 0**）。
反映する箇所と、反映**しない**箇所を明示する。

| 畳み込み | 反映 | 理由 |
| --- | --- | --- |
| `PnlAggregator.Aggregate` | する | 評価損益（参考）が実在しない建玉から出るのを止める |
| `FillPnlAttributionBuilder.Build` | する（帰属行は作らない） | 畳み込み順序・規則を `PnlAggregator` と一致させる不変条件（IADR-0301） |
| `TradeHistoryViewBuilder.Build` | する（§2 の明細行にはしない） | 同上。§2 の実現損益が §1 とずれない |
| `FxTranslationBuilder.Build` | する（明細は作らない） | 実在しない建玉の**期末レートでの再測定**を止める |
| `ReportDraftService.ResolveCurrentPricesAsync` | する | 実在しない建玉の現在値を市場データ源へ取りに行かない |
| 🔴 `ThreeWayComparisonAggregator.Aggregate` | **しない** | 段は**発注先**で分ける。取り込み行は発注先が**不明**であり、どちらの段にも算入しない（IADR-0271 の既存規律）。本集計は評価損益を出さないため幻の建玉による誤りも生じない |
| `PeriodCostReviewBuilder` | しない | 入力は帰属行のみ。取り込み行は帰属行にならず、費用の概算へ構造的に入らない |

**否定形テストで固定する**: 実現損益・決済件数・勝率・費用の概算・三者比較のいずれも、取り込み行の有無で動かない。

### 決定 5: SC-03 の「当期」は**当日**とし、境界は市場ごとの現地取引日で判定する

`RiskStatusView.DriftAdoptionCountToday`（末尾に既定値つきで追加）。当日の判定は
`PortfolioProjection.TradeDate`（取り込みの**市場**の現地取引日）で行う ——当日実現損益・日次発注枠と同じ境界を使う
（片側だけ JST に残すとずれる。IADR-0246）。画面（`ControlStatusPage`）は件数と併せて
**「実現損益は不明であり、当日損益・基準資金は実際の口座とずれ得る」**ことを文言で出す（数字だけでは読めない）。
🔴 **SC-03 から取り込みは行わない**（計画が「参照のみ」と定める）。

## 検討した代替案

| 案 | 採らない理由 |
| --- | --- |
| `fills` に `isDriftAdoption` を足す | 決定 2 のとおり、除外の書き落としが**確定値の捏造**へ倒れる。7 箇所の消費側すべてに規律を要求し続けることになる |
| `TradeExpenseCategory` に値を足して由来を表す | 軸が違う（ADR-0041 決定 1・planning 用語集が明示）。「手動売買」という費用区分が生まれる |
| 取り込み行を §2 の 11 列へ混ぜ、約定単価と実現損益を `不明` にする | 計画が「本表に載せず、下の別欄に出す」と明記（テンプレート §2 直下の注記）。11 列の表に `不明` が 2 つ並ぶ行は、集計可能な行と見分けがつかない |
| 取り込み時の推定損益（監査イベントが持つ参考値）を §2-b に出す | FR-16「推定で埋めない」。推定を確定値から分離して運ぶ通り道が系に無いのが、そもそも実現損益を記録しない理由である（ADR-0041 決定 1） |
| 為替差損益へ、取り込みで減った建玉の再測定を（取り込み時のレートで）載せる | **決済時の認識時レートは知り得ない。** 取り込み日のレートで測ると、いつ売られたか分からない建玉に日付を捏造することになる |

## 影響

- **互換**: `LedgerFill` の位置引数 `bool IsDriftAdoption` → `TradeOrigin Origin`（既定 `System`）。呼び出しは 3 箇所＋テスト。
  wire へ `origin` が増えるのは加算であり、既存の読み手（`HttpPeriodFillSource`）は未知フィールドを無視する。
- **DB マイグレーション不要**: 由来は `position_drift_adoptions` 表の**存在そのもの**が持つ。列を足さない。
- **SC-03 の応答**: 末尾に `driftAdoptionCountToday` が増える。フロントの型・画面・フィクスチャを追随させる。

## 残余リスク

- **為替差損益の欠け**: 取り込みで減った建玉の決済時の差損益は知り得ないため、欄から落ちる（0 円とも書かない・推定もしない）。
  事実は §2-b が別掲する。計画が改めて求めるまで欄を増やさない。
- **`/risk-controls/drift-adoptions` の未供給**: §2-b が「照会できませんでした」になると同時に、在庫の畳み込みにも
  取り込みが入らない（実在しない建玉の評価損益が出得る）。両方が同じ 1 入力に依存することを `ReportInput.DriftAdoptions`
  として提示する。**黙って 0 件へ倒さない。**
- **当日のずれ**: ADR-0041 決定 1 が受け入れた副作用（取り込み前に数えていた含み損が実現へ振り替わらずに消えるため、
  日次損失上限の判定が当日のうちは緩む）は本 IADR では塞がない。**塞ぐのは基準資金側（ADR-0041 決定 2・#874）**である。
