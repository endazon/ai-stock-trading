---
title: 日報 §6 振り返り（週次目標との照合）を実装し、週報 §6 の「数値目標:」行の文法・確定前の警告・週初来の実現損益・週報 §1／§4 の照合を同じ参照値と定義で埋める（#1218・計画 ADR-0059 フォローアップ 1〜5）
type: spec
status: accepted
related_ids: [FR-06, FR-16, FR-07, UC-03, UC-04, ADR-0059, ADR-0030, ADR-0053, ADR-0051, IADR-0291, IADR-0252, IADR-0492, IADR-0493, IADR-0470, IADR-0519]
author: claude (Claude Code)
created: 2026-10-09
updated: 2026-10-09
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0059_weekly-goal-reference-line-and-week-to-date-pnl.md (決定 1〜5・フォローアップ 1〜6)
  - planning:projects/ai-stock-trading/07_adr/ADR-0030_report-section-numbering-is-plan-canonical.md (決定 3・決定 5・フォローアップ 3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0053_report-closing-is-per-market-session-window.md (決定 2 セッションの窓)
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (日報 §5・§6／週報 §1・§4・§6・2026-10-09 の注記)
  - planning:projects/ai-stock-trading/10_feedback/20261009_weekly-goal-reference.md (裁定 1〜3・残るもの)
  - planning:docs/glossary.md (「週次目標の書式行」)
---

# 日報 §6 振り返り（週次目標との照合）と週次目標の書式行（#1218）

## 背景

- 日報 §6 は「週次目標: <参照値> に対し <進捗・乖離の評価>」を求める（04_report-templates）。実装は未実装の見出し
  （`ReportRenderer.DailyReviewReason`）を出していた（IADR-0291 決定 2）。
- 計画は参照値の形・比較の定義・参照する週報を決めていなかった。環流 planning#748 の裁定で計画 ADR-0059 が起票された（2026-10-09）。
  - 決定 1: 週報 §6 の数値目標は範囲（下限〜上限）と単位を書いた決まった書式の 1 行。**文法の正は実装 IADR**。単位は必須。
  - 決定 2: 書式どおりの行が無い週報は確定の前に警告し、確定は止めない。参照する週報に行が無い・書式外なら日報 §6 は「照合不能」。LLM に読ませない。
  - 決定 3: 比較対象は週報 §1 と同じ定義（ADR-0053 の窓・税引後・費用込み・基準通貨 USD）で数えた週初来の実現損益。単位が違えば換算せず「照合不能」。
    比較はコード、LLM は評価の文章だけ。週報 §1「週次目標に対する達成」と §4 も同じ参照値と定義で比べる。週の最終日の日報 §6 と週報 §1 の値は一致する。
  - 決定 4: 「当週の週報」は前週の週報。未確定なら最新の確定済み週報を注記つきで使う。確定済みが 1 件も無ければ「週次目標なし」。
  - §結果: 週報 §1 の達成・未達を範囲のどこで分けるかは**決めていない**（フォローアップ 6・計画）。

## 実測（origin/develop d6f720e3）

- `ReportRenderer.cs:95` 日報 §6 は `AppendNotImplemented(..., DailyReviewReason)`。`:1705` 週報 §1「週次目標に対する達成」は `Pending`（「（データ連携後）」）。
- 週報 §4 の見出しは実装では「## 4. 振り返りと評価」（計画語は「目標達成の評価（月次目標との照合）」。IADR-0291 決定 6 の受容のまま）。本文は LLM 散文だけ。
- 週報 §1 の「週間実現損益」は `ReportDraftService.BuildDraftAsync` が `PnlAggregator.Aggregate(fills, TradingAssumptionsDefaults.Create(), currentPrices, adoptions, opening)`
  で集計し、期間開始時点の在庫が照会できなければ `OpeningInventoryUnknown` を立てる。入力は `ReportAutoGenerator.CollectInputsAsync` の
  `SafeFillsAsync`・`SafeDriftAdoptionsAsync`・`SafeOpeningInventoryAsync`（いずれも `ReportSchedule.SessionWindowOf(due)` の窓）。
- 週報の窓は `(DailyBoundaryOnOrBefore(週初−1), DailyBoundaryOnOrBefore(週の最終営業日)]`（IADR-0492 決定 1・2。期間の日報の窓の和）。
- 日報の上位方針は `store.GetLatestConfirmed(Weekly)`（当週を対象とするかは確かめない）。
- 散文（LLM）は 1 回の呼び出しで 1 本（`IReportNarrativeDrafter`）。日報では §5 へ置く。
- 週報 §6 の方針は利用者が書く自由文（`PolicySummary`）。構造化 YAML に週次目標の欄は無い（IADR-0252）。

## 決定（IADR-0519 の要約）

1. **文法**（フォローアップ 1）: 週報の方針の中の 1 行
   `[- * ・ のどれか 1 つ]数値目標: <下限> 〜 <上限> <単位>`（NFKC で全角を半角へ寄せ、前後の空白を除いて 1 行ずつ見る）。
   - 金額: 符号は任意（`+` / `-`）。整数部は 3 桁ごとのカンマか、カンマなしの 1〜12 桁。小数は 2 桁まで。
   - 範囲の区切り: `〜`（U+301C）か `~`（全角の `～` は NFKC で `~` になる）。前後の空白は任意。
   - 単位: 英大文字 3 字の通貨コードか `円`。**必須**。基準通貨（`MarketCurrency.Base`＝USD）以外は「単位が違う」（換算しない）。
   - 下限 ≤ 上限。読んだ語の後ろに文字があれば書式外。
   - 「数値目標」の語を含む行（コロンの有無・見出し・説明の文を問わない）を候補とし、候補が 0 行＝行なし、2 行以上＝書式外（どれが目標か決めない）、
     1 行でも書式に合わなければ書式外。🔴 **自由文からは読まない**（IADR-0252・IADR-0470 再監査の是正と同じ）。
2. **確定前の警告**（フォローアップ 2）: 週報の方針に書式どおりの行が無い（行なし・書式外・単位違い）とき、日報の利確の警告（IADR-0470 決定 4）と
   同じ 3 経路（自動生成の提示の要約・`/policy` の改訂の案内文と本文の改訂の記録・作り直しの再提示の要約）に警告を出す。確定は止めない。
   印は契約アセンブリの新しい定数 `ReportSummaryMarkers.WeeklyGoalLineMissingPrefix`。通知サービスは同じ印で提示の通知を Warning へ上げ、
   `/policy` の承認待ちの案でも印で始まる行を確認ボタンの前に出す。
3. **週初来の実現損益**（フォローアップ 3）: 日報 D の週初来の窓は「D の ISO 週の週報の窓を D で打ち切ったもの」
   `ReportSchedule.SessionWindowOf(DueReport(Weekly, 週初, D))`＝当週のその日までの日報の窓の和。入力は週報 §1 と**同じ供給元・同じ関数**
   （`SafeFillsAsync`・`SafeDriftAdoptionsAsync`・`SafeOpeningInventoryAsync` に週初来の `DueReport` を渡す）。集計は週報 §1 と**同じ関数**
   `PeriodPnl.Aggregate`（新設。`ReportDraftService` の §1 の集計もこれを通す）。窓が日報の窓と一致する日（週の最初の営業日）は日報の入力を使い回す。
   週の最終営業日は窓が週報の窓と一致するので値も一致する（試験で固定）。
4. **日報 §6**（フォローアップ 4）: 参照する週報は前週（D の週初 − 7 日の週）の週報。確定済みでなければ、週初より前を対象とする確定済み週報のうち最新を注記つきで使う。
   無ければ「週次目標なし」。比較はコード（下限未満・範囲内・上限超と差）。物語の生成は 1 回の呼び出しのまま、日報だけ出力を区切り行で §5 と §6 に分ける
   （区切り行が無ければ全文を §5 に置き、§6 の散文は「（散文ドラフトなし）」。散文を両節へ複製しない＝IADR-0291 決定 4）。`DailyReviewReason` は消す。
5. **週報 §1・§4**（フォローアップ 5）: 同じ参照値（週報 W の前週の週報）と §1 の値で比べる。§1 のセルは位置（範囲内・下限未満・上限超）を事実として示し、
   🔴 **達成・未達は判定しない**（「判定保留（達成・未達を範囲のどこで分けるかは計画で未決）」。ADR-0059 §結果・フォローアップ 6）。§4 は散文の前にコードの照合の行を置く。
   達成・未達の分け方は planning へ環流した（planning#766。起票前に `feedback` ラベルの open / closed を検索し、計画 ADR-0059 フォローアップ 6 に対応する issue が無いことを確かめた）。
6. 窓を持たない経路（手動の生成 API）は週次目標を照会しない。日報 §6・週報 §1／§4 は「照会していません」と書く（「週次目標なし」「照合不能」と混ぜない）。

## 範囲

- Domain（新設）: `WeeklyGoalLine`（文法）・`WeeklyGoalLineCheck`（警告）・`WeeklyGoalReference`／`WeeklyGoalActual`／`WeeklyGoalComparison`（照合）・
  `PeriodPnl`（§1 と週初来の共通の集計）・`DailyNarrativeSections`（散文の区切り）。
- `ReportRenderer`（日報 §6・週報 §1 のセル・週報 §4 の照合の行）・`ReportView`・`DraftRequest`・`ReportDraftService`・`ReportNarrativeContext`／`ReportNarrativePromptBuilder`
  （照合の事実と日報の区切り）・`PolicyRevisionPromptBuilder`（週報の改訂へ書式を案内）・`ReportAutoGenerator`（参照する週報の解決・週初来の入力）・
  `ReportRegenerationService`・`ReportPolicyRevisionService`（警告）・`ReportDependencyObservation.Leave`（週初来の照会の失敗を見送りの判定へ混ぜない）。
- 契約: `ReportSummaryMarkers.WeeklyGoalLineMissingPrefix`（定数の追加。イベントの形は変えない）。通知サービス: 重大度と `/policy` の警告行。
- 文書: IADR-0519（新設）・索引・IADR-0291 への日付つき追記（決定 2 の表の日報 §6 の行・フォローアップ 3 の解消）・本仕様書。
  ゴールデン（日報・週報の 4 本）は §6・§1 のセル・§4 の照合の行が差分（意図した更新）。

範囲外:
- 週報 §1 の達成・未達の判定規則（計画 ADR-0059 フォローアップ 6。planning へ環流）。
- 週報 §4 の見出し語の計画整合（IADR-0291 決定 6 の受容のまま）・「月次目標への進捗」（月次目標の参照値の経路は無い）。
- 日報 §6 の週初来の値の「集計したセッション」の範囲の明記（日報の窓の行とは別の範囲。定義は「週報 §1 と同じ」と本文に書く）。
- 構造化 YAML への週次目標の欄（ADR-0059 が案 (c) を退けた）。
- 機能仕様書・テスト仕様書: FR-06／FR-16 は網羅裁定の必須範囲外（`docs/README.md`）。作業仕様書と xUnit を正の記録とする。

## 母集合（規則 9。誤りの側＝「日報 §6 は未実装」「週次目標の参照値が無い」を前提にした記述から引く）

引き方: `git grep -n -e "DailyReviewReason" -e "週次目標の参照値" -e "振り返り（週次目標との照合）" -e "週次目標に対する達成" -e "週次目標" -- backend docs .ai-context/adr`。

| 箇所 | 扱い |
| --- | --- |
| `ReportRenderer.cs` の `DailyReviewReason`・日報の `AppendNotImplemented` 呼び出し・`Labels` の注記（「未実装の節（月報 §2・§3 / 日報 §6）」） | **直す**（定数を消し実体の描画へ。注記は月報 §3 だけに） |
| `ReportRenderer.cs` の週報 §1 の `Pending` | **直す** |
| `ReportRendererTests.cs` の未実装の節の Theory（日報 §6 の行）・`週次目標に対する達成 | （データ連携後）`・§5/§6 の注記 | **直す**（行を外し、実体の試験へ） |
| `ReportRendererTradeHistoryTests.cs:150` の「§6 振り返りは未実装だが」 | **直す**（注記の語） |
| ゴールデン `daily-*.md`・`weekly-*.md` | **直す**（意図した更新） |
| `ReportPolicyDraftTests.cs:140`（方針の文の中の「週次目標」の語） | 変えない（テストデータの自由文。照合と無関係） |
| IADR-0291（決定 2 の表・フォローアップ 3） | **日付つき追記**（凍結記録） |
| `.ai-context/specs/` の過去の仕様書（`20260902_612`・`20260903_adr0030`・`20260904_615c`・`20260711`） | 変えない（point-in-time の記録） |
| IADR-0306 の「日報 §6」への言及 | 変えない（月報 §2 の決定。日報 §6 が未実装だった時点の事実の記録） |

規則 10（この変更で新たに誤りになる自分の記述）: `ReportView.Narrative` の要約「LLM ドラフトの散文（市況・振り返り・評価等）」（日報では §5 だけになる）・
`ReportNarrativePromptBuilder` の末尾の指示（日報は 2 部構成になる）・`ReportDraft.Narrative`（日報では §5 の部分）。いずれも本 PR で直す。

## 受け入れ基準 → 試験

| ID | 受け入れ基準（Given / When / Then） | 試験 |
| --- | --- | --- |
| T-06-077 | Given 週報の方針に `数値目標: -200 〜 +500 USD`（全角・箇条書き・カンマ・小数・`～` を含む書き方）When 読む Then 下限・上限・単位 USD を返す | `WeeklyGoalLineTests` |
| T-06-078 | （否定形）Given 単位なし・範囲なし・下限 > 上限・後ろに文字・小数 3 桁・`$`・「数値目標」の行が 2 行・見出しや説明の文に「数値目標」 When 読む Then 書式外（値を返さない） | `WeeklyGoalLineTests` |
| T-06-079 | Given 行が無い When 読む Then 行なし。Given `円`・`JPY` When 読む Then 単位違い（換算しない） | `WeeklyGoalLineTests` |
| T-06-080 | Given 書式どおりの行が無い（行なし・書式外・単位違い）週報の方針 When 自動生成の提示・`/policy` の改訂・作り直しの再提示 Then 要約・案内文・本文の改訂の記録に印つきの警告が出て、確定は止めない。日報・月報には出さない | `WeeklyGoalLineCheckTests`・`ReportAutoGeneratorWeeklyGoalTests`（提示の要約・`/policy` の改訂）・`WeeklyGoalLineWarningNotificationTests`（通知サービス） |
| T-06-081 | Given 前週の確定済み週報に書式どおりの目標 When 日報を生成する Then §6 に参照値（どの週報か）・週初来の実現損益・位置（下限未満／範囲内／上限超）と差が出る。数値はコードの値 | `ReportAutoGeneratorWeeklyGoalTests`・`WeeklyGoalComparisonTests`・`ReportRendererWeeklyGoalTests` |
| T-06-082 | Given 当週の月〜金の約定（持ち越しの在庫を含む）When 金曜の日報と週報を生成する Then 日報 §6 の週初来の値と週報 §1 の週間実現損益が一致する。火曜の日報の値は月〜火の窓の集計 | `ReportAutoGeneratorWeeklyGoalTests` |
| T-06-083 | Given 前週の週報が未確定で、より前の週報が確定済み When 日報を生成する Then その週報の目標と照らし、どの週の目標かを注記する。Given 確定済みが 1 件も無い Then 「週次目標なし」 | `ReportAutoGeneratorWeeklyGoalTests` |
| T-06-084 | Given 参照する週報に行が無い・書式外・単位違い When 日報を生成する Then §6 は「照合不能」と理由を書き、範囲・位置を書かない | `ReportRendererWeeklyGoalTests` |
| T-06-085 | Given 週初来の値が部分値（在庫の照会失敗・算定できない決済）・約定の照会失敗 When 日報を生成する Then §6 は「算出不能」と理由を書き、位置を書かない | `WeeklyGoalComparisonTests`・`ReportRendererWeeklyGoalTests`・`ReportAutoGeneratorWeeklyGoalTests`（黙った照会失敗） |
| T-06-086 | （否定形）日報の §5 と §6 を統合しない。散文は区切り行で分け、両節へ複製しない。区切りが無ければ §6 の散文は「（散文ドラフトなし）」。`DailyReviewReason`（未実装の文言）は出ない | `DailyNarrativeSectionsTests`・`ReportRendererWeeklyGoalTests` |
| T-06-087 | Given 前週の週報の目標 When 週報を生成する Then §1 のセルは位置を事実として示し「判定保留」と書き、達成・未達を書かない。§4 は散文の前に照合の行 | `ReportRendererWeeklyGoalTests`・`ReportAutoGeneratorWeeklyGoalTests` |
| T-06-088 | 散文のプロンプトには照合の事実（コードの値）を渡し、日報には区切り行の指示、照合できないときは推測させない指示を出す。週報の改訂のプロンプトに書式を案内する（例は文法で読める） | `ReportAutoGeneratorWeeklyGoalTests`（散文の文脈とプロンプト）・`WeeklyGoalLineCheckTests`（改訂のプロンプト） |
| T-06-089 | 窓を持たない経路（手動の生成 API）は「照会していません」と書く | `ReportRendererWeeklyGoalTests` |

## 自己変異（変異を入れて赤を確かめ、戻す）

- 文法: 単位を任意にする／下限 ≤ 上限の検査を外す → T-06-078 が赤になること。
- 比較: 範囲内の判定を `<=` から `<` にする（境界ちょうど）／週初来の窓の始まりを日報の窓にする → T-06-081・082 が赤になること。

結果は本文末の「実施記録」に書く。

## 実施記録

- 2026-10-09（origin/develop d6f720e3 の上）:
  - `dotnet build backend/backend.slnx`: 0 警告・0 エラー。ReportService の試験 1800 件・通知サービスの試験は全件成功（下の検証の節）。
  - ゴールデン 4 本（日報・週報 × 供給あり／なし）は `UPDATE_GOLDEN=1` で更新し、差分が §6・§1 のセル・§4 の照合の行だけであることを確かめた。
  - 既存の試験の追随: `ReportOpeningInventoryWiringTests.T06_050`（日報の後に週初来の窓の下端〔米国 ET 10-02・東証 10-03〕で在庫を引く）・
    `ReportRendererTests`（未実装の節の Theory から日報 §6 の行を外した・週報 §1 のセル）。
  - 自己変異（変異 → 赤 → 戻す。いずれも赤になった）:

| 変異 | 赤になった試験 |
| --- | --- |
| 文法: 単位を任意にする | `WeeklyGoalLineTests.書式外の行は読まない("数値目標: -200 〜 +500")` |
| 文法: 下限 ≤ 上限の検査を外す | `WeeklyGoalLineTests.書式外の行は読まない("数値目標: 500 〜 -200 USD")` |
| 比較: 上限を `>=` にする | `WeeklyGoalComparisonTests.範囲の両端ちょうどは範囲内で…(500)` |
| 比較: 下限を `<=` にする | `WeeklyGoalComparisonTests.範囲の両端ちょうどは範囲内で…(-200)` |
| 週初来の窓の始まりを日報の日にする | `ReportAutoGeneratorWeeklyGoalTests` の火曜・金曜の 2 件・`ReportOpeningInventoryWiringTests.T06_050` |
| 参照する週報に当週の週報を許す | `ReportAutoGeneratorWeeklyGoalTests.当週以後の確定済み週報は参照値にせず…` |
| 週初来の集計から期間開始時点の在庫を落とす | `ReportAutoGeneratorWeeklyGoalTests.金曜の日報の週初来の値は週報の週間実現損益と一致する` |

［2026-10-09 追記 / #1218］ 独立監査（フェーズ末監査）の指摘を同じ PR で是正した。

- R1（要是正）: 週初来の約定の照会は `Leave()` の後に行うが、実在の供給元（HTTP・gRPC）は失敗を空列＋観測だけで返すため、`SafeFillsAsync` の
  失敗の印だけを見ると黙った失敗が「0.00 USD で範囲内」になっていた。照会の前後で観測の失敗件数が増えたら照会の失敗とみなすよう
  `ReportAutoGenerator.CollectWeekToDateAsync` を直した（IADR-0519 決定 3 の追記）。試験
  `ReportAutoGeneratorWeeklyGoalTests.週初来の約定の照会が空列と観測だけで失敗したら算出不能と書く`（T-06-085）を足し、是正前のコードで
  赤（§6 が「0.00 USD で **範囲内**」）になることを確かめた。
- Y1: `WeeklyGoalLine` の金額が `\d` で他の文字体系の数字（`٣`・`१२`）に一致し、`decimal.Parse` の例外で生成が落ちていた。
  `[0-9]` に限り `decimal.TryParse` で書式外へ倒した（IADR-0519 決定 1 の追記）。試験 `WeeklyGoalLineTests.ASCII以外の数字は書式外として読まない`
  （T-06-078）を足し、是正前は `FormatException` で赤になることを確かめた。
- 任意（bot の指摘）: `ReportDraftService.WeeklyGoalOf` が日報・週報以外で null を返す契約（月報へは参照値を渡さない）をコメントで明記した。
