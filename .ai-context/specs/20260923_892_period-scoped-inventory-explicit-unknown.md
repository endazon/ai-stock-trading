---
title: 期間で切った在庫は「期間前の建玉の決済」を幻のショートにせず、算定できないことを明示する（#892）
type: spec
status: accepted
related_ids: [FR-06, FR-11, FR-16, UC-03, ADR-0041, IADR-0025, IADR-0033, IADR-0301, IADR-0352, IADR-0360, IADR-0381]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-06 報告書 / FR-16 損益集計)
  - planning:projects/ai-stock-trading/04_workflows/04_report-templates.md (数値の定義・日報 §1/§2・週報 §1/§2/§3/§5・月報 §1/§2/§5)
---

# 仕様書: 期間で切った在庫の「期間前の建玉の決済」を幻のショートにしない（#892）

## 起点

- **#892**。起点 ID: **FR-06**（報告書）・**FR-16**（損益集計）。関連 **FR-11**（手動売買の取り込み）。
- 関連 IADR: [IADR-0033](../adr/IADR-0033_shared-inventory-fold.md)（符号付き在庫の共有畳み込み）、
  [IADR-0301](../adr/IADR-0301_fill-level-pnl-attribution-single-fold.md)（期間全体を 1 回だけ畳む）、
  [IADR-0360](../adr/IADR-0360_manual-trade-origin-and-report-drift-adoption-section.md)（取り込みは在庫だけを畳む・クランプ）、
  [IADR-0352](../adr/IADR-0352_report-defers-on-transient-dependency-failure.md)（未供給だった入力の記録と提示）。
- 本作業で起草する実装 ADR: **IADR-0381**。

## 診断（コードの実測）

報告書の在庫は **その期間の約定だけ**から畳まれる（期間の切り出しは権威源側の `PeriodFillQuery`）。
したがって**期間より前に建てた建玉は報告書の在庫に存在しない**。それを当期に決済すると、
`SignedInventory.Apply`（`backend/Shared/AiStockTrading.Shared.Contracts/Trading/SignedInventory.cs`）が
「在庫 0 への売り」を**新規建て**として畳み、平均取得単価＝決済価格の**幻のショート**が開く。

幻のショートは

- 現在値を引かれて**実在しない建玉の評価損益**になる（日報 §1「評価損益（税引前・参考）」）
- 続く決済を「決済」ではなく「新規ショート」に変え、**実現損益・決済件数・勝ち決済件数**を落とす

畳み込みを行う箇所は 5 つあり、**すべて同じ順序・同じ規則でなければ内訳の合計が §1 とずれる**
（IADR-0301 の明文。ずれても各集計は自分の中では整合するため全テストは緑のままになる）。

| # | 場所 | 現状 |
| --- | --- | --- |
| 1 | `Domain/PnlAggregator.cs:57` | `SignedInventory.Apply` を素で呼ぶ |
| 2 | `Domain/FillPnlAttribution.cs:147` | 同上（週報 §2/§3・月報 §2 の帰属） |
| 3 | `Domain/TradeHistoryViewBuilder.cs:63` | 同上（日報 §2 の明細） |
| 4 | `Features/Reports/ReportDraftService.cs:217` | 同上（評価損益の現在値を取りに行く銘柄の決定） |
| 5 | `Domain/FxTranslationBuilder.cs:122` の `Apply` | 自前の同型分岐（反転で新規ロットを建てる） |

🔴 **取り込み（`PeriodDriftAdoption`）とは無関係に起きる。** 取り込みは #870 / IADR-0360 決定 4 で
`ReducedQuantity` によりクランプ済み（建てない・反転しない）だが、**約定は建てる操作でもある**ため
同じクランプを一律に当てると、当期に始めた正当な新規ショートまで消える。

## 採る在庫の規約（🔴 本作業の中心）

### 選んだもの: **期間で切った在庫のまま（opening inventory は持ち込まない）＋算定できないことの明示**

issue が挙げた 3 方向のうち **方向 2** を採る。理由:

- **方向 1（期間開始時点の在庫を供給する）は、供給元が無い。** リスク管理の `ProjectOpenPositions` は
  **現在の台帳全体**を畳む口であり、**過去時点の射影**を返す口は存在しない。新設はサービス境界と
  台帳のスナップショット方式に触れる判断であり、報告書サービスに閉じない。
- **方向 3（報告書の期間を建玉の生涯で切る）は計画の粒度定義（04_report-templates）に触れる。**
- 方向 2 は**本サービスが全節で守っている規律**（未供給と 0 を区別する・推定で埋めない）と同じ形であり、
  実装側だけで閉じる。issue が認める副作用（「日報の評価損益が頻繁に未供給になる」）は**受容する**
  —— 幻の数字より「算定できません」のほうが読み手を誤らせない。

### 期間前の建玉の決済をどう見分けるか: `PositionEffect`

issue は「在庫 0 への反対売買」を検出器の案として挙げるが、それでは**当期に始めた正当な新規ショート**まで
巻き添えになる。本作業は**台帳が既に持っている軸** `PeriodTradeFill.PositionEffect`
（`AiStockTrading.Shared.Contracts.Trading.PositionEffect`。`Open` ＝新規建て／`Close` ＝手仕舞い）を使う。

`PositionEffect` は `TradeDecisionService.Domain.PositionEffectResolver` が**判断時点の実保有数量**から解決し、
台帳（`LedgerFill.PositionEffect`）へ記録され、`GET /risk-controls/fills` から報告書まで素通しで届く
（`Infrastructure/ExternalServices/HttpPeriodFillSource.cs`）。**新しい入力は 1 つも要らない。**

規約（`PeriodInventory` を単一情報源にする）:

| 約定 | 期間の在庫 | 扱い |
| --- | --- | --- |
| `Open` | 何であれ | 従来どおり `SignedInventory.Apply`（**正当な新規建て・建て増し・反転はここに来る**） |
| `Close` | 賄える | 従来どおり `SignedInventory.Apply`（期間内で建てて期間内で決済した） |
| `Close` | 賄えない（0・同方向・不足） | 賄える分だけ決済し、**賄えない分は建てない（クランプ）**。その分の実現損益は**算定しない** |

「賄えない分」を **`UnvaluedQuantity`**（取得原価が当期間に無く評価できない数量）と呼ぶ。

🔴 **クランプは「決済が無かったことにする」のではない。** 約定件数・費用は従来どおり計上する
（費用は約定ごとに掛かり、取得原価を要しない）。落とすのは**取得原価を要する値だけ**である。

### 算定できないことをどこで言うか

`UnvaluedQuantity > 0` の約定が 1 件でもあれば、その期間の**実現損益・源泉徴収税額・勝率・評価損益は部分値**である。
部分値を数字として出さない（「黙って間違った数字」は「拒む報告書」より悪い）。

🔴 **新しい語彙を作らない。** 本サービスは既に 3 語を区別している——`算出不能`（計算できない・期間の集計値。
既存は週報 §5 の費用率の分母 0）／`不明`（計算できない・行単位。日報 §2-b）／`未供給`（記録源が無い）。

- **§1 サマリ**: 実現損益（税引後・費用込み）・源泉徴収税額・勝率・評価損益の各セルを
  **`**算出不能**（期間より前に建てた建玉の決済が N 件あり、その取得原価が当期間の約定に含まれていません）。
  **0 ではありません。**`** に置き換える。**取引回数・費用合計は事実なので出す。**
- **日報 §2 の明細**: 当該行の「実現損益」列を `**不明**` にする（§2-b と同じ語。`0` と書かない）。
- **Discord の提示要約**: 実現損益と決済・勝ち件数を「算出不能」にする（要約だけを見て確定する利用者がいる）。
- **週報 §2 日別推移 / §3 ハイライト / §5 費用率 / 月報 §2 内訳 / 月報 §5 三者比較**:
  算入しなかった件数を明記する行を足す（`UnattributedTradeCount` の既存の作法と同型）。
  費用率は分母が部分値になるため**算出不能**にする。
- **未供給の入力として記録する**: `ReportInput` に **`OpeningInventory`（期間開始時点の在庫）** を足す。
  これで IADR-0352 の既存経路（`reports.UnsuppliedInputs` への記録・Discord 提示通知の警告・
  `/report show`・版番号なしの `/report approve` の警告）へ**そのまま乗る**。
  🔴 **見送り（リトライ）の対象にはしない**——供給元が存在しない入力であり、待っても変わらない。
  `TryDefer` は散文の判定より前に終わっているため、追加位置（ドラフト生成の後）で自然にそうなる。

## 実装（変更するファイル）

| ファイル | 変更 |
| --- | --- |
| `Domain/PeriodInventory.cs` | **新規**。`UnvaluedQuantity` / `Apply` の純関数（規約の単一情報源） |
| `Domain/PnlAggregator.cs` | `PeriodInventory.Apply` へ。未評価の決済を数える |
| `Domain/PnlSummary.cs` | `UnvaluedSettlementCount` を追加 |
| `Domain/FillPnlAttribution.cs` | `PeriodInventory.Apply` へ。`Unvalued` 列・`DailyPnlRow.UnvaluedCount`・ハイライト母集合から除外 |
| `Domain/TradeHistoryViewBuilder.cs` / `TradeHistoryView.cs` | `PeriodInventory.Apply` へ。`TradeHistoryLine.RealizedPnlUnvalued` |
| `Domain/FxTranslationBuilder.cs` | 自前 `Apply` に同じ規則（`PeriodInventory.UnvaluedQuantity`）を当てる |
| `Domain/ThreeWayComparisonAggregator.cs` / `ThreeWayComparison.cs` | 列に算入しなかった決済の件数を運ぶ |
| `Domain/ReportRenderer.cs` | 上記の描画 |
| `Domain/ReportInput.cs` | `OpeningInventory` を追加（全種別に適用） |
| `Domain/PeriodBreakdown.cs` | 方向の導出（`IsLong`）で `Unvalued` を決済側へ数える |
| `Domain/ReportSummary.cs` | Discord 要約でも部分値を数字として出さない |
| `Domain/TradeHistoryRenderer.cs` | 日報 §2 の実現損益セルと凡例 |
| `Features/Reports/ReportDraftService.cs` | 現在値を引く銘柄の畳み込みを `PeriodInventory.Apply` へ |
| `Features/Reports/ReportAutoGenerator.cs` | ドラフト生成後に `OpeningInventory` を未供給へ入れる |

## テスト（xUnit v3・注入した時計・壁時計の待ちなし）

**テスト ID 帯**: `T-16-001`〜 を本作業で新設する。走査（`grep -rnoE "\bT-16-[0-9]+\b"`）の結果、
**本リポジトリに `T-16` 帯は 1 件も存在しない**ため最大値は無く、`T-16-001` から採る。
`T-10-6xx` は他レーンが押さえているため使わない。`docs/tests/` に FR-16 のテスト仕様書は無い
（網羅裁定 #211 の必須範囲外）ため、採番の記録は**本仕様書と本作業の実装 ADR が持つ**。

| ID | 何を固定するか |
| --- | --- |
| T-16-001 | 期間前に建てた建玉の決済（`Close`・在庫 0）で**幻のショートが開かない**（評価損益 0・issue 実測 1） |
| T-16-002 | 同上で**実現損益・決済件数を捏造しない**、かつ未評価の決済として数える |
| T-16-003 | **正当な新規ショート**（`Open`・在庫 0 の売り）は従来どおり建ち、評価損益が出る（巻き添えにしない） |
| T-16-004 | 取り込みを挟む issue 実測 2 の並びで、幻のショートが開かない |
| T-16-005 | 期間内に建てて期間内に決済した `Close` は**従来どおり**（退行が無い） |
| T-16-006 | 部分的にしか賄えない `Close`（在庫 5 に対し 10 の決済）は賄えた分だけ実現し、残りを建てない |
| T-16-007 | 費用・約定件数は未評価の決済でも従来どおり計上される |
| T-16-008 | `PeriodInventory.UnvaluedQuantity` の境界（`Open` は常に 0／同方向の `Close`／不足） |
| T-16-009 | 帰属（`FillPnlAttributionBuilder`）が `Unvalued` を立て、ハイライトの母集合から外す |
| T-16-010 | 日報 §2 の明細が当該行の実現損益を `**不明**` にする |
| T-16-011 | §1 サマリが実現損益・評価損益・源泉徴収税額・勝率を `**算出不能**` にする（日報・週報） |
| T-16-012 | 為替差損益の畳み込みでも幻のショートが開かない |
| T-16-013 | `ReportInput.OpeningInventory` が未供給として記録され、警告経路に乗る |
| T-16-014 | 現在値の解決が、幻のショートの銘柄の相場を取りに行かない |
| T-16-015 | 方向別の内訳が、期間前の建玉の決済を**ショートの新規建て**として数えない |

🔴 **T-16-006 は既存テストの置き換えである。** `PnlAggregatorTests` の
`反転_ロングからショート_は決済分の実現損益と残ショートの評価損益を扱う` が固定していた「`Close` の余りを
新しいショート建玉にする」挙動は、本作業で**意図して変えた**（実装 ADR 決定 2）。

## 受け入れ基準

1. issue の実測 1（`Sell(100 @250)` 単独・現在値 300）で `unrealized == 0`・`realized == 0`・
   `realizingCount == 0` かつ未評価の決済 1 件として記録される。
2. issue の実測 2（買い 10 → 取り込み 110→100 → 売り 10）で幻のショートが開かない。
3. 在庫 0 への `Open` の売り（正当な新規ショート）は従来どおり建ち、評価損益が出る。
4. 5 つの畳み込みが同じ規則を通る（`SignedInventory.Apply` の素の呼び出しが報告書サービスから消える）。
5. 部分値になった数値は §1 で数字として出ない。
6. `dotnet build` / `dotnet test`（ReportService）・`dotnet format --verify-no-changes`・文書検査が通る。

## 射程外（本作業で**やらないこと**）

- **期間開始時点の在庫を供給する口の新設**（issue の方向 1）。サービス境界に触れるため計画への環流が要る。
- **報告書の期間を建玉の生涯で切る**（方向 3）。計画の粒度定義に触れる。
- **計画（04_report-templates）への環流 issue の起票。** §1 の数値が「算定できません」になり得ることは
  テンプレートの数値定義に関わるため、**planning への `decision-needed` 起票が要る**が、本作業では起票しない
  （実装は計画に反しない側＝数字を騙らない側へ倒しているため、先に動ける）。報告に残す。
- 既に確定済みの報告書の書き換え（本文は生成時に一度だけ組み立てられる。`ReportBodyStatus` の規律）。
