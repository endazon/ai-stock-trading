---
title: コマンド解析の会話キー値域が末尾 LF を通す —— アンカーを \A…\z へ寄せる
type: spec
status: accepted
related_ids: [FR-07, FR-14, UC-03, UC-04, UC-05, IADR-0240]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-07「報告書は利用者の確定をもって有効になる」/ FR-14「Discord からの操作」)
---

# 仕様書: 会話キーの値域のアンカーを `\A…\z` へ寄せる（#837）

## 起点

- #837（#836 の監査が実測。**develop でも同じ挙動であり退行ではない**）。
- `BotCommandParser.PeriodKeyPattern` は `^[A-Za-z0-9-]{1,32}$`。.NET の `$` は（`RegexOptions.Multiline` が無くても）
  **文字列末尾だけでなく「末尾の LF の直前」にもマッチする**ため、`abc\n` が値域を通る。

## 原因

`Parse` は `raw.Trim()` の後に**半角空白だけ**でトークンへ割る（`Split(' ', RemoveEmptyEntries)`）。
入力の途中にある LF はトークンに残り、`/report approve abc\n 1` の会話キーは `abc\n` になる。
`$` がこの LF の直前にマッチするため、LF を含む会話キーが `ReportApprove` として解析される。

実害は小さい（`Uri` / `HttpRequestMessage` がパーセント符号化するためリクエスト分割は起きず、最悪でも 404）。
ただし「値域制限を parser の段階で構造的に効かせる」という IADR-0240 決定 6 の趣旨から外れる。

## 決定

1. **`PeriodKeyPattern` を `\A[A-Za-z0-9-]{1,32}\z` にする。** `\z` は文字列の真の末尾にしかマッチしない
   （`\Z` は `$` と同じく末尾 LF の直前にもマッチするため使わない）。`^` は Multiline 無しなら `\A` と同義だが、
   対で書いて意図を明示する（同リポジトリの先例 `OpendConsoleCommand` の `PhoneCodePattern` / `PicCodePattern` と同じ形）。
2. **入力の正規化（LF を空白とみなして割る・LF を落とす）はしない。** 書式外は Unknown へ倒す（推測で補正しない＝
   #835 決定 3 と同じ方針）。
3. `IsPeriodKey`（入力補完の候補側と共用。#834）は同じ正規表現を使うため、**同じ 1 箇所の修正で候補側も直る**。
   候補側にも末尾 LF の否定形テストを足して固定する。

## 母集合（着手前に自分で引いた。2026-09-19・origin/develop `2d678da3`）

引き方: リポジトリ直下で `grep -rnE "Regex|GeneratedRegex" --include=*.cs .`（`bin/` `obj/` と `using` 行を除く）。
別形の検証入口（`[RegularExpression]` 属性・ルート制約 `:regex(`・FluentValidation の `.Matches(`）も引き、
**正規表現を取るものは 0 件**（`.Matches(` のヒットは `ContractFixtureComparer.Matches` / `BannedSymbol.Matches` で正規表現ではない）。
C# 以外: Python は追跡下に 0 ファイル（Python の `$` も同じ落とし穴を持つが対象が無い）。JavaScript / TypeScript の `$` は
`m` フラグ無しでは末尾 LF の直前にマッチしない（言語仕様）ため**同型の落とし穴が存在しない**——母集合から除く。

| # | 場所 | パターン（要旨） | `^`/`$` | 用途 | 判定 |
| --- | --- | --- | --- | --- | --- |
| 1 | `NotificationService/Domain/BotCommandParser.cs` `PeriodKeyPattern` | `^[A-Za-z0-9-]{1,32}$` | あり | **外部入力の値域検証**（URL パスへ載る） | **直す**（本件） |
| 2 | `OpendAuthGateway/Features/OpendAuth/OpendConsoleCommand.cs` `PhoneCodePattern` / `PicCodePattern` | `\A[0-9]{4,8}\z` / `\A[A-Za-z0-9]{4}\z` | 既に `\A…\z` | 外部入力の値域検証 | 直す必要なし（既に正しい形） |
| 3 | `ReportService/Domain/ReportPolicyDraft.cs` `GeneratedNoise.Patterns`（4 本） | `^（自動生成ドラフト・未確定）$` 等 | あり | **自クラスが生成した行の判別**（畳み込み） | 直さない。入力は `.Split('\n')` → `line.Trim()` を経た 1 行であり **LF を含み得ない**（`IsGeneratedLine(line.Trim())`）。値域検証でもない |
| 4 | 同 `GeneratedNoise.BlankRun` | `\n{3,}` | なし | 空行の畳み込み | 対象外（アンカー無し） |
| 5 | `OpendAuthGateway/Features/OpendAuth/ConsoleTail.cs`（4 本） | ANSI エスケープ・秘匿引数の置換 | なし | 置換 | 対象外（アンカー無し） |
| 6 | `TradeDecisionService/Domain/RationaleQuantityReconciler.cs`（2 本） | 「N 株」「N shares」の抽出 | なし | 自由文からの抽出 | 対象外（アンカー無し） |
| 7 | `Bff/AiStockTrading.Bff.Endpoints/{Assumptions,Monitor,RiskControls}BffEndpoints.cs`（各 2 本） | `error="([^"]*)"` 等 | なし | `WWW-Authenticate` ヘッダからの抽出 | 対象外（アンカー無し） |
| 8 | `Tests/AiStockTrading.Architecture.Tests/DomainSourceScan.cs` `UsingDirective` / `GlobalUsingPrefix` | `^\s*(?:global\s+)?using … ;\s*$` / `^\s*global\s+using\s` | あり | **ソースコード行の解析**（アーキテクチャ検査） | 直さない。値域検証ではない。末尾は `\s*$` で**空白類（LF を含む）を元から許す**形であり、`\z` にしても受理集合は変わらない |
| 9 | 同 `CSharpSource.cs` `UsingDirectiveLine` / `UsingAliasLine` | `^[ \t]*…[^\n]*$`（`RegexOptions.Multiline`） | あり | ソースコード全文から行単位の抽出 | 直さない。**Multiline で行頭・行末を意図して使う**（`\A…\z` にすると壊れる） |
| 10 | 同 `KnowledgeTagVocabularyTests.cs:164` | `^\s*([A-Za-z_]…)\s*,?\s*$`（Multiline） | あり | enum 本体から行単位の抽出 | 直さない（9 と同じ理由） |
| 11 | 同 `DiRegistrationScan.cs` / `DomainSourceScan.cs:57,425,484` / `KnowledgeTagVocabularyTests.cs:139,162` / `RetrievalSourceVocabularyTests.cs:90` / `CSharpSource.cs` `Identifier` | 抽出・置換 | なし | ソース走査 | 対象外（アンカー無し） |
| 12 | `RiskManagementService/Tests/`（`Regex.Replace` / `IsMatch` 計 13 箇所） | `"stopLossMethod":\d+` 等 | なし | テストが JSON を書き換える | 対象外（アンカー無し） |
| 13 | `TradeDecisionService/Tests/Features/TradeDecision/RecordStage0Decisions/Stage0DecisionRecorderTests.cs:274` | `MatchRegex("^[0-9a-f]{64}$")` | あり | **テストの表明**（自前の SHA-256 16 進出力の形） | 直さない。外部入力の値域検証ではなく、対象は自コードが整形する 16 進文字列で LF が入る経路が無い。他サービスのテストへ差分を広げない（1 issue = 1 PR） |
| 14 | `ReportService/Tests/Features/Reports/ReportNarrativePromptBuilderTests.cs`（`MatchRegex` 4 箇所） | `再計算\|改変\|変更しない` 等 | なし | テストの表明 | 対象外（アンカー無し） |

**結論: 同じ落とし穴（`$` で外部入力の値域を検証している箇所）は #1 の 1 箇所だけ**である。

### 正規表現以外の値域（issue の「段階・GFV 等」）

`BotCommandParser` の他の値域は正規表現を使っていない。

- 動詞・副コマンド（`/stage` `promote` `clear` `off` 等）は `switch` の**完全一致**であり、`clear\n` のようなトークンは一致しない（Unknown）。
- 段階（0〜3）と版番号（1 以上）は `int.TryParse`。既定の `NumberStyles.Integer` は前後の空白類（LF を含む）を許すため
  `\n2` のようなトークンは数値として解析される（一時的な探針テストで実測: `/stage promote \n2` → `StagePromote/2`、
  `/report approve abc \n1` → `ReportApprove/1`、`/gfv clear\n x` → `Unknown`。探針はコミットしていない）が、
  **結果は `int` であり LF は下流へ運ばれない**。
  会話キーと違い文字列のまま URL・ログへ載る経路が無いため、本件の射程外とし変更しない。

## 変更しないもの

- 既存の解析結果（日報・週報・月報・他コマンド）。受理する会話キーの集合は「末尾が LF のもの」を除いて 1 つも変わらない。
- トークン分割の規則（半角空白のみ）・入力の正規化をしない方針。
- 報告書サービス側・多層認証・版番号ガード（IADR-0240 決定 2・7）。

## 受け入れ基準

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | `/report approve daily-2026-08-28\n 1` が `Unknown` へ倒れる（`show` / `request-changes` も同じ） | 単体（`BotCommandParserTests`）。**修正前コードで落ちることを確認する** |
| 2 | `IsPeriodKey("daily-2026-08-28\n")` が `false` | 単体 |
| 3 | 入力補完の候補に末尾 LF を含むキーを出さない | 単体（`ReportPeriodSuggestionsTests`） |
| 4 | 既存の解析結果（日報・週報・月報・他コマンド）が変わらない | 既存単体が全件通る |
