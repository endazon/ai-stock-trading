---
title: 取引ガード（FR-19・再実装）テスト仕様書
type: test-spec
status: approved
related_ids: [FR-19, FR-10, FR-20, UC-06, ADR-0007, ADR-0009, ADR-0016, IADR-0131, IADR-0132]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
related_specs:
  - ../functional/FR-19_trading-guard.md
  - ../specs/20260804_332_trading-guards.md
  - ../adr/IADR-0132_product-type-tri-state-and-guard-scope.md
  - ./README.md
  - ./FR-19_manipulation-detection-tests.md
  - ./FR-10_risk-controls-tests.md
---

# テスト仕様書: 取引ガード（FR-19・再実装 #332）

> 全面再実装（[#344](https://github.com/endazon/ai-stock-trading/issues/344)）の
> [#332](https://github.com/endazon/ai-stock-trading/issues/332) が扱う取引ガードのテスト仕様である。
> **相場操縦パターン検知の写像表は [FR-19_manipulation-detection-tests](./FR-19_manipulation-detection-tests.md) を
> 引き続き正とし**、本書は再実装で確定した規則（商品種別の 3 値化・ガードの適用範囲・
> 差金決済ガードの日本株現物限定・禁止銘柄の照合規則）を扱う。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-19**（取引ガード）／ FR-10（空売り統制との接続）・FR-20（段階別の商品種別＝ #333）は境界
- ユースケース（UC）: UC-06（ガード設定の変更・強制）
- 関連 ADR: **ADR-0016**（決定1＝ 3 値化・決定8＝段階解禁・決定13＝空売りは米国株のみ）・
  **ADR-0007**（ソフト設定・禁止銘柄）・ADR-0009（手仕舞い不停止）
- 受け入れ基準の所在: `02_requirements/01_requirements.md` FR-19 本文・受け入れ基準（103 行）／
  `05_trading-assumptions.md` §5（商品種別・差金決済防止・禁止銘柄・発注パターン・米国口座の種別）

## テスト対象・範囲

| 対象 | テストプロジェクト | ファイル |
| --- | --- | --- |
| 商品種別 3 値・実効値の解決・ガードの適用範囲 | `RiskManagementService.Domain.Tests` | `TradingGuardProductTypeTests.cs` |
| 差金決済ガードの適用範囲（日本株現物） | `RiskManagementService.Domain.Tests` | `TradingGuardProductTypeTests.cs` / `RiskEvaluatorTests.cs` |
| 約定到達後に差金決済ガードが拘束すること（#270 回帰） | `RiskManagementService.Infrastructure.Tests` | `MoomooFillControlRegressionTests.cs` |
| 禁止銘柄の登録内容と照合規則 | `RiskManagementService.Domain.Tests` | `TradingGuardProductTypeTests.cs` |
| 相場操縦パターン禁止（既定・クラス C） | `RiskManagementService.Domain.Tests` / `…Application.Tests` | `TradingGuardProductTypeTests.cs` / `Manipulation/*` |
| 空売り統制との接続（有効化しても統制は解除されない） | `RiskManagementService.Domain.Tests` | `TradingGuardProductTypeTests.cs` / `ShortSellingControlsTests.cs` |
| 既定値の計画適合（`ProductType.Values` ほか） | `AiStockTrading.PlanConformance.Tests` | `PlanRiskDefaults.cs` / `ActualDefaults.cs` |
| 画面（SC-02）の商品種別 3 値・危険な緩和の確認 | `frontend`（vitest） | `risk/contracts.test.ts` / `sc02-risk-settings/RiskSettingsPage.guard.test.tsx` |

対象外（担当 issue）: 段階別の商品種別強制（#333）・発注先の 2 軸分離（#334）・
相場操縦しきい値の較正（#251）・空売り文脈の供給元（#342）・信用買いの建玉表現（未起票）。

## 3 点セット（テスト戦略 §2）

### 1. 境界値／組み合わせ表

| ID | 観点 | 入力 | 期待 | テスト |
| --- | --- | --- | --- | --- |
| T-19-201 | 商品種別 × 有効/無効 × 市場（**12 通り**） | 3 値 × {有効, 無効} × {日本, 米国} | 有効なら `ProductTypeDisabled` を含まない／無効なら必ず含む | `商品種別3値と有効無効と市場の組み合わせで商品種別ガードが決まる` |
| T-19-202 | **既定＝現物のみ有効** | `TradingDefaults.CreateGuardSettings()` | `{ Cash }`（`MarginLong` / `ShortSell` を含まない） | `既定の有効な商品種別は現物のみである` |
| T-19-203 | 3 値の**独立性**（部分集合 8 通り） | 空集合〜全集合 | 種別 T が拒否されないのは T ∈ 有効集合のときに限る | `商品種別の有効化は互いに独立である` |
| T-19-204 | 差金決済の適用範囲（市場 × 種別の全組み合わせ） | 同日取引済み × 2 市場 × 3 種別 | `SameDayReentry` は**日本株 × 現物**のときだけ | `差金決済ガードは日本株現物の新規建てにのみ作動する` |
| T-19-205 | 計画登録の禁止銘柄 3 件 | 6457 / 6902 / 6502（日本株） | 拒否され、理由・登録日（2026-07-07）を持つ | `計画が登録した禁止銘柄は発注前に拒否される` |
| T-19-206 | 既定値の計画適合 | `ProductType.Values` | `Cash, MarginLong, ShortSell` | `PlanConformanceTests`（既知逸脱レジストリ） |

### 2. プロパティベース（入力によらず成り立つ不変条件）

| ID | 不変条件 | 反証の意味 | テスト |
| --- | --- | --- | --- |
| T-19-211 | **無効な商品種別は、どの市場・どの金額・どの数量でも通らない**（2 種別 × 2 市場 × 4 価格 × 3 数量） | 金額を小さくすれば無効な種別が通る＝統制が金額に依存して漏れる | `無効な商品種別はどの市場でもどの金額でも通らない` |
| T-19-212 | **新規売り建ての実効商品種別は申告値によらず空売り**（申告 3 通り） | 申告を変えれば空売り統制を外せる | `新規売り建ての実効商品種別は申告値によらず空売りである` |
| T-19-213 | 新規売り建て**以外**の実効商品種別は申告どおり（過剰な読み替えをしない） | 手仕舞いや買いの種別が勝手に書き換わる | `新規売り建て以外の実効商品種別は申告どおりである` |
| T-19-214 | 商品種別の有効化は互いに独立（T-19-203 と同一の性質を部分集合全体で） | 1 つ有効化すると他も通る（束ね制御） | `商品種別の有効化は互いに独立である` |

### 3. 否定形（統制を迂回できないこと）

**否定形は「拒否されること」ではなく「迂回経路が塞がれていること」を見る。**

| ID | 塞ぐ迂回 | 手口 | 期待 | テスト |
| --- | --- | --- | --- | --- |
| T-19-221 | **差金決済ガードの誤適用**（本 issue の必須要請） | 米国株で同日再エントリー | `SameDayReentry` を含まず**承認**される | `差金決済ガードは米国株では作動しない` |
| T-19-222 | 誤適用の是正で**取りこぼさない**（正の対照） | 日本株現物で同日再エントリー | 拒否され `SameDayReentry` | `差金決済ガードは日本株現物では作動する` |
| T-19-223 | 米国株の回転が**無統制**になっていない | 日次発注枠を使い切った状態で同日再エントリー | `DailyOrderAmountExceeded` で拒否 | `米国株の回転数は日次発注金額上限で管理される` |
| T-19-224 | **申告値の詐称** | 新規売り建てを `Cash` と申告 | `ProductTypeDisabled` ＋ `ShortSellDisabled` | `空売りを現物と申告してもガードを迂回できない` |
| T-19-225 | **別表記**（禁止銘柄） | `aapl` / `AAPL␣` / `␣Aapl` | いずれも `BannedSymbol` | `禁止銘柄は表記差で迂回できない` |
| T-19-226 | **商品種別の付け替え**（禁止銘柄） | 6457 を現物 / 信用買い / 空売りで発注 | いずれも `BannedSymbol` | `禁止銘柄は商品種別を変えても迂回できない` |
| T-19-227 | **統制違反の計上汚染** | 商品種別ガードの拒否 | クラス B（計上しない）／禁止銘柄はクラス C（計上する） | `商品種別ガードの拒否は統制違反に計上されない` |
| T-19-228 | **手仕舞いの封鎖**（逆向きの事故） | 無効な種別（空売り・信用買い）の Close | **承認**される（ADR-0009） | `無効な商品種別でも手仕舞いは止めない` |
| T-19-229 | **設定の緩和で統制ごと解除** | 商品種別で空売りを有効化 | `ShortSellDisabled` は消えるが `BorrowUnavailable`（フェイルクローズ）で止まる | `空売りを有効化しても専用統制は解除されない` |
| T-19-230 | **二重の情報源** | 空売り統制値だけを緩める | 有効・無効は変わらない（`Guard.EnabledProductTypes` が単一情報源） | `空売りの有効無効は取引ガードの商品種別だけで決まる` |
| T-19-231 | 画面からの**危険な緩和**の素通し | SC-02 で信用買い／空売りを有効化 | 確認チェックなしでは保存不可・警告に該当種別が出る | `treats enabling margin-long / short-selling as a dangerous change…`（vitest） |

## 計画確定値との適合検査（IADR-0127）

`ProductType.Values` の既知逸脱（担当 #332）を解消し、レジストリから削除した。
**赤→緑の実測**（IADR-0127 の機械的証明）:

| 段階 | 結果 |
| --- | --- |
| 実装を 3 値へ一致させ、登録行を残したまま実行 | **Failed: 2, Passed: 4**（検査3「登録済み逸脱は実際に逸脱している」・検査4「登録済み逸脱の現行値は実装の実際値と一致する」が `ProductType.Values` を名指し） |
| 登録行を削除して実行 | **Failed: 0, Passed: 6** |

なお `Guard.EnabledProductTypes`（既定＝ `Cash`）・`Guard.BannedSymbols`（6457/6502/6902）・
`Guard.PreventSameDayReentry`（True）・`Guard.ProhibitManipulativeOrderPatterns`（True）は
**逸脱していなかった**ため、レジストリに登録が無く、`PlanConformanceTests` が常時突き合わせている。

## テストデータ

- equity: `TradingDefaults.InitialCapital`（＝ $3,000 × 163.7 の円建て換算値。IADR-0130 決定3）
- 銘柄: `AAPL`（米国株）／ `7203`（日本株・禁止リスト外）／ `6457`・`6902`・`6502`（計画登録の禁止銘柄）
- 空売りの成立条件は米国株・株価 $5.00 以上・逆指値つき（ADR-0016 決定7・決定2(b)）

## 未カバー・実施予定

| 項目 | 理由 / 担当 |
| --- | --- |
| 段階別の商品種別強制（Stage 2＝現物のみ） | #333（本ガードの有効集合と AND で効かせる） |
| 相場操縦検知のしきい値較正 | #251（既存の検知アルゴリズムのテストは `Manipulation/*` が担う） |
| 信用買いの建玉・金利・必要証拠金 | 未起票（実弾解禁は Stage 3） |
| 禁止銘柄・市場ガードの手仕舞い適用の是非 | 計画側の裁定待ち（作業仕様書 #332 未決事項 1） |

## 関連仕様

- 機能仕様書: [FR-19 取引ガード](../functional/FR-19_trading-guard.md)・[FR-10 リスク統制](../functional/FR-10_risk-controls.md)
- 作業仕様書: [20260804_332_trading-guards](../specs/20260804_332_trading-guards.md)
- 実装 ADR: [IADR-0132](../adr/IADR-0132_product-type-tri-state-and-guard-scope.md)・
  [IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)
- テスト仕様書: [FR-19 相場操縦パターン検知](./FR-19_manipulation-detection-tests.md)・
  [FR-10 リスク統制（再実装）](./FR-10_risk-controls-tests.md)・[FR-10 リスクガードコア](./FR-10_risk-guard-core-tests.md)

## 未決事項

- 差金決済ガードの適用範囲を計画本文（FR-19）が「日本株の現物取引」と明記している一方、
  06_daytrading-review §2.2 には「日本の差金決済禁止は moomoo の米国株現物にも適用される」という
  2026-07 時点の調査記述が残る。**口座種別の裁定（2026-07-31・project-planning#81）と FR-19 の
  2026-08-01 改訂が新しく、実装はそちらに従った**。§2.2 の記述の更新要否は計画側の判断に委ねる。
  → 2026-08-04 に計画へ環流した（[feedback/20260804_fr19-guard-scope.md](../../feedback/20260804_fr19-guard-scope.md)
  論点 3。**裁定待ち**）。あわせて論点 1（商品種別ガードの Open 限定の明示化）・論点 2（禁止銘柄ガードの
  Close 適用の裁定）も同文書で環流している。
