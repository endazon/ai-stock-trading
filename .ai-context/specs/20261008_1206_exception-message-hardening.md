---
title: 例外メッセージを応答へそのまま返す経路（報告書の InvalidOperationException→409・ArgumentException→400・gRPC INVALID_ARGUMENT）を、業務の例外だけ文言を載せ、それ以外は固定文言へ寄せる（#1206）
type: spec
status: accepted
related_ids: [NFR-06, NFR-05, FR-06, FR-07, FR-10, FR-13, FR-17, IADR-0503, IADR-0496, IADR-0450, IADR-0405, IADR-0024]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-06 発注機能へのアクセスは利用者本人のみ・NFR-05 認証情報の秘匿)
---

# 例外メッセージを応答へ返す経路を固定文言へ寄せる（#1206）

## 背景（issue の観測）

- #1192 の独立監査（#1205）で、応答本文へ例外の `Message` をそのまま載せる経路が残っていると確認された。
  - `ReportEndpoints.MapException` は**任意の** `InvalidOperationException` を 409 に写し `e.Message` を返す。EF Core やフレームワークが投げる
    `InvalidOperationException`（DbContext の並行使用・`Single()` の要素なし・未登録のサービス等）も同じ経路で返る。
  - 複数の `ArgumentException` 経路が `e.Message` を返す。
  - gRPC の `InvalidArgument` も `e.Message` を status detail へ載せる。
- 起点: **NFR-06**（セキュリティ）。例外メッセージは内部構造（テーブル名・制約名・接続先など）を含み得る。

## 受け入れ基準（issue のまま）

1. EF / フレームワーク由来の `InvalidOperationException` を投げても、応答本文に例外メッセージが含まれないことをテストで固定する。
2. 業務エラーの 409 / 400 応答の文言は維持する（既存テストが緑のまま）。

## 母集合（規則 9・10。誤りの側の文字列で全走査した）

走査: `git grep -nE '\b(e|ex|exception|err)\.Message\b' -- 'backend/**/*.cs'`（試験を除く）、
`git grep -nE '\.Message\b'`（変数名を問わず）、`git grep -nE 'Results\.Problem|detail:|Detail\s*=|RpcException\(new Status'`、
`git grep -nE '(error|message|detail|reason)\s*=\s*\w+\.(Message|ToString\(\))'`、`InnerException|GetBaseException`。origin/develop fae477af。

### 応答（HTTP 本文・gRPC の status detail）へ例外の文言が載る経路 —— **全件を本件の対象にする**

| # | 場所 | 例外 → 応答 | 処置 |
| --- | --- | --- | --- |
| A1 | `ReportService/Features/Reports/ReportEndpoints.cs` `MapException`（REST 群のフィルタと gRPC 書き込み `ReportWriteGrpcReplies.RunAsync` が共有） | 任意の `InvalidOperationException` → 409 `{error: e.Message}` | 業務の例外だけ専用の型 `ReportAlreadyConfirmedException`（`InvalidOperationException` の派生）で識別し 409＋文言を維持。それ以外の `InvalidOperationException` は写さない（未処理例外として #1192 の共通の例外処理が 500 の ProblemDetails にし、Error ログへ例外ごと出す。gRPC は Grpc.AspNetCore が詳細なしの `UNKNOWN` にしてログへ出す） |
| A2 | 同 `MapException` | `ArgumentException` → 400 `{error: e.Message}` | 下の「自前の送出か」の判定で、自前のコードが投げたものは文言を維持、フレームワーク等が投げたものは固定文言（400 は維持）・Warning ログへ例外ごと出す |
| A3 | `ReportService/Features/Reports/ReportOwnerReadGrpcService.cs` `Reply` | `ArgumentException` → `INVALID_ARGUMENT`＋`e.Message` | A2 と同じ |
| A4 | `RiskManagementService/Features/RiskManagement/RiskControlEndpoints.cs` `MapException`（REST と gRPC 書き込み `RiskWriteGrpcReplies.RunAsync` が共有） | `ArgumentException` → 400 | A2 と同じ |
| A5 | `RiskManagementService/Features/RiskManagement/RiskControlsReadGrpcService.cs` `Reply` | `ArgumentException` → `INVALID_ARGUMENT` | A2 と同じ |
| A6 | `MarketMonitorService/Features/MarketMonitor/MonitorSettingsEndpoints.cs` `MapException`（REST と `WatchlistOwnerWriteGrpcService` が共有） | `ArgumentException` → 400 | A2 と同じ |
| A7 | `ConfigurationService/Features/Assumptions/AssumptionsEndpoints.cs` 群のフィルタ | `ArgumentException` → 400 | A2 と同じ（`AssumptionsConcurrencyException` → 409 は自前の業務の型なので維持） |
| A8 | `CostControlService/Features/CostControl/CostControlEndpoints.cs` 群のフィルタ | `ArgumentException` → 400 | A2 と同じ |

`ReportConcurrencyException`（自前の型・409）と `DbUpdateConcurrencyException`（既に固定文言）は不変。

### 除外（応答へは載らない、または載るのは自前の業務の文言）

| 場所 | 除外の理由 |
| --- | --- |
| `ReportService/.../RegenerateReport/Endpoint.cs`・`RevisePolicy/Endpoint.cs`・`ReportEndpoints.ReviewResult` の `result.Message` | 結果型の文言を自前で組み立てている（例外の文言ではない。各サービスの組み立てに `ex.Message` は 0 件） |
| `OrderExecutionAppService.cs:947`（`rejectReasonMessage: ex.Message`） | 応答ではなく試行の記録（監査台帳）へ入る。監査台帳の自由記述は `AuditFreeText` が接続先を伏せ・長さを切る（IADR-0405 の線引き。本件の対象外） |
| `MoomooHistoricalBarSource.cs:108`（欠測の記録） | バックテストの欠測の記録。中身は OpenD の非成功応答（retMsg）で、IADR-0405 決定 2 の「載せてよい」側 |
| `TradeDecisionParser.cs:156` | LLM の出力の解析失敗の記録（判断の記録・ログ）。HTTP 応答ではない |
| `MoomooBrokerAdapter.cs:232`・`MinimumEntryNotionalOptionsLoader.cs:36` | 例外の連鎖（内側の例外として包む）と起動時の構成エラー。応答ではない |
| `OrderFeeProbeCommand.cs`・`KLineQuotaProbeCommand.cs` | 運用者が手で回す計測コマンドの標準出力（長い数字列は伏せ済み）。応答ではない |
| `GrpcPositionStore.cs`・`HttpPositionStore.cs` | 計器とログへの報告。応答ではない |
| `CompositionWiringGuard.cs` | 試験支援（本番の組み立てを検査する試験の失敗文） |
| `NotificationGrpcCalls.cs:61`（`ex.Status.Detail`） | 受け手の側（提供側が返した detail を読む）。提供側を本件で直すので連動して安全になる |
| `ExceptionHandlingExtensions.cs` | #1192 の共通の例外処理（既に type/title/status/traceId のみ） |

## 設計判断（IADR-0503）

- **`InvalidOperationException`（A1）は専用の型で識別する**（issue の対応案 1）。業務として投げているのは「確定済み報告書は変更できません」
  の 2 箇所（`EfReportStore`・`InMemoryReportStore`）だけ（`ReportService` の `throw new InvalidOperationException` の全走査。
  台帳の「試行がありません」は内部の不変条件の破れ、`ReportGrpcCalls.ResolveAddress` は構成の解決）。型は `InvalidOperationException` の派生にし、
  自動生成（`ReportAutoGenerator` の `catch (InvalidOperationException)`）の挙動を変えない。
- **写さない `InvalidOperationException` は 409 ではなく未処理例外にする**（issue の対応案 2「#1205 の `UseAiStockTradingExceptionHandler` と同じ形」）。
  フレームワーク由来の `InvalidOperationException` は利用者の操作と状態の競合ではなく、サーバーの不具合であり 500 が正しい。ログは共通の例外処理が
  Error で例外ごと出す（traceId で突き合わせる）。
- **`ArgumentException`（A2〜A8）は送出元で分ける**。入力検証の業務の文言（「market は必須です。」など）を利用者・Discord Bot・画面がそのまま読むため
  （`HttpStageGateController` は 400 の `error` を利用者へ返す、画面は 400 の詳細を併記する）、型の付け替えで全送出点を書き換えると変更が
  5 サービスの送出点の全数に比例して膨らむ。そこで「例外のスタックの先頭（CoreLib の送出用の補助関数を飛ばした最初のフレーム）が、そのサービスの
  アセンブリか `AiStockTrading.Shared.*` か」で判定し、自前の送出だけ文言を載せる。フレームワーク・EF・ドライバが投げたものは固定文言
  「要求の内容が正しくありません。」（400 は維持）にし、Warning で例外ごとログへ出す。判定は共有の 1 関数（`ClientFacingErrors`）に置く。
- 状態コード・gRPC の状態の分類・`error` の欄の名前は変えない（受け手が読む機械的な形を保つ）。

## 実装タスク

1. `ReportService/Common/Exceptions/ReportAlreadyConfirmedException.cs` を足し、`EfReportStore`・`InMemoryReportStore` の送出を置き換える。
2. `ReportEndpoints.MapException` を `ReportAlreadyConfirmedException` だけ 409＋文言にし、`ArgumentException` は `ClientFacingErrors` を通す。
3. `TestSupport.PlatformShim/Foundation/Extensions/ClientFacingErrors.cs`（判定・固定文言・ログ）。
4. A3〜A8 を同じ関数へ寄せる（gRPC の読み取りサービスは `ILoggerFactory` を受ける）。
5. 試験（下）。テスト仕様書（FR-10 の #1192 の節の隣）へ ID を足す。

## 試験の写像

| 受け入れ基準 | 試験 |
| --- | --- |
| 1（REST） | 報告書のホストで EF 由来の `InvalidOperationException`（接続文字列に似た目印を含む）を投げると 500 の ProblemDetails・本文に目印も例外メッセージも無い |
| 1（gRPC 書き込み） | 同じ例外で gRPC の確定を呼ぶと `UNKNOWN`・detail に目印が無い |
| 1（ArgumentException の延長） | フレームワークが投げた `ArgumentException`（目印つき）を REST の各群・gRPC の読み取りで受けても 400／`INVALID_ARGUMENT` で固定文言、目印が無い |
| 2 | 確定済みの報告書の変更は 409 で文言「確定済み報告書 … は変更できません。」のまま／自前の `ArgumentException` は 400 で文言のまま／既存試験が緑 |
| 判定の単体 | 自前の送出（直接・`ThrowIfNullOrWhiteSpace`・async）は真、フレームワークの送出・スタックの無い例外は偽 |

## 検証

- `dotnet build backend/backend.slnx`（警告 0）・`dotnet format --verify-no-changes`・影響サービスの試験・Architecture.Tests・文書系の検査器。
- 変異（≥3）を当てて赤になることを確かめ、結果を IADR とテスト仕様書へ記録する。

## 結果

- 試験 ID: T-10-2388〜T-10-2402（テスト仕様書 FR-10 の「例外の文言を応答へ載せるのは業務の例外だけにする」節）。
- 既存の試験の変更は 1 件: リスク管理の `T_10_1051`（gRPC 読み取りの ArgumentException → INVALID_ARGUMENT）。投げ手は試験の代役（サービスのコードではない）なので、
  状態の期待は保ち、detail は固定文言を期待するよう直した（IADR-0503 の意図どおりの変化）。他の既存試験は無変更で緑。
- 変異 M1〜M7（IADR-0503 の表）はすべて赤。
- 配備: report・risk-management・market-monitor・configuration・cost-control のイメージの作り直しが要る。
