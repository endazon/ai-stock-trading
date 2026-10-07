---
title: 400 / INVALID_ARGUMENT へ ArgumentException の文言を載せる判定を、スタックの先頭のフレームから明示の印（Exception.Data の ClientVisibleArgument）へ置き換える（#1230）
type: spec
status: accepted
related_ids: [NFR-06, NFR-05, FR-03, FR-06, FR-07, FR-10, FR-11, FR-13, FR-17, FR-20, IADR-0509, IADR-0503, IADR-0450, IADR-0256]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-06 発注機能へのアクセスは利用者本人のみ・NFR-05 認証情報の秘匿)
---

# ArgumentException の文言を載せる判定を明示の印へ置き換える（#1230）

## 背景（issue の観測）

- #1206（PR #1229・IADR-0503 決定 2）は、400 / INVALID_ARGUMENT の応答へ `ArgumentException` の文言を載せるかを
  `ClientFacingErrors.IsRaisedByOwnCode`（スタックの先頭から CoreLib を飛ばした最初のフレームがサービスか `AiStockTrading.Shared.*` か）で決めた。
- PR #1229 の独立監査が Release で再現: 段階コンパイルの tier-1 で第三者の小さなメソッドが呼び出し元のサービスのフレームへインライン化されると、
  CoreLib の検証補助（`ThrowIfNullOrEmpty`）・コレクション（`Dictionary.Add` の重複キー。キーの値を引用）経由の例外が「自前」と判定され文言が載る。
  起動直後（tier-0）と温まった後（tier-1）で同じ要求の応答の文言が変わる。冷えた試験では再現しない。
- 起点: **NFR-06**。

## 受け入れ基準（issue のまま）

1. 第三者のライブラリ（または CoreLib）から投げられた `ArgumentException` は、JIT の段階・インライン化に依らず常に固定文言「要求の内容が正しくありません。」。
   tier-1 を強制した試験（`DOTNET_TieredCompilation=0`、または `[MethodImpl(AggressiveInlining)]` の第三者相当の補助）で固定する。
2. サービス自前の入力検証の利用者向けの業務の文言（Discord・画面が読む文言）は従来どおり載る。判定はスタックではなく、載せてよい文言を明示する形
   （専用の例外型、または `Exception.Data` の印）で行う。
3. 否定形: 自前のコードが `ThrowIfNullOrWhiteSpace(reason)` 等の CoreLib の補助で投げる検証も、明示の印が無い限り固定文言。**印を付けた箇所の一覧を作業仕様書に残す。**
4. IADR-0503 の残余リスクへ解消した旨を日付つきで追記する（新しい判断は新しい IADR ＝ IADR-0509）。

## 設計判断（IADR-0509）

- **印は `Exception.Data` に置く**（`AiStockTrading.Shared.Contracts.Errors.ClientVisibleArgument`。`throw new ArgumentException(…).ClientVisible();`、
  CoreLib の空欄検査の代わりに `ClientVisibleArgument.ThrowIfNullOrWhiteSpace(reason)`）。専用の型にしない理由:
  - 自前の検証は `ArgumentOutOfRangeException`（`Stage1TradeCountBounds`。試験が型と `ActualValue` を固定している）と CoreLib の補助
    （null で `ArgumentNullException`・空白で `ArgumentException`）も使う。`ArgumentException` の派生 1 つではこれらの型を保てず、
    文言（`(Parameter 'reason')` を含む）も作り直しになる。印なら型・`ParamName`・文言を 1 文字も変えずに付けられる。
  - Domain（`RiskLimitBounds`・`Stage1TradeCountBounds`）から使う必要がある。Domain が `using` してよい共有物は `Shared.Contracts` / `Shared.Kernel` だけ
    （IADR-0256 の検査）なので、印は `.NET` 標準だけで書いて `Shared.Contracts` に置く。`TestSupport.PlatformShim`（判定の置き場所）も `Shared.Contracts` を参照済み。
- **判定 `ClientFacingErrors.MessageFor(exception, logger, fixedMessage)` は印だけを見る。** 引数のサービスのアセンブリと `IsRaisedByOwnCode` は撤去する。
  印は `ArgumentException` の系統にだけ効く（同じキーを置いた別の型は印とみなさない）。内側の例外は見ない。
- 印の無いもの（第三者・CoreLib・フレームワーク・印を付けていない自前の送出）は固定文言・400 / INVALID_ARGUMENT は維持・Warning で例外ごとログ（IADR-0503 と同じ）。

## 母集合（規則 9・10。誤りの側の文字列で全走査した）

### A. スタックの判定に依っていた呼び出し（全件を印の判定へ置き換える）

走査: `git grep -n "IsRaisedByOwnCode\|ClientFacingErrors" -- 'backend/**/*.cs'`（origin/develop f0bbaa42）。

| # | 場所 | 経路 |
| --- | --- | --- |
| A1 | `ReportService/Features/Reports/ReportEndpoints.cs` `MapException` | REST の群のフィルタ・gRPC 書き込み（`ReportWriteGrpcReplies`） |
| A2 | `ReportService/Features/Reports/ReportOwnerReadGrpcService.cs` `Reply` | gRPC 読み取り |
| A3 | `RiskManagementService/Features/RiskManagement/RiskControlEndpoints.cs` `MapException` | REST・gRPC 書き込み（`RiskWriteGrpcReplies`） |
| A4 | `RiskManagementService/Features/RiskManagement/RiskControlsReadGrpcService.cs` `Reply` | gRPC 読み取り |
| A5 | `MarketMonitorService/Features/MarketMonitor/MonitorSettingsEndpoints.cs` `MapException` | REST・gRPC 書き込み（入れ替え案の適用） |
| A6 | `ConfigurationService/Features/Assumptions/AssumptionsEndpoints.cs` 群のフィルタ | REST |
| A7 | `CostControlService/Features/CostControl/CostControlEndpoints.cs` 群のフィルタ | REST |
| A8 | `TestSupport.PlatformShim/Foundation/Extensions/ClientFacingErrors.cs` | 判定そのもの（`IsRaisedByOwnCode` を撤去） |

### B. 自前の `ArgumentException` の送出点（A の経路から届くもの）と印の有無

走査: 上の 5 サービスと `backend/Shared/**` で
`git grep -nE "throw new Argument(OutOfRange)?Exception|ArgumentException\.Throw|ArgumentOutOfRangeException\.Throw"`（試験を除く）。
`ArgumentNullException.ThrowIfNull`（本文・依存の欠落＝プログラムの誤り）は全件印なし（固定文言）とし、表に載せない。

**印を付けた箇所（文言を利用者へ見せる。20 箇所）**:

| # | 場所 | 送出 | 読み手 |
| --- | --- | --- | --- |
| V1 | `ReportService/Features/Reports/ReportDraftService.cs` `BuildDraftAsync` | 期間キーと種別・対象日の不一致（`throw new ArgumentException`） | 画面（下書きの生成） |
| V2 | `RiskManagementService/Domain/RiskLimitBounds.cs` `ThrowIfOutOfRange` | リスク上限の値域外（全違反を列挙） | 画面（端点も事前に検査するが、サービスの不変条件として残る経路） |
| V3 | `RiskManagementService/Domain/Stage1TradeCountBounds.cs` `ThrowIfOutOfRange` | 最小取引件数の値域外（`ArgumentOutOfRangeException`） | 同上 |
| V4 | `RiskManagementService/Features/RiskManagement/RiskSettingsService.cs` `ThrowIfUnsupportedByAccount` | 口座種別が対応しない商品種別 | 画面（取引ガードの設定） |
| V5 | 同 `UpdateStage` | 段階の既定発注先が既知でない | 画面 |
| V6 | 同 `RequireActorAndReason`（`reason`） | 理由の空欄（`ClientVisibleArgument.ThrowIfNullOrWhiteSpace`） | 画面（設定の変更 4 種） |
| V7 | `RiskManagementService/Features/RiskManagement/KillSwitchService.cs` `RequireActorAndReason`（`reason`） | 理由の空欄 | Discord（gRPC 書き込み）・画面 |
| V8 | `RiskManagementService/Features/RiskManagement/PauseService.cs` `RequireActorAndReason`（`reason`） | 理由の空欄 | Discord（gRPC 書き込み）・画面 |
| V9 | `MarketMonitorService/Features/MarketMonitor/MonitorSettingsEndpoints.cs` `MarketOf` | `market は必須です。` | 画面 |
| V10 | `MarketMonitorService/Features/MarketMonitor/MonitorWatchlistService.cs` `Add` | 既に監視対象 | 画面 |
| V11 | 同 `Add` | Finnhub の巡回に収まらない追加 | 画面 |
| V12 | 同 `Remove` | 監視対象にない | 画面 |
| V13 | 同 `ApplyProposal` | 案の形（`WatchlistProposalPlan.ValidateShape` の文言） | Discord（gRPC 書き込み）・REST |
| V14 | 同 `Normalize`（`symbol`） | 銘柄コードの空欄（`ClientVisibleArgument.ThrowIfNullOrWhiteSpace`） | 画面 |
| V15 | 同 `Normalize`（`market`） | 未定義の市場 | 画面 |
| V16 | 同 `RequireActorAndReason`（`reason`） | 理由の空欄 | 画面 |
| V17 | `MarketMonitorService/Features/MarketMonitor/MonitorSettingsService.cs` `UpdateMovementThreshold`・`UpdateCooldown` | 変動閾値・クールダウンの値域外（2 箇所） | 画面 |
| V18 | 同 `Replace` | 値域外（2）・銘柄コードの無い要素・未定義の市場・重複・巡回に収まらない置換（計 6 箇所） | 画面・API |
| V19 | 同 `RequireActorAndReason`（`reason`） | 理由の空欄 | 画面 |
| V20 | `ConfigurationService/Features/Assumptions/AssumptionsService.cs` `Update`（`reason`） | 理由の空欄 | 画面（前提条件） |

（V17・V18 は 1 行に複数の送出点をまとめた。送出点の数は V1〜V16・V19・V20 の 18 と V17 の 2・V18 の 6 で計 26。）

**印を付けない箇所（固定文言になる）と理由**:

| 場所 | 理由 |
| --- | --- |
| 各サービスの `ThrowIfNullOrWhiteSpace(actor)`（報告書・リスク管理・市場監視・前提条件） | `actor` は認証（トークンの名前）から入る。利用者の入力ではなく、空はサーバー側の不整合 |
| `ReportAppService` / `ReportDraftService` の `ThrowIfNullOrWhiteSpace(periodKey / PeriodKey)` | 経路の値（空白だけの経路は利用者の操作では作られない）。T-10-2422 が固定文言になることを固定する |
| `ReportGenerationDeferralTracker`・`ReportRegenerationLedgers`（「断った試行の結果ではありません」） | 内部の不変条件 |
| `RiskManagementService` の保存（`EfPortfolioLedgerStore` 等の `symbol` / `orderId`）・`EfWithdrawalNotificationStore` | 内部の不変条件（端点で検証済みの値しか届かない） |
| `EntryBlockersService.Build`（`symbol`・未定義の市場） | REST・gRPC の読み取りとも端点が先に検証して自前の文言で 400 / INVALID_ARGUMENT を返す |
| `BorrowFeeAccrualService`・`ShortSellBorrowObservation.Unknown`・`ControlViolationTally`・`GoodFaithViolationTally`・`ShortSellingLimits` | 内部の計算・観測（利用者の入力ではない） |
| `MinimumEntryNotional.Validate` | 起動時の構成の検査（応答の経路ではない） |
| `RiskReadWireMapping`（新規建ての可否の理由の写し） | 内部の不変条件 |
| `CostControlService` の台帳（`month`） | 内部の値（端点から空は届かない） |
| `Shared.*`（`Currency`・`TradeExpenseClassification`・`BusinessMetrics`・`PolicyTakeProfitConditions`・`TradeExpenseLedger`・`RetentionScope`・`LogSanitizer`・`FinnhubDailyVolumeEstimator`・`TokenBucket`） | 内部の計算・構成・計器。IADR-0503 は `Shared.*` の送出を「自前」として載せていたが、利用者向けの文言は無い |

### C. 文書の追随（規則 9: 誤りの側の文字列「自前のコードが投げた」「IsRaisedByOwnCode」で走査）

- 呼び出し側のコメント 7 箇所（A1〜A7）を「利用者へ見せる印のあるものだけ」へ直した。
- `docs/tests/FR-10_risk-controls-tests.md` の該当節: T-10-2394・T-10-2395 は判定の単体（スタック）の試験で、撤去した（行を「廃止」として残す）。
  T-10-2396・T-10-2401 の記述を印の判定へ直し、T-10-2418〜T-10-2426 を足した。残余リスクの記述を更新した。
- IADR-0503 本文の残余リスク・索引行へ日付つき追記（本文は書き換えない）。`.ai-context/specs/20261008_1206_*` は凍結記録なので触らない。

## 実装タスク

1. `Shared.Contracts/Errors/ClientVisibleArgument.cs`（印・判定・印つきの空欄検査）。
2. `ClientFacingErrors.MessageFor` を印の判定へ。`IsRaisedByOwnCode`・アセンブリの引数を撤去し、A1〜A7 の呼び出しを直す。
3. B の V1〜V20 に印を付ける。
4. 試験（下）。テスト仕様書・IADR-0509・索引・IADR-0503 の追記。

## 試験の写像

| 受け入れ基準 | 試験 |
| --- | --- |
| 1（判定の単体） | T-10-2418: `AggressiveInlining` の第三者相当の補助（重複キー）を 1,000 回・正規表現・自前の直接の送出・自前の `ThrowIfNullOrWhiteSpace`（空白・null）・await の後・投げていない例外 → すべて固定文言 |
| 1（HTTP・gRPC） | T-10-2420（報告書: REST・gRPC 読み取り・gRPC 書き込みを 200 回）・T-10-2424（リスク管理: REST・gRPC 読み取り）・T-10-2426（市場監視: REST）。既存の T-10-2392・T-10-2397・T-10-2398・T-10-2400・T-10-2402 |
| 2 | T-10-2419（印は型・文言・`ParamName`・`ActualValue` を変えない）・T-10-2421（報告書: 印のある送出を REST・gRPC 2 面で保つ）・T-10-2423（リスク管理: 理由の空欄を REST・gRPC で保つ）・T-10-2424（同: 印のある送出）・T-10-2425（市場監視: 案の形の検証を REST・gRPC で保つ）。既存の T-10-2391・T-10-2399・T-10-2401 |
| 3（否定形） | T-10-2418（自前の `ThrowIfNullOrWhiteSpace`）・T-10-2420（`unmarked-throw-if`）・T-10-2422（実際の経路: 下書きの生成の空白の期間キー） |

`DOTNET_TieredCompilation=0` で走らせる試験は置かない（同じプロセスの中で切り替えられず、試験の実行の構成を分けることになる）。
判定がスタックを読まなくなったため、インライン化の有無は結果に関係しない。`AggressiveInlining` の補助と繰り返しで「段階が上がっても同じ」を併せて固定する。

## 検証

- `dotnet build backend/backend.slnx`（警告 0）・`dotnet format backend/backend.slnx --verify-no-changes`・影響する試験プロジェクト・Architecture.Tests・文書系の検査器。
- 変異: Y1（判定を常に「見せる」）・Y2（常に「見せない」）・Y3（印つきの空欄検査が印を付けない）を当て、赤になることを確かめる。

## 結果

- 試験 ID: T-10-2418〜T-10-2426（テスト仕様書 FR-10 の「例外の文言を応答へ載せるのは業務の例外だけにする」節）。T-10-2394・T-10-2395 は廃止。
- 変異の結果は IADR-0509 の表。
- 配備: 報告書・リスク管理・市場監視・前提条件・費用統制のイメージの作り直しが要る（`Shared.Contracts` と `TestSupport.PlatformShim` の変更は全サービスに入るが、挙動が変わるのは呼ぶ 5 サービスだけ）。
