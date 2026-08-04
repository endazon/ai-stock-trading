---
title: 作業仕様書 — 計画 submodule を ADR-0022 / ADR-0023 の新設へ同期し、為替レート源と鮮度の確定値を計画適合レジストリへ転記する
type: work
status: review
related_ids: [NFR, FR-10, FR-15, FR-17, ADR-0004, ADR-0008, ADR-0022, ADR-0023, IADR-0107, IADR-0112, IADR-0127, IADR-0134, IADR-0135]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0022_fx-rate-source-and-freshness.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
related_specs:
  - ./20260804_374_short-sell-rejection-reasons-nine.md
  - ../adr/IADR-0135_fx-freshness-plan-transcription-and-section3-scope.md
  - ../adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md
  - ../adr/IADR-0127_plan-conformance-known-deviation-registry.md
  - ../adr/IADR-0112_fx-rate-freshness-publication-cadence.md
  - ../adr/IADR-0107_base-currency-conversion.md
  - ../DEFINITION_OF_DONE.md
---

# 作業仕様書: 為替レート源・鮮度の計画確定値を計画適合レジストリへ転記する（#381）

## 起点となる計画書（トレーサビリティ）

- 非機能（NFR）: 計画 submodule のピン更新（計画と実装の同期）
- 機能要求（FR）: **FR-10**（リスク統制。為替鮮度による新規建て停止）／ FR-17（全体前提条件）／ FR-15（バックテスト。ADR-0023 の関係先）
- 関連 ADR: **[ADR-0022](../../planning/projects/ai-stock-trading/07_adr/ADR-0022_fx-rate-source-and-freshness.md)**（為替レートの情報源と鮮度。決定1〜5）／
  [ADR-0023](../../planning/projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md)（米国株の日足 OHLC 履歴源）／
  [ADR-0004](../../planning/projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md)（両 ADR が部分改定する元 ADR）
- 計画書リンク: `06_technical/05_trading-assumptions.md` **§3**（為替・通貨）・**§5**（為替レートの鮮度による縮退）
- 実装 ADR: [IADR-0135](../adr/IADR-0135_fx-freshness-plan-transcription-and-section3-scope.md)（本作業）／
  [IADR-0134](../adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定3（本作業が実行する運用規律）／
  [IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)（計画適合レジストリ）／
  [IADR-0112](../adr/IADR-0112_fx-rate-freshness-publication-cadence.md)（`DefaultMaxRateAgeDays = 14` の一次記録）／
  [IADR-0107](../adr/IADR-0107_base-currency-conversion.md)（為替レート源の安全既定）
- 起点 issue: [#381](https://github.com/endazon/ai-stock-trading/issues/381)（為替源の日銀第一化・鮮度 3/30 日）
- 由来: [project-planning#195](https://github.com/endazon/project-planning/pull/195)（環流 #57・#59・#64・#68・#196 のトリアージ結果）

## 目的・背景

[IADR-0134](../adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定3 は次の運用規律を定めた。

> `planning` submodule のピンを動かす作業は、**ピン更新と同じ PR の中で**計画差分
> （`git -C planning diff <旧ピン>..<新ピン>`）を読み、`PlanRiskDefaults` の対象範囲に触れる変更が
> あれば表を追随させる。**転記さえ行えば、そこから先（実装との乖離）は機構が守る。**

本作業はこの規律の**初回の実行**である。planning のピンを `4cbd3e2` → `d980a01` へ進めると
ADR-0022 が §3・§5 へ確定値を追加するため、`PlanRiskDefaults` を追随させる。

**本作業は実装を直さない。** 為替源の日銀第一化・鮮度 3/30 日への実装追随は #381、
Stooq 関連は [#382](https://github.com/endazon/ai-stock-trading/issues/382) の担当である。
本作業がやるのは「計画側の表への転記」と「乖離の登録簿への登録」だけであり、
**乖離を実装で消すのではなく、可視化した状態で担当 issue へ引き渡す**。

## 計画差分の内容（一次情報から）

### ADR-0022 の決定 1〜5

| 決定 | 内容 | 値か / 振る舞いか |
| --- | --- | --- |
| 決定1 | 為替の第一の情報源は**日銀 時系列統計データ「外国為替市況（日次）」**（毎営業日公表）。系列 ID の確定は実装着手時 | **値**（情報源の識別子）＋ 未確定部分あり |
| 決定2 | **FRED `DEXJPUS` はフォールバックとして残す**。切り替わった事実・期間を日報／監査ログへ記録し Discord へ通知する | **値**（フォールバック源の識別子）＋ **振る舞い**（記録・通知） |
| 決定3 | 鮮度は**営業日カレンダーではなく冗長化で担保**する（カレンダーを保持しない） | **構造**（型の不在。値ではない） |
| 決定4 | **警告しきい値は 3 日**（3 日を超えて古ければ警告） | **値** |
| 決定5 | **絶対上限は 30 日**。3 日超〜30 日以下は直近レートで続行し警告、30 日超で**新規建てを停止**（手仕舞い・損切りは止めない） | **値**（30 日）＋ **振る舞い**（縮退の段階） |

### 05_trading-assumptions の変更

- **§3（為替・通貨）**: 「為替レートの取得元」行が「日銀API または FRED」から**優先順位つき**へ改まり、
  「**為替レートの鮮度**」行（警告 3 日超／絶対上限 30 日）が**新設**された。
- **§5（リスク統制・取引ガードの既定値）**: 「**為替レートの鮮度による縮退**」行が**新設**された
  （3 日以下＝通常運用／3 日超〜30 日以下＝続行し警告／30 日超＝新規建て停止）。
  備考に「実装が独自に持っていた `DefaultMaxRateAgeDays = 14` を計画側の決定へ置き換える」と明記。

### ADR-0023（Stooq）が `PlanRiskDefaults` の対象範囲に入らないこと

`PlanRiskDefaults` の対象範囲は **05_trading-assumptions §1/§4/§5/§6 ＋ ADR-0008 / ADR-0016 / ADR-0018**
である（[IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)・
`AiStockTrading.PlanConformance.Tests.csproj` の冒頭コメント）。ADR-0023 は次のとおり範囲外である。

- ADR-0023 が改定するのは **ADR-0004 §決定 の「検証・学習用」**であり、ADR-0008 の**値**（Stage 0 の
  合格判定の許容値・出金の DD 倍率）は 1 つも変えていない。ADR-0008 は「Stage 0 の判定が実施できない」
  という**帰結**の説明として参照されているだけである。
- 05_trading-assumptions への変更は **§3・§5（いずれも ADR-0022 由来）のみ**であり、ADR-0023 は同ファイルを
  変更していない（`git -C planning diff 4cbd3e2..d980a01` で確認）。
- 情報源の可否は**値ではなく情報源の選定**であり、`PlanRiskDefaults` が収録する「実装から機械的に抽出できる
  既定値」ではない。

したがって **#382（Stooq）は登録簿へ登録しない**。範囲外の行を足すと登録簿が「担当 issue の一覧」へ
退化し、逸脱の登録簿としての意味が薄れる。

## 対象範囲

### 対象

1. planning submodule のピン更新（`4cbd3e2` → `d980a01`）。
2. `PlanRiskDefaults` への ADR-0022 確定値の転記（3 行）と、§2/§3 の対象外コメントの是正。
3. `ActualDefaults` への抽出経路の追加（`Fx` の 3 キー）。**これが無いと検査2 が恒久的に赤になる**（後述）。
4. `KnownPlanDeviations` への逸脱 3 件の登録（担当 #381）。
5. 実装 ADR [IADR-0135](../adr/IADR-0135_fx-freshness-plan-transcription-and-section3-scope.md) の起案と索引更新。

### 対象外（担当を明記）

| 事項 | 担当 |
| --- | --- |
| 日銀アダプタの新設・`FxOptions` の既定値変更（14 → 30）・警告 3 日の実装 | **#381** |
| フォールバック切り替えの記録・Discord 通知（ADR-0022 決定2） | **#381** |
| 30 日超の新規建て停止と [ADR-0009](../../planning/projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md) の停止状態への対応づけ | **#381** |
| Stooq 関連の実装変更・moomoo 履歴取得の検証 | **#382** |
| 日銀の系列 ID の確定（計画側フォローアップ） | **#381**（実装着手時） |

## 設計

### 1. §3 の対象化方針（IADR-0135 決定1）

`PlanRiskDefaults` の既存コメントは「**§2/§3 は「要確認」のため確定値を持たず対象外**」と述べていた。
ADR-0022 により §3 に確定値が入ったため、この説明は現状と食い違う。**節単位ではなく行単位で対象化する**
（根拠は [IADR-0135](../adr/IADR-0135_fx-freshness-plan-transcription-and-section3-scope.md) 決定1）。
コメントを実態に合わせて書き換える。

### 2. 転記するキー（3 行）

| キー | 計画値（正規化） | 出典 |
| --- | --- | --- |
| `Fx.RateSourceProviders` | `boj, fred` | §3 / ADR-0022 決定1・2 |
| `Fx.StaleRateWarningDays` | `3 days` | §3 / §5 / ADR-0022 決定4 |
| `Fx.MaxRateAgeDays` | `30 days` | §3 / §5 / ADR-0022 決定5 |

- 日数は**単位を含めて**正規化する（`3` ではなく `3 days`）。`RiskLimits.MaxOpenPositions` の `3`（無次元の
  件数）と取り違えないため、また「日か営業日か」を将来足す余地を残すためである。
- 優先順位（第一＝日銀／フォールバック＝FRED）**そのものは値ではなく振る舞い**（切り替え条件・記録・通知）
  であるため、表は**集合**として持ち、順位は #381 のテスト仕様で担保する
  （[IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md) 決定4）。

### 3. 転記しないもの

| 計画の記述 | 転記しない理由 |
| --- | --- |
| ADR-0022 決定3（営業日カレンダーを持たない） | **不在の宣言**であり値ではない。「持たないこと」は型の不在で書けるが、実装に対応する型が元から無いため常に一致し、検知力がゼロの行になる |
| ADR-0022 決定2 の記録・通知、決定5 の縮退の段階 | **振る舞いの規則**（IADR-0127 決定4）。テスト仕様書の 3 点セットで #381 が検証する |
| 日銀の系列 ID | 計画側が**未確定**（ADR-0022 決定1・INDEX の未決事項）。確定していない値は転記できない |
| 日銀のクレジット表記 | 報告書テンプレート（04_report-templates）の担当であり §1/§3〜§6 の既定値ではない |
| `FxOptions.MaxAllowedRateAgeDays = 31` | **計画に対応する値が無い**。構成で指定できる鮮度上限のクランプ（IADR-0112 決定2）であり、計画の「絶対上限」は既定値側に対応する。ただし 31 > 30 のため #381 が既定を 30 へ直す際にクランプ側の見直しが要る（未決事項 §1） |
| §3 の「基準通貨（判定）＝USD」「基準通貨（表示）＝JPY」「為替評価方法」 | 前 2 者は `Capital.Initial` の通貨（`TradingDefaults.EquityCurrency`）として**すでに実質的に検知対象**であり、二重に持つと同じ事実を 2 か所で管理することになる。「為替評価方法」は振る舞いの規則 |

### 4. `ActualDefaults` への抽出経路の追加

`FxOptions` は `TradeDecisionService.Infrastructure` の **`internal` 型**であり、計画適合テストの
プロジェクト参照に入っていない。転記だけを行うと**検査2（計画確定値の全キーが実装側スナップショットに
存在する）が恒久的に赤**になり、`KnownPlanDeviations` では緑に戻せない（検査2 は登録簿を参照しない）。
[IADR-0134](../adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定3 が
`ActualDefaults` の抽出候補について指摘したのと**同種の穴**である。

したがって次を追加する。**実装には触れない**（可視性も広げない。`InternalsVisibleTo` は増やさない）。

- `AiStockTrading.PlanConformance.Tests.csproj` へ `TradeDecisionService.Infrastructure` のプロジェクト参照。
- `ActualDefaults` に**リフレクションによる**抽出（`internal` 型でも `Assembly.GetType` と
  `BindingFlags.Public | BindingFlags.Static` で読める。既存の `FindType` と同じ方式）。

### 変更するファイル

| ファイル | 変更 |
| --- | --- |
| `planning`（submodule） | ピンを `d980a01` へ |
| `backend/Tests/AiStockTrading.PlanConformance.Tests/PlanRiskDefaults.cs` | `Fx` 3 行の追加・§2/§3 のコメント是正・`Assumptions3` 定数の追加 |
| `backend/Tests/AiStockTrading.PlanConformance.Tests/ActualDefaults.cs` | `Fx` 3 キーの抽出（リフレクション） |
| `backend/Tests/AiStockTrading.PlanConformance.Tests/AiStockTrading.PlanConformance.Tests.csproj` | Infrastructure へのプロジェクト参照 |
| `backend/Tests/AiStockTrading.PlanConformance.Tests/KnownPlanDeviations.cs` | 逸脱 3 件（担当 #381）の登録 |
| `docs/adr/IADR-0135_*.md` / `docs/adr/README.md` | 実装 ADR の起案と索引 |
| `feedback/20260804_adr0022-fx-rate-source-and-freshness.md` | 環流の裁定結果の控え（新規。#59 の控えが本リポに無かったため） |

## 受け入れ基準

- [x] planning のピンが `d980a01` である。
- [x] ADR-0022 の確定値のうち**値であるもの**が `PlanRiskDefaults` に転記されている。
- [x] 「§2/§3 は要確認のため対象外」というコメントが**現状と食い違ったまま残っていない**。
- [x] 転記だけを行った状態で計画適合テストが**赤くなること**を実測し、記録した。
- [x] 逸脱が `KnownPlanDeviations` に担当 issue（#381）・理由つきで登録され、テストが緑に戻る。
- [x] #382（Stooq）が対象範囲外であることを確認し、登録していない。
- [x] `dotnet build backend/backend.slnx` が 0 Warning / 0 Error。
- [x] `dotnet test backend/backend.slnx --filter "Category!=Integration"` が全件成功。
- [x] `dotnet format backend/backend.slnx --verify-no-changes` が通る。
- [x] `check-commit-messages.js` / `check-test-traceability.js` / `check-doc-links.js` / `check-banned-libraries.js` が通る。

## テスト方針

新しいテストは足さない。**既存の計画適合テスト 6 検査が本作業の検証手段そのもの**である。
本作業で検証するのは「転記が赤を生み、登録が緑へ戻す」という機構の往復であり、これは
[IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md) の設計意図どおりの動作である。

為替鮮度の**振る舞い**（3 日超で警告・30 日超で新規建て停止・フォールバックの通知）のテストは
**#381 の担当**である。値の一致だけを本作業が担保する。

### 計画適合検査の赤 → 緑（実測）

#### 第 1 段階: 転記のみ（`PlanRiskDefaults` に 3 行を追加した状態）

```
$ dotnet test backend/backend.slnx --filter "FullyQualifiedName~PlanConformance"
失敗: PlanConformanceTests.計画確定値の全キーが実装側スナップショットに存在する
  計画側にあって実装側スナップショットに無いキーは、抽出漏れか綴り誤りである。
  ActualDefaults.Snapshot() へ抽出を追加すること。
  欠落キー: Fx.RateSourceProviders, Fx.StaleRateWarningDays, Fx.MaxRateAgeDays
  （失敗 1 / 成功 5）
```

**期待していた検査1（値の不一致）ではなく、検査2（抽出漏れ）が落ちた。** `FxOptions` が計画適合テストの
参照範囲外にあり、実装値 `14` が**計画適合検査から見えていなかった**ためである。

#### 第 2 段階: 抽出経路を追加した状態

```
$ dotnet test backend/backend.slnx --filter "FullyQualifiedName~PlanConformance"
失敗: PlanConformanceTests.計画確定値と実装値が一致する_登録済み逸脱を除く
  逸脱:
    Fx.RateSourceProviders: 計画「boj, fred」(05_trading-assumptions §3 / ADR-0022 決定1・2)
      / 実装「fred」
    Fx.StaleRateWarningDays: 計画「3 days」(... / ADR-0022 決定4)
      / 実装「(FxOptions.DefaultStaleRateWarningDays not found)」
    Fx.MaxRateAgeDays: 計画「30 days」(... / ADR-0022 決定5)
      / 実装「14 days」
  （失敗 1 / 成功 5）
```

計画と実装の乖離が**値として**現れた。実測値は本仕様書「## 検証結果」に転記する。

#### 第 3 段階: `KnownPlanDeviations` へ登録

3 件を担当 #381 で登録し、6 検査すべて成功へ戻る。以降、#381 が実装を直すと検査3（登録簿の陳腐化）
または検査4（登録値の追随漏れ）が赤くなり、**登録簿の更新が機械的に強制される**。

## 計画書との差異

無し。本作業は計画に**追随する**側であり、計画へ差異を持ち込まない。
計画の記述が曖昧で判断を要した点は「未決事項」に記す。

## 未決事項

1. **`FxOptions.MaxAllowedRateAgeDays = 31` と計画の絶対上限 30 の関係**。計画は「絶対上限 30 日」と
   述べるが、実装には既定値（14）と構成で指定できる上限のクランプ（31）の**2 つの上限**がある。
   #381 が既定を 30 へ直す際、クランプ 31 を 30 へ揃えるのか、構成で 31 日まで許す余地を残すのかは
   計画から一意に読めない。**計画の文言（「絶対上限」）は後者を許さないと読めるため、#381 で
   クランプ側も 30 へ揃えることを推奨する**が、決めるのは #381 である。
2. **警告 3 日の起算点**。計画は「取得できている最新レートの**日付**が 3 日を超えて古い場合」と述べる。
   実装の鮮度判定は観測時刻との差（`TimeSpan`）であり、日付境界での丸めの有無が読み取れない。
   境界値の扱いは #381 のテスト仕様で確定する。
3. **フォールバック中の鮮度しきい値**。FRED は公表が週次であるため、フォールバック稼働中は
   3 日超の警告が**常時**立つ。ADR-0022 決定2 は「フォールバック中である旨を通知する」と述べており、
   鮮度警告と二重に鳴ることになる。抑制の要否は計画に記述が無い（#381 で環流の要否を判断する）。
4. **`Fx.RateSourceProviders` の粒度**。日銀の系列 ID が未確定であるため、表は provider 識別子
   （`boj` / `fred`）の集合に留めた。系列 ID が確定したら §3 の「取得元」行が具体化するため、
   そのときに再度転記の要否を判断する。

## 検証結果

- `dotnet build backend/backend.slnx`: **0 Warning / 0 Error**
- `dotnet test backend/backend.slnx --filter "Category!=Integration"`: **全件成功**
- `dotnet format backend/backend.slnx --verify-no-changes`: 差分なし
- `node scripts/check-commit-messages.js` / `check-test-traceability.js` / `check-doc-links.js` /
  `check-banned-libraries.js`: いずれも成功

（実測値は PR 本文へ記載する。）

## 関連仕様

- [IADR-0135](../adr/IADR-0135_fx-freshness-plan-transcription-and-section3-scope.md)（本作業の実装 ADR）
- [IADR-0134](../adr/IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定3（本作業が実行する運用規律）
- [IADR-0127](../adr/IADR-0127_plan-conformance-known-deviation-registry.md)（計画適合レジストリ）
- [作業仕様書 20260804（#374）](./20260804_374_short-sell-rejection-reasons-nine.md)（同じ規律の前回の作業）
- [環流記録: ADR-0022 の裁定](../../feedback/20260804_adr0022-fx-rate-source-and-freshness.md)
