---
title: 建てた条件の記号を、稼働中の方針の書式（「A 上放れ追随:」）でも読めるようにし、方針の生成側にも書式を守らせる（#1393）
type: spec
status: done
related_ids: [FR-04, FR-07, FR-10, ADR-0003, IADR-0470, IADR-0558]
author: claude (Claude Code)
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04・FR-07)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 「X 名前:」の書式行も建てた条件の記号として読む（#1393）

> 本仕様書は実装着手前に作成した（着手 2026-10-20）。基点は AST `origin/develop` `c61663a3`（#1390 の `27ccbc0a` を含む）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-04**（方針の範囲で判断し判断根拠を記録する）、FR-07（方針の改訂）、FR-10（方針に基づく出口）
- 関連 ADR: ADR-0003（判断入力）
- 関連 IADR: **IADR-0558**（決定 1・8 を追記で広げる。新規 IADR は作らない）、IADR-0470（自由文は読まない）
- 起票: #1393（`Closes #1393`。`Refs #1384`）

## 目的・背景

PoC（2026-10-20 00:32 JST、AST rev 50）で SMCI の新規建て（条件 A）の記録が「不明」になった。
reason は `方針に「条件 X:」行が無い`。効いている方針（daily-2026-10-17 の版 5）は「A 上放れ追随: …」「B 急落反発: …」と
記号を行頭に書き、「条件」の語を持たない。`PolicyEntryConditions` は「条件 X:」の行だけを読むため集合が空になり、記録は常に不明になる。

## 方針

1. **読み取り（共有カーネル `PolicyEntryConditions`）**: 既存の「条件 X:」行に加え、行頭の「X 名前:」の行も読む。
   - NFKC 後・前後空白の除去後に `\A(?:[-*・]\s*)?(?<symbol>[A-Z])\s+(?<name>[^:。]{1,20}):` に合う行。
   - X は英大文字 1 字で直後に空白が要る（「AI:」「EPS:」「AI 銘柄:」は読まない）。名前は 1〜20 字で「:」「。」を含まない（全角「：」は NFKC で「:」）。
   - 行頭以外（「注: A 上放れ追随: …」・文中の「A:」）は読まない。区切りの無い行（「A 社の決算が…」）は読まない。
   - 名前の無い「A: …」は読まない（issue の書式の外。書式を増やさない）。
2. **生成側**: 方針の本文を書く LLM プロンプトは `PolicyRevisionPromptBuilder` 1 つである（後述の母集合）。
   その案内（`EntryConditionLineRule`）と材料の行（`HeldPositionMaterial`）を、2 つの書式のどちらかで書かせる形へ広げる。
3. **読み手の文言の追随**: 判断の出力の規則（`TradeDecisionPromptBuilder.OutputEntryBasisRule`・`EntryBasisLotsLead`）と
   検証の理由（`EntryBasisValidator`）の「条件 X:」行の言い方を、2 つの書式を指す言い方へ揃える。
4. 既存の方針は書き換えない。

## 母集合（規則 9）

走査語: `条件 X`・`条件 <記号>`・`「条件 [A-Z]`・`PolicyEntryConditions`（`grep -rn` で全ファイル。`.ai-context/specs/` は凍結のため除く）。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `Shared.Kernel/Trading/PolicyEntryConditions.cs` | 2 本目の正規表現を足す・冒頭コメントの書式を更新 |
| 2 | `Shared.Kernel.Tests/Trading/PolicyEntryConditionsTests.cs` | 版 5 の本文・誤読しない例・混在の試験を足す（既存は不変） |
| 3 | `ReportService/Features/Reports/PolicyRevisionPromptBuilder.cs`（`HeldPositionMaterial`・`EntryConditionLineRule`・コメント） | 2 つの書式を案内する |
| 4 | `ReportService/Tests/Features/Reports/PolicyRevisionEntryBasisMaterialTests.cs` | 案内の例（両書式）が共有カーネルで読めることを固定 |
| 5 | `TradeDecisionService/.../TradeDecisionPromptBuilder.cs`（`OutputEntryBasisRule`・`EntryBasisLotsLead`・コメント） | 2 つの書式を指す言い方へ |
| 6 | `TradeDecisionService/.../EntryBasisValidator.cs`（理由文・コメント） | 理由文を「条件の記号の行（「条件 X:」「X 名前:」）が無い」へ |
| 7 | `Shared.Contracts/Trading/EntryBasis.cs`・`RiskManagementService/.../PersistenceRows.cs` のコメント | 言い方を揃える |
| 8 | `TradeDecisionService/Tests/.../EntryBasisRecordAndHandOffTests.cs` | 「条件 X:」を含む断言は維持される（変更不要を確認） |
| 9 | IADR-0558 本文・README 索引行 | 日付つき追記（［2026-10-20 追記 / #1393］） |

除外: `ReportNarrativePromptBuilder`（散文のみ。方針の本文は書かない）・`ReportPolicyDraft.CarryOver`（自動生成は直近の確定済み方針の継続でコード生成。LLM が方針を書かない）・
`MonthlyBootstrap`（コード定数の方針文）。**「報告書の方針の生成」と `/policy` の改訂は、どちらも `PolicyRevisionPromptBuilder.Build`（`LlmReportPolicyReviser`）を通る**
（`/policy` は通知サービス → 報告書サービス `ReportPolicyRevisionService`、報告書の画面からの改訂も同じサービス）。

## 受け入れ基準

- [x] AC-1: 版 5 の方針の本文（「A 上放れ追随: …」「B 急落反発: …」）から `{A, B}` が取れる
- [x] AC-2: 誤読しない例（区切りの無い「A 社の決算が…」・文中の「A:」・「AI:」「EPS:」・行頭でない位置・名前が 21 字以上）が拾われない
- [x] AC-3: 既存の「条件 X:」の試験がすべて緑
- [x] AC-4: 新しい書式の読み取りを外すと AC-1 が赤になる（変異）
- [x] AC-5: 方針の改訂のプロンプトが 2 つの書式を案内し、案内の例が共有カーネルで記号として読める

## 検証

- `dotnet test` を Shared.Kernel.Tests・ReportService.Tests・TradeDecisionService.Tests へ（--filter なし）
- `dotnet format --verify-no-changes`、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、`node scripts/check-adr-index-sync.js`
