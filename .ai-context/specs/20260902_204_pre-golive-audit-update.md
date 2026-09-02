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
