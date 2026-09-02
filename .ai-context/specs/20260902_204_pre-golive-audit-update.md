---
title: 実環境構築前 実装監査の更新版（#204）— 結線の実走査・安全性再裏取り・Conditional-Go 条件の書き直し
type: spec
status: approved
related_ids: [FR-01, FR-02, FR-03, FR-04, FR-05, FR-06, FR-07, FR-08, FR-09, FR-10, FR-11, FR-12, FR-13, FR-14, FR-15, FR-16, FR-17, FR-18, FR-19, FR-20, FR-21, NFR, UC-01, UC-02, UC-03, UC-04, UC-05, UC-06, UC-07, SC-01, SC-02, SC-03, ADR-0008, ADR-0016, ADR-0023, ADR-0027, IADR-0111, IADR-0129, IADR-0259]
author: endazon (with Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0027_borrow-fee-accrual-recording.md
---

# 仕様書: 実環境構築前 実装監査の更新版（#204）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01〜FR-21 の全件（監査対象）
- 非機能要件（NFR）: NFR-01〜NFR-17 の全件（監査対象）
- ユースケース（UC）: UC-01〜UC-07 の全件（監査対象）
- 画面（SC）: SC-01〜SC-03 の全件（監査対象）
- 関連 ADR: 計画 ADR-0001〜ADR-0029 の全件（監査対象。初版監査は ADR-0001〜0008 のみ）
- 起点 issue: [#204](https://github.com/endazon/ai-stock-trading/issues/204)（**監査資料の本体は issue 側**。本書はその**証跡**であり、走査の母集合・再現手順・行番号つきの根拠を残す）

## 目的・背景

[#204](https://github.com/endazon/ai-stock-trading/issues/204) の初版監査（2026-07-19・AST develop `a48835a`）は **Conditional-Go** と判定した。
その後、全面再実装（[#344](https://github.com/endazon/ai-stock-trading/issues/344)）と VSA 移送（`IADR-0259`）により
**トレース表・安全性確認の相当部分が現状と一致しなくなった**。

さらに 2026-08-28 のコメントが、**初版監査が「実装済み」と評価し得た統制のうち実際には効いていなかったもの 7 件**を挙げ、
いずれも「**コードは存在するが結線されていない**」型であり、**ファイルの実在や型の定義を数える監査では検出できない**と申し送っている。

本書は、その申し送りに従って **結線（DI 登録 → 本番の呼び出し元 → イベント発行 → consumer 受信）を実際に辿った**
更新版監査の証跡である。**判断（Go / No-Go）は利用者に留保し、AI は Conditional-Go の条件を更新するに留める。**

## 対象範囲

| 項目 | 値 |
| --- | --- |
| 監査日 | 2026-09-02 |
| 対象コミット | AST `develop` = `0979f17d`（`chore(FR-01,ADR-0020): Finnhub Free の実効レート制限を実測し監視銘柄数上限を検討する (#624)`） |
| 監査ブランチ | `docs/NFR-pre-golive-audit-20260902` |
| 計画書 | 隣接クローン `../project-planning`（読み取り専用・pin 無し。`ADR-0029` 決定2） |
| 実クラスタ | rancher-desktop k3s / namespace `ai-stock-trading`（**読み取り専用で観測**。`kubectl get` / `kubectl logs`） |
| 変更しないもの | バックエンド・フロントエンドのコード（欠陥は issue 起票候補として列挙するに留める）、`docs/blocked-tasks.md` |

## 方法 —— 「コードは存在するが結線されていない」を機械で狙う

初版監査の見落とし 7 件はすべて同じ形をしている。**型は在り、テストも通り、しかし本番の実行経路から呼ばれない。**
これを狙うため、本監査は次の 5 つの機械走査を行った。**走査の母集合と除外を明示する**（除外を書かない走査は、
次に同じ走査をした者が同じ結論に辿り着けない）。

### 走査 1: 本番コードから一度も参照されない型

- **母集合**: `backend/**/*.cs` から `*/bin/*` `*/obj/*` `*/Tests/*` `*TestSupport*` `*/Migrations/*` を除いたもの（＝本番コードのみ）
- **抽出**: `public` / `internal` の `class` / `record` 宣言の型名
- **判定**: その型名を**本番コードの母集合内で**単語一致検索し、ヒットしたファイルが**自分自身の定義ファイルだけ**なら「本番から参照ゼロ」
- **既知の偽陽性**（判定から除外し、個別に確認した）:
  - Wolverine ハンドラ（`Infrastructure/Steps/*Handler`）——**明示登録せずアセンブリ走査で発見される**（`IADR-0129` / `IADR-0268`）ため型名の参照が無くて正常
  - 拡張メソッドだけを持つ静的クラス（`*Endpoints`）——呼ばれるのは `MapXxx()` であって型名ではない
  - HTTP エンドポイントの引数 DTO（`*Request`）・表示 DTO（`*View`）
  - `*DbContextFactory`（EF の設計時ツールがリフレクションで解決する）
  - `InMemory*` 永続化（テスト専用の差し替え。本番は EF 実装が登録される）
  - **入れ子の結果 record**（`XxxService.cs` の中で宣言され、呼び出し側が `var` で受ける戻り値型）——外側の型名で呼ばれるため内側の名前は参照されない。`ControlActivationReport` / `OpenDUptimeDistribution` / `MaintenanceMarginEvaluation` などがこれに当たり、**個別確認で結線済みと判明した**

### 走査 2: イベントの publisher / consumer 突合

- **母集合**: `backend/Shared/AiStockTrading.Shared.Contracts/Events/*.cs` の全 47 件（`AuditDetailJson` `EventTypeDiscovery` はイベントでないため除外し **45 件**）
- **publisher**: 本番コード（`/Tests/` 除く）に `new <Event>(` があるか
- **consumer**: 本番コードに `Handle(<Event> ` があるか
- **判定**: **45 件すべてに consumer が在る。** ただし publisher 側は「文字列が在る」ことしか見ないため、
  **その publisher 自身が本番から呼ばれるかを走査 1 と突き合わせて二段で確認した**——これが D-1（FR-15）と D-2（ADR-0016 決定15）を出した。

### 走査 3: 常駐（`IHostedService`）の宣言と登録の突合

- **母集合**: `: BackgroundService` / `: IHostedService` を継承する本番クラス（**14 件**）
- **突合先**: `backend/Services/*/Program.cs` の `AddHostedService`
- **結果**: **14 件すべて登録済み**（未登録の常駐はゼロ）。ただし登録が `if (...)` の内側にあるものは、その条件（構成キー・ブローカ選択）を記録した

### 走査 4: 安全既定（`NoOp*` / `Placeholder*` / `InMemory*` / `const false`）の DI 登録

- **母集合**: 同名接頭辞を持つ本番クラスと、`Options` の `public bool Enabled`
- **突合先**: `Program.cs` の登録行と、`deploy/helm/ai-stock-trading/values.yaml` / `values-local.yaml` の投入有無
- **意図的な閂との区別**: `LiveTradingGate.LiveTradingReleased`（`const false`）は**実弾未解禁という設計上の閂**であり、
  未結線ではない（`IADR-0111`）。同様に `UnavailableMaintenanceMarginSnapshotSource` は「値を発明しない」ための安全既定である

### 走査 5（本監査で追加）: 実クラスタの実効構成の観測

**`values.yaml` / `values-local.yaml` を読むだけでは実環境の発動条件は決まらない。**
`k8s-local-deploy.sh` が `--set` で上書きするため、**実際に動いている Pod の env が唯一の権威**である
（[#626](https://github.com/endazon/ai-stock-trading/issues/626) が同じ食い違いを扱っている）。
そこで `kubectl get deploy -n ai-stock-trading -o json` で全 Deployment の env を吸い出し、
**`valueFrom`（秘匿参照）と空文字を区別して**記録した——Kubernetes は `value: ""` を `omitempty` で省略するため、
素朴に読むと**空文字の設定点が「未設定」と見分けられない**。

## 実クラスタの実効構成（2026-09-02 実測）

12 Deployment すべて `Running`。統制の発動可否に効く env だけを抜き出す。

| サービス | 実効構成（抜粋） | 監査上の意味 |
| --- | --- | --- |
| order-execution | `Broker__Provider=moomoo` / `Broker__Environment=sim` | **paper ではなく moomoo-sim である。** `if (brokerSelection.IsMoomoo)` で条件登録される常駐（約定ポーリング・建玉スナップショット・保護逆指値ガード・口座種別供給）が**すべて有効**になる |
| order-execution | OpenD `opend:11111` へ接続試行が**毎回タイムアウト**（`BrokerUnavailableException`。ログ実測） | ブローカ稼働も口座種別も観測できず、`BrokerAccountTypeUnverified` で**新規建てが全件拒否**される（フェイルクローズは設計どおり作動） |
| opend | ログ最終行が `input_phone_verify_code -code=...`（**SMS 認証コード待ちで停止**） | OpenD の無人常駐が成立していない。[#342](https://github.com/endazon/ai-stock-trading/issues/342)（`blocked:env`）の範囲 |
| trade-decision | `MarketMonitor__BaseUrl` = **空** | watchlist は権威源照会でなく構成フォールバック（`TradeCycle__Watchlist__0` = AAPL/UnitedStates）。[#286](https://github.com/endazon/ai-stock-trading/issues/286) が扱う |
| trade-decision / information-collection | `KnowledgeBase__Search__BaseUrl` = **空** | **RAG 取得は実クラスタでも不活性**（`NoOpKnowledgeBaseSearch`）。FR-08 の「参照」側は実環境で一度も効いていない |
| trade-decision | `Configuration__BaseUrl` **不在** | 採算評価ゲート（FR-17）の前提条件が未解決＝費用見積り不能。`Configuration__BaseUrl` を持つのは cost-control のみ |
| trade-decision | `LlmGateway__BaseUrl` 設定済・`LlmPricing__PerModel__*` 5 モデル投入済 | 実 LLM 経路と費用単価は結線済み |
| notification | `Reports__BaseUrl` **不在**（`RiskManagement__BaseUrl` は在る） | Discord からの**報告書**操作系は実クラスタで到達しない。kill switch / pause / 段階ゲートは到達する |
| report | `Reports__AutoGeneration__Enabled` **不在** | 日報/週報/月報の自動生成常駐は**起動していない**（`ReportAutoGenerationService` は opt-in） |
| risk | `MarketData__EnableMarkToMarket=true` / `ObservedDrawdownRefresh__Enabled=true` / `WithdrawalEvaluation__Enabled=true` / `Risk__SimulatorProfile__Enabled=true` | 時価評価・実DD 供給・撤退評価はいずれも**有効**。SIMULATE プロファイル（基準資金の嵩上げ）も有効 |
| information-collection | ログ実測 `KB 保存: 0/3 件を platform 文書管理へ登録（未保存は fail-safe 縮退）` | **KB 保存が実環境で全件失敗**。[#626](https://github.com/endazon/ai-stock-trading/issues/626)（レルム名）／[#627](https://github.com/endazon/ai-stock-trading/issues/627)（Istio STRICT mTLS 全断）の範囲 |
| trade-decision | `InformationCollected` を 5 分ごとに受信し **0〜18 ms で完了**（ログ実測） | 市場時間ゲートで早期 return しており LLM 照会に到達していない（監査時刻は米国市場の閉場時間帯であり、これ自体は正常） |

> 🔴 **`values.yaml`（本番プロファイル）と実クラスタは一致しない。** 上表は**実クラスタ**の値である。
> 本番配備を起こす際は、上表の「不在／空」が本番 values でどう埋まるかを別途確認すること。

## 安全性の再裏取り（実コードの行で確認）

初版監査 §7 の 12 項目を、再実装後のコードで引き直した（監査網羅を足して 13 項目）。

| # | 安全性要件 | 確認結果（再実装後の実コード） | 判定 |
| --- | --- | --- | --- |
| S-1 | 実弾を撃たない（閂 0） | `LiveTradingGate.LiveTradingReleased = false`（`backend/Services/OrderExecutionService/Infrastructure/ExternalServices/LiveTradingGate.cs:22`）。`Ensure()` が `BrokerFactory.Create` の入口で live を停止（`BrokerFactory.cs:20`） | ✅ |
| S-2 | 閂 2（TrdEnv 固定） | `MMApiMoomooTradeClient.cs:464` が `.SetTrdEnv((int)TrdCommon.TrdEnv.TrdEnv_Simulate)` を固定 | ✅ |
| S-3 | 閂 3（構成の受理語彙） | `MoomooBrokerOptions.EnsureSimulate`（`MoomooBrokerOptions.cs:63-75`）が `simulate` 以外を**起動時例外**。未設定は `simulate` へ | ✅ |
| S-4 | 閂 4（SIMULATE 口座のみ） | `MMApiMoomooTradeClient.cs:409` が `acc.TrdEnv == TrdEnv_Simulate` の口座のみ採用 | ✅ |
| S-5 | 外周の閂（Helm） | `deploy/helm/ai-stock-trading/templates/deployment.yaml:14-16` が `broker.tier=moomoo-live` を**描画時に `fail`**。未知 tier も `fail`（同 :18-20） | ✅ |
| S-6 | ブローカ既定はペーパー | `BrokerSelection.Parse` の既定が paper/sim。未知 provider・paper×live の矛盾は**起動時停止**（黙って安全側へ倒さず誤設定を隠さない） | ✅ |
| S-7 | 損切りが機能する | **ブローカー側逆指値へ一本化**（planning#88 裁定・NFR-04 追随）。`OrderExecutionAppService.cs:53-64` が **逆指値を張れない `Open` は発注せず見送る**（`StopLossPriceMissing` / `StopOrderUnsupported`）。`PaperBrokerAdapter` も `IProtectiveOrderBroker` を実装（`PaperBrokerAdapter.cs:17`）。`StopLossTriggeredHandler` は**記録のみ**で二重決済を作らない | ✅ |
| S-8 | kill switch / pause / 縮退が発注前に効く | `PortfolioSnapshotBuilder.cs:26-53` が kill switch・pause・口座種別観測・情報源縮退・GFV を snapshot へ合成し、`RiskEvaluator` が `KillSwitchActive`(:54) / `TradingPaused`(:61) / `InformationSourceDegraded`(:73) / `BrokerAccountTypeUnverified`(:42) を立てる。**20 種類の拒否理由が結線されている** | ✅ |
| S-9 | 統制の fail-closed（未観測は止める） | 口座種別の観測は **singleton・非永続**（`RiskManagementService/Program.cs:124`）で、再起動・30 分失効は「不明」＝新規建て停止。情報源の縮退も同様（同 `:142`・`IADR-0267`）。**実クラスタで OpenD 未接続のため実際に `BrokerAccountTypeUnverified` が効いていることをログで確認した** | ✅ |
| S-10 | 統制値が計画と一致 | `TradingDefaults.CreateRiskLimits()` の `PerTradeRiskRatio=0.01` / `DailyLossLimitRatio=0.02` / `MaxDrawdownRatio=0.10` / `LosingStreakThreshold=5` / 空売り `PerSymbolCapRatio=0.10` / `BorrowRateCapAnnual=0.20` / `MaintenanceMarginThreshold=0.40` / `PriceFloorUsd=5.00` が **ADR-0016 決定6 の表と一致** | ✅ |
| S-11 | 発注は at-most-once（冪等） | `OrderExecutionAppService.cs:70` の 3 相予約（`TryReserve` を**ブローカ送信前**に置く）。`Reserved` は期限超過でもパージしない（NFR-09 / `RetentionOptions`） | ✅ |
| S-12 | 秘密情報の非混入 | `.gitignore` に `*.pem` / `*.key` / `.env*`（`!.env.example`）。`.claude/hooks/guard-secrets.js`・`security.yml` の gitleaks。`.gitleaksignore` は fingerprint 指定で理由を明記 | ✅ |
| S-13 | 監査ログの網羅 | `AuditCycleCompletenessTests` が `EventTypeDiscovery.GetEventTypes()` と `AuditEntryFactory.From` の**全件一致**を表明し、**サイクル 1 周の実走で台帳へ落ちること**まで検査する（`AuditCycleCompletenessTests.cs:145-180`） | ✅ |

> **安全性所見**: **危険側へ倒れる既定は 1 件も検出されなかった。** 実弾は 5 重（閂 0〜4）＋ Helm 外周で塞がれ、
> 未観測はすべて「止める」側に倒れる。**実クラスタでフェイルクローズが実際に作動していること**（OpenD 不通 →
> 口座種別未検証 → 新規建て拒否）をログで確認できたのは、机上でなく実測による裏取りである。
>
> ただし **S-10 の「計画と一致」を機械で保ち続ける仕組みは失われている**（後述 D-4）。**今回は人手で突き合わせた。**

## 「コードは存在するが結線されていない」— 本監査で検出した統制

初版監査が見落とした型と**同じ形**のものを、走査 1〜4 の突き合わせで検出した。

### D-1: Stage 0 判定が本番で一度も走らない（FR-15 / FR-20 / ADR-0008）

- `BacktestService/Program.cs:10-17` が**自ら**「本ホストの責務は実過去データ源の合成に限る」「本番戦略（`IBacktestStrategy` 実装）はまだ存在せず、実行する対象が無い」「`BacktestEvaluated` の実 publish は #82」と述べている。
- 走査 1: `BacktestEvaluatedFactory` は**本番コードからの参照が自分自身の定義ファイルだけ**（`BacktestService/Features/Backtest/BacktestEvaluatedFactory.cs:14`）。`Stage0GateService` / `BacktestRunner` / `WalkForwardSplitter` / `SymbolAnonymizer` も同じ。
- 走査 2: `BacktestEvaluated` は **本番 publisher が実質ゼロ**（唯一の生成箇所が上記の未呼び出し純写像）。受け側 `RiskManagementService/Infrastructure/Steps/BacktestEvaluatedProjectionHandler.cs` は**待っているが一通も来ない**。
- **帰結**: `IStagePerformanceStore` の `BacktestPassed` は未記録のまま＝ Stage 0 → 1 の昇格が**構造的に不可能**。
  これは**安全側**（未記録は `false`）だが、**go-live の進行そのものが止まる**。
- **初版監査は FR-15 を「✅（実データ供給/E2E は🅱️）」と評価していた。実際には判定エンジンを駆動する本番経路が存在しない。**
- 既存 issue の検索結果: [#388](https://github.com/endazon/ai-stock-trading/issues/388) は**空売り解禁の verdict** に限定した話で、Stage 0 判定の**駆動そのもの**は追跡されていない。依存とされた #382（履歴源）は CLOSED/COMPLETED。
- **起票**: [#632](https://github.com/endazon/ai-stock-trading/issues/632)

### D-2: 取引の経費区分が一度も記録されない（ADR-0016 決定15 / ADR-0027 / FR-11 / FR-16）

- 走査 2: `TradeExpenseRecorded` の `new` は**テストコードにしか存在しない**（`AuditService/Tests/AuditCycleCompletenessTests.cs:120` ほか）。本番の publisher はゼロ。
- `TradeExpenseLedger`（`backend/Shared/AiStockTrading.Shared.Contracts/Trading/TradeExpenseLedger.cs:16`）も本番からの参照ゼロ。
- 監査側は受け口を持つ（`AuditService/Infrastructure/Steps/AuditEventHandlers.cs:389` / `AuditEntryFactory.cs:339`）。**受け皿だけが在る。**
- 日報の損益は `PnlAggregator` が **`CostCalculator.EstimateOneWayCost` の概算**で計上しており（`ReportService/Domain/PnlAggregator.cs:26-27`）、**実費用の明細は台帳に一行も残らない**。
- ADR-0016 決定15 は「**集計は後から作れても記録は遡って復元できない**ため、記録の設計は最初から入れる」と述べ、`Realized` / `BorrowFee` / `MarginInterest` / `DividendInLieu` / `Commission` / `Fee` / `FxCost` の 7 区分を**建玉単位で紐づけること**を要件にしている。`BorrowFee` のみ `BorrowFeeAccrued` として別経路で publish されるが、**残り 6 区分は記録経路が無い**。
- 既存 issue の検索結果: **無し**（[#615](https://github.com/endazon/ai-stock-trading/issues/615) は週報の「費用レビュー節」の**表示側**であり、記録側ではない）。
- **起票**: [#633](https://github.com/endazon/ai-stock-trading/issues/633)

### D-3: 維持率割れの自動縮小に駆動が無い（ADR-0016 決定7 / FR-10 / UC-06）

- `MaintenanceMarginReductionService` は DI 登録される（`RiskManagementService/Program.cs:210`）が、**本番の呼び出し元がゼロ**（`Hosted/` にも `Infrastructure/Steps/` にも `Features/` のエンドポイントにも参照が無い）。
- 供給元も `UnavailableMaintenanceMarginSnapshotSource`（常に null）で固定（同 `:209`）。
- `Program.cs:208` のコメントは「**維持率の供給元が未実装のため既定は「供給なし」＝発動しない**」と述べているが、**駆動が無いことには触れていない**。
  🔴 **供給元が入っても、呼ぶ者がいないため発動しない。** これは「供給待ち」ではなく未結線である。
- 既存 issue の検索結果: **無し**。
- **起票**: [#634](https://github.com/endazon/ai-stock-trading/issues/634)

### D-4: 統制値の計画適合検査が消え、それに依存する記述だけが残っている（NFR / FR-10）

- `backend/Tests/AiStockTrading.PlanConformance.Tests/`（`PlanConformanceTests` / `PlanRiskDefaults` / `ActualDefaults` / `KnownPlanDeviations` / `PlanSourceDigests` ほか 11 ファイル）は **#536（資料再編・planning 依存の全撤去）で削除**された（`git log --diff-filter=D` で確認）。planning submodule を読む検査だったため、`ADR-0029` 決定2 の帰結としては筋が通る。
- **しかし依存していた記述が残っている**:
  - `docs/DEFINITION_OF_DONE.md:31` —— 「削除しないと計画適合テストが失敗するため、**CI が通っていればこの項目は自動的に満たされている**」。**この保証はもう存在しない。**
  - `docs/blocked-tasks.md:541` —— 借株料 20% 統制の意図的残置を「**削除を検知するテスト 3 件と計画適合検査で担保している**」。担保の半分が消えている。
- **受容する逸脱の登録簿（`KnownPlanDeviations`）ごと消えている**ため、「計画値と実装値のずれを、受容済みとして把握している」状態も失われている。
- 既存 issue の検索結果: **無し**（[#378](https://github.com/endazon/ai-stock-trading/issues/378)（人手転記の検査）は CLOSED で、削除より前の話）。
- **起票**: [#636](https://github.com/endazon/ai-stock-trading/issues/636)

## 本監査で起票した issue（全 8 件）

| # | 内容 | ラベル |
| --- | --- | --- |
| [#632](https://github.com/endazon/ai-stock-trading/issues/632) | Stage 0 判定を本番で走らせる駆動が無い（D-1） | `enhancement` |
| [#633](https://github.com/endazon/ai-stock-trading/issues/633) | 取引の経費区分が本番で一度も記録されない（D-2） | `bug` |
| [#634](https://github.com/endazon/ai-stock-trading/issues/634) | 維持率割れの自動縮小に駆動が無い（D-3） | `bug` |
| [#636](https://github.com/endazon/ai-stock-trading/issues/636) | 計画適合検査が消えたのに DoD が「担保している」と書き続けている（D-4） | `tech-debt` |
| [#637](https://github.com/endazon/ai-stock-trading/issues/637) | NFR-01/02 の実測検証が追跡先を失っている（計測器自体も不足） | `chore` |
| [#640](https://github.com/endazon/ai-stock-trading/issues/640) | SC-03 の空売り現況が本番で必ず「取得不能」（BFF ルート欠落） | `bug` |
| [#642](https://github.com/endazon/ai-stock-trading/issues/642) | ADR-0016 決定11 の空売り AI ガードレール 4 件が不在・未追跡 | `enhancement` |
| [#643](https://github.com/endazon/ai-stock-trading/issues/643) | 日銀が第一に設定されていない／FINRA がアダプタ不在のまま必須登録 | `bug` |


### D-5: `StageProhibitsLiveTrading` は事実上トートロジーである（FR-20・**参考所見・起票しない**）

- `RiskEvaluator.cs:81` は `intent.Mode == MoomooReal && settings.Stage.Mode != MoomooReal` で拒否する。
- しかし `intent.Mode` の供給元は `SizingContextService.cs:30` の `Mode: settings.Stage.Mode` であり、**同じ値から作られている**。
- **完全に到達不能ではない**——サイジング文脈の取得と発注審査の間に段階が変わった場合の**陳腐化ガード**として働く。
  ただし「段階と独立に発注先設定を変えられる」ことを止める統制としては**機能していない**（そちらの経路は現状存在しない）。
- **起票はしない**（安全側であり、計画上の穴でもない）。**次回監査で「効いている統制」と数えないための記録**である。

## 初版監査の見落とし 7 件の再確認（すべて是正済み）

2026-08-28 のコメントが挙げた 7 件について、**現在の develop で結線されていることを確認した**。

| 統制 | 現在の状態 | 根拠 |
| --- | --- | --- |
| フォールバック禁止 | 結線済み | `PublishingLlmGovernanceReporter` が `LlmFallbackFired` を publish し、監査・通知の双方に handler が在る |
| LLM 費用上限の用途 | 結線済み | `LlmCostIncurred` に purpose を **計測ごとに** egress が載せる（`TradeDecisionService/Program.cs` の `IADR-0212` コメント） |
| 情報源の欠測判定 | 結線済み | `DegradationStateTracker` が `InformationSourceDegraded` / `Recovered` / `StateObserved` を publish し、Risk の `InformationDegradationHandlers` が畳む |
| 冪等な報告書確定 | 結線済み | `ReportEndpoints` から `ReportConfirmed` が publish され、監査・通知に handler が在る |
| GFV 通知 | 結線済み | `GoodFaithViolationRecorded` に `NotificationHandlers` の handler が在る |
| 縮退による新規建て停止 | 結線済み | `RiskEvaluator.cs:73` の `InformationSourceDegraded`。再起動 fail-open は `IADR-0267` の heartbeat 方式で是正（`RiskManagementService/Program.cs:142`） |
| 日報の取引履歴 | 結線済み | [#563](https://github.com/endazon/ai-stock-trading/issues/563) CLOSED。`TradeHistoryView` が `ReportService/Domain` に在り本番から参照される |

## トレース表

判定基準は 3 点をそれぞれ個別に確認したもの: **(a)** `Program.cs` の DI 登録 ／ **(b)** 本番の呼び出し元（`Hosted/` の常駐・`Infrastructure/Steps/` の Wolverine ハンドラ・`Features/` の HTTP）／ **(c)** publish するイベントの購読ハンドラ。

## トレース表 1: FR-01〜FR-21

| ID | 実装の所在（`backend/` 相対） | 結線（根拠 `パス:行`） | 発動条件（既定） | 判定 |
| --- | --- | --- | --- | --- |
| FR-01 | `InformationCollectionService/Hosted/CollectionPollingService.cs` ほか | (a) `Program.cs:69`/`:96` (b) `Program.cs:133` 常駐＋`:150` `POST /internal/collection/run-once` (c) `InformationCollected` → `TradeDecisionService/.../InformationCollectedHandler.cs:30` | `Collection:Source:Provider` 既定 空（no-op）。実クラスタ `finnhub,sec-edgar,fred` | ✅ 結線済み |
| FR-02 | `TradeDecisionService/Infrastructure/Steps/*`・`Features/TradeDecision/*` | (a) `Program.cs:292`/`:298` (b) `InformationCollectedHandler.cs:30` (c) `TradeDecisionMade` → `RiskManagementService/.../TradeDecisionMadeHandler.cs:24` | `Collection:PollIntervalSeconds`=1800。休場ガード `IMarketCalendar`（`Program.cs:195`） | ✅ 結線済み |
| FR-03 | `MarketMonitorService/Hosted/MonitorPollingService.cs`・`Features/MarketMonitor/MarketMonitorAppService.cs` | (a) `Program.cs:55`/`:96` (b) `MonitorPollingService.cs:70` publish (c) `PriceMovementDetected` → `PriceMovementDetectedHandler.cs:30` | `MarketData:Provider` 既定 空（no-op）。実クラスタ `finnhub` | ✅ 結線済み |
| FR-04 | `TradeDecisionService/Features/TradeDecision/{DecisionOrchestrator,TradeDecisionAppService}.cs` | (a) `Program.cs:90`/`:143`/`:217` (b) 両 Steps ハンドラ (c) `TradeDecisionMade`/`TradeDecisionSkipped` に監査・通知 | `LlmGateway:BaseUrl` 未設定＝`Placeholder`＝**常に Hold**。実クラスタ設定済 | ✅ 結線済み（用途別割当の一部が基盤側未登録 #571） |
| FR-05 | `OrderExecutionService/Infrastructure/Steps/OrderApprovedHandler.cs`・`Features/OrderExecution/*` | (a) `Program.cs:57`/`:184` (b) `OrderApprovedHandler.cs:25` (c) `OrderExecuted` → Risk 台帳＋監査＋通知 | `Broker:Provider`×`Environment` 既定 paper。`LiveTradingGate.Ensure` が live を起動時停止（`Program.cs:46`） | ⚠️ 一部（**訂正・取消に駆動元が無い**。下記 §11-2） |
| FR-06 | `ReportService/Domain/{ReportSchedule,ReportRenderer}.cs`・`Features/Reports/ReportDraftService.cs` | (a) `Program.cs:53`/`:131` (b) `ReportEndpoints.cs:91`/`:145` (c) `ReportConfirmed` → 通知・監査 | 常時（HTTP 経路） | ✅ 結線済み |
| FR-07 | `ReportService/Hosted/ReportAutoGenerationService.cs`・`Domain/ReportNoResponsePolicy.cs` | (a) `Program.cs:334`／`:335-337` **条件付き** 常駐 (b) 手動は `ReportEndpoints.cs:91/136/145` (c) `ReportDraftPresented` → 通知 | `Reports:AutoGeneration:Enabled` 既定 **false**。**`values.yaml` にも `values-local.yaml` にも設定が無い** | ⚠️ opt-in 既定オフ（自動生成が起動しない） |
| FR-08 | `Shared.KnowledgeBase/*`・`InformationCollectionService/.../KnowledgeBaseWriterSink.cs`・`TradeDecisionService/.../KnowledgeBaseRetrievalContextProvider.cs` | (a) 3 サービスで `AddAiStockTradingKnowledgeBase`（`InformationCollectionService/Program.cs:86`・`ReportService/Program.cs:135`・`TradeDecisionService/Program.cs:185`）(b) 収集＝Sink、報告書＝confirm 時保存、**判断＝取得のみ** | `Search:BaseUrl` 既定 空（NoOp）。**実クラスタでも空** | 🔴 部分未結線（**判断根拠の KB 保存が無い**＋RAG 取得が実環境で不活性） |
| FR-09 | `NotificationService/Infrastructure/Steps/NotificationHandlers.cs` | (a) `Program.cs:36`/`:155` (b) 18 個の `*NotificationHandler`（走査発見）(c) 該当 | `Notifications:Provider` 既定 空（no-op）。実クラスタ `discord-webhook` | ✅ 結線済み |
| FR-10 | `RiskManagementService/Domain/RiskEvaluator.cs`・`Features/RiskManagement/OrderScreeningService.cs` | (a) `Program.cs:193` (b) `TradeDecisionMadeHandler.cs:36` (c) `OrderApproved`/`OrderRejected` 購読あり | 常時（`TradingDefaults`）。時価評価は `MarketData:EnableMarkToMarket` 既定 false | ⚠️ 一部（**維持率自動縮小・借株料計上・空売り文脈が未供給**） |
| FR-11 | `AuditService/Infrastructure/Steps/AuditEventHandlers.cs`・`Features/AuditEvents/*` | (a) `Program.cs:39`/`:49` (b) 48 個の `*AuditHandler`＋`AuditQueryEndpoints`（`Program.cs:76`）(c) 該当 | 常時 | ⚠️ 一部（**経費区分 `TradeExpenseRecorded` の publisher が 0**・#633） |
| FR-12 | `OrderExecutionService/.../{BrokerSelection,BrokerFactory}.cs`・`frontend/.../PaperModeBanner.tsx` | (a) `Program.cs:41`/`:57` (b) `OrderApprovedHandler` 経由で擬似約定 (c) 該当。バナーは 3 画面すべてに配置 | `Broker:Provider` 既定 `paper` | ✅ 結線済み |
| FR-13 | `RiskControlEndpoints.cs`・`MonitorSettingsEndpoints.cs`・`AssumptionsEndpoints.cs`・`frontend/src/features/sc0*` | (a) `RiskManagementService/Program.cs:152`・`MarketMonitorService/Program.cs:86-88`・`ConfigurationService/Program.cs:37` (b) BFF `RiskControlsBffEndpoints.cs:42-62` ほか (c) `AssumptionsChanged` 購読あり | 常時（要 `trading-owner`）。変更理由必須・楽観排他 | ✅ 結線済み |
| FR-14 | `NotificationService/Features/Notifications/{BotCommandParser,*CommandHandler}.cs` | (a) `Program.cs:52/68/83/99/117/132` (b) `Program.cs:140` 常駐（`DiscordBotHostedService`） | `Bot:Enabled` 既定 false＋多層認証（**未設定は全拒否**）。実クラスタ true | ✅ 結線済み（**報告書系コマンドは `Reports__BaseUrl` 不在で到達しない**） |
| FR-15 | `BacktestService/Domain/*`・`Features/Backtest/{BacktestRunner,Stage0GateService,BacktestEvaluatedFactory}.cs` | (a) `Program.cs:57` は `IHistoricalBarSource` のみ (b) **呼び出し元なし**（HTTP 0・常駐 0・`Program.cs:10-17` が自認）(c) `BacktestEvaluated` の **publisher なし** | `Backtest:BarData:Provider` 既定 `none`。**有効化しても実行主体が無い** | 🔴 **未結線**（#632） |
| FR-16 | `ReportService/Domain/{ReportRenderer,PnlAggregator,TradeHistoryViewBuilder,FxTranslationSummary}.cs` | (a) `Program.cs:131` (b) `ReportDraftService.cs` から呼ぶ | 常時。数値はコード集計・散文のみ LLM | ⚠️ 一部（**為替差損益の集計に呼び出し元が無い**＝節が恒久 null。#611） |
| FR-17 | `ConfigurationService/Features/Assumptions/*`・`AssumptionsProfitabilityProvider.cs` | (a) `ConfigurationService/Program.cs:37-38/68`・`TradeDecisionService/Program.cs:228/230` (b) 採算ゲート (c) `AssumptionsChanged` → `CostControlService/.../AssumptionsChangedHandler.cs`＋監査＋通知 | 採算ゲートは `Profitability:*` 既定無効。**実クラスタは trade-decision に `Configuration__BaseUrl` 不在＝費用見積り不能** | ⚠️ opt-in ＋実環境未投入 |
| FR-18 | — | — | — | ➖ 計画で Won't（将来拡張）。**過剰実装なし** |
| FR-19 | `RiskManagementService/Domain/{RiskEvaluator,ShortSellEvaluator,AccountTypePolicy,Manipulation/*}.cs` | (a) `Program.cs:184-200`（相場操縦検出器＋活動射影）/`:123`/`:129` (b) `TradeDecisionMadeHandler.cs:36` (c) `BrokerAccountObserved`/`GoodFaithViolationRecorded` 購読あり | 常時。`RejectionReason` 29 種すべて本番で使用 | ⚠️ 一部（**空売り文脈が恒久 fail-closed**＋相場操縦の一部指標が実クラスタで観測不能。§11-2） |
| FR-20 | `RiskManagementService/Domain/StageGate.cs`・`Features/RiskManagement/StageGateService.cs` | (a) `Program.cs:160-161`/`:72`/`:75`/`:78` (b) `RiskControlEndpoints.cs:386-422`＋Discord (c) `StageTransitioned`/`WithdrawalTriggered` 購読あり | 常時。撤退定時評価は `WithdrawalEvaluation:Enabled` 既定 false（実クラスタ true） | 🔴 一部（**Stage 0→1 の `BacktestPassed` 供給が構造的に不可能**・`Domain/StageGate.cs:158`） |
| FR-21 | `RiskManagementService/Features/RiskManagement/{IPositionObservationArrivalStore,ObservationCoverage}.cs` | (a) `Program.cs:231`（EF 実装） (b) `Steps/BrokerPositionsObservedHandler.cs:27` が記録・`RiskControlEndpoints.cs:76/111` が消費 (c) publisher は `OrderExecutionService/Hosted/BrokerPositionSnapshotService.cs` | 供給元は **moomoo 選択時のみ**。paper では 1 件も届かない（＝仕様どおり「未供給」表示） | ✅ 結線済み |

## トレース表 2: UC-01〜07 / SC-01〜03

| ID | 結線（根拠 `パス:行`） | 判定 |
| --- | --- | --- |
| UC-01 | 収集→判断→審査→発注→通知の 5 サービス連鎖: `InformationCollectionService/Program.cs:133` → `InformationCollectedHandler.cs:68` → `TradeDecisionMadeHandler.cs:47` → `OrderApprovedHandler.cs:51` → `NotificationHandlers.cs` | ✅ 結線済み |
| UC-02 | `MonitorPollingService.cs:65/70` publish → `PriceMovementDetectedHandler.cs:30` ／ `StopLossTriggeredHandler`（**記録のみ**・二重決済防止） | ✅ 結線済み |
| UC-03 | `ReportEndpoints.cs:91/136/140/145` ＋ Discord `ReportCommandHandler`（`NotificationService/Program.cs:128`） | ✅ 結線済み（定時起動のみ opt-in オフ） |
| UC-04 | `ReportService/Domain/ReportSchedule.cs:44`（`ReportKind.Weekly`）・同一エンドポイント | ✅ 結線済み（**起点 ID コメントが 0 件**＝トレーサビリティ表記の欠落） |
| UC-05 | `ReportSchedule.cs:52`・`Domain/MonthlyBootstrap.cs` → `Features/Reports/ReportAppService.cs` | ✅ 結線済み（**起点 ID コメントが 0 件**） |
| UC-06 | 設定・停止系は `RiskControlEndpoints.cs:130/136/147/150/274/299/312/390`、Discord は `NotificationService/Program.cs:52/68/83` | ⚠️ 一部（**維持率割れ自動縮小のみ呼び出し元なし**・#634） |
| UC-07 | `AuditService/Features/AuditEvents/AuditQueryEndpoints.cs`（`Program.cs:76`） | ⚠️ 一部（**判断根拠が KB へ入らない**ため RAG では引けない。監査台帳の期間照会では引ける） |
| SC-01 | `/bff/assumptions` GET/PUT/history → `Bff/.../AssumptionsBffEndpoints.cs:29/34/39` → ConfigurationService。paper バナー配置済 | ✅ 結線済み |
| SC-02 | 全 9 経路が BFF に存在（`RiskControlsBffEndpoints.cs:33-62`＋`MonitorBffEndpoints.cs:33-74`）。実弾切替の確認は `RiskControlEndpoints.cs:312` が**サーバ側でも強制** | ✅ 結線済み |
| SC-03 | status / stage-gate / history は BFF あり。🔴 **`/risk-controls/short-selling` が BFF に無い**（画面 `ControlStatusPage.tsx:85` ↔ BFF `RiskControlsBffEndpoints.cs` の 8 経路に不在） | 🔴 部分未結線（#640） |

## 追加で検出した未結線（D-1〜D-4 に加えて）

1. **判断根拠が KB へ保存されない（FR-08 / UC-07）** —— `TradeDecisionService` に `IKnowledgeBaseWriter` の参照が**1 件も無い**（`Program.cs:185` は取得側 `IKnowledgeBaseSearch` の配線）。根拠の権威源は監査台帳で、報告書は `ReportService/.../HttpTradeRationaleSource.cs:9-15` が台帳から引く設計である。**設計としては一貫しているが、FR-08 の「根拠を KB 保存し RAG 参照」は満たしていない。** RAG 取得側も実クラスタで `Search:BaseUrl` が空。

2. **注文の訂正・取消に駆動元が無く、実クラスタでは型ごと存在しない（FR-05 / FR-19）** —— `OrderAmendmentDispatcher` は `OrderExecutionService/Program.cs:85` の DI 登録以外に本番参照ゼロ。しかも `Program.cs:79` の `if (!brokerSelection.IsMoomoo)` の内側にあり、**実クラスタ（moomoo-sim）では登録すらされない**。
   これは `Program.cs:73-77` が**意図的な fail-safe として明記**しており（「実ブローカー選択時は本経路を登録しない＝実弾に対する訂正・取消が構成上も存在しない」）、**未結線そのものは欠陥ではない。**
   🔴 **ただし波及が文書化されていない**: `OrderModified` / `OrderCancelled` が実クラスタで一度も publish されないため、相場操縦検知（FR-19）が使う `MaxCancellationRatio`（取消率 0.7）と `MaxAmendmentsPerOrder`（3.0）が**構造的に発火し得ない**。`TradingDefaults.CreateManipulationDetectionSettings()` の 2 指標は実クラスタで死んでいる。

3. **借株料の日次計上が動かない（FR-10 / ADR-0027）** —— `RiskManagementService/Program.cs:236-241` が「日次で計上を回すスケジューラも、料率の供給元も登録しない」と明記。`BorrowFeeAccrualService` は登録以外に本番参照なし。**意図的な保留であり自認もある。**

4. **空売り専用統制が恒久 fail-closed（FR-10 / FR-19 / ADR-0016）** —— `OrderScreeningService.cs:49` が `shortSellContext` を渡さず（同 `:42-44` が「借株照会の供給元が無いため空売り文脈は今も組めない」と自認）、`ShortSellEvaluator.cs:83-86` が `context is null` で `BorrowUnavailable` を足して即 return する。**安全側だが、Stage 1 での空売り統制の検証は不可能。**

5. **UC-04 / UC-05 の起点 ID コメントが本番コード・テストのいずれにも 0 件** —— 実体（`ReportKind.Weekly/Monthly`）は実装済みだが、**トレーサビリティ規約上の欠落**である。

> §11 の 2〜4 は**いずれもコード側に自認コメントがあり、意図的な保留である**。
> 本監査が指摘するのは**保留そのものではなく、保留の波及（とくに 2 の FR-19 への影響）が記録されていないこと**である。

## トレース表 3: 計画 ADR-0001〜0029

> **初版監査は ADR-0001〜0008 しか見ていない。ADR-0009〜0029 は本監査が初めての監査である。**


| ADR | 決定の要旨 | 実装と結線（根拠 `パス:行`） | 判定 |
| --- | --- | --- | --- |
| 0001 | platform を無改修で再利用し、可変ユニットとして `src/<unit>/` へ合成 | 11 サービスは VSA 構成で実在。ただし**本番配備物の 11 個の `Program.cs` がすべて `AiStockTrading.TestSupport.PlatformShim` を参照**（例 `RiskManagementService/Program.cs:9-10`）。shim の csproj 自身が「本番では platform 本体を用いるため本プロジェクトは本番非使用」と宣言（`AiStockTrading.TestSupport.PlatformShim.csproj:1-3`） | ⚠️ 一部（合成未了） |
| 0002 | moomoo OpenAPI 採用・OpenD 常駐・SIMULATE PoC | 閂 0〜4 ＋ Helm 外周が健在（`LiveTradingGate.cs:22` ほか）。OpenD chart は `templates/opend.yaml` | ✅ 準拠 |
| 0003 | AI 入力を確定済み日報等に限定・方針確定は対話必須・Risk を直列配置 | policy-null は取引しない（`TradeDecisionAppService.cs:99-104`）。Risk 直列＝`RiskEvaluator`。注入対策は JSON フェンスの構造分離（`TradeDecisionPromptBuilder.cs:150-175`） | ✅ 準拠 |
| 0004 | 案A+ の情報源構成・日米両市場 | `InformationSourceCatalog.cs:118-154` に全源を登録。**取得アダプタが実在するのは 7 種のみ**（`InformationSourceFactory.cs:22-29`） | ⚠️ 一部（FINRA 不在・#643） |
| 0005 | 有料情報源は条件付き・既定は無料 | 実接続する源はすべて無料。決定5 の一時降格は `InformationSourceCatalog.DemoteToRecommended`（`:101-111`） | ✅ 準拠 |
| 0006 | Hetzner 上に k3s ＋ OpenD | `deploy/argocd` のマニフェストのみ。`docs/infra/infra.md:37,55` が **Tier 3・対象外／未充足**と明記 | 🔴 未実装（#24） |
| 0007 | 取引ガードをソフト設定で保持し発注前に決定的に強制。適用範囲は非対称 | `RiskEvaluator.cs`: 商品種別 `:115-116`（新規建てのみ）／市場 `:137`（全注文）／禁止銘柄 `:142-144`（全注文）／差金決済 `:162-166`（新規建てのみ）／相場操縦 `:211-215`（全注文） | ✅ 準拠 |
| 0008 | FR-15 を Must 化・Stage 0〜3 ゲート・撤退基準 1.5 倍 | 判定は純ドメイン（`BacktestService/Domain/Stage0Gate.cs:73-` 7 条件）。🔴 **本番の呼び出し元が無い**（`BacktestService/Program.cs:10-17` が自認） | 🔴 一部（未結線・#632） |
| 0009 | pause を日次損失ロックアウトと別状態に。Close/損切りは止めない | 3 統制の OR と `isEntry` 短絡（`RiskEvaluator.cs:53-63`）。優先順位表示（`RiskStatusService.cs:31-41`）。pause/resume の監査記録と冪等（`PauseService.cs:30-33, 50-53`） | ✅ 準拠 |
| 0010 | 全実装の TFM を `net10.0` へ | `Directory.Build.props:14`（`net10.0`）・`:17`（C# 13） | ✅ 準拠 |
| 0011 | 取引判断モデルをピン留めし基盤の既定改定に追随しない | `Shared.Contracts/Llm/LlmAssignments.cs:15-50`。実効モデル検証は `HttpLlmCompletionClient.cs:170` / `HttpReportNarrativeDrafter.cs:139` | ✅ 準拠 |
| 0012 | 取引文書を MCP 公開許可リストに含めない | `Tests/AiStockTrading.Architecture.Tests/McpExposureNotDeclaredTests.cs`（`backend`/`deploy` に `mcp` の出現 0 件を強制）＋基盤側許可リストのドリフト検査。実測結合確認済み | ✅ 準拠 |
| 0013 | Wolverine へ移行・RabbitMQ 継続・Kafka は先送り | `Directory.Packages.props:30-32`（WolverineFx・**MassTransit なし**）。全 host が `UseAiStockTradingRabbitMq` | ✅ 準拠 |
| 0014 | 用途別割当を確定・**取引判断は Stage 0 再検証を実弾解禁の必須ゲート**とする | 割当は `LlmAssignments.cs:37-50`。🔴 **決定3 のゲートが機械化されていない** —— `LiveTradingGate.cs:36-41` の解禁前提の列挙にモデル再検証が無い。Stage 0 自体も未結線（ADR-0008 行）ため**二重に空洞** | 🔴 一部 |
| 0015 | 月報を `claude-fable-5` → `claude-opus-5` へ | `LlmAssignments.cs:46`。`ForbiddenModel = claude-fable-5`（`:31`）を全用途から排除 | ✅ 準拠 |
| 0016 | 空売りの段階解禁（15 決定） | 統制値は `TradingDefaults.cs:74-` が決定6 の表と一致。8 規則は `ShortSellEvaluator.cs`（文脈 null は fail-closed `:42`）。段階解禁は `StageProductPolicy.cs:29-110`。決定4 の事後推定は `BuyInInferenceService.cs`。🔴 **決定11（AI ガードレール 4 件）がリポジトリ全体に 1 文字も無い**。決定7 の自動縮小は呼び出し元なし。決定12 の FINRA アダプタなし。決定15 の経費実費は供給ゼロ | 🔴 一部（#642 / #634 / #643 / #633） |
| 0017 | 用途別フォールバック順・**取引判断はフォールバック禁止**・失敗分類・可観測性 3 点 | 順序と禁止フラグ `LlmAssignments.cs:41-50`。429 と 400 系の分離 `LlmFailureClassification.cs:22-27`。発行は `PublishingLlmGovernanceReporter`（`TradeDecisionService/Program.cs:85`・`ReportService/Program.cs:82`）。月報 §7（`ReportRenderer.cs:717-748`）・日報スキップ行（`:849`） | ✅ 準拠 |
| 0018 | 統制既定値の確定単一値・Stage 0 最大 DD を 10% | `TradingDefaults.cs:52-71`（2% / 1% / 10% / 5 連敗）。Stage 0 は `Stage0Gate.cs:33`（`MaxDrawdownToleranceDefault = 0.10m`） | ✅ 準拠 |
| 0019 | moomoo PoC 9 項目と不成立時の帰結 | 実装側は全項目に対し fail-closed。借株照会は未実装（`MMApiMoomooTradeClient.cs:574` の `OnReply_GetMarginRatio` が**空実装**） | ⏳ PoC 待ち（#342） |
| 0020 | 情報源 4 区分・欠測時 3 挙動・一般 Web 4 条件 | 区分と挙動は `InformationSourceCatalog.cs:4-56, 118-154`、判定は `Domain/CollectionDegradation.cs:105-130`。判断側への遮断は `RiskEvaluator.cs:71-75`。一般 Web 4 条件は `Domain/GeneralWebActivation.cs` → `Program.cs:167` | ⚠️ 一部（FINRA 未実装・#643） |
| 0021 | 信用口座を既定・現金口座は拡張・照会結果を正 | 照会は `BrokerAvailabilityProbeService.cs:130` → `BrokerAccountObservedHandler.cs`。fail-closed は `RiskEvaluator.cs:36-42`（`BrokerAccountTypeUnverified`）。口座種別分岐は `AccountTypePolicy.cs:70` | ✅ 準拠（決定4 の現金口座運用は供給待ち。計画も同認識） |
| 0022 | 為替は**日銀を第一・FRED をフォールバック**、警告 5 日・上限 30 日 | 実装は決定どおり（`FxRateSourceFactory.cs:48, 88-120`／`FxOptions.cs:46/60/72`）。🔴 **配備構成が日銀を選んでいない** —— `values.yaml:421` は `Fx__Provider: ""`、`values-local.yaml:227` は `"fred"`。**`boj` を指す設定行が 1 件も無い** | 🔴 一部（構成未適用・#643） |
| 0023 | Stooq は取得不能として扱い回避しない・履歴源は moomoo 履歴 K 線 | 回避実装なし（`HistoricalBarSourceFactory.cs:17-21` に明記）、moomoo アダプタあり、既定は no-op | ✅ 準拠（実装側確認 2 点は未了で本番投入せず） |
| 0024 | 無人再起動は条件付き成立（デバイス信頼の PVC 永続化＋安定 egress IP） | PVC を `$HOME/.com.moomoo.OpenD` へ（`templates/opend.yaml:25-30, 124, 134`）、RSA Secret 未指定は描画時 fail（同:18） | ✅ 準拠（実クラスタでは SMS 認証待ちで停止中・#342） |
| 0025 | 決済済み資金は PoC 項目 8・GFV 回数は自前計数・現金口座は解禁不可 | 自前計数は `GoodFaithViolationCountingService.cs`（`OrderExecutedGoodFaithViolationHandler.cs:26` から結線）。決済済み資金は**常に null**（`MoomooBrokerAdapter.cs:181-182`）→ fail-closed | ✅ 準拠（PoC 待ちを含む） |
| 0026 | `ShortFeeRate` の単位確定を PoC 項目 9 とする | 実装側は単位確定済み年率のみ受ける契約で遮断（`ShortSellOrderContext.cs:23-28`） | ⏳ PoC 待ち |
| 0027 | 借株料を日次で積む（5 決定） | 記録側は 5 決定どおり実装（`BorrowFeeAccrualService.cs:20-70`・`DaysPerYear=365`）。🔴 **本番の呼び出し元が無い**（`RiskManagementService/Program.cs:241` の DI 登録のみ）。ただし同 `:238-240` が決定6（PoC 成立まで供給しない）に沿った**意図的な遮断**と明記 | ⚠️ 一部（意図した遮断） |
| 0028 | GFV 記録は失効させない・解除は明示操作＋監査・窓口は Discord | 解除は追記のみ（`GoodFaithViolationClearingService.cs:40-57`）、監査発行は `RiskControlEndpoints.cs:215-236`、窓口は `GoodFaithViolationCommandHandler.cs`、フロントに解除経路なし | ✅ 準拠（決定4/6 の突合手順は計画どおり繰り延べ） |
| 0029 | IADR/仕様書を `.ai-context/` へ・planning 依存全撤去・trace ブロック | `.ai-context/{adr,specs}` 実在、`.gitmodules` 不在、`feedback/` 不在、`check-trace-blocks.js` ＋ `gen-knowledge-graph.js` 実在 | ✅ 準拠 |

## トレース表 4: NFR-01〜NFR-17

| NFR | 要件 | 実装・確認（根拠 `パス:行`） | 判定 |
| --- | --- | --- | --- |
| 01 | 価格変動検知→発注完了 5 分以内 | 計測は判断ハンドラ内のみ（`PriceMovementDetectedHandler.cs:45` → `ast.trade_cycle.decision_duration_ms`）。**発注完了までの端点間計測器が無い** | 🔴 一部（#637） |
| 02 | 定時サイクル 1 回 10 分以内 | `BusinessMetricNames.cs:26-58` の 9 メトリクスに**サイクル端点間の所要時間が存在しない** | 🔴 未実装（#637） |
| 03 | 開場時間帯稼働率 99%・停止を Discord 通知 | 本リポジトリに `PrometheusRule` / アラート定義が **0 件**。計画どおり基盤管掌だが、その基盤が未稼働 | 🔴 未実装（#24） |
| 04 | 障害時は安全側（新規発注停止・損切りはブローカー逆指値） | 全統制が `isEntry` 短絡で Close/損切りを通す。逆指値の再発注巡回は `ProtectiveStopGuard.cs`、`StopLossTriggeredHandler.cs:13` は記録のみ | ✅ 準拠 |
| 05 | 認証情報を Vault で秘匿・リポに含めない | 平文なしは gitleaks で担保。🔴 **`values.yaml:147-148` で `externalSecrets.enabled: false`**、同 `:111` が「ストア整備は #24 で未充足」と明記。現行は手動 k8s Secret | ⚠️ 一部（#24） |
| 06 | 発注機能は利用者本人のみ・外部公開しない | Keycloak `OwnerOnly` / `OwnerOrService`。`templates/service.yaml` に `type:` 指定なし（ClusterIP）、Ingress テンプレート無し | ✅ 準拠 |
| 07 | メトリクス・ログ・トレースを基盤スタックへ | 全 host が `AddAiStockTradingObservability`（OTel＋Serilog）。業務メトリクスは `Observability/BusinessMetrics.cs` | ✅ 準拠 |
| 08 | 重複排除メタデータのパージ（既定 90 日・下限 7 日） | `Operations/RetentionOptions.cs:19` ＋ `RetentionPolicy.MinimumRetentionDays` クランプ。実行は `CostControlService/Hosted/ProcessedMessageRetentionService.cs` | ✅ 準拠 |
| 09 | 未確定予約は期限超過でも削除しない | `OrderReservationRetentionService.cs:13-14`（Completed のみ・**Reserved は対象外**） | ✅ 準拠 |
| 10 | 業務台帳・監査証跡は 7 年・自動パージ対象外 | `Operations/RetentionScope.cs:39-60`（**許可リスト方式**・列挙外を指すと例外）。両パージが `EnsurePurgeable` を通る | ✅ 準拠 |
| 11 | パージは既定無効・失敗をサービス停止に波及させない | `RetentionOptions.cs:13`（`Enabled` 既定 false）＋各常駐の無効時ログ | ✅ 準拠 |
| 12 | データ取得費用 0 円/月 | 実接続する 7 源はすべて無料枠。履歴 OHLC も moomoo（追加費用なし） | ✅ 準拠 |
| 13 | 月次 LLM 費用上限・80% で間隔延長・100% で停止。**対象は取引判断のみ** | `CostControlService/Domain/CostGovernor.cs:43-63`、対象範囲判定 `Llm/LlmCostScope.cs:20-24`、消費側は `CollectionPollingService.cs:91-102` | ✅ 準拠 |
| 14 | Hetzner インフラ費の月次上限管理 | 実基盤が未稼働（NFR-03 と同根） | 🔴 未実装（#24） |
| 15 | 月次総費用は目安のみ・自動統制も定期把握も行わない | 総費用に対する統制コードが**存在しないこと自体**が要求どおり（統制は `CostCategory.Llm` のみ） | ✅ 準拠 |
| 16 | 証券会社・情報源・LLM をポートで抽象化 | `Shared.Contracts/Ports/` 9 種＋各 Factory（`BrokerFactory` / `MarketDataSourceFactory` / `FxRateSourceFactory` / `HistoricalBarSourceFactory` / `InformationSourceFactory`） | ✅ 準拠 |
| 17 | 情報源・証券 API の利用規約遵守 | `RateLimiting/TokenBucket.cs` を全源に配分（BOJ 1 回/分・FRED 5 回/分・SEC は秒単位）。SEC は連絡先入り UA 必須で未設定なら無効化（`InformationSourceFactory.cs:210-215`）。Stooq のボット検知回避は実装せず | ✅ 準拠 |

## 走査の再現手順

本監査の走査は次で再現できる（いずれも読み取りのみ）。

| 走査 | 再現コマンドの骨子 |
| --- | --- |
| 1（未参照の型） | 本番 `.cs` 一覧を作り、`public\|internal` の `class\|record` 名を抽出し、各名を本番一覧内で `grep -rlwF` してヒット 1 件（自分自身）のものを拾う |
| 2（イベント突合） | `Shared.Contracts/Events/*.cs` の各名について `new <E>(` と `Handle(<E> ` を本番コードに `grep -rl` し、**publisher 側は走査 1 と二段で突き合わせる** |
| 3（常駐） | `grep -rlE ":\s*(BackgroundService\|IHostedService)\b"` と `grep -rn "AddHostedService"` を突合 |
| 4（安全既定） | `grep -rnE "(NoOp\|Placeholder\|InMemory\|Null)[A-Za-z0-9_]*" backend/Services/*/Program.cs` と `grep -rn "public bool Enabled"` |
| 5（実クラスタ） | `kubectl get deploy -n ai-stock-trading -o json` の env を `valueFrom` / 空文字 / 実値 の 3 値で分類（**空文字は `omitempty` で省略されるため未設定と見分けが要る**） |

## 受け入れ基準

- [x] FR-01〜FR-21 / NFR-01〜NFR-17 / UC-01〜07 / SC-01〜03 / ADR-0001〜0029 をトレース表にした（表の本体は issue [#204](https://github.com/endazon/ai-stock-trading/issues/204) のコメント）
- [x] 「コードは存在するが結線されていない」型を**機械走査で**列挙し、走査の母集合と除外を書いた
- [x] 安全性 12 項目（＋監査網羅で 13）を**再実装後の実コードの行**で裏取りした
- [x] 実クラスタの実効構成を読み取り専用で観測し、`values*.yaml` との食い違いを記録した
- [x] 初版監査の Go 条件（G-1 性能実測・§6 の段階的有効化・#22 エンベロープ）の現況を更新した
- [x] 新規の欠陥候補は**起票前に既存 issue を検索**し、無いものだけ起票した
- [x] コードを変更していない（本書と issue コメント・起票のみ）

## やらないこと（意図的な非対象）

- **Go / No-Go の判断**——利用者に留保する。AI は Conditional-Go の**条件を更新する**に留める。
- **検出した欠陥の修正**——監査は指摘までとし、実装は別 issue・別 PR に分ける。
- **`docs/blocked-tasks.md` の更新**——別エージェントが編集中のため触れない（D-4 の指摘は本書と issue に留める）。
- **実弾解禁に関わる一切の変更**——閂 0〜4 は 1 行も触れていない。
