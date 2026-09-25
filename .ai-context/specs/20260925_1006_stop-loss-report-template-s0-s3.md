---
title: 日報・月報の損切りの実行機構を改定後のテンプレート（S0〜S3・見送りの欄・月報 1 行）に揃える（#1006）
type: spec
status: accepted
related_ids: [FR-06, FR-10, SC-03, ADR-0040, IADR-0429, IADR-0422, IADR-0342, IADR-0344, IADR-0347]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (日報 §4「損切りの実行機構（当日）」・月報 §6・記載要件。2026-09-25 訂正・fd1032b)
  - planning:projects/ai-stock-trading/05_screens/01_screens.md (SC-03 の「S1・S3 未実装の併記」の取り下げ。fd1032b)
  - planning:projects/ai-stock-trading/10_feedback/20260925_stop-loss-method-s1-s3-implemented.md (planning#646 の裁定 1〜5)
---

# 仕様書: 日報・月報の損切りの実行機構を改定後のテンプレートに揃える（#1006）

## 起点

- #1006（planning#646 の裁定・PR planning#647 / `fd1032b` の「実装側の残作業」1〜3）。先行実装は #1002（PR #1004・`d15ec5f`・IADR-0429）。
- 割り当て: ブランチ `fix/FR-10-1006-stop-loss-report-template-s0-s3`・**IADR-0429 への日付つき追記**（新規 IADR は起こさない）・
  テスト ID **T-10-1110〜T-10-1113**（origin/* 11 本〔develop・main・changelog・進行中の 6 ブランチ・audit/pr919・audit/pr940〕で
  `git grep -h -o "T-10-1[0-9]{3}"` の最大は `T-10-1099`。1110〜1119 はいずれにも無い）。
- `git rev-parse --is-shallow-repository` → `false`（本仕様書は `git log` を出典に引かない）。

## 計画書の確認（隣接クローン `C:/10_SourceCode/worktree/project-planning`・`fd1032b`・読み取り専用）

- 日報 §4:
  - `選ばれていた手法（承認時点）: <なし（当日の新規建ての承認は 0 件） / 計 n 件 — S0 ブローカー側逆指値 <a> 件 / S1 ソフトウェア逆指値 <b> 件 / S2 逆指値なしの建玉を許容 <c> 件 / S3 他のブローカー側注文種別 <d> 件>`
  - `実際に適用された手法（発注執行の解決結果）: <なし / 計 n 件 — S0 … <a> 件 / S1 … <b> 件 / S2 … <c> 件 / S3 … <d> 件 / 見送り（実際の発注先が SIMULATE でない）<e> 件>`
  - `<2 行が食い違う場合はその理由（実際の発注先が SIMULATE でないための見送り・空売りの新規建て・未知の値）を併記する>`
  - 記載要件: 「内訳は S0〜S3 の区分ごとに数え、2 行目は『見送り』を加える。**内訳の合計は『計 n 件』と一致させる**。件数 0 の区分は省いてよい」。
- 月報 §6: `当月の損切りの実行機構: <S0 a 日 / S1 b 日 / S2 c 日 / S3 d 日>／選択と実際が食い違った日数: <n 日>`
  （承認が無い月も「なし」。個々の日の内訳は該当日報）。記載要件「月報 §6 の日数ベースの内訳も同様に S0〜S3 の 4 区分とする」。
- SC-03: 「S1・S3 が未実装である旨の併記」は取り下げ（裁定 1）。

## 母集合（規則 1〜6・9・10。着手時に自分で引いた）

### 1. #1004 の出力とテンプレートの照合（develop `d15ec5f` の `ReportRenderer.cs` L983–1195・`StopLossMethodComparison.cs`・`StopLossMethodUsage.cs`）

| # | 箇所 | #1004 | テンプレート | 判定 |
| --- | --- | --- | --- | --- |
| a | 日報 1 行目の区分名 | `StopLossMethodUsage.Label`: `S0 ブローカー側逆指値` / `S1 ソフトウェア逆指値` / `S2 逆指値なしの建玉を許容` / `S3 他のブローカー側注文種別` | 同じ | 一致 |
| b | 並び | 列挙の序数順（S0=0・S1=1・S2=2・S3=3）。2 行目の見送り（null）は最後 | S0→S3、見送りは最後 | 一致 |
| c | 件数 0 の区分 | 出さない（`GroupBy` で現れない） | 省いてよい | 一致 |
| d | 1 行目の合計 | `計 {TotalApprovals}` ＝ `Counts` の和 | 一致させる | 一致 |
| e | 2 行目の合計 | `計 {ResolvedCount}` ＝ `AppliedCounts` の和（同じ母集合＝解決結果が見つかった承認） | 一致させる | 一致（見送りの区分を含めて一致） |
| f | **2 行目の見送りの区分名** | `発注せず（拒否）` | `見送り（実際の発注先が SIMULATE でない）` | **差分** |
| g | **食い違いの理由（見送り）** | `S0 以外の手法は moomoo SIMULATE でしか適用しないため発注しなかった` | 「実際の発注先が SIMULATE でないための見送り」 | **差分** |
| h | 食い違いの理由（空売り・未知） | `空売りの新規建ては S0 で扱う` / `未知の手法の値のため S0 と同じ扱いにした` | 空売りの新規建て・未知の値 | 空売りは一致。**未知は監査で計画の語「未知の値」へ揃えた**（「未知の値のため S0 と同じ扱いにした」。PR #1008 の監査） |
| i | **月報の行名** | 本行 `実際に適用された手法の日数: …` と別行 `選択と実際が食い違った日数: …` | `当月の損切りの実行機構: …／選択と実際が食い違った日数: …` の 1 行 | **差分** |
| j | **月報の内訳の区分** | S0〜S3 ＋ `発注せず（拒否） n 日` | S0〜S3 の 4 区分 | **差分** |
| k | 承認 0 件 | 日報「なし（当日の新規建ての承認は 0 件）」・月報「当月の損切りの実行機構: なし」 | 「なし」 | 一致 |
| l | SC-03 の未実装の併記 | 無い（`frontend/` を `未実装` で引いて S1/S3/損切りに当たる行 0 件） | 入れない | 一致 |
| m | 週報 | 出さない | 出さない | 一致 |

### 2. 変更で直す文字列の出現（誤りの側から引いた）

`git grep -n -E "発注せず|実際に適用された手法の日数|解決の後の見送り|S0 n 日|S2 m 日|しか適用しないため発注しなかった"`（`CHANGELOG.md` 除く）と
`git grep -n -E "SIMULATE でしか適用しない|食い違った日数|当月の損切りの実行機構|（拒否）|重複して数え|AppliedLabel|ReasonLabel|AppliedDays|MixedDays"`
（`CHANGELOG.md`・`.ai-context/specs` 除く）の 2 軸で引いた。拡張子・行では絞っていない。

| 反映先 | 理由 |
| --- | --- |
| `backend/Services/ReportService/Domain/StopLossMethodComparison.cs` | 区分名（f）・理由（g）・月報の日数（j） |
| `backend/Services/ReportService/Domain/ReportRenderer.cs` | 月報の 1 行（i）・日報の固定の注記（下記 10） |
| `backend/Services/ReportService/Tests/Domain/Golden/{daily-supplied,daily-unsupplied,monthly-supplied}.md` | 出力の固定 |
| `backend/Services/ReportService/Tests/Domain/StopLossMethodComparisonTests.cs` | 旧い文字列を期待している |
| `backend/Services/ReportService/Tests/StopLossMethodResolutionWiringTests.cs` | 同上（L111–113） |
| `backend/Services/ReportService/Tests/Features/Reports/ReportAutoGeneratorStopLossMethodTests.cs` | 月報の行頭 `- **選択と実際が食い違った日数` を期待している（L197） |
| `docs/functional/FR-10_risk-controls.md` | 「拒否は『発注せず（拒否）』」・月報の説明（L933–937） |
| `docs/tests/FR-10_risk-controls-tests.md` | 新しいテスト ID の行 |
| `.ai-context/adr/IADR-0429_*.md`・`.ai-context/adr/README.md` | 決定 3・6 の描画の改め（日付つき追記・索引行） |

**除外したもの**:

| 除外 | 理由 |
| --- | --- |
| `.ai-context/specs/20260925_1002_*.md` | 確定済みの作業仕様書（凍結記録。書き換えない） |
| `AuditEntryFactory.cs` の要約「適用なし（発注しない）」「S0 以外は moomoo SIMULATE でしか適用しない」 | 監査台帳の要約であり報告書テンプレートの射程外（T-10-1085 が固定する既存の記録の文言） |
| `StopLossMethodResolved.cs` / `StopLossMethodResolutionReason.cs` / `StopLossMethodPolicy.cs` の「拒否」 | 型・解決規則のコメント（表示文言ではない）。意味は変わらない |
| 発注執行・通知・契約の「発注せず見送る」等（`OrderExecution*`・`NotificationFormatter.cs`・`OrderDispatchForgone.cs` ほか） | 別の事実（発注の見送り）を述べる語で、本件の表示と無関係 |
| `IADR-0016`・`IADR-0057`・`IADR-0210` ほかの「発注せず」 | 別の決定 |

### 3. この変更で新たに誤りになる自分の記述（規則 10）

- 日報の固定の注記「解決の後の**見送り**や約定の有無は反映しません」: 2 行目に「見送り」の区分を入れると、同じ語が「解決の時点で
  発注しなかった（2 行目に数える）」と「解決の後に発注を見送った（数えない）」の 2 つを指す。→ 注記を書き分ける（下記 10）。
- `StopLossMethodComparison` の XML コメント「拒否は最後」「拒否を含む」: 表示名と合わせて直す。
- `docs/tests/FR-10_risk-controls-tests.md` の T-10-1089 の行「手法ごとの日数（重複日の併記）」: 月報から見送りの日数が消えることと矛盾しない（書き換え不要）。

## 決定（仕様）

1. **日報 2 行目の見送りの区分名**を `見送り（実際の発注先が SIMULATE でない）` にする（`AppliedLabel(null)`）。食い違いの行の矢印の先も同じ語になる。
2. **食い違いの理由（見送り）**を `実際の発注先が SIMULATE でないための見送り`（発注先が分かれば `。実際の発注先: moomoo REAL` を続ける）にする。
   空売り・未知の理由は変えない（テンプレートの語を既に含む）。
3. **月報 §6 は 1 行**: `- **当月の損切りの実行機構: <S0〜S3 の日数>／選択と実際が食い違った日数: n 日**（新規建ての承認があった日 N 日。…。個々の日の内訳と理由は該当日報を参照）`。
4. **月報の内訳は S0〜S3 の 4 区分**。見送りは実行機構が働かなかった承認であり日数に数えない（食い違った日数には数える）。
   見送りがあった月は括弧に「見送り（実際の発注先が SIMULATE でない）は内訳に含めていません」と書く（内訳の日数の和が承認のあった日に届かない理由を黙らせない）。
   複数の手法が適用された日（`MixedDays`）も S0〜S3 の間だけで数える。
5. 解決結果はあるがすべて見送りだった月は `当月の損切りの実行機構: S0〜S3 のいずれも適用されませんでした（解決結果はすべて見送り）` と書く（空の内訳を出さない）。**記録の無い承認もある月は** `照合できた承認では S0〜S3 のいずれも適用されませんでした（照合できた解決結果はすべて見送り）` と限定する（不明を「適用なし」へ潰さない。PR #1008 の監査）。
6. 解決結果が 1 件も見つからない月は `- **当月の損切りの実行機構**: 数えられません／**選択と実際が食い違った日数**: 判定できていません（解決結果の記録が見つかった承認がありません。新規建ての承認があった日 N 日）` の 1 行（「0 日」と書かない規律は #1004 のまま）。
7. 承認 0 件の月（「なし」）・照会できない場合・記録の無い承認を含む日・復元できなかった記録・JST の注記の各行は変えない。
8. 日報 1 行目の未知の手法は `不明(N)` の区分で数えたまま残す——内訳を S0〜S3 だけにすると合計が「計」と合わなくなる（記載要件「合計は計と一致させる」が優先。
   SC-03 の「未知の値は S0 へ倒して表示しない」と同じ趣旨）。2 行目は解決規則により未知が S0 へ倒れて届くため `不明` は現れない。
9. 日報 2 行目の「計」は解決結果が見つかった承認の数のまま（IADR-0429 決定 3。記録の無い承認は別の行）。1 行目と 2 行目の計の差は「解決結果の記録が見つからない承認 n 件」の行が説明する。
10. 日報の固定の注記の「解決の後の見送り」を書き分ける: 「『見送り（実際の発注先が SIMULATE でない）』は解決の時点で発注しなかった承認です。解決の後に発注を
    見送った場合（逆指値価格が無い等）や約定の有無は反映しません」。
11. **見送りの区分名と件数の間に半角空白を置かない**（計画の書式 `…でない）<e> 件`。S0〜S3 は空白あり。PR #1008 の監査）。

## 受け入れ基準とテスト

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | 日報 2 行目の見送りの区分名・食い違いの理由（見送り）がテンプレートの語 | T-10-1110（＋ T-10-1088 の既存の期待を更新） |
| 2 | 月報 §6 が 1 行・4 区分・見送りは内訳に含めない・すべて見送りの月・解決結果なしの月 | T-10-1111（＋ T-10-1089 の既存の期待を更新） |
| 3 | 日報の 2 行それぞれで内訳の和＝「計 n 件」（S0〜S3・見送り・空売り・未知・記録なしの組合せを種固定で多数） | T-10-1112 |
| 4 | S1・S3・見送りを含む日のゴールデン（日報・月報） | T-10-1113（`ReportTemplateGoldenTests` の代表データ） |

## 検証

- `dotnet test backend/Services/ReportService/Tests`・`backend/Architecture.Tests`・`dotnet format --verify-no-changes`
- 文書検査: `check-trace-blocks`・`check-test-traceability`・`check-adr-index-addendum-loss`・`check-adr-index-sync --range=origin/develop..HEAD`・`check-commit-messages`・`check-cross-repo-refs`

## 結果・残余リスク

- 監査台帳の要約（「適用なし（発注しない）」）は報告書と別の語のまま残る（射程外。上の除外）。
- 月報の内訳から見送りの日数が消える。見送りがあった日は「食い違った日数」と括弧の注記・日報で読む（テンプレートの 4 区分に従う）。
