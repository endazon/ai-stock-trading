---
title: 日をまたいで持った建玉の決済の実現損益が報告書で算定されない（期間開始時点の在庫の供給元が無い）を、リスク管理の取引台帳が市場ごとの窓の下端までを畳んだ射影を返す口で直す（#1181）
type: spec
status: accepted
related_ids: [FR-06, FR-11, FR-16, UC-03, UC-04, UC-05, ADR-0052, ADR-0053, IADR-0493, IADR-0381, IADR-0492, IADR-0033, IADR-0107, IADR-0286, IADR-0301, IADR-0350, IADR-0352, IADR-0360, IADR-0427, IADR-0447, IADR-0480, IADR-0491]
author: claude (Claude Code)
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements (FR-06 当日の取引結果・損益の集計 / FR-16 数値はコードで集計し LLM に計算させない)
  - planning:projects/ai-stock-trading/06_technical/04_report-templates (数値の定義: 実現損益＝約定代金差額−費用−源泉徴収税額・評価損益＝(現在値−平均取得単価)×数量・為替差損益は独立行)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions (§1 譲渡益税率 20.315%・§3 為替評価方法: 実現損益＝約定時レート)
  - planning:projects/ai-stock-trading/07_adr/ADR-0053 (各報告書は前回の生成の後から今回の生成までに閉場したセッションを集計する・市場ごとの現地取引日)
  - planning:projects/ai-stock-trading/07_adr/ADR-0052 (作り直しは自動生成と同じ入力の規則。中核の入力の取得失敗は断る)
---

# 期間開始時点の在庫を取引台帳から供給する（#1181）

## 背景（issue の観測）

- PoC（2026-10-06 23:20 JST・AST ec572e1b）で `/report regenerate daily-2026-10-06` の版 2 に「⚠ 未供給の入力があります: 期間開始時点の在庫」が出た。
  ET 10/05 の決済 2 件（MSFT 468 株 平均取得単価 511.912 → 527.15、NVDA 1049 株 230.77 → 237.69）は、どちらも期間より前に建てた建玉の決済である。
- 原因は設計どおり（IADR-0381）: 報告書は期間内の約定しか受け取らず、期間より前に建てた建玉の決済は取得原価が無いので実現損益を算定できない。
  「期間開始時点の在庫」は**供給元が存在しない入力**と明記されていた（`ReportInput.OpeningInventory`）。
- 影響: PoC の取引は日をまたいで建玉を持つのが普通であり、決済のある日の日報はほぼ毎回、実現損益・税・勝率を出せない。FR-06 の「当日の取引結果・損益の集計」が実質的に満たせない。

## 実測（origin/develop ec572e1b）

- 在庫の畳み込みは報告書サービスに 5 か所ある: `PnlAggregator` / `FillPnlAttributionBuilder` / `TradeHistoryViewBuilder` / `ReportDraftService.ResolveCurrentPricesAsync` /
  `FxTranslationBuilder`。いずれも空の在庫から始め、規則は `PeriodInventory`（IADR-0381）が持つ。
- 約定の単価は報告書へ届く時点で基準通貨（USD）へ換算済み（`HttpPeriodFillSource.ToFill` が `Price × FxRateToBase`）＝リスク管理の `LedgerFill.PriceInBase` と同じ式。
- リスク管理の射影（`PortfolioProjection.Project` / `ProjectOpenPositions`）は台帳の全約定（取り込み行を含む）を約定時刻の昇順に `ApplyToLot`
  （`SignedInventory.Apply`＝平均取得単価法。取り込み行は平均取得単価で畳み実現損益 0）で畳む。基準通貨の平均取得単価（`positionsInBase`）を並行して持つ。
- 報告書の窓（IADR-0492）は市場ごとの現地取引日 `[From(M), To(M)]` で約定を絞る。取引日は両サービスとも「市場のタイムゾーンでの暦日」
  （報告書 `ReportSessionWindow.Includes` と リスク管理 `TradingDay.Of(instant, market)`）で一致する。
- 取り込み行は台帳では `ExecutedAt = AdoptedAt` で約定列に合流し、報告書の窓は取り込みを `AdoptedAt` の取引日で絞る（同じ境界）。
- 読み取りの経路は REST `GET /risk-controls/*`（既定・正）と gRPC `RiskControlsRead`（`RiskManagement:Grpc` を宣言したときだけ）の並走。
  報告書の 6 つの取引台帳の供給元は両方の実装を持ち、Program.cs が選ぶ（IADR-0427）。PoC の構成（values-local）は REST。

## 計画の確認

- 計画（FR-06・FR-16・04_report-templates の数値定義）は実現損益を「約定代金差額 − 費用 − 源泉徴収税額」、評価損益を「(現在値 − 平均取得単価) × 数量」と定め、
  取得原価の方法は**平均取得単価**である（05_trading-assumptions §3 は実現損益を約定時レートで換算する）。FIFO は計画のどこにも無い。
  **報告書の畳み込み（IADR-0033）とリスク管理の台帳の畳み込みは同じ平均取得単価法（同じ純関数 `SignedInventory.Apply`）である。**
- 期間開始時点の在庫の供給方法について計画は**沈黙している**（実装の判断）。ADR-0053 は窓の下端を市場ごとの閉場で定める——
  期間開始時点の在庫は「窓の下端より前に閉場したセッションまでの約定」を畳んだものである。
- 結論: 計画に反せず、計画の数値定義を満たす側へ直す。**計画への起票は不要**（改定 IADR で足りる。下記「計画への環流」）。

## 決定（IADR-0493 の要約）

- 案 (a) を採る: **リスク管理の取引台帳が、指定した市場について指定した現地取引日より前（排他）までの台帳行（約定と取り込み）を
  畳んだ在庫（銘柄ごとの数量・基準通貨の平均取得単価・認識時レートの加重平均）を返す口**を新設する。
  REST `GET /risk-controls/opening-inventory?market&before`・gRPC `RiskControlsRead/GetOpeningInventory`。
- 報告書は窓の市場ごとの下端 `From(M)` を `before` に渡して市場ごとに 1 回ずつ引き、5 つの畳み込みの初期在庫に置く。
- 取得に失敗したら `OpeningInventory` を未供給として記録し（従来の切った在庫のまま畳む）、§1 の実現損益・税・勝率・評価損益を「算出不能」にする（fail-closed）。
  在庫の取得は中核の入力（`IsCore`）とし、一過性の失敗は長く待つ・作り直しは断る（ADR-0052 決定 4）。
- 期間開始時点の在庫と期間の買いで賄えない決済は、従来どおり `PeriodInventory` が「算定できない」と数え、`OpeningInventory` を未供給として記録する。
- 案 (b)（報告書が期間より前の全約定を受け取って自分で畳む）・案 (c)（決済の約定に取得原価を載せる）は採らない。理由は IADR-0493 §却下した案。

## 範囲

1. リスク管理: `GetOpeningInventory/OpeningInventoryQuery`（純関数）・`OpeningInventoryView`・REST エンドポイント・gRPC の rpc（proto 追加・`RiskReadWireMapping`）。
2. 共有 proto: `risk_controls_read.proto` へ rpc と message を追加（非破壊のフィールド追加）・`proto-contract-baseline.json` の更新。
3. 報告書: `OpeningInventorySnapshot`（Domain）・`IOpeningInventorySource`・`HttpOpeningInventorySource`・`GrpcOpeningInventorySource`・`UnsuppliedOpeningInventorySource`・
   Program.cs の結線・`ReportAutoGenerator`（窓の下端で引く・未供給の判定）・`ReportInputSnapshot`・`DraftRequest`・`ReportDraftService`・5 つの畳み込みの初期在庫・
   `PnlSummary.OpeningInventoryUnknown`・`ReportRenderer` / `ReportSummary` / `ReportNarrativePromptBuilder` の算出不能の分岐・`ReportInput` の注記と `IsCore`。
4. 文書: IADR-0493（新設）・IADR-0381 への日付つき追記・索引 2 行・データ仕様書（報告書）・通信仕様書（east-west gRPC）・本仕様書。
5. 試験 T-06-040〜（既存の `T-06` 帯は T06_035 まで。並行 PR との衝突を避けて 040 から始める。`git grep -hoE "\bT-06-[0-9]+\b|\bT06_[0-9]+"`）。

範囲外:
- 三者比較（月報 §5）は発注先ごとに在庫を分けて畳むが、期間開始時点の在庫は発注先を持たない（台帳の在庫は発注先で分かれない）。従来どおり切った在庫で畳み、算入できない決済の件数を注記する（IADR-0381 の残余のまま）。
- 機能仕様書・テスト仕様書（FR-06 は安全・統制の中核 FR ではなく必須でない。網羅裁定 #211）。作業仕様書と xUnit テストを正の記録とする。
- 確定済みの報告書の書き換え（しない）。稼働中のクラスタへの配備（本 PR では行わない）。

## 母集合（規則 9。誤りの側＝「期間開始時点の在庫は供給元が無い」「空の在庫から畳む」から引く）

引き方（origin/develop ec572e1b）: `git grep -n "期間開始時点の在庫\|OpeningInventory\|供給元が存在しない" -- ':!.ai-context/specs'`、
畳み込みの起点 `git grep -n "PeriodLedgerTimeline.Merge\|PeriodInventory.Apply\|PnlAggregator.Aggregate(" -- backend/Services/ReportService ':!*Tests*'`、
文書 `git grep -ln "期間より前に建て\|drift-adoptions" -- docs`。

| 箇所 | 扱い |
| --- | --- |
| `PnlAggregator.Aggregate`（§1 サマリ） | **直す**（初期在庫を置く） |
| `FillPnlAttributionBuilder.Build`（週報 §2/§3/§5・月報 §2） | **直す**（同じ初期在庫。内訳の合計が §1 と一致する条件＝IADR-0301） |
| `TradeHistoryViewBuilder.Build`（日報 §2） | **直す** |
| `ReportDraftService.ResolveCurrentPricesAsync`（評価損益の現在値を取りに行く銘柄） | **直す**（持ち越した建玉の現在値も引く） |
| `FxTranslationBuilder.Build`（為替差損益） | **直す**（初期在庫に認識時レートの加重平均を持たせる。未記録は件数に足す） |
| `ThreeWayComparisonAggregator`（月報 §5・発注先ごと） | 変えない（範囲外の理由は上） |
| `ReportInput.OpeningInventory` の注記（「供給元が存在しない」）・`IsCore` | **直す** |
| `ReportAutoGenerator.DraftFromInputsAsync` の注記（「供給元が存在しない入力であり待っても変わらない」） | **直す** |
| IADR-0381 決定 1・5（方向 1 は供給元が存在しない／取りに行かない・見送らない） | 本文は書き換えず日付つき追記（凍結記録） |
| データ仕様書（報告書）・通信仕様書（east-west gRPC の rpc 表） | **直す** |
| OpenAPI（`docs/api/openapi.yaml`） | 変えない（リスク管理の `/risk-controls/*` は収載対象外。実測: `grep risk-controls docs/api/openapi.yaml` は trace ブロックのみ） |

規則 10（この変更で新たに誤りになる自分の記述）: `PnlAggregator` 冒頭の「在庫は期間で切られている」・`PeriodInventory` 冒頭の「期間より前に建てた建玉は報告書の在庫に存在しない」・
`PnlSummary.UnvaluedSettlementCount`・`FillPnlAttribution.Unvalued`・`PeriodTradeFill` の注記・`ReportRenderer` の `UnvaluedCellFormat`（理由の文言）。
注記は「期間開始時点の在庫を受け取らない（未供給の）とき」へ条件付きに直す。描画の文言は変えない（期間開始時点の在庫と期間の買いで賄えない決済の件数であることは変わらない）。

## 受け入れ基準 → 試験

| ID | 受け入れ基準 | 試験 |
| --- | --- | --- |
| T-06-040 | （再現・手計算）MSFT・NVDA を前期に建て当期に全量決済した日報の実現損益が手計算と一致し、算出不能・未供給にならない | `OpeningInventoryAggregationTests` |
| T-06-041 | 前期に建てた建玉の一部決済（2 回）と当期の建て増しを挟む決済の実現損益が平均取得単価法の手計算と一致する | 同上 |
| T-06-042 | 持ち越して当期に決済しない建玉が評価損益に入る（現在値を取りに行く） | 同上 |
| T-06-043 | 期間開始時点の在庫と当期の買いを超える売り（手仕舞い）は従来どおり算定できないと数え、`OpeningInventory` を未供給として記録する | 同上 |
| T-06-044 | 内訳（週報の日別・帰属）と日報 §2 の明細の実現損益の合計が §1 と一致する（同じ初期在庫） | 同上 |
| T-06-045 | 為替差損益は持ち越した建玉の認識時レートの加重平均から決済時レートへ再測定する。認識時レートが未記録の持ち越しは件数に足して未供給にする | 同上 |
| T-06-046 | （台帳）窓の下端の取引日ちょうどの約定は期間開始時点の在庫に入らず、前日の約定は入る（米国は ET・東証は JST の暦日。夏時間・ET 深夜の境界） | `OpeningInventoryQueryTests` |
| T-06-047 | （台帳）期間より前に全量決済した建玉は返さない。取り込み行は平均取得単価で数量だけ減らす。基準通貨の平均取得単価は `PriceInBase` で畳む（日本株） | 同上 |
| T-06-048 | （台帳）認識時レートは基準通貨の原価で加重平均し、未記録の約定が建玉に残れば `null` と件数を返す。全決済で数え直す | 同上 |
| T-06-049 | （REST・gRPC）`market`・`before` の欠落・未定義は 400 / `INVALID_ARGUMENT`。gRPC は REST と同じ行を返す | `OpeningInventoryEndpointTests` |
| T-06-050 | （報告書の生成）日報・週報・月報の自動生成は窓の市場ごとの下端を `before` に渡して在庫を引き、警告「期間開始時点の在庫」が消える | `ReportOpeningInventoryWiringTests` |
| T-06-051 | （fail-closed）在庫の照会に失敗（例外・`null`）したら `OpeningInventory` を未供給として記録し、§1 の実現損益・評価損益・税・勝率と提示の要約を算出不能にする。散文へ値を渡さない | 同上 |
| T-06-052 | 在庫の取得は中核の入力（一過性の失敗は中核の上限で見送る） | 同上 |
| T-06-053 | （受け手の写し）REST・gRPC の応答の欠けた行は応答全体を読めない（未供給）とし、既定値で在庫を作らない | `OpeningInventorySourceTests` |

## 配備の注記

- 構成の追加は無い（既存の `RiskManagement:BaseUrl`／`RiskManagement:Grpc` の宛先をそのまま使う）。リスク管理と報告書の**両方**を同じ版へ上げる。
  報告書だけを上げると、旧版のリスク管理は REST で 404・gRPC で `UNIMPLEMENTED` を返し、在庫は**未供給**（算出不能）へ倒れる（誤った数字は出ない）。
- 確定済みの報告書は書き換えない。未確定の daily-2026-10-06 は、両サービスの配備後に `/report regenerate daily-2026-10-06` で引き直すと、
  MSFT・NVDA の決済の実現損益が出て警告が消える（未確定であることを確かめてから）。

## ［2026-10-06 追記 / #1181 の独立監査（PR #1185）］指摘への対応

| 指摘 | 対応 | 試験 |
| --- | --- | --- |
| 🟡1 中核の入力が 3 → 4 になったのに IADR-0480 決定 3 が「3 つ」のまま。計画 ADR-0052 決定 4 の括弧書きも遅れる | IADR-0480 に日付つき追記・索引行の追記。IADR-0493 §フォローアップに計画側への環流（起票済み。planning#729）を記す | — |
| 🟡2 照会失敗の期間に §1 の為替差損益が部分値を完全な値として出す | `FxTranslationCell` が `OpeningInventoryUnknown` で算出不能を描く（日報・月報） | T-06-055（`ReportOpeningInventoryWiringTests` の T06_051 に追加）・T-06-056 |
| 🟡3 台帳の反転の分岐が未検証（変異が生き残る） | 反転後の数量が小さい形・大きい形 × レートあり・なし | T-06-054（`OpeningInventoryQueryTests`） |
| 🟡4 週報 §5 の費用率が `IsPartial` に固定されていない | 件数 0・照会失敗の週報で費用率が算出不能 | T-06-056（`OpeningInventoryAggregationTests`） |
| 🟢1 受け手が重複・負の単価・レートと未記録の数の食い違いを読む | `Interpret` が拒否（未供給）。REST と gRPC の両方 | T-06-057（`OpeningInventorySourceTests`） |
| 🟢2 一過性の失敗（503）での見送りの結線が未検証 | 本物の鎖越しに 503 → 中核の上限で見送り → 回復で縮退なし | T-06-058（`ReportAutoGeneratorDependencyRetryTests`） |
| 🟢（AI レビュー）`SafeOpeningInventoryAsync` の市場の絞り込みは到達しない防御 | 二つ目の安全弁である旨の注記を足した | — |
| 🟢3 性能（毎回台帳の全行を畳む） | 本 PR では扱わない（#1186） | — |

自己変異（scratchpad/ast1181-mut/mut2.py・mut3.py）: 反転の分岐を消す／週報 §5 を件数で判定／為替差損益の行が旗を見ない／受け手の重複・食い違い・
正でないレート・負の単価を受け入れる／中核から外す（見送りの上限）——いずれも赤（8 件すべて）。
