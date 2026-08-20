---
title: 作業仕様書 — 取引ガードの再実装（商品種別 3 値化・差金決済ガードの日本株現物限定・禁止銘柄）
type: work
status: review
related_ids: [FR-19, FR-10, FR-11, FR-20, UC-06, ADR-0007, ADR-0009, ADR-0016, IADR-0130, IADR-0131, IADR-0132]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
related_specs:
  - ../adr/IADR-0132_product-type-tri-state-and-guard-scope.md
  - ../adr/IADR-0131_short-selling-controls-fail-closed.md
  - ../../docs/functional/FR-19_trading-guard.md
  - ../../docs/tests/FR-19_trading-guards-tests.md
  - ../specs/20260804_329_short-selling-controls.md
  - ../../docs/tests/README.md
  - ../../docs/DEFINITION_OF_DONE.md
---

# 作業仕様書: 取引ガードの再実装（#332）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-19**（取引ガード）／ FR-10（空売り統制との接続）・FR-11（監査）・FR-20（段階別の商品種別＝ #333）は境界
- ユースケース（UC）: **UC-06**（設定変更・統制の強制）
- 関連 ADR: **ADR-0016**（決定1＝商品種別の 3 値化・決定8＝段階解禁・決定13＝空売りは米国株のみ）／
  **ADR-0007**（取引ガードのソフト設定・禁止銘柄。§決定の商品種別は ADR-0016 が部分改定）／
  ADR-0009（手仕舞い・損切りは止めない）
- 実装 ADR: [IADR-0132](../adr/IADR-0132_product-type-tri-state-and-guard-scope.md)（本作業）／
  [IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)（#329 第 2 段階）／
  [IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)（既知逸脱レジストリ）
- 起点 issue: [#332](https://github.com/endazon/ai-stock-trading/issues/332)（親: [#344](https://github.com/endazon/ai-stock-trading/issues/344)）
- 計画書リンク: 02_requirements FR-19（計画リポ） ／
  05_trading-assumptions §5（計画リポ） ／
  06_daytrading-review §2（計画リポ） ／
  ADR-0016（計画リポ）

## 目的・背景

[#329](https://github.com/endazon/ai-stock-trading/issues/329) は空売り専用統制（FR-10）を発注前の決定的コードへ載せたが、
**空売りの有効・無効は `ShortSellSettings.Enabled` という専用フラグで持ったまま**であった。
`ProductType` が `Cash, Margin` の 2 値であり、3 値化が本 issue の担当だったためである
（[IADR-0131 §結果](../adr/IADR-0131_short-selling-controls-fail-closed.md)・
[作業仕様書 #329 第 2 段階 未決事項 4](./20260804_329_short-selling-controls.md)）。

本 issue は商品種別を計画の 3 値（**現物 / 信用買い / 空売り**）へ改め、有効・無効の単一情報源を
取引ガード（`Guard.EnabledProductTypes`）へ統合する。あわせて、差金決済防止ガードが**日本株の現物取引に
限られる**こと（米国株は信用口座で運用するため Good Faith Violation が発生しない）を実装へ反映する。

## 対象範囲

### 対象

1. **商品種別の 3 値化**（`ProductType` = `Cash` / `MarginLong` / `ShortSell`。ADR-0016 決定1）と
   既定「現物のみ有効」の固定
2. **`ShortSellSettings.Enabled` の統合**（有効・無効の単一情報源を `Guard.EnabledProductTypes` へ）
3. **商品種別ガードの適用範囲**（新規建てのみ。手仕舞い・損切りは止めない。ADR-0009）
4. **差金決済防止ガードの日本株現物限定**（FR-19 本文・§5「米国口座の種別・決済」）
5. 禁止銘柄リスト・発注パターン禁止の**計画との突合と退行防止テスト**（実装は既存）
6. 逸脱レジストリ `ProductType.Values` の削除（赤→緑の実測）
7. 画面（SC-02）の商品種別選択肢 3 値化と「危険な緩和」判定の追随

### 対象外（担当を明記）

| 項目 | 担当 |
| --- | --- |
| **段階別**の商品種別強制（Stage 1＝3 種／Stage 2＝現物のみ／Stage 3＝条件付き解禁） | [#333](https://github.com/endazon/ai-stock-trading/issues/333)（本 issue は**ガード機構**、段階側が許可判定の供給元） |
| 発注先（Broker Provider）の 3 値と段階との 2 軸分離 | [#334](https://github.com/endazon/ai-stock-trading/issues/334) |
| 相場操縦検知の**しきい値較正** | [#251](https://github.com/endazon/ai-stock-trading/issues/251)（IADR-0040 の初期値は据え置き） |
| 借株照会・維持率・権利確定日など空売り文脈の**供給元** | [#342](https://github.com/endazon/ai-stock-trading/issues/342) |
| 信用買い（`MarginLong`）の**建玉・金利・必要証拠金の扱い** | 未起票（本書「未決事項」§3） |

## 計画書との突合（原文で確認した値・規則のみ）

**実装が発明した値・規則は 1 つも無い。**

| # | 項目 | 計画の確定値 | 出典（原文） |
| --- | --- | --- | --- |
| 1 | 商品種別 | **現物 / 信用買い / 空売り の 3 値。それぞれ独立に有効・無効を設定できる** | FR-19 本文／ ADR-0016 決定1／ §5 表「取引可能な商品種別」 |
| 2 | 既定 | **いずれも「現物のみ有効」** | FR-19 本文／ ADR-0016 決定1 |
| 3 | 名称 | `Cash, MarginLong, ShortSell` | `PlanRiskDefaults`「ProductType.Values」（計画適合検査の期待値） |
| 4 | 差金決済防止 | **同一銘柄の同日再エントリー禁止（現物）**。本ガードは**日本の差金決済規制**（金商法 161 条の 2）向け | §5 表「差金決済防止」／ FR-19 本文／ 06_daytrading-review §2.1 |
| 5 | 米国株 | **信用口座（margin account）**であり **GFV は発生しない**。決済制度による回転数の上限は無く、回転数は**日次発注金額上限・保有建玉数上限**で管理する | §5 表「米国口座の種別・決済」／ FR-19 本文 |
| 6 | 取引禁止銘柄 | **グローリー（6457）・デンソー（6902）・東芝（旧 6502。2023 年上場廃止中のため再上場時に適用）**。発注前に強制し**理由と登録日を記録** | §5 表「取引禁止銘柄リスト」／ INDEX 決定 20 |
| 7 | 発注パターン禁止 | **約定意思のない発注・板演出・過剰な注文訂正/取消** の禁止 | §5 表「発注パターン禁止」／ 06_daytrading-review §2.3 |
| 8 | 拒否理由のクラス | 「統制違反 0 件」は**クラス C 限定**（`BannedSymbol` / `ManipulativeOrderPattern`） | ADR-0016 決定10／ project-planning#58 の裁定 |
| 9 | 空売りの対象市場 | **米国株のみ** | §5 表「空売りの対象市場」／ ADR-0016 決定13 |
| 10 | 段階別の商品種別 | Stage 1＝3 種すべて／**Stage 2＝現物のみ**／Stage 3＝条件付き解禁 | FR-20 本文／ ADR-0016 決定8（**強制は #333**） |
| 11 | ガード設定の変更 | **利用者のみ**が行え、**変更履歴を記録**する。生成 AI は上書きできない | ADR-0007 §決定（既存実装。本 issue で変更しない） |

## 設計

### 1. 商品種別の 3 値化（IADR-0132 決定1）

```
enum ProductType { Cash = 0, MarginLong = 1, ShortSell = 2 }
```

- 序数を保つ（旧 `Margin = 1` → `MarginLong = 1`）。設定は JSON へ**数値**で永続化されており
  （`RiskSettingsSerialization` は `JsonSerializerDefaults.Web`＝数値 enum）、画面も数値で送受信する
  （IADR-0086 決定4）。序数を変えると**既存行の「有効な商品種別」が別の意味に化ける**。
- 既定は `{ Cash }` のまま（計画の既定「現物のみ有効」）。3 値それぞれ独立に集合へ入れられる。

### 2. 有効・無効の単一情報源（IADR-0132 決定2）

`ShortSellSettings.Enabled` を**削除**し、空売りの有効・無効を `Guard.EnabledProductTypes.Contains(ShortSell)` から
導出する。`ShortSellSettings` は**統制値（`Limits`）のみ**を持つ。2 箇所に分かれた有効・無効は必ず食い違う。

### 3. 実効商品種別（IADR-0132 決定3）

ガードが見るのは**申告値そのものではなく実効値**である。

| 注文 | 実効商品種別 |
| --- | --- |
| `Side == Sell` かつ `PositionEffect == Open`（新規売り建て） | **`ShortSell`**（申告値に関わらず） |
| それ以外 | 申告どおり |

新規売り建ての識別は IADR-0131 決定1 と同じ（`ShortSellEvaluator.IsShortEntry`）。上流（AI）が
`ProductType.Cash` と申告しても空売りは空売りとして扱う。**申告値を信じると、商品種別ガードは
「AI の自己申告で解除できるガード」になる。**

### 4. 商品種別ガードの適用範囲（IADR-0132 決定4）

**新規建て（`PositionEffect.Open`）にのみ適用する。** 3 値化により、無効な商品種別の建玉を
**手仕舞えなくなる**経路が実在するようになったためである（既定では空売りが無効であり、
空売り建玉の買戻し＝ `Buy × Close × ShortSell` が `ProductTypeDisabled` で拒否される）。
損失に上限が無い建玉を閉じられないことは、FR-10 の不変条件
「いずれも手仕舞い（Close）と損切りは止めない」（ADR-0009）に真っ向から反する。

### 5. 差金決済防止の適用範囲（IADR-0132 決定5）

```
isEntry && PreventSameDayReentry
        && intent.Market == Market.Japan          // 日本の差金決済規制（金商法 161 条の 2）
        && 実効商品種別 == ProductType.Cash        // 現物のみ（信用は同一保証金で同日無制限回転が可能）
        && SymbolsTradedToday.Contains((Symbol, Market))
```

米国株へ適用しないのは、**米国口座が信用口座であり GFV が発生しない**ためである（§5・FR-19）。
回転数は日次発注金額上限（equity の 150%/日）と保有建玉数上限（3）で管理する。

### 6. #333（段階ゲート）への接続点

段階側は「その段階で許可される商品種別の集合」を供給し、ガード側の
`Guard.EnabledProductTypes` と**AND** で効かせればよい（厳しい方が効く）。本 issue は
判定の入口（`ProductTypeResolver.Resolve` ＋ `EnabledProductTypes` 照合）を 1 箇所に閉じるところまでを行い、
段階別の許可集合そのものは定義しない（計画に無い値を実装側で決めないため）。

### 変更するファイル

| 層 | ファイル | 変更 |
| --- | --- | --- |
| Shared.Contracts | `Trading/ProductType.cs` | **3 値化**（`Cash` / `MarginLong` / `ShortSell`） |
| Domain | `ProductTypeResolver.cs` | **新規**（実効商品種別の解決。申告値に依存しない） |
| Domain | `RiskEvaluator.cs` | 実効商品種別での照合・適用範囲（Open 限定）・差金決済の日本株現物限定・空売り有効判定の導出 |
| Domain | `ShortSellSettings.cs` | `Enabled` を削除（統制値のみ） |
| Domain | `ShortSellEvaluator.cs` | 有効・無効を引数で受け取る（設定の単一情報源はガード側） |
| Domain | `TradingGuardSettings.cs` / `TradingDefaults.cs` | 3 値の説明・既定（現物のみ）の明示 |
| Infrastructure | `RiskSettingsSerialization.cs` | **変更なし**（`ShortSellSettings` 全体を往復するため `Enabled` 削除でも成立する。旧行の `enabled` は無視され、可否は `EnabledProductTypes` が決める＝安全側） |
| Frontend | `features/risk/contracts.ts` / `sc02-risk-settings/RiskSettingsPage.tsx` | 選択肢 3 値・危険な緩和の判定を信用買い/空売りの双方へ |
| Tests | `TradingGuardProductTypeTests.cs` | **新規**（3 点セット: 組み合わせ表・プロパティ・否定形） |
| Tests | `KnownPlanDeviations.cs` | **逸脱 1 行の削除**（`ProductType.Values`） |

## 受け入れ基準

計画書（02_requirements 受け入れ基準・FR-19 本文）から本 issue が満たすものを転記する。

- [x] 取引ガードに反する注文（**禁止銘柄・無効化された商品種別・差金決済該当**）が発注段階で拒否され、記録される
- [x] 商品種別が **3 値**であり、**それぞれ独立に**有効・無効を設定できる
- [x] 既定が「**現物のみ有効**」である
- [x] 差金決済防止ガードが**日本株の現物取引にのみ**適用され、**米国株には適用されない**
- [x] 禁止銘柄・差金決済は（銘柄, 市場）で照合し、別市場の同一コードを誤拒否しない
- [x] 禁止銘柄が**理由と登録日**を伴って登録されている（6457 / 6902 / 6502）
- [x] 禁止銘柄・相場操縦パターンの拒否が**クラス C**であり、他の拒否理由がクラス C に混入しない
- [x] **手仕舞い（Close）・損切りが商品種別ガードで止まらない**（ADR-0009）
- [ ] 段階別の商品種別強制（Stage 2 は現物のみ）… **#333**

## テスト方針

[テスト戦略](../../docs/tests/README.md) §2 の 3 点セットで写像する。詳細は
[テスト仕様書 FR-19](../../docs/tests/FR-19_trading-guards-tests.md)。
**否定形は「拒否されること」ではなく「迂回経路が塞がれていること」**を見る
（申告値の詐称・別市場・別表記・手仕舞い偽装・設定の緩和）。

### 計画適合検査の赤→緑（IADR-0127 の機械的証明）

| 段階 | 結果 |
| --- | --- |
| 実装を計画へ一致させ、登録 1 行を残したまま実行 | **Failed: 2, Passed: 4** |
| 登録 1 行を削除して実行 | **Failed: 0, Passed: 6** |

## 計画書との差異

- 差異: **あり**（2 件・いずれも安全側かつ計画の不変条件に従うもの）
  1. **商品種別ガードを新規建てに限定した**（IADR-0132 決定4）。FR-19 は適用範囲を明記していないが、
     全注文へ適用すると無効な商品種別の建玉を手仕舞えず、FR-10 の不変条件
     「手仕舞い（Close）と損切りは止めない」（ADR-0009）に反する。**3 値化により実在する経路になった**
     （既定で空売りが無効＝空売り建玉の買戻しが拒否される）ため、本 issue で是正した。
  2. **実効商品種別で照合する**（IADR-0132 決定3）。計画は「注文の商品種別」を照合対象としか書かないが、
     申告値をそのまま信じると新規売り建てを `Cash` と申告してガードを迂回できる。IADR-0131 決定1
     （空売りの識別は `Side` × `PositionEffect`）と同じ規律を商品種別ガードへ適用した。

いずれも**計画の値・規則を変更するものではない**（実装が推論で埋めた適用範囲である）。
**差異 1 は監査の判断により計画へ環流した**（2026-08-04・
feedback/20260804_fr19-guard-scope.md（環流記録） 論点 1。
**【✅ 裁定済み 2026-08-04・ADR-0007 追補（質問票 第 1 回 Q3・Q4・project-planning#179）】** **商品種別＝新規建て（Open）のみで確定**。実装は現行のままでよい。#380）。
FR-19 が各ガードの適用範囲（Open / Close）を明記していないため、実装側の推論に依存させず
計画で定めることを求めている（#333 の段階別強制で**段階を差し戻したときに既存建玉を手仕舞えなくなる**
同型の事故が起こり得るため）。差異 2 は IADR-0131 決定1 の既定路線の適用であり環流は要さない。

## 未決事項

1. **市場ガード・禁止銘柄ガードの手仕舞い適用**（**✅ 解決済み 2026-08-04**）: `MarketDisabled` /
   `BannedSymbol` は現在も**全注文**へ適用する（既存挙動・`RiskEvaluator.cs:72`）。禁止銘柄へ登録した
   瞬間に既存建玉を手仕舞えなくなる（利用者が禁止登録した銘柄の建玉が閉じられない）という同種の懸念が
   あるが、**商品種別と違って「登録は利用者の意思」であり計画（ADR-0007）が「登録されたものを確実に
   強制する」と明記している**ため、本 issue では変更しない。
   → 2026-08-04 に計画へ環流した（feedback/20260804_fr19-guard-scope.md（環流記録）
   論点 2）。選択肢 A（現状維持＝ロックイン受容）/ B（Close 除外）/ C（Close 許可＋監査記録）と
   それぞれの代償を併記して裁定を仰いだ。
   → **【✅ 裁定済み 2026-08-04・ADR-0007 追補（質問票 第 1 回 Q3・Q4・project-planning#179）】** **選択肢 A（全注文適用・ロックイン受容）で確定**した。理由は
   **インサイダー取引は売付けも対象**であり、AI が利用者の関知しないタイミングで規制対象銘柄を自動売却する
   経路を残さないため。**実装の変更は不要**である（現状が A）。
   **この非対称は意図である。** ADR-0007 追補は「ガードごとに適用範囲が異なるのは**各ガードの目的が異なるためであり、揃えるべき不整合ではない**」と明示している。**揃える方向の変更を提案しないこと。**
   **手仕舞いが必要になったときの手順**は [禁止銘柄の一時解除 Runbook](../../docs/operations/banned-symbol-unlock-runbook.md) に定めた（#380）。
2. **相場操縦検知の本番 DI 登録・しきい値較正**: 検出器は注入時のみ判定する（未注入ならスキップ）。
   実注文履歴テレメトリからの供給と初期値の較正は [#251](https://github.com/endazon/ai-stock-trading/issues/251)。
3. **信用買い（`MarginLong`）の建玉表現**: 3 値化は「有効・無効の制御」までであり、信用金利・必要証拠金・
   建玉の区別（現物と信用買いの併存）は未実装である。ADR-0016 決定8 により実弾解禁は Stage 3 であり、
   本 issue の範囲外とした。担当 issue の起票要否を監査判断に委ねる。
4. **`06_daytrading-review` §2.2 の記述の陳腐化**（**✅ 更新済み 2026-08-04**）: 同節は「日本の差金決済禁止は
   moomoo の米国株現物にも適用される」（2026-07 時点の調査）と記すが、口座種別の裁定
   （project-planning#81・2026-07-31）と FR-19 の 2026-08-01 改訂と齟齬する。本 issue は新しい記述に
   従い**実在した誤適用を是正した**が、§2.2 を読んだ将来の実装者が誤適用を再導入し得る。
   → 2026-08-04 に更新（または新しい裁定への参照追記）を計画へ環流した
   （feedback/20260804_fr19-guard-scope.md（環流記録） 論点 3）。
   → **2026-08-04 に計画側で §2.2 が更新された**（口座種別の両対応・ADR-0021 への参照が入った）。#380
5. **東芝（旧 6502）の再上場時の銘柄コード**: 計画は「旧 6502。再上場時に適用」と記す。再上場時の
   コードが 6502 と同一である保証は無く、変わる場合は利用者による再登録が要る（システムは登録されたものを強制する）。

## 検証結果

| 検証 | 結果 |
| --- | --- |
| `dotnet build backend/backend.slnx` | 0 Warning / 0 Error |
| `dotnet test`（`Category!=Integration`） | **2,476 passed / 0 failed**（着手前 2,426 から +50） |
| 計画適合の赤→緑 | 削除前 **Failed: 2, Passed: 4** → 削除後 **Failed: 0, Passed: 6**（実測） |
| `dotnet format --verify-no-changes` | 差分なし |
| `node scripts/check-test-traceability.js` | OK（テスト 323 ファイル・起点 ID 25 種） |
| `node scripts/check-coverage.js` | 行カバレッジ **65.90%**（12,986/19,706 行）/ floor 62.00% |
| `node scripts/scripts.test.js` | 143 tests passed |
| `node scripts/check-banned-libraries.js` | OK |
| フロントエンド（`npm test`） | **72 passed / 10 ファイル**（0 failed） |
| アーキテクチャテスト | 4 passed |
| `node scripts/check-commit-messages.js` | OK（5 件） |

## 変更履歴

| 日付 | 変更 |
| --- | --- |
| 2026-08-04 | 初版（#332 の実装と検証の記録） |
| 2026-08-04 | 監査指摘により、ガードの適用範囲 3 論点を計画へ環流（feedback/20260804_fr19-guard-scope.md（環流記録））。計画書との差異 1 と未決事項 1・4（新設）を「環流済み・裁定待ち」へ更新 |

## 関連仕様

- 計画への環流: feedback/20260804_fr19-guard-scope.md（環流記録）
  （論点 1: 商品種別ガードの Open 限定の明示化 ／ 論点 2: 禁止銘柄ガードの Close 適用の裁定 ／
  論点 3: 06_daytrading-review §2.2 の更新）
- 実装 ADR: [IADR-0132](../adr/IADR-0132_product-type-tri-state-and-guard-scope.md)（本 issue）・
  [IADR-0131](../adr/IADR-0131_short-selling-controls-fail-closed.md)（空売り統制）・
  [IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)（既知逸脱レジストリ）
- 機能仕様書: [FR-19 取引ガード](../../docs/functional/FR-19_trading-guard.md)・[FR-10 リスク統制](../../docs/functional/FR-10_risk-controls.md)
- テスト仕様書: [FR-19 取引ガード（再実装）](../../docs/tests/FR-19_trading-guards-tests.md)・
  [FR-19 相場操縦パターン検知](../../docs/tests/FR-19_manipulation-detection-tests.md)
- 作業仕様書: [20260804_329_short-selling-controls](./20260804_329_short-selling-controls.md)（#329 第 2 段階）
