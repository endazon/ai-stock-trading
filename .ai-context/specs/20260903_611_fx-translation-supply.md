---
title: 為替差損益の供給——認識時 JPY/USD レートの記録と期末レート照会経路（#611）
type: spec
status: review
related_ids: [FR-06, FR-16, FR-10, FR-17, UC-05, ADR-0022, IADR-0107, IADR-0152, IADR-0194, IADR-0197, IADR-0250, IADR-0251, IADR-0271, IADR-0282]
author: endazon (with Claude Code)
created: 2026-09-03
updated: 2026-09-03
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0022_fx-rate-source-and-freshness.md
---

# 仕様書: 為替差損益の供給——認識時 JPY/USD レートの記録と期末レート照会経路（#611）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-06（報告書の階層管理）・FR-16（**数値はコード集計であり LLM に計算させない**）・
  FR-10 / FR-17（為替レート源・全体前提条件 §3）
- ユースケース（UC）: UC-05（日報・月報）
- 関連 ADR: ADR-0022（為替レート源＝日銀第一・FRED フォールバック・鮮度 5 日警告／30 日停止）
- 関連 IADR: [IADR-0271](../adr/IADR-0271_report-three-way-uptime-supply-and-fx-translation-blocked.md) 決定4（**推定で埋めない**・
  必要な 2 変更の名指し）・[IADR-0250](../adr/IADR-0250_report-supply-seam-nullable-view-properties.md)（未供給と 0 を潰さない継ぎ目）・
  [IADR-0251](../adr/IADR-0251_report-numeric-aggregation-outside-llm-context.md)（集計は Domain の純関数）・
  [IADR-0107](../adr/IADR-0107_base-currency-conversion.md) 決定2（承認時点のレートを台帳へ固定＝約定時レートの近似）・
  [IADR-0152](../adr/IADR-0152_usd-base-currency-migration.md)（基準通貨 USD・表示通貨 JPY）・
  [IADR-0194](../adr/IADR-0194_boj-fx-rate-source-and-ordered-fallback.md) 決定1（**3 つ目の利用者が現れたら抽出する**）・
  [IADR-0197](../adr/IADR-0197_fx-stale-exit.md)（鮮度切れでも値は返す）
- 本作業の実装ADR: [IADR-0282](../adr/IADR-0282_fx-translation-supply-recognition-rate-and-period-end-rate.md)
- 計画書リンク: `04_report-templates.md` §数値の定義（為替差損益・円換算）・日報 §1・月報 §1、
  `05_trading-assumptions.md` §3（基準通貨〔判定〕USD・〔表示〕JPY・**為替評価方法＝実現損益は約定時レート／評価損益は日次終値**）

## 目的・背景

PR #610 / IADR-0271 決定4 が「為替差損益は供給不能」と確定し、必要な変更を 2 点に名指しした:

1. **認識時の JPY/USD レートを承認台帳へ persist する**（基準通貨市場＝米国株の注文でも解決が要る）
2. **期末レートの照会経路を作る**（どのサービスも公開していない）

本作業はこの 2 点を実装し、`FxTranslationSummary`（描画・集計は #338 で実装済み）へ結線する。
**既存データは推定で埋めない**（IADR-0271 決定4・issue の「決まっていること」）。

### 何が無いのか（再確認・ファイル:行）

- `Shared.Contracts/Ports/IFxRateSource.cs` —— `FxRateToBase` は**ローカル通貨 → 基準通貨（USD）**の軸であり、
  基準通貨市場（米国株）では契約上**外部照会せず必ず 1** を返す。したがって `approved_orders.FxRateToBase` は
  米国株の約定について**円の情報を 1 ビットも持たない**。日本株の約定は `1 / FxRateToBase` で円/ドルを復元できるが、
  日本株は**円建て**であり円換算による差損益を生まない（計画 §数値の定義「円換算 | **米国株は**前提条件の為替評価方法に従い円換算」）。
- `ReportService/Domain/FxTranslationSummary.cs:19-22` —— 集計入力は「USD 建て金額 × 認識時／期末の **1 USD あたりの円**」。
- `TradeDecisionService` は HTTP 面を持たず、`FxRateSourceUsed` はレート値を運ばない（IADR-0271 決定4 のとおり）。

## 対象範囲

- 対象:
  - リスク管理: 承認台帳 `approved_orders` に **`FxRateBaseToDisplay`**（1 USD あたりの円・nullable）を追加し、
    **承認記録時にリスク管理サービス自身が為替レート源から解決して固定**する（3 つの承認ハンドラすべて）。
    `GET /risk-controls/fills` の応答（`LedgerFill`）へ同列を載せる。
  - 共有基盤: 為替レート源のアダプタ群（日銀・FRED・フォールバック・鮮度装飾・no-op・factory・構成・通知ポート）を
    `TradeDecisionService` から `AiStockTrading.Shared.Infrastructure/Composable/Adapters/Fx/` へ**移設**する
    （利用者が 3 つ〔判断・リスク・報告〕になったため。IADR-0194 決定1 が定めた抽出条件）。
  - 報告書: 期末レートの供給ポート `IPeriodEndFxRateSource`（既存 `IFxRateSource` を同じ factory で組み、
    JPY の読みの**逆数**を「1 USD あたりの円」として返す）と、純関数 `FxTranslationBuilder`
    （期間の USD 建て約定を畳み込み `FxTranslationEntry` 列を組み立てる）を新設し、`ReportDraftService` で集計・`ReportView` へ結線する。
  - Helm: `report` / `risk-management` に `Fx__Provider` / `Fx__Fred__ApiKey` を追加（既定 `""`＝no-op。`values-local` は `fred`）。
- 対象外:
  - **既存行の遡及**（推定で埋めない）。認識時レートが未記録の USD 建て約定を含む期間は**未供給**のまま、未記録件数を明記する。
  - USD 現金残高（入金・出金の外貨換算）に対する為替差損益。台帳は約定しか持たず、資金の取得レートを知らない。
  - 週報への為替差損益行の追加（計画が週報に求めていない。既存の描画どおり）。
  - `OrderIntent`（共有契約）の変更。本作業では**変更しない**（下記「設計」決定1）。

## 母集合の引き直し（規則 5・6・8。軸ごとの件数と除外理由）

> 走査は `git grep -ln`（**追跡下のみ・パス除外のみ**）で行い、`--include`・行フィルタで絞っていない。
> 数は**本仕様書を書く前**に取った。本仕様書自身が母集合へ入る軸は自己参照を引き算して示す（規則 8）。

| # | 軸（検索語） | 生の件数 | 採用 | 除外と理由 |
| --- | --- | --- | --- | --- |
| 1 | `FxTranslation` | 12 ファイル（`backend` 7 ＋ `.ai-context` 5） | `backend` 7（Domain 2・Draft 1・Renderer 1・View 1・テスト 2）＋ゴールデン `*-supplied.md` 2 | `.ai-context` 5（凍結記録） |
| 2 | 為替レート源の型名（`BojFxRateSource` / `FredFxRateSource` / `FallbackFxRateSource` / `CachingFxRateSource` / `NoOpFxRateSource` / `FxRateSourceFactory` / `FxOptions` / `IFxSourceStatusNotifier` / `NoOpFxSourceStatusNotifier`） | 24 ファイル | 移設 9（本体 8 ＋通知ポート 1）・追随 7（`TradeDecisionAppService` / `PublishingFxSourceStatusNotifier` / `Program.cs` / `FxWiringTests` / `TradeDecisionServiceTests` / `CurrentPriceProviderSelectionTests` / `PublishingFxSourceStatusNotifierTests`）・移設テスト 5（`Boj` / `Fred` / `Fallback` / `Caching` / `Factory`） | `Shared.Contracts` 3（コメント内の言及のみ。契約は変えない）・`BaseCurrencyOnlyFxRateProvider`（コメント内の言及のみ）・`FxCalendarIndependenceTests`（判断サービスの `MarketCalendar` を参照するため**移設せず**判断サービスに残し、using だけ追随） |
| 3 | `LedgerFill` | 19 ファイル（`backend`） | Risk 側 4（型・EF ストア・InMemory ストア・`PortfolioProjection` は無変更）・Report 側 1（`HttpPeriodFillSource` の DTO） | テスト（位置引数の末尾に省略可能引数を足すため追随不要） |
| 4 | `PeriodTradeFill` | 18 ファイル（`backend`） | Report 側 2（型・DTO 写像）＋新設 `FxTranslationBuilder` | テスト 15（末尾省略可能引数で追随不要。新設テストは追加） |
| 5 | `AppendApproval(`（署名変更の追随先） | 24 ファイル・本番 6 箇所・テスト 37 箇所 | 宣言 3（インターフェース・EF・InMemory）＋呼び出し 3（承認ハンドラ 3 本） | テスト 37 箇所は**省略可能引数（既定 `null`＝未記録）で追随不要** |
| 6 | 既存 EF マイグレーション（**1 バイトも変えない**対象） | `Migrations/*.cs` 45 本（実体 44 ＋ `ModelSnapshot` 1） | 追加のみ 1 本（`AddApprovedOrderFxRateBaseToDisplay`） | 既存 44 本は変更しない。`ModelSnapshot` は列追加の追記のみ |
| 7 | `Fx__Provider`（Helm・docs の設定点） | 4 ファイル（`deploy` 2 ＋ `docs` 2） | `values.yaml` / `values-local.yaml`（report・risk-management へ追加）・chart README | `docs/operations/operations.md`（trade-decision の症状記述。変更不要） |

> **自己参照の引き算（規則 8）**: 本仕様書は走査時点で未追跡のため母集合に入っていない。コミット後に軸 1・2・3・4 は
> 本仕様書と IADR-0282 のぶん **+2** される（例: 軸 1 は 12 → 14）。**値はコミットで固定する。**

**除外の総則**: `.ai-context/adr/` と `.ai-context/specs/` は凍結記録であり、本作業の是正対象に含めない。

## 設計

### 決定1: 認識時レートは**承認台帳の新列**へ、**リスク管理サービスが承認記録時に解決して固定**する（`OrderIntent` は変えない）

| 案 | 内容 | 判定 |
| --- | --- | --- |
| A | 既存 `FxRateToBase` を「認識時 JPY/USD」として読む | **不可**。軸が違う（ローカル→USD）。米国株では常に 1 で円の情報が無い。日本株は逆数で復元できるが、日本株は円建てで差損益を生まない |
| B | `OrderIntent` に列を足し、取引判断が意図生成時に載せる（issue の想定） | **採らない**。承認台帳へ届く Intent は取引判断由来だけではない——**保護逆指値レグ・保護喪失の手仕舞いレグは発注執行が再構成する**（`ProtectiveStopGuard` / `OrderExecutionAppService`）が、発注執行は為替レート源を持たない。共有契約を変えたうえで**機械執行の決済だけ未記録**になる |
| C | **承認記録の漏斗**（`OrderApprovedLedgerHandler` / `ProtectiveStopPlacedLedgerHandler` / `ProtectiveStopCoverageLostLedgerHandler` の 3 本＝`AppendApproval` の全呼び出し）で、リスク管理が自分の `IFxRateSource` から JPY の読みを引き、**逆数**を `approved_orders.FxRateBaseToDisplay` へ固定する（**採用**） | 承認は取引判断の直後・約定の直前であり、IADR-0107 決定2 が「承認時点の換算レート＝約定時レートの近似」と定めた既存の規律と同じ時点である。共有契約・イベント基準・監査レジストリに波及しない |

- 記録の規則: `GetReadingAsync(Currency.Jpy)` が `null`（源が無い・取得不可）なら **`null`（未記録）**。鮮度切れ
  （`Expired`＝30 日超）も **`null`**——統制が「新規建てに使えない」と判定した観測を認識時レートとして残すと、
  税務にも効く数値の根が推定になる。警告域（5〜30 日）は取引側と同じく採る（計画 §5「直近レートで続行」）。
- **承認記録は為替解決の失敗で止めない**（fail-safe）。例外は捕捉して `null`、取り消しだけ伝播する。
- 市場を問わず解決する（日本株の承認にも入る）。報告書の集計は USD 建て約定にしか使わないが、分岐を持たないほうが
  「どの承認に入るか」が読みやすい。日本株の `FxRateToBase` との厳密な一致は要求しない（同じ系列の同じ日の観測であり、
  照会の時刻差で日をまたぐことがあり得る。集計へは使わない）。
- 既存行は `null` のまま（**推定で埋めない**）。

### 決定2: 期末レートは「**期末日以前の直近の日次観測**」とし、源は既存 `IFxRateSource` を**同じ factory**で組む

- 計画 §3「為替評価方法: 実現損益＝約定時レート／評価損益＝**日次終値**」に従い、期末レートは期末日の日次観測である。
  日銀は「東京市場 17 時時点の仲値」・FRED は「NY 正午の買相場」で、いずれも **1 日 1 値の日次系列**であり、
  **収録は翌々営業日（日銀）／週次（FRED）**である（ADR-0022 決定1 の落とし穴 3・IADR-0112）。したがって
  生成時点で得られる最新観測は期末日**以前**の直近であり、これを期末レートとする。**観測日を報告書に明記する**。
- 採らない条件（**推定しない**）: 読みが `null`／鮮度 `Expired`／**観測日が期末日より後**（遅延生成で後日の観測を
  引いた場合。後日のレートは期末レートではない）。いずれも**未供給**へ倒す。
- 源: `FxRateSourceFactory`（日銀第一・FRED フォールバック・鮮度装飾）を **`Shared.Infrastructure` へ移設**し、
  報告書サービスとリスク管理サービスが判断サービスと同じ構成キー（`Fx:*`）で組む。HTTP 面は新設しない
  （判断サービスの状態は in-memory であり照会先として権威がない。IADR-0199 決定1 と同じ理由）。
- 移設先: `AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx`（`MarketData` と同じ樹形）。
  `PublishingFxSourceStatusNotifier`（Wolverine 発行）は判断サービスに残す。報告書・リスク管理は通知ポートを差さない
  （フォールバック・鮮度の可視化は取引経路が担う。IADR-0196 の責務は動かさない）。

### 決定3: 既存行の扱い——認識時レートが未記録の USD 建て約定が期間に 1 件でもあれば**節ごと未供給**とし、件数を明記する

- 畳み込み（決定4）は状態を持つため、未記録の約定を落として残りだけ集計すると**別の数値**になる（先の買いを落とすと
  後の売りが幻のショートになる）。部分集計は出さない。
- 🔴 **黙って落とさない**（IADR-0271 決定2 と同じ規律）。描画は
  「**供給されていません**（0 円ではありません。認識時レートが未記録の USD 建て約定 N 件）」とする。
  未記録が 0 件の既存の未供給描画（ゴールデン `*-unsupplied.md`）は**1 バイトも変えない**。

### 決定4: 集計の定義——期間の USD 建て約定を**符号付き在庫**で畳み込み、決済分は決済時レート・期末残は期末レートで再測定する

`FxTranslationAggregator.Aggregate`（Σ 金額 × (期末 − 認識)）は変えない。**何を明細にするか**を純関数 `FxTranslationBuilder` に置く。

- 対象: 市場通貨が**基準通貨（USD）かつ表示通貨（JPY）でない**約定（＝米国株）。日本株は円建てで円換算を要しない。
- 畳み込み（`SignedInventory` と同じ平均法・IADR-0033）: (銘柄, 市場) ごとに **平均取得単価（USD）と認識時レートの原価加重平均**を持ち、
  - 建て増し: 取得単価は加重平均、認識時レートは USD 原価で加重平均（等しいレートの平均は除算せずそのレート＝「等レートで 0」の不変条件を丸めで崩さない）。
  - 減少（決済）: 減少分の USD 原価を明細にする——`(±USD 原価, 加重平均認識時レート, 決済約定の認識時レート)`。
    ロングは +、ショートは −（USD 建て負債の再測定は符号が逆）。
  - 反転: 全量決済の明細を出してから余りを新規建てとする。
  - 期末: 残る建玉ごとに `(±USD 原価, 加重平均認識時レート, 期末レート)`。
- **期末レートが要るのは期末に建玉が残るときだけ**である。残らなければ期末レートが無くても集計できる（未供給へ倒さない）。
  USD 建て約定が 1 件も無ければ「0 円（明細 0 件）」——事実であり未供給ではない。
- 退けた案「**約定ごとに約定代金を明細にする**」: 期間内に同じ建玉を建てて決済すると**両脚を二重に数える**。
  例: $1,000 を 150 円で買い、$1,100 を 155 円で売り、期末 160 円 → 約定ごとでは 1,000×10 ＋ 1,100×5 ＝ **15,500 円**だが、
  決済で確定した為替差損益は 1,000×(155−150) ＝ **5,000 円**（＋$100 の利益の再測定 500 円）である。
- 既知の限界（`PnlAggregator` と同じ）: 報告書は**期間内の約定しか受け取らない**ため、前期間に建てた建玉の決済は
  在庫 0 からの反対売買として畳み込まれる。日報の為替差損益は当日の約定に閉じ、月報は当月の約定に閉じる。
  **円建て表示は参考値**（計画 §3）であり、統制の判定には用いない。

### 決定5: 描画——供給時は期末レートと観測日を併記する

`FxTranslationSummary` に `PeriodEndRate`（1 USD あたりの円）と `PeriodEndRateAsOf`（観測日）を**省略可能**で足し、
供給時のセルを「`-1,234 JPY（明細 5 件・期末レート 150.25 JPY/USD〔2026-08-28 観測〕）`」とする。期末レートを使わなかった
（期末に建玉が残らない）ときは従来の「`… JPY（明細 N 件）`」。**ゴールデンは `*-supplied.md` だけを更新する。**

### データモデル・API

- `approved_orders.FxRateBaseToDisplay numeric NULL`（EF マイグレーション 1 本・列追加のみ）。
- `LedgerFill.FxRateBaseToDisplay: decimal?`（末尾・既定 `null`）→ `GET /risk-controls/fills` の JSON に `fxRateBaseToDisplay` が載る。
  旧版 Risk の応答（キー無し）は `null` として読む。
- `IPortfolioLedgerStore.AppendApproval(decisionId, intent, approvedAt, decimal? fxRateBaseToDisplay = null)`。
- Risk 新設: `IRecognitionFxRateResolver`（Features）＋ `FxSourceRecognitionFxRateResolver`（Infrastructure/ExternalServices）。
- Report 新設: `PeriodEndFxRate(JpyPerUsd, AsOf)`・`IPeriodEndFxRateSource`・`FxRateSourcePeriodEndFxRateSource`・
  `UnsuppliedPeriodEndFxRateSource`・`FxTranslationBuilder`。`DraftRequest.FxTranslation` を `PeriodEndFxRate` へ置き換え
  （三者比較と同じく**集計は Draft の内側で行う**。IADR-0271 決定3 の形）。
- 構成情報 API: report / risk-management が `fx-rate` ポートを自己申告する（判断サービスと同じ `ResolveProvider`）。

## 受け入れ基準

- [ ] 承認記録時に JPY/USD の認識時レート（1 USD あたりの円）が `approved_orders` へ固定され、`GET /risk-controls/fills` で読める
- [ ] 為替レート源が無い・取得不可・鮮度切れ・例外のいずれでも**承認記録は止まらず**、列は `null`（未記録）になる
- [ ] 報告書（日報・月報）の「為替差損益（独立表示）」に、期間の USD 建て約定から集計した実値と明細数・期末レート・観測日が出る
- [ ] 期末レートの供給が落ちた（未注入・照会失敗・鮮度切れ・観測日が期末より後）とき、期末に建玉が残る期間は
  **「供給されていません（0 円ではありません）」へ戻る**（否定形）。対の肯定形を添える
- [ ] 認識時レートが未記録の USD 建て約定を含む期間は未供給とし、**未記録の件数を明記**する
- [ ] 日本株のみ／約定なしの期間は「0 JPY（明細 0 件）」（未供給と混同しない）
- [ ] レートが等しければ 0 になる不変条件（既存プロパティテスト）を畳み込み経由でも保つ
- [ ] ゴールデンは `*-supplied.md` のみ更新、`*-unsupplied.md` は不変
- [ ] 為替レート源の移設後、判断サービスの既存テスト（配線・鮮度・フォールバック）がそのまま緑
- [ ] `dotnet build` 警告ゼロ・`dotnet format --verify-no-changes`・検査器（trace-blocks / test-traceability / cross-repo-refs / adr-index-sync / doc-links）が exit 0
- [ ] EF: `dotnet ef migrations has-pending-model-changes` が変更なし。既存マイグレーションは 1 バイトも変えない

## テスト方針

| 対象 | テスト |
| --- | --- |
| Risk: 解決器 | 読み無し→null／`Expired`→null／`Warning`→採る／例外→null（取り消しは伝播）／逆数（150 円/ドル ← 1/150） |
| Risk: 承認ハンドラ | 解決した値が台帳へ固定される（Wolverine ハーネス）／解決器が投げても承認は記録される |
| Risk: EF ストア | 列の永続化と `GetFills` での復元／列追加前の行（null）は null のまま |
| Risk: 配線 | `Fx:Provider` 未設定→`NoOpFxRateSource`／`fred`＋鍵→`CachingFxRateSource`／introspection `fx-rate` |
| Report: `FxTranslationBuilder` | 米国株のみ対象／等レートで 0（プロパティ）／決済の明細／期末残の明細／反転／未記録→null＋件数／USD 約定なし→0 件／期末レート不要なら無くても集計／建玉が残り期末レート無し→未供給 |
| Report: 期末レート源 | 逆数／null→null／`Expired`→null／観測日 > 期末→null／観測日 = 期末→採る |
| Report: 自動生成 | 未注入→未供給（否定形）／照会が投げる→未供給（否定形）／供給→本文に実値（肯定形） |
| Report: 描画 | 期末レート併記のセル／未記録件数つきの未供給セル／ゴールデン（supplied のみ更新） |
| Report: 配線 | `Fx:Provider` 未設定→`UnsuppliedPeriodEndFxRateSource`／`fred`＋鍵→HTTP 実装 |
| Report: DTO | `fxRateBaseToDisplay` をそのまま通す／キー無し（旧版）は null |
| Shared.Infrastructure | 移設した 5 テストファイルを `Shared.Infrastructure.Tests/Fx/` で実行（内容は不変）＋ `FxBaseToDisplayRateTests`（逆数・鮮度の規則） |

## 計画書との差異

- 差異: なし。計画 §3 の「為替評価方法（実現損益＝約定時レート・評価損益＝日次終値）」を、承認時点の観測（約定時の近似・
  IADR-0107 決定2）と期末日以前の直近日次観測で実装する。**円建て表示は参考値**とする計画の位置づけは変えない。
- 計画へ環流する事項: 期末レートの「日次終値」が源の収録遅延により**期末日以前の直近観測**になることは実装上の解釈であり、
  計画の書き換えを要しない（ADR-0022 が既に収録遅延を記録している）。環流しない。

## 未決事項

- 実 FX 源（日銀 API・FRED）との疎通は実環境（Istio 断・API キー）の都合で本作業では未検証。単体は fake ハンドラで固定する。
- USD 現金残高に対する為替差損益（入出金の取得レート）は台帳が持たず、本作業の対象外（残余リスクとして IADR-0282 に記す）。
