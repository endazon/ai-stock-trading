---
title: 通知の段階ゲート表示 ModeLabel が発注先の全列挙値にラベルを与え、Stage 1 の moomoo SIMULATE を「不明(2)」と出さない（#982）
type: spec
status: accepted
related_ids: [FR-20, FR-14, FR-12, UC-06, IADR-0081, IADR-0140]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/05_screens/01_screens.md (表示規約（共通）: 発注先の呼び分け)
  - planning:docs/glossary.md (デモ取引 / SIMULATE / paper)
---

# 仕様書: 段階ゲート表示の発注先ラベルを全列挙値へ（#982）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-20（段階ゲートと発注先の 2 軸）、FR-14（Discord Bot の段階ゲート操作）、FR-12（内蔵 paper の呼び分け）
- ユースケース（UC）: UC-06（段階ゲートの承認）
- 画面（SC）: なし（Discord の `/stage status` 応答）
- 関連 ADR: なし（計画 ADR の決定は変えない）
- 関連 IADR: IADR-0081（Risk は enum を数値で返し、通知側のアダプタ 1 か所で整形する）、IADR-0140（`BrokerProvider` の序数と用語）
- 関連 issue: #982（本件）、#957 B / PR #980（発見元。契約テスト T-10-914〜916。未マージ）

## 現物で確認した（是正前・`origin/develop` a0d600b）

| 事実 | 出典 |
| --- | --- |
| `ModeLabel(int)` は `0 => "ペーパー"`・`1 => "実弾"`・`_ => "不明(n)"` の 3 枝。`2`（`MoomooSimulate`）は「不明(2)」になる | `NotificationService/Infrastructure/ExternalServices/HttpStageGateController.cs` |
| 届く値は Risk の `StageSettings.Mode`（型 `BrokerProvider`）。数値でシリアライズされる | `RiskManagementService/Domain/StageSettings.cs`・IADR-0081 決定1 |
| `BrokerProvider` は `InternalPaper=0`・`MoomooReal=1`・`MoomooSimulate=2`（append-only）。通知サービスは `Shared.Contracts` を参照済み | `Shared.Contracts/Trading/BrokerProvider.cs`・`NotificationService.csproj` |
| 用語規約: `MoomooSimulate` を「ペーパー」と呼ばない。`InternalPaper` を「SIMULATE」「デモ取引」と呼ばない。**「ペーパー」を単独で使わない** | 同 enum の doc コメント（計画 05_screens 表示規約・用語集）、用語集の「デモ取引」「SIMULATE」「paper」行 |
| 画面側の表示ラベルは `0: '内蔵 paper（擬似約定・外部へ発注しない）'`・`1: 'moomoo REAL（実弾）'`・`2: 'moomoo SIMULATE（デモ環境）'` | `frontend/src/lib/risk/contracts.ts` の `BROKER_PROVIDER_LABELS` |

## 決定

- `ModeLabel` の分岐を `BrokerProvider` の列挙子で書き、3 値すべてにラベルを与える。未知値（範囲外の整数）だけを「不明(n)」とする（fail-safe・例外を出さない方針は IADR-0081 のまま）。
- **表記は画面の `BROKER_PROVIDER_LABELS` と同一文字列**にする。用語集・表示規約に沿う既存の表記がすでにあり、Discord と画面で呼び分けを割らないため。
  クロス言語（C# / TypeScript）で一致を機械的に強制する手段は無い（`BelowStatisticalBasisWarning` と同じ残余リスク）。
- 前置きの「モード:」は変えない（最小の変更。表示の枠は本件の射程外）。
- `ModeLabel` を `internal static` にしてテストから直接呼ぶ（`InternalsVisibleTo` は既存）。

## 受け入れ基準

1. `Enum.GetValues<BrokerProvider>()` の全値について `ModeLabel` が「不明」で始まらない（列挙値が増えてラベルを足し忘れると赤）。
2. 各値のラベルが画面と同じ文字列である（0 → 内蔵 paper…、1 → moomoo REAL（実弾）、2 → moomoo SIMULATE（デモ環境））。
3. どのラベルも「ペーパー」を含まない（用語規約の否定形）。
4. 範囲外の値（3・-1）は「不明(3)」「不明(-1)」。
5. 既存の `/stage status` 整形試験（`mode: 0`）は、アサーションを「内蔵 paper」へ改めて緑。Stage 1 ＋ `mode: 2` の本文で「moomoo SIMULATE」を含み「不明(」を含まない。

テスト ID: 段階ゲートの Discord 表示は FR-10 のテスト仕様書の対象外であり、FR-20 のテスト仕様書（`T-nn` 採番）にも Discord のモード表示の行は無い。
割り当て外の ID 系列を使わないため、ID は振らない（既存 `HttpStageGateControllerTests` の慣習どおり日本語名＋コメントに起点 ID）。

## 母集合（規則 9〜11）

- **規則 9（誤りの側の文字列で走査）**: `git grep '"ペーパー\|=> "実弾"\|0=Paper\|Paper・1=Live\|不明({'`（backend・frontend/src、`.md` 除く）。
  - `HttpStageGateController.ModeLabel`: 本件の対象。
  - `HttpStageGateControllerTests` の `Contain("ペーパー")`: 本件で追随する。
  - `AuditEntryFactory` / `Stage0ExclusionSummary` の「不明(n)」: 別の列挙（as-of 除外理由）。対象外。
  - 「保護逆指値を**ペーパーで**免除」（`NotificationFormatter`・監査要約とそのテスト）: 「ペーパー」を単独で使う同型の表記だが、
    発注先ではなく損切り機構の免除文言であり、#982 の射程（段階ゲートの `ModeLabel`）外。見送り、報告に残す。
  - `BrokerSelection.cs` の「ペーパーは内蔵の擬似発注であり…」: 構成エラーの文言。射程外。
  - `StageGateCommandHandlerTests` のスタブ文字列「Stage 1（ペーパー）」: ハンドラ試験の偽の戻り値で、整形の結果を検証していない。変えない。
  - 通知サービスで発注先を表示する他の箇所: `git grep Provider -- backend/Services/NotificationService`（テスト除く）→ `ModeLabel` 以外は `e.Provider`（文字列をそのまま表示）のみ。
  - `docs/`: `git grep 'モード: \|/stage status' -- docs` → モードのラベル文字列を記載した文書は無い。
- **規則 10（この変更で新たに誤りになる記述）**: 走査語 `0=Paper`・`ペーパー`・`不明(2)`。
  - `ModeLabel` 直上のコメント「BrokerProvider（0=Paper・1=Live）」: 本件で書き換える。
  - IADR-0408 の追記（PR #980 のブランチ上、未マージ）「`MoomooSimulate`（2）を『不明(2)』と出す」: 当時の記録（凍結）。本件で変えない（develop に未着地でもある）。
- **規則 11（窓）**: 該当しない（時間差を扱う是正ではない）。

## 射程外（見送り）

- 「保護逆指値をペーパーで免除」の表記（通知・監査）。同型の用語違反だが別の文言。
- 「モード:」という前置き（FR-20 の語では「発注先」）。表示の枠の変更は本件の求めに無い。

## 検証

- `dotnet build backend/backend.slnx -v q`: 0 警告 0 エラー。
- `dotnet test NotificationService.Tests`: 583/583 合格（`HttpStageGateControllerTests` は 34 件）。
- 変異注入（`MoomooSimulate` の枝を消す＝列挙値を足してラベルを付け忘れた状態と同型）: 3 件赤
  （全列挙値の網羅・画面と同じ表記の 2 の行・Stage 1 ＋ `mode: 2` の現況照会）。復元後は緑。
