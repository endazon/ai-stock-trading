---
title: Claude 5.5 系への切替の移行段（直前世代の受け入れ）を撤去し、5.5 系の ID だけを受け付ける（#1296）
type: spec
status: accepted
related_ids: [FR-04, FR-06, FR-15, NFR, ADR-0011, ADR-0014, ADR-0017, ADR-0054, ADR-0064, IADR-0524, IADR-0122, IADR-0215]
author: claude (Claude Code)
created: 2026-10-11
updated: 2026-10-11
plan_refs:
  - planning#783（利用者裁定 2026-10-10。裁定 3「旧モデルは廃棄し 5.5 系だけに対応する」・裁定 4「移行段は一切設けない。AST#1296 で PoC 配備の前に撤去させる」）
  - planning:projects/ai-stock-trading/07_adr/ADR-0064（決定 7・決定 8。決定 6 の表・決定 9 の暫定）
  - planning:projects/microservices-platform/07_adr/ADR-0138（決定 7・決定 8）
---

# Claude 5.5 系への切替の移行段を撤去し、5.5 系の ID だけを受け付ける（#1296）

## 起点

- [#1296](https://github.com/endazon/ai-stock-trading/issues/1296)。#1295（PR #1299・IADR-0524 決定 1）が設けた移行段
  （割当表が各 5.5 系 ID の直前世代 `claude-sonnet-5` / `claude-haiku-4-5` / `claude-opus-5` を同じ位置で受ける段）を外す。
- 計画: ADR-0064 決定 8（移行段は設けない。基盤と AST を同時に配備する）・決定 7（旧モデルは廃棄し、割当表・許可・単価表の現行値から
  5.5 系以外の Claude モデルを外す）。planning#783 の再確認の裁定で「**PoC への配備の前に AST#1296 で撤去する**」とされた
  （オーナー判断 2026-10-11: 月曜の開場前に配備する）。
- #1296 本文の「外す条件」（MSP 配備＋1 営業日の観測）は、planning#783 裁定 4（同時配備・移行段なし）で上書きされた。

## 射程の判断（`claude-fable-5` の行・未知モデルの計上単価）

**本作業の射程に入れない。** 原文の根拠:

- #1296 の「やること」は、直前世代の受け入れ・旧世代の単価行（opus_5 / sonnet_5 / haiku_4_5）・それを引く注記・IADR-0524 の追記だけを挙げる。
- planning#783 の「残作業」は、「AST#1296 で移行段を撤去する。対象は直前世代の受け付け・旧世代の単価行・関連の注記である」と
  「未知モデルの計上単価を、専用の設定値で持つ。`claude-fable-5` の行を外し、`IADR-0122` 決定 3 を改める」を**別の項目**に分けている。
- ADR-0064 決定 6 の表（決定 9 の行）は「**配備まで現行の表の最大で計上する**（`claude-fable-5` の行を残したまま運用する。上限は
  $10 / $50 のまま下がらない）。`claude-fable-5` の行は、**専用の設定値の配備と同時に外す**」と定める。fable の行だけを先に外すと
  上限が `claude-opus-5-5` の $4 / $20 に下がり、ADR-0064 が「採れない」とした選択肢 C（過小計上）になる。

したがって `claude-fable-5` の単価行は残し、未知モデルの計上単価（既定 $10 / $50）は別作業とする（残り 1）。
#1296 本文の「未知モデルの最大単価は 5.5 系の表で決まる」は fable の行が残る間は成り立たない（最大は fable の $10 / $50 のまま）。

`claude-opus-4-8` の単価行は射程に**入れる**。#1296 は名指ししていないが、同じ「移行期間のみ」の段落に載る旧世代の行であり、
ADR-0064 決定 7（単価表の現行値は 5.5 系だけ）と食い違う。fable と違って「未知モデルの上限」の役を担っていない
（$5 / $25 < fable の $10 / $50）ため、外しても計上の上限は変わらない。

## 現況（origin/develop c7458ab6）

- `LlmAssignments` に直前世代の定数 3 つ・`PreviousGenerationAccepted`・`IsPreviousGenerationOf` があり、評価器が直前世代を同じ位置で受けて
  `LlmAssignmentEvaluation.PreviousGenerationAccepted` の印を付ける。`MatchesCurrentPin` は印付きの Primary を除く。
- `LlmPreviousGenerationWarning` が 3 か所（取引判断・報告書散文・方針改訂）で印を見て Warning を 1 回出す。
- Helm `values-local.yaml` の report-service / trade-decision-service に旧世代の単価行（opus_5 / opus_4_8 / sonnet_5 / haiku_4_5）。
- README・operations・helm.yml・FR-15 の機能仕様書とテスト仕様書・Stage 0 の注記が移行段を前提に書いている。

## やること

1. `LlmAssignments` から直前世代の定数・`PreviousGenerationAccepted`・`IsPreviousGenerationOf` と評価器の分岐を外す。
   `LlmAssignmentEvaluation.PreviousGenerationAccepted` と `MatchesCurrentPin`（`Primary` と同義になる）を外し、
   `Stage0TwoTierModels` は `Outcome == Primary` で照合する。
2. `LlmPreviousGenerationWarning` とその試験・3 か所の呼び出しを外す。
3. Helm `values-local.yaml` の旧世代の単価行を両サービスから外す。`helm.yml` に「旧世代の行が描画されない」否定の検査を足す。
4. README・operations・FR-15 の機能仕様書／テスト仕様書・コードの注記から移行段の前提を外す。
5. IADR-0524 へ日付つき追記で撤去を記録する（新 IADR は作らない。決定の新設ではなく、IADR-0524 フォローアップ 1 の実施である）。

## 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| 1 | 全用途で、直前世代（`claude-sonnet-5` / `claude-haiku-4-5` / `claude-opus-5`）と `claude-opus-4-8` は `Unassigned`・`Allowed=false` になる | `LlmAssignmentsTests.旧世代のモデルはどの用途でも未割当として受けない` |
| 2 | 取引判断系で `Allowed` になるのは第 1 候補ただ 1 つ（旧世代を母集合に含めても増えない） | `LlmAssignmentsTests.取引判断系で許可される実効モデルは第1候補だけである` |
| 3 | 取引判断で直前世代が応答したら本文を破棄して Hold・見送り（`model-mismatch`）を記録し、再呼び出ししない | `HttpLlmCompletionClientFallbackBanTests.実効モデルがピンと違えば発注へ進まず_呼び出しも増やさない`（`claude-sonnet-5` / `claude-haiku-4-5` の行） |
| 4 | Stage 0 の両層の照合は旧組・片方だけ旧世代を一致と読まない（T-15-124。既存の試験が引き続き守る） | `Stage0DecisionRecordTests`・`Stage0ReplayEvaluationTests` |
| 5 | Helm の values-local は両サービスで旧世代の単価行を描画しない。5.5 系の行と `claude-fable-5` の行は残る | `helm.yml`（values-local の描画検査） |
| 6 | 割当表のスナップショットは不変（5 用途・5.5 系） | `LlmAssignmentsTests.割当表は計画の確定値と一致する` |

## 母集合（規則 9・10）

走査語（誤りの側の文字列）: `claude-(sonnet-5|haiku-4-5|opus-5)`（5-5 を除く）・`PreviousGeneration`・`IsPreviousGenerationOf`・
`Haiku45` / `Sonnet5` / `Opus5`・`claude_(opus_5|sonnet_5|haiku_4_5|opus_4_8)__`・`opus-4-8`・`直前世代`・`移行段`・`移行期間`・`#1296`。
`git grep` で全追跡ファイル（`CHANGELOG.md` を除く）を走査し、76 ファイルが当たった。

| 区分 | ファイル | 扱い |
| --- | --- | --- |
| 移行段の本体 | `LlmAssignments.cs`・`LlmPreviousGenerationWarning.cs`・`Stage0TwoTierModels.cs`・`HttpLlmCompletionClient.cs`・`HttpReportNarrativeDrafter.cs`・`LlmReportPolicyReviser.cs` | 撤去・注記の改め |
| 移行段の試験 | `LlmAssignmentsTests.cs`・`LlmPreviousGenerationWarningTests.cs`・`HttpLlmCompletionClientFallbackBanTests.cs` | 「旧世代は受けない」へ書き換え・削除 |
| 移行期間を引く注記 | `Stage0DecisionRecordTests.cs`・`Stage0ReplayEvaluationTests.cs`・`LiveTradingGate.cs` | 注記だけ改める（試験の期待値は不変） |
| 単価行・その説明 | `values-local.yaml`・`deploy/helm/ai-stock-trading/README.md`・`.github/workflows/helm.yml`・`docs/operations/operations.md` | 行を外し、説明を改める |
| docs の移行期間の記述 | `docs/functional/FR-15_backtest.md`・`docs/tests/FR-15_backtest-tests.md` | 記述を改める |
| 記録 | `IADR-0524`・`.ai-context/adr/README.md` | 日付つき追記・索引の注記 |

除外（理由つき）:

- `.ai-context/specs/` の 26 件・`.ai-context/adr/` の IADR-0524 以外 19 件: 凍結記録（当時の記述）。
- 歴史の記述（導出時・旧割当の値として名指し）: `DecisionOrchestrationOptions.cs`・`values.yaml`（2 か所）・`appsettings.Development.json`・
  `FR-15_backtest.md` 182 行・`FR-15_backtest-tests.md` 335 行・`operations.md` 529 行・README の「恒久値ではない」段落・`values-local.yaml` の出典注記。
- 試験の任意のモデル名・未知モデルの例: `Stage0DecisionRecordTests`（戦略識別子の素材）・`LlmPriceTableTests`（未知モデルの例・単価表の素材）・
  `PublishingLlmUsageReporterTests`・`HttpReportNarrativeDrafterVisibilityTests`・`PublishingLlmReportersTests`・`ReportRendererLlmModelUsageTests`・
  `LiveTradingGateTests`（旧組を名乗らないことの否定）・`HttpLlmCompletionClientFallbackBanTests` の `claude-opus-4-8` 行（既に Unassigned の例）・
  `scripts/nightly-ledger-summary.pg.test.sh`・`scripts/fixtures/helm-release-drift/credential-cases.js`（台帳・描画ずれの素材）。
- `scripts/check-consumer-endpoint-names.js` の「移行期間」: Wolverine 移行の話で無関係。
- `docs/operations/live-trading-cutover-runbook.md` の当たり（frontmatter の `#1296`・5.5 系の組の記述）: 移行段を前提にしていない。

規則 10（この変更で新たに誤りになる自分の記述）: README の fail-safe 段落「現行 fable-5」は fable の行を残すため正しいまま。
#1296 本文の「未知モデルの最大単価は 5.5 系の表で決まる」は転記しない（fable の行が残る間は誤り）。

## 変異で試験の効きを確かめた

| 変異 | 結果 |
| --- | --- |
| 評価器に移行段を戻す（第 1 候補の直前世代 `claude-sonnet-5` / `claude-opus-5` / `claude-haiku-4-5` を Primary・Allowed で受ける） | `LlmAssignmentsTests` 8 件（旧世代は未割当 7 行・取引判断系の許可は第 1 候補だけ）と `HttpLlmCompletionClientFallbackBanTests`（`claude-sonnet-5` 行）が赤 |
| `values-local.yaml` の trade-decision に `claude_sonnet_5` の単価行を 1 行戻す | `helm.yml` の values-local 検査（ローカルで同じ step を helm v3.16.4 で実行）が `旧世代の単価行が描画された` で赤 |

どちらも確かめた後に元へ戻した。

## 配備で変わる点

- 割当表は 5.5 系の ID だけを受ける。基盤が旧 ID を返すと、取引判断（`trade-decision` / `trade-decision-screening`）は
  割当不一致で見送り（発注しない）、報告書は本文を採るが割当逸脱を通知する。費用は未知モデルとして表の最大（`claude-fable-5` の $10 / $50）で計上する。
- `LLM 直前世代を移行期間の受け入れで採用` の Warning は出なくなる。
- ロールバックは移行段の入った版（本 PR のマージ直前の develop。オーナー指定の `4b556615` もその系統）へ戻す。症状と手順は PR 本文に書く。

## 残り（本作業の外）

1. 未知モデルの計上単価の専用設定値（既定 $10 / $50）と、`claude-fable-5` の単価行の撤去・IADR-0122 決定 3 の改め（ADR-0064 決定 9。planning#783 の残作業）。#1305 として起票済みで、本作業の後に直列で進める。
2. Haiku 5.5 の 2 段で「入力の合計が取れないときは高い側の段」（ADR-0064 決定 4。planning#783 の残作業）。
3. #1296 の再検証期限（2026-10-24）のコメントは、本 PR のマージで issue を閉じれば不要になる。
