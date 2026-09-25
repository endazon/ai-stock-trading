---
title: 送り手の型による契約テストが無かったサービス間の読み取り（日報方針・費用統制・報告書のレビュー・段階ゲート・監査台帳）に契約テストと送り手側の JSON 設定の固定を足す（#957 の B の残り）
type: spec
status: accepted
related_ids: [FR-10, FR-01, FR-04, FR-06, FR-07, FR-11, FR-14, FR-16, FR-20, UC-06, ADR-0003, IADR-0408, IADR-0390, IADR-0031, IADR-0028, IADR-0081, IADR-0199, IADR-0240]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-07 日報方針 / FR-11 監査 / FR-14 通知 / FR-20 段階ゲート)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (不確実な場合は取引しない)
---

# 仕様書: サービス間の読み取り契約の残り（#957 の B 群の送り手型による契約テスト）

## 起点

- #957「B. 送り手の型による契約テストが無い照会」のうち、1 本目（PR #970・IADR-0408）が残した行:
  判断 ← 報告書の日報方針（`/reports/daily-policy`）／情報収集 ← 費用統制（`/costs/state`）／通知 ← 報告書のレビュー
  （`/reports/{periodKey}/review`）／通知 ← リスク管理の段階ゲート（現況・遷移・撤退評価）／報告書 ← 監査台帳 4 本（`/audit/events/by-type`）。
- 🔴 **射程外（#957 に残す）**: C の全行（判断 ← 市場監視の監視銘柄、通知の kill switch・pause・GFV の操作結果、報告書の稼働／段階の照会）。
  受け手の実行時の挙動は変えない（本件は契約テストだけ。費用統制の受け手 `HttpCostControlGate` は #915 で nullable 化済み）。

## 🔴 実測（コードで確認・`origin/develop` = `058475d8`）

| 事実 | 出典 |
| --- | --- |
| 報告書・費用統制の Program.cs は web 既定に `JsonStringEnumConverter` を足す（列挙は文字列）。監査・リスク管理は web 既定のまま | 各 `Program.cs` の `ConfigureHttpJsonOptions` |
| 費用統制の応答 `CostControlDecision(State, IntervalMultiplier)` は計算プロパティ `IsHalted` を持ち、本文に `isHalted` が載る。受け手は `isHalted` と `intervalMultiplier` だけを読む | `CostControlService/Domain/CostGovernor.cs`・`HttpCostControlGate.cs` |
| 監査の応答は `AuditEntry` の一覧。報告書の 4 アダプタは `(Id, EventType, Detail)` だけを private DTO で読み、`Detail` は共有の `AuditDetailJson.Options` で本文の型へ戻す。既存テストは detail こそ送り手と同じ設定だが、外側は手書きの匿名型 | `AuditService/Domain/AuditEntry.cs`・`ReportService/Infrastructure/ExternalServices/Http{BorrowFee,FxSourceStatus,LlmUsage,TradeRationale}*.cs` |
| 段階ゲートの応答型は `StageGateStatus`・`StageTransitionResult`・`WithdrawalAssessment`（リスク管理）。通知は列挙を int で受ける射影 DTO で読む | `HttpStageGateController.cs` |
| リスク管理の JSON 設定は T-10-805 が本物の Program.cs で固定している（設定は全エンドポイント共通） | `RiskManagementService/Tests/Features/RiskManagement/ReadContractWireFormatTests.cs` |
| 報告書・費用統制・監査の送り手側で JSON 設定を固定するテストは無い | 走査（`ReadContractWireFormat` / `ConfigureHttpJsonOptions` を Tests で grep） |

## 決定（記録は IADR-0408 への日付つき追記。新 IADR は作らない）

1. 各受け手のテストプロジェクトに送り手サービスへのテスト専用参照を extern alias で足す（`ReportWorker`・`CostControlWorker`・`AuditWorker`・
   `RiskManagementWorker`。`AiStockTrading.IntegrationTests` と同じ別名）。本体の `.csproj` は参照しない。
2. 契約テストは送り手の本物の型を**送り手の実際の JSON 設定**で直列化した応答を受け手のアダプタに読ませる（報告書・費用統制＝web 既定＋文字列列挙、
   監査・リスク管理＝web 既定）。監査は外側の組み立てに送り手の `AuditEntryFactory` を使う（要約・相関 ID を含む本物の記録）。
3. 送り手側の固定（T-10-805 と同じ形）を報告書・費用統制・監査に 1 本ずつ足す: 本物の Program.cs の応答本文と、DI から引いた応答値を
   その設定で直列化したものが JSON の木として一致すること。
4. 変えない: 受け手・送り手の実装と JSON 設定。

## 受け入れ基準

1. （T-10-910）判断: `ConfirmedDailyPolicy` を報告書の設定で直列化した応答から日付と方針を読める。
2. （T-10-911）情報収集: `CostControlDecision`（通常・間隔延長・停止）を費用統制の設定で直列化した応答から、停止と倍率を読める。
3. （T-10-912）費用統制の本物の Program.cs の `/costs/state` の本文が web 既定＋文字列列挙の直列化と一致し、`state`（文字列）・`intervalMultiplier`・`isHalted` の 3 項目である。
4. （T-10-913）通知: `ReportReviewView` を報告書の設定で直列化した応答から版番号と未供給の入力の警告を読める。
5. （T-10-914〜916）通知: `StageGateStatus`／`StageTransitionResult`（受理 200・受理不能 422）／`WithdrawalAssessment` を web 既定で直列化した応答から、
   現段階・モード・未充足の基準・履歴・引き下げ警告／受理と拒否・拒否理由／撤退理由・自動停止・降格提案を読める。
6. （T-10-917〜920）報告書: `AuditEntryFactory` で作った `AuditEntry` を web 既定で直列化した応答から、借株料・為替の情報源・LLM 使用量・判断根拠を読める。
7. （T-10-921）報告書の本物の Program.cs の `/reports/daily-policy`・`/reports/{periodKey}/review` の本文が web 既定＋文字列列挙の直列化と一致する。
8. （T-10-922）監査の本物の Program.cs の `/audit/events/by-type` の本文が `AuditEntry` の一覧の web 既定の直列化と一致する。
9. （T-10-923）変異注入: 送り手の通信路の名前の変更（各 1 項目）と送り手の JSON 設定の変更で対応するテストが赤になる（実測をテスト仕様書へ）。
10. 触ったテストプロジェクトの既存テストは緑。

## 🔴 母集合（規則 9〜11）

**規則 9（誤りの側の文字列で走査）**: `日報方針・費用統制` / `契約テストがまだ無い` / `#957 に残` / `送り手の型による契約テストが無い` と、
受け手のアダプタ名 8 つを `*.md`（確定済みの `.ai-context/specs`・`superpowers` を除く）で走査した。

| 箇所 | 扱い |
| --- | --- |
| `docs/tests/FR-10_risk-controls-tests.md` の #943 節の残余リスク（「日報方針・費用統制・…段階遷移・監査台帳・通知の操作結果の読み取りには…まだ無い」） | **規則 10**: 誤りになる。直し、T-10-910〜923 の節を足す |
| `IADR-0408` の残余リスク「#957 の B のうち日報方針・費用統制・…は本件に含まない」 | 凍結記録。日付つき追記で解消を書く（README の索引行にも追記） |
| `IADR-0390` 末尾の追記「#943 の走査表の残り（日報方針・費用統制・報告書のレビュー・段階遷移・監査台帳・…）は #957 に残る」 | **規則 10**: B の分は誤りになる。日付つき追記（README の索引行にも追記） |
| `IADR-0399` の残余リスク | **変えない**（報告書・サイジングの堅牢化の記述で、本件と無関係） |
| `IADR-0028` / `0031` / `0051` / `0081` / `0180` / `0199` / `0254` / `0269` ほかのアダプタへの言及 | **変えない**（挙動は変えていない） |
| `docs/tests/FR-20_staged-gates-tests.md` の `HttpStageGateController` への言及 | **変えない**（既存のテストの記述のまま正しい） |

**規則 10（導出値）**: 残りの件数は #970 の報告を転記せず、#957 本文の B 表 7 行を数え直した（本件で 5 行、1 本目で 2 行＝全 7 行が埋まる）。C の 3 行は残る。

**規則 11（窓）**: 対象の窓は「送り手だけを先に配備した」間。増える側（送り手が項目名・設定を変えて出す）のプローブは T-10-923 の変異注入、
減る側（受け手だけが新しい名前を期待する）は本件で受け手を変えないので該当しない。形（契約テストのみ／受け手の堅牢化／両方）の選択は
#957 の射程 1 に従い契約テストのみとした。🔴 したがって窓の間、受け手は改名された項目を従来どおり既定値で読む（例: 日報方針の `summary` の
改名は方針 null のまま判断へ渡る）。契約テストは改名のマージを止めるだけである（残余リスクとして IADR-0408 の追記とテスト仕様書に書く）。

**テスト ID**: 割り当て範囲 **T-10-910〜T-10-929** のうち 910〜923 を使う（`git grep` で origin/* 全ブランチに未使用を確認）。924〜929 は未使用。
