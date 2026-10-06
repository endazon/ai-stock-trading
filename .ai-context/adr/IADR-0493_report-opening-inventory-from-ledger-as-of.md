---
title: IADR-0493 報告書の期間開始時点の在庫は、リスク管理の取引台帳が市場ごとの窓の下端（現地取引日・排他）まで畳んだ射影として供給する（IADR-0381 決定 1・5 の改定）
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-11, FR-16, UC-03, UC-04, UC-05, ADR-0052, ADR-0053, IADR-0381, IADR-0492, IADR-0033, IADR-0107, IADR-0246, IADR-0286, IADR-0301, IADR-0350, IADR-0352, IADR-0360, IADR-0427, IADR-0447, IADR-0480, IADR-0491]
author: claude (Claude Code)
created: 2026-10-06
updated: 2026-10-06
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-06 当日の取引結果・損益の集計 / FR-16 数値はコード集計)
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (数値の定義: 実現損益・評価損益＝(現在値−平均取得単価)×数量・為替差損益は独立行)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§1 譲渡益税率・§3 実現損益は約定時レート)
  - planning:projects/ai-stock-trading/07_adr/ADR-0053 (各報告書は前回の生成の後から今回の生成までに閉場したセッションを集計する)
  - planning:projects/ai-stock-trading/07_adr/ADR-0052 (作り直しは自動生成と同じ入力の規則・中核の入力の取得失敗は断る)
---

# IADR-0493: 期間開始時点の在庫を取引台帳の as-of 射影として供給する（#1181）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-06
- 決定者: Claude Code（実装）。[#1181](https://github.com/endazon/ai-stock-trading/issues/1181)（PoC の作り直しで観測）

## 起点・関連

- 関連する計画書 ID: FR-06（報告書の損益の集計）・FR-16（数値はコード集計）・FR-11（手動売買の取り込みは在庫だけを動かす）・UC-03〜05・
  ADR-0053（窓の下端は市場ごとの閉場）・ADR-0052（作り直しは同じ入力の規則・中核の入力の取得失敗は断る）
- 改める実装判断: [IADR-0381](./IADR-0381_period-scoped-inventory-explicit-unknown.md) 決定 1（方向 1「期間開始時点の在庫を供給する」を
  「供給元が存在しない」として採らない）・決定 5（取りに行かない・見送らない）
- 維持する実装判断: IADR-0381 決定 2〜4（`PositionEffect` で賄えない決済を見分ける・規則は `PeriodInventory` 1 か所・部分値は「算出不能」）、
  [IADR-0033](./IADR-0033_shared-inventory-fold.md)（平均取得単価法の共有の畳み込み）、[IADR-0301](./IADR-0301_fill-level-pnl-attribution-single-fold.md)（期間全体を 1 回だけ畳む）、
  [IADR-0492](./IADR-0492_report-session-window-by-market-close.md)（市場ごとの窓）
- 輸送の作法: [IADR-0427](./IADR-0427_risk-read-grpc-stage2.md)（REST と gRPC の並走・原則 A・解釈の共有・門と観測）・
  [IADR-0447](./IADR-0447_grpc-read-latent-residuals.md)（10 進の桁あふれは読めない値）・[IADR-0352](./IADR-0352_report-defers-on-transient-dependency-failure.md)（未供給の記録と見送り）・
  [IADR-0480](./IADR-0480_report-narrative-unsupplied-vs-zero-and-core-input-deferral.md)（中核の入力）
- 関連する実装仕様書: [`.ai-context/specs/20261006_1181_report-opening-inventory.md`](../specs/20261006_1181_report-opening-inventory.md)

## 背景

- 報告書は期間の約定しか受け取らず、期間より前に建てた建玉の決済は取得原価が無いので実現損益を算定できない。IADR-0381 はこれを
  「算出不能」と明示し、`ReportInput.OpeningInventory`（期間開始時点の在庫）を**供給元が存在しない入力**として記録してきた。
- PoC（2026-10-06・AST ec572e1b）で daily-2026-10-06 版 2 に「⚠ 未供給の入力があります: 期間開始時点の在庫」が出た。ET 10/05 の決済
  MSFT 468 株（511.912 → 527.15）・NVDA 1049 株（230.77 → 237.69）は、どちらも期間より前に建てた建玉の決済である。PoC の取引はエントリーから
  利確・損切りまで数日持つのが普通で、**決済のある日の日報はほぼ毎回、実現損益・税・勝率を出せない**——FR-06 の「当日の取引結果・損益の集計」が
  実質的に満たせない。週報・月報も期間をまたぐ建玉で同じになる。
- IADR-0381 が方向 1 を採らなかった理由は「リスク管理の `ProjectOpenPositions` は**現在**の台帳全体を畳む口で、過去時点の射影を返す口が無い。
  新設はサービス境界と台帳のスナップショット方式に触れる」だった。実測すると、**台帳は全期間の約定（と取り込み行）を追記専用で持ち、
  射影は毎回それを畳み直している**——スナップショットは要らず、畳む行を取引日で絞るだけで過去の時点の射影になる。

### 実測（origin/develop ec572e1b）

| # | 事実 | 確かめ方 |
| --- | --- | --- |
| 1 | 取得原価の方法は計画・報告書・台帳とも**平均取得単価法**（計画 04_report-templates の評価損益の定義・05_trading-assumptions §3。FIFO はどこにも無い）。報告書と台帳は同じ純関数 `SignedInventory.Apply` で畳む | `PeriodInventory.Apply`・`PortfolioProjection.ApplyToLot`（IADR-0033） |
| 2 | 報告書の約定単価は基準通貨（USD）へ換算済み（`HttpPeriodFillSource.ToFill`＝単価 × `FxRateToBase`）。台帳の `LedgerFill.PriceInBase` と同じ式 | `HttpPeriodFillSource`・`LedgerFill` |
| 3 | 台帳は取り込み行を `ExecutedAt = AdoptedAt` で約定列に合流させ、射影は取り込み行を**その時点の平均取得単価**で畳む（実現損益 0）。報告書の `PeriodDriftAdoption.ApplyTo` も数量だけ減らし平均を動かさない | `EfPortfolioLedgerStore.GetFills`・`PortfolioProjection.ApplyToLot`（IADR-0350 決定 3） |
| 4 | 報告書の窓（IADR-0492）は約定を**市場の現地取引日**で絞る（`ReportSessionWindow.Includes`＝市場のタイムゾーンの暦日）。台帳の取引日（`TradingDay.Of(instant, market)`）と同じ定義 | `ReportSessionWindow`・`TradingDay` |
| 5 | 報告書の在庫の畳み込みは 5 か所（`PnlAggregator`・`FillPnlAttributionBuilder`・`TradeHistoryViewBuilder`・`ReportDraftService` の現在値の決定・`FxTranslationBuilder`）で、いずれも空の在庫から始める | `git grep -n "PeriodLedgerTimeline.Merge" -- backend/Services/ReportService` |
| 6 | 為替差損益は建玉ごとに認識時レート（1 USD あたりの円）を USD 原価で加重平均して持ち回る。台帳は約定ごとの認識時レート（`FxRateBaseToDisplay`）を持つ | `FxTranslationBuilder.Apply`・`LedgerFill.FxRateBaseToDisplay` |

## 検討した選択肢

| 案 | 中身 | 判定 |
| --- | --- | --- |
| **a** | **リスク管理の台帳が、指定した市場の指定した現地取引日より前（排他）までの台帳行を畳んだ在庫を返す**（新しい読み取りの口） | **採用** |
| b | 報告書が期間より前の全約定（と取り込み）を受け取り、自分で畳む | 不採用 |
| c | 決済の約定に取得原価（平均取得単価）を載せる | 不採用 |

- **b を採らない理由**:
  - **取得原価の単一情報源が 2 つになる。** 台帳は射影（統制の含み損益・段階資金・損切りライン）で既に平均取得単価を畳んでいる。報告書が同じ
    畳み込みを別に持てば、取り込み行の扱い（実測 3）・同時刻の並び・基準通貨の換算のどれか 1 つがずれただけで、報告書の取得原価が統制の
    見ている建玉と黙って食い違う（IADR-0301 が書いた「各集計は自分の中では整合しているため全テストが緑のままずれる」形である）。
  - **データ量が期間の長さではなく運用の長さに比例する。** 日報のたびに運用開始からの全約定を運ぶ（月報・作り直しも同じ）。
  - 取り込み行は約定の口（`/fills`）から除外されている（IADR-0350 決定 4）ため、b は取り込みの全履歴も別に運ぶ必要がある。
- **c を採らない理由**:
  - **決済の約定 1 件では足りない。** 持ち越して当期に決済しない建玉の評価損益（§1）、持ち越した建玉に当期に建て増してから決済する場合の
    平均取得単価（持ち越し分と混ぜた平均）、為替差損益の認識時レートは、いずれも決済の約定に載らない。
  - 取得原価は**約定の時点**ではなく**承認・記録の時点**で台帳へ書くことになり、台帳の行（承認 Intent × 約定）の形と書き込み経路に触れる。
    過去の行には遡って入らない（PoC の既存の建玉は救えない）。
- **a の代償**: リスク管理に読み取りの口が 1 つ増える（REST と gRPC の両方）。報告書が 1 回の生成で市場ごとに 1 回ずつ（2 回）照会する。
  畳み込みは毎回台帳の全行を読む（射影と同じ費用。PoC の台帳の行数では問題にならない）。

## 決定

### 決定 1: 案 a を採り、窓の**市場ごとの下端**（現地取引日・排他）で引く

- 期間開始時点の在庫は、市場 M について「取引日 `< From(M)`」の台帳行（約定と取り込み）を畳んだものである。`From(M)` は報告書の窓
  （IADR-0492 `ReportSessionWindow.TradingDays(M).From`）の下端＝窓に入る最初の現地取引日である。
- 🔴 **時刻（JST 0 時・期間の初日）で切らない。** 窓は約定を市場の現地取引日で絞るので、同じ取引日で切れば「期間開始時点の在庫」と
  「窓の約定」は**重ならず隙間も無い**（実測 4。取引日 = `From(M)` の行は窓に入り、在庫には入らない）。JST 0 時で切ると、ET の深夜（JST の翌日）
  の約定を窓と在庫の両方で数えるか、どちらにも入れない。
- 週報・月報も同じ規則（窓の下端）で引く。週報・月報の窓は日報の窓の和（ADR-0053 決定 2）なので、期間開始時点の在庫は**その期間の最初の
  日報の期間開始時点の在庫と同じ**になる。
- 市場は取引台帳が持ち得るすべて（約定の照会と同じ `ReportSessionWindow.Markets`）。窓にその市場のセッションが無くても下端は定まる
  （土日・休場日の下端の前は直前の営業日までと同じ在庫）。

### 決定 2: 契約（REST と gRPC の並走）

- REST `GET /risk-controls/opening-inventory?market={Japan|UnitedStates|数値}&before=yyyy-MM-dd`（OwnerOrService）・
  gRPC `RiskControlsRead/GetOpeningInventory`（`GrpcOwnerOrService`）。`market`・`before` の欠落・未定義は 400 / `INVALID_ARGUMENT`。
  **`GET /open-positions` とは別の口である**（あちらは現在の台帳全体を畳み損切りラインを載せる。市場監視・取引判断の入力）。
- 応答は数量が 0 でない銘柄ごとに `symbol`・`market`・`side`・`quantity`（正）・`averageCostInBase`（**基準通貨の平均取得単価**）・
  `averageFxRateBaseToDisplay`（認識時レートの基準通貨の原価による加重平均。**建玉に未記録の約定が含まれれば null**）・
  `unrecordedFxRateFillCount`。並びは銘柄コードの序数順（決定的）。
- 畳み込みは**射影と同じ 1 行の入口 `PortfolioProjection.ApplyToLot`**（約定は `PriceInBase`、取り込み行は平均取得単価で数量だけ）を使い、
  約定時刻の昇順（射影と同じ）で畳む（純関数 `OpeningInventoryQuery.AsOf`）。認識時レートの加重平均は報告書の `FxTranslationBuilder` と同じ規則
  （建て増しで原価加重・一部決済で不変・全決済で数え直す・同じレートの加重平均はそのレート）。
- 受け手（報告書）は輸送に依らず 1 つの解釈（`HttpOpeningInventorySource.Interpret`）を持ち、gRPC は同じ nullable の行へ写してから呼ぶ
  （IADR-0427 決定 5）。**必須の項目（銘柄・市場・向き・数量・平均取得単価・未記録の数）が 1 行でも欠ける、数量が 0 以下、要求と違う市場の行が
  ある応答は全体を読めない**（既定値で在庫を作らない。原則 A）。10 進の書式・桁あふれは `FormatException`（IADR-0447 決定 4）で読めない応答になる。
- 輸送の deadline・再試行・門と観測は既存の `risk-ledger`（REST の HttpClient.Timeout 10 秒）／`RiskManagementGrpcTransport`（同じ 10 秒・1 試行）を
  そのまま使う（新しい構成キーは無い）。proto は rpc と message の追加だけ（非破壊。baseline を更新）。

### 決定 3: 報告書の 5 つの畳み込みは同じ初期在庫から始める

- `OpeningInventorySnapshot.Seed` が (銘柄, 市場) → 符号付き在庫・基準通貨の平均取得単価を返し、`PnlAggregator`・`FillPnlAttributionBuilder`・
  `TradeHistoryViewBuilder`・`ReportDraftService.ResolveCurrentPricesAsync`・`FxTranslationBuilder` が**同じ値**を初期在庫に置く（内訳・明細の合計が
  §1 と一致する条件。IADR-0301）。`PeriodInventory`（IADR-0381 決定 2）の規則はそのまま——期間開始時点の在庫と期間の買いで賄えない手仕舞いは
  従来どおり「算定できない」と数え、`OpeningInventory` を未供給として記録する（台帳と報告書の窓の食い違いを数字で隠さない）。
- 持ち越して当期に決済しない建玉も在庫に入るので、評価損益（§1）に入り、その銘柄の現在値を市場データ源へ取りに行く（約定が無い日も）。
- 為替差損益は、持ち越した建玉を台帳の認識時レートの加重平均から決済時レート（決済したとき）・期末レート（期末に残るとき）へ再測定する。
  平均が作れない建玉（`null`）は、建玉に残る未記録の台帳行の数を「認識時レートが未記録の約定」の件数へ足して節ごと未供給にする（推定で埋めない）。
- 三者比較（月報 §5）は発注先ごとに在庫を分けて畳むが、台帳の在庫は発注先で分かれないため初期在庫を置かない（従来どおり。残余リスク）。

### 決定 4: 取得の失敗は fail-closed（未供給・算出不能）。中核の入力として見送る

- 供給元（`IOpeningInventorySource`）を注入した生成器は、入力を引く段（`CollectInputsAsync`。自動生成と作り直しで 1 本）で市場ごとに引き、
  **1 市場でも取得できなければ（null・例外）在庫全体を未供給**とする。在庫は期間で切ったまま畳み、`PnlSummary.OpeningInventoryUnknown` を立てる。
- `OpeningInventoryUnknown` の期間は、算定できない決済が 0 件でも §1 の実現損益・税・勝率・評価損益（と週報 §5 の費用率）・提示の要約・
  散文の文脈を「算出不能」にする。🔴 理由は、持ち越した建玉が評価損益から落ち、当期に建て増してから決済した分の平均取得単価が持ち越し分と
  混ざっていない——**部分値を完全な値として出さない**ためである。文言は既存の `算出不能`（IADR-0381 決定 4）に理由を足すだけで語彙は増やさない。
- `OpeningInventory` を**中核の入力**（`ReportInputs.IsCore`。IADR-0480 決定 3）に加える。欠けると報告書の損益の主張が成り立たないためである。
  一過性の失敗（観測が一過性と記録した場合）は中核の上限で長く見送り、作り直し（ADR-0052 決定 4）は取得の失敗を断る（現行の下書きは残る）。
  期間開始時点の在庫は台帳から引ける過去の時点の値なので、`IsPointInTime`（今の値しか引けない入力）には入れない——作り直しでも取りに行く。
- 供給元を注入しない経路（単体テスト・手動の生成 API `POST /reports/{periodKey}/draft`）は従来どおり（IADR-0381: 算定できない決済を検出した回だけ
  未供給）。本番の Program.cs は必ず注入する（リスク管理の所在が未構成なら `UnsuppliedOpeningInventorySource`＝常に未供給）。

## 理由

- **取得原価の権威は台帳の畳み込み 1 つに置く**（案 b の却下理由）。報告書は返された値を初期在庫に置くだけで、規則を持たない。
  統制（含み損益・段階資金）と報告書が同じ建玉・同じ平均取得単価を見る。
- **境界は約定の窓と同じ取引日で切る**ので、二重計上も取りこぼしも構造的に起きない（決定 1）。
- **確定の再現性**: 在庫は追記専用の台帳を取引日で切って畳むだけなので、同じ期間の作り直しは同じ値になる（台帳の過去の行が変わらない限り）。
  統制の側の「今」の射影（`/open-positions`）とは違い、作り直しで時点がずれない。

## 結果

- **良い影響**
  - 持ち越した建玉の決済の実現損益・税・勝率が日報・週報・月報に出る（daily-2026-10-06 の MSFT・NVDA）。警告「期間開始時点の在庫」は、
    台帳と窓の食い違い（賄えない手仕舞い）か照会の失敗のときだけ出る。
  - 持ち越して当期に決済しない建玉が評価損益と為替差損益に入る（従来は在庫に存在せず、警告も無く落ちていた）。
- **悪い影響 / トレードオフ**
  - 報告書の生成ごとにリスク管理へ照会が 2 回（市場ごと）増える。リスク管理と報告書を同じ版へ上げる必要がある（報告書だけを上げると旧版の
    リスク管理は REST 404・gRPC `UNIMPLEMENTED` を返し、在庫は未供給＝算出不能へ倒れる。誤った数字は出ない）。
  - 照会に失敗した回は、従来なら数字が出ていた期間（当期に建てて当期に決済しただけの期間）も算出不能になる（fail-closed の代償）。
- **残余リスク**
  - **台帳の過去の行が後から変わると、確定済みの報告書と作り直しの値がずれ得る**（約定時刻が過去の照合の追記など）。確定済みは書き換えない。
  - **三者比較（月報 §5）は初期在庫を持たない**（発注先ごとの在庫が台帳に無い）。持ち越した建玉の決済は従来どおり勝率・平均損益に算入せず件数を注記する。
  - **照会に失敗した回の行単位の値**（日報 §2 の明細・週報の日別推移・月報の内訳）は期間で切った在庫のままの値で、§1 だけが算出不能になる。
    報告書の冒頭の未供給の警告（「期間開始時点の在庫」）が同じ報告書に出る。中核の入力として見送るので、この形の報告書は見送りの上限・窓の終端の
    後にしか出ない。
  - 同時刻の約定の並びは台帳の読み出し順（射影と同じ安定ソート）に従う。報告書の `PeriodLedgerTimeline` の並びと同時刻で違っても、
    期間開始時点の在庫の側は窓の外なので影響しない。
- **フォローアップ**
  - 計画への起票は不要と判断した（計画は期間開始時点の在庫の供給方法を定めておらず、本決定は計画の数値定義〔平均取得単価・税引後〕を満たす側の実装である）。

## 関連

- 関連要求 / UC: FR-06・FR-16・FR-11・UC-03〜05
- 関連する計画 ADR: ADR-0053（窓の下端）・ADR-0052（作り直し）
- 関連 IADR: IADR-0381（改定）・IADR-0492・IADR-0033・IADR-0301・IADR-0350・IADR-0427・IADR-0447・IADR-0480
- 作業仕様書: `.ai-context/specs/20261006_1181_report-opening-inventory.md`
