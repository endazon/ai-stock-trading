---
title: 月報 §7 へ stage0-recording の費用実績と見積り承認額との対比を出す
type: spec
status: done
related_ids: [FR-06, FR-15, FR-16, ADR-0033, ADR-0037, IADR-0254, IADR-0318, IADR-0329]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0033_stage0-evaluation-target-is-ai-decision-replay.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0037_sonnet5-price-correction-and-cutoff-mapping.md
---

# 仕様書: 月報 §7 の `stage0-recording` 対比列（#750）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-06**（月報→週報→日報の方針階層。月報テンプレートの生成）・**FR-15**（Stage 0）・
  FR-16（数値はコード集計・LLM に計算させない）
- 関連 ADR: **`ADR-0037` 決定 3**（月報 §7 へ `stage0-recording` の行を置き、**見積り承認額との対比を含める**）・
  `ADR-0033` 決定 5.1（Stage 0 の費用は月次上限 15,000 円の**外**）・同 決定 5.3（実行中に見積り額を超えたら
  停止し利用者へ報告する）・`ADR-0030`（節番号・節順は計画が正）
- 実装 ADR: `IADR-0254`（期間集計の権威源は監査台帳）・`IADR-0318` 決定 4/決定 5（`stage0-recording` 区分と
  承認ゲート）・`IADR-0329`（#632 の残作業として本件を明示的に切り出した）
- 計画書の該当箇所: `06_technical/04_report-templates.md` 月報 §7 の表 3 行目
  「Stage 0 記録実行の費用実績（`stage0-recording`。**上限の対象外**）と見積り承認額との対比」
  値の形 `<実績 N 円 / 承認 N 円 / 差 ±N 円（±n%）>`、および同節直後の注記
  「**当月に記録実行が無ければ空欄とし、`0 円` と書かない**（『実行しなかった』と『実行して 0 円だった』を区別する）」

## 目的・背景

計画 `ADR-0037` 決定 3（2026-09-09 裁定）は月報 §7 に `stage0-recording` の**見積り対実績**の列を求めた。
計画側のテンプレートには既に行が入っているが、**値を出す実装が無い**（同 ADR §統制と現在の実現手段は
「🔴 **無い。** 月報テンプレートに列を置くだけであり、**値を書き込む実装はこれからである**」と明記している）。
駆動側（記録・承認ゲート）は #632 / PR #747 で着地済みで、報告書生成側だけが残っていた（`IADR-0329` §対象外）。

対比が無いと、`ADR-0033` 決定 5.3 の停止（見積り超過で止める統制）が**働いたかどうかを事後に読めない**。
統制は、作動したことを確かめられて初めて統制と呼べる。

## 対象範囲

- **対象**
  - `LlmUsageAggregator` が `stage0-recording` の費用を**独立した区分**として集計する
    （現状は「その他の用途」へ吸われている）。
  - 承認済み見積り額の供給口（`IStage0RecordingEstimateSource`）と、構成から読む実装・composition root の結線。
  - `ReportView` / `DraftRequest` / `ReportAutoGenerator` の受け渡し。
  - `ReportRenderer` の月報 §7 に対比行を描画する（**未供給と 0 円を区別する**）。
  - 上記の xUnit 試験（実績あり／なし／見積りのみ／両方なし・対の肯定形と否定形・golden の更新）。
  - `deploy/helm` への構成点の可視化（既定は空＝未供給）。
- **対象外**（理由つき）
  - **取引判断サービス（記録器・承認ゲート）とバックテストの駆動ロジック**。本件は報告書生成側の作業であり、
    #750 の射程がそう定めている。記録側の契約を触ると PR が 2 つの関心事を持つ。
  - **稼働クラスタへの投入**。承認額の実値は利用者が書き入れる値であり（`docs/blocked-tasks.md` B-7）、
    AI が自分の消費する費用を自分で承認しない。**構成の既定は空＝未供給**に留める。
  - **`docs/functional/` `docs/tests/` の更新**。`docs/README.md` の網羅裁定（#211）が必須とするのは
    安全・統制の中核 FR のみで、月報テンプレートの描画（FR-06）は作業仕様書と xUnit を正の記録とする。
    加えて `docs/functional/FR-15_backtest.md` L171 と `docs/tests/FR-15_backtest-tests.md` L250 は
    「記録そのものの作り方（…**費用の見積り承認**）は取引判断側の関心事であり本書の範囲外」と明記しており、
    本件で射程が変わらない（**両者を読んで確認した。§7 を説明する記述は `docs/` に 1 件も無い**）。
  - **`docs/blocked-tasks.md` B-7 の更新**。B-7 は「記録の実行」が blocked である旨の登録であり、
    本 PR は実行しない（blocked の状態は動かない）。

## 走査した母集合

母集合の規則（`.claude/rules/traceability.md` §是正・追随の母集合の取り方）に従い、**着手前に**引いた。
軸は 5 本、いずれも `git grep -I`（追跡下の全ファイル・拡張子で絞らない・行フィルタを継がない）で引いた。
**出力は生のまま読んだ**（`head` で切らない・整形しない）。

| 軸 | 検索語 | 命中 | 扱い |
| --- | --- | --- | --- |
| 1 | `stage0-recording` / `Stage0Recording` | 18 ファイル（95 行） | 記録側 12 ファイルは**対象外**（射程）。`LlmPurposes.cs` は読むだけ（キーの定義は変えない）。`values.yaml` は**対象**（report サービスへ構成点を足し、trade-decision 側にも同値である旨を書く）。`values-local.yaml` は**対象外**（局所クラスタでは記録実行が無効であり、承認額は利用者が書き入れる値である。既定＝未供給で正しく振る舞う） |
| 2 | `LLM 利用実績` / `月報 §7` | 34 ファイル | 本体は `ReportService`（`ReportRenderer` / `LlmUsageRecord` / `ReportView` / `ReportDraftService` / `ReportAutoGenerator`）＋その試験。`CostControlService` の 3 ファイルは**上限側のカウンタ**で本件の権威源ではない（`IADR-0254`）ため対象外。`.ai-context/specs/` の既存記録は凍結記録であり**書き換えない** |
| 3 | `その他の用途の費用実績` / `OtherCostJpy` | 8 行 / 4 ファイル | **全件対象**。`stage0-recording` を独立区分へ移すと「その他」の値が変わるため、宣言・描画・試験・golden の 4 面すべてを引き直す |
| 4 | `見積り承認` / `承認額` / `ApprovedEstimate` | 12 ファイル | 承認値の**所在**（`Stage0RecordingOptions.ApprovedEstimateJpy`）を特定するために引いた。記録側は対象外。`docs/blocked-tasks.md` B-7 は状態が動かないため対象外（上記） |
| 5 | `供給されていません` | 20 ファイル | **既存の未供給表記の規約**を確認するために引いた（新しい言い回しを発明しない）。変更するのは `ReportRenderer` と golden のみ |

**除外したものと理由**（黙って落とさない）:

- `.ai-context/specs/`・`.ai-context/adr/` の既存記録 —— **凍結記録**であり本文プロズを書き換えない
  （`.ai-context/README.md`）。例外は `IADR-0254` への**日付つき追記**（後述）。
- `backend/Services/CostControlService/**` —— 月次上限の対象カウンタであり、`IADR-0254` が
  「期間集計の権威源は監査台帳であって費用統制サービスの月次カウンタではない」と決めている。
- `backend/Services/TradeDecisionService/**` —— 記録側。#750 の射程外。
- `CHANGELOG.md` —— 生成物（`scripts/gen-changelog.js`）。手で書き足さない。

## 設計

### 1. 実績（actual）—— 監査台帳から引く（`IADR-0254` のまま）

`LlmUsageRecord.Costs`（監査台帳の `LlmCostIncurred` 全量）から `purpose == stage0-recording` を拾う。
`LlmUsageAggregator.Aggregate` に区分を 1 つ足す。

```
LlmUsageSummary.Stage0RecordingCostJpy : decimal?
```

- 🔴 **`null` は「当月に記録実行が無かった」であり `0 円` ではない。** 計画注記が名指しで求めた区別である。
  台帳に `stage0-recording` の計上が 1 件も無ければ `null`、1 件でもあれば合計（**0 円の計上が 1 件あれば `0` を出す**）。
- 現状この費用は `OtherCostJpy`（その他の用途）へ吸われている。**独立区分へ移すことで「その他」からは外れる**
  ——二重計上を作らないため、分岐は `IsGoverned` → `IsReport` → **`IsStage0Recording`** → その他 の順で排他にする。
- 判定は `LlmPurposes.Stage0Recording` との比較（`OrdinalIgnoreCase`。既存 `IsReport` と同じ規律）を
  `LlmPurposes.IsStage0Recording` として**契約側に置く**——報告書と統制で分別がずれないようにするため、
  用途の語彙は `Shared.Contracts.Llm` に閉じる（`LlmCostScope.IsGoverned` が偽であることは既存の試験が固定済み）。

### 2. 見積り（estimate）—— 台帳に無い。構成から読む

承認額は**台帳に載る事象ではない**（`ADR-0033` 決定 5 の「承認」は構成値であり、
`IADR-0318` 決定 5 が「構成値が算出した見積りと一致すること」で承認を表している）。
権威源は取引判断サービスの `Stage0Recording:ApprovedEstimateJpy` だが、**サービス間は直接参照しない**（CLAUDE.md）。

- 新しい供給口 `IStage0RecordingEstimateSource`（`Features/Reports/`）を置く。
  返り値は `decimal?` で、**`null` ＝承認額が供給されていない**。
- 実装は `ConfigurationStage0RecordingEstimateSource`（`Infrastructure/ExternalServices/`）で、
  同じセクション名 `Stage0Recording` の `ApprovedEstimateJpy` を報告書サービスの構成から読む。
  **未設定・空・解釈不能・負値はすべて `null`（未供給）へ倒す**（0 円へ倒さない）。
- **HTTP で取りに行かない。** 記録側は HTTP 面を持たない（`IADR-0318` 決定 5:
  「HTTP へは足さない＝本サービスの HTTP 面は無認可」）。口を開けるのは本件の射程外であり、
  無認可の面に承認額を晒す変更を月報のために入れるのは主従が逆である。
- 🔴 **残余リスク**: 同じ値が 2 サービスの構成に載るため、**片方だけ変えると対比が黙って誤る**。
  緩和は (a) 既定を空＝未供給にして「設定しない限り出さない」ことと、(b) helm の両方の設定点に
  「必ず同値にする」注記を置くこと（`Stage0Recording__LlmTrainingCutoff` が既に同じ注記を持つ先例である）。
  検知する機械は無い（人手で守る）。

### 3. 受け渡し

`ReportAutoGenerator`（optional ctor 引数・未注入＝`null`＝未供給）→ `DraftRequest` → `ReportView`。
既存の 10 個以上の供給と**同じ形**にする（`IADR-0250` の nullable view property の縫い目）。

### 4. 描画（`ReportRenderer.AppendLlmUsage`）

計画の行順に合わせ、**報告書生成の費用実績の直後**へ置く（`ADR-0030`: 節順は計画が正。
「その他の用途」は実装が足した行であり、計画の 3 行目の後ろへ送る）。

| 実績 | 承認 | 出力 |
| --- | --- | --- |
| あり | あり | `実績 +N JPY / 承認 +N JPY / 差 +N JPY（+n.n%）` |
| あり | 無し | `実績 +N JPY / 承認 **供給されていません**（0 円ではありません） / 差 算出不能` |
| 無し | あり | `実績 **当月の記録実行はありません**（0 円ではありません） / 承認 +N JPY / 差 算出不能` |
| 無し | 無し | `**供給されていません**（当月の記録実行なし・承認額の供給なし。**0 円ではありません**）` |

- 差の率は `(実績 − 承認) ÷ 承認`。**承認が 0 のときは率を出さない**（`算出不能`。消費率が上限 0 で
  `算出不能` を出すのと同じ規律）。
- 金額は既存と同じ `ReportAmountFormat.Jpy`（符号つき・`JPY` 単位）。**新しい書式を発明しない。**
- `view.LlmUsage` そのものが `null`（台帳を照会できていない）なら、§7 は既存どおり
  「照会できませんでした」の 1 行で終わる（本行も出ない）。**未供給の階層を潰さない。**

### 5. 実装 ADR

新規 IADR は起こさず、**`IADR-0254`（期間集計の権威源は監査台帳）へ日付つき追記**を入れる。
本件が足すのは「**見積り承認額は期間の集計ではなく承認の構成値であり、台帳に事象として載らない。
したがって §7 の対比列のうち承認側だけは構成から読む**」という**同 ADR の射程の明確化**であり、
決定そのものを覆さない（実績側は台帳のままである）。棄却案と残余リスクも追記に残す。

## 受け入れ基準

1. 月報 §7 に `Stage 0 記録実行の費用実績（stage0-recording。上限の対象外）と見積り承認額との対比` 行が出る。
2. `stage0-recording` の計上があれば実績が出る（**「その他の用途」に混ざらない**）。
3. 当月に `stage0-recording` の計上が 1 件も無ければ **`0 円` と書かない**。
4. 承認額が供給されていなければ **`0 円` と書かない**し、差も出さない。
5. 実績・承認の両方があるとき、差額と差率が出る。
6. 台帳そのものが未供給なら §7 は従来どおり「照会できませんでした」の 1 行（本行は出ない）。
7. composition root で供給口が実際に結線されている（配線試験）。
8. `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` と文書検査器が通る。

## テスト方針

`LlmUsageAggregatorTests` の既存規律（**境界値テーブル ＋ 否定形 ＋ 対の肯定形**）に倣う。

- `LlmUsageAggregatorTests`: 用途テーブルへ `stage0-recording` を足し、**その他へ入らない**ことを固定する（否定形）と
  **独立区分へ入る**こと（対の肯定形）。計上 0 件で `null`・計上 1 件で `0` を出す境界。
- `ReportRendererReportingCycleTests`: 上の 4 象限（実績あり／なし × 承認あり／なし）と、台帳未供給の階層。
- `ReportAutoGeneratorReportingCycleTests`: 供給口の値がビューまで届く／未注入で `null` のまま。
- `ReportingCycleWiringTests`: composition root の結線（構成値あり／なし）。
- golden（`monthly-supplied.md` / `monthly-unsupplied.md`）の更新。
- **変異試験**: 「未供給を 0 円へ倒す」実装へ変異させ、赤が出ることを確認して戻す（証跡を PR に残す）。

## リスク・未決事項

- 承認額の構成が 2 サービスに分かれる（上記残余リスク）。**機械検査は無い。**
- 実績は見積りを**最大 1 判断ぶん**上回り得る（`docs/blocked-tasks.md` B-7 の運用上の注意）。
  本 PR は差をそのまま出すだけで、超過の良否は判定しない（判定は利用者の読みに委ねる＝計画の求める形）。

## 実施結果（2026-09-11）

- `dotnet build backend/backend.slnx`: 成功・警告 0・エラー 0。
- `dotnet format backend/backend.slnx --verify-no-changes`: 差分なし。
- `dotnet test backend/backend.slnx`: `ReportService.Tests` **873 合格**（本 PR 前 851 → **+22**）、
  `AiStockTrading.Shared.Contracts.Tests` **408 合格**（+3 テストケース）。他 19 アセンブリも全合格。
  **`AiStockTrading.IntegrationTests` のみ 8 失敗**——`DockerUnavailableException`（Testcontainers が
  `npipe://./pipe/docker_engine` へ接続できない）であり、**本変更とは無関係の環境要因**である。
- 🔴 **変異試験**（3 本。いずれも「未供給を 0 へ潰す」変異を入れ、赤を確認して戻した）:

  | 変異 | 内容 | 結果 |
  | --- | --- | --- |
  | M1 | `LlmUsageAggregator` の `stage0Recording` を `null` ではなく `0m` で初期化する（計上 0 件を 0 円と書く） | **10 失敗 / 863 合格** → 戻して 873 合格 |
  | M2 | `ConfigurationStage0RecordingEstimateSource` が構成未設定で `0m` を返す（未承認を 0 円と書く） | **3 失敗 / 870 合格** → 戻して 873 合格 |
  | M3 | `Stage0RecordingComparison` が欠けた側を 0 とみなして必ず差を出す | **4 失敗 / 869 合格** → 戻して 873 合格 |

- 文書・トレーサビリティ検査: `check-trace-blocks` OK（44 件）／`check-doc-links` OK（732 件）／
  `check-cross-repo-refs` OK（2,200 件）／`check-plan-id-qualification` OK（2,251 件）／
  `gen-knowledge-graph --check` OK／`check-reading-budget` OK（44,041 バイト・予算の 86%。**本 PR で増減なし**）／
  `check-adr-index-sync` OK。
- 🔴 **`check-test-traceability` は本作業前から Windows で赤い**（T1）。原因は**検査器の環境依存**であって
  本変更ではない —— 同検査は旧樹形の有無を `fs.existsSync(<Svc>/'tests')` で数えるが、**Windows のファイル
  システムは大小を区別しないため、実在する新樹形 `Tests/` が旧樹形としても数えられる**（`dirs.old = 12`）。
  一方で走査件数は実パス（`Tests/`）で仕分けるため旧樹形は 0 件になり、T1 が「痩せ」と判定する。
  `git ls-files | grep -c '^backend/Services/[^/]*/tests/'` は **0** であり、旧樹形のファイルは 1 件も無い
  （CI（Linux）では赤にならない）。**本 PR では直さない**（射程外・別 issue 相当）。

## 追加した構成点

| サービス | キー | 既定 |
| --- | --- | --- |
| report | `Stage0Recording__ApprovedEstimateJpy` | 空＝未供給 |
| trade-decision | `Stage0Recording__ApprovedEstimateJpy`（可視化のみ・既存の受け口） | 空＝未承認 |

`values-local.yaml` へは足していない —— 局所クラスタでは記録実行そのものが無効であり
（`docs/blocked-tasks.md` B-7）、**承認額を書き入れるのは利用者の操作である**。既定（未供給）で正しく振る舞う。
