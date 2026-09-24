---
title: 判断サービスが読む約定済み保有（/open-positions）の応答に契約テストを足し、欠けた識別項目を不明へ倒す — サービス間の読み取り契約の走査
type: spec
status: accepted
related_ids: [FR-04, FR-10, FR-03, FR-06, UC-01, UC-02, ADR-0003, IADR-0390, IADR-0358, IADR-0351, IADR-0030, IADR-0029, IADR-0269]
author: endazon (with Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04・FR-10)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (判断入力「保有ポジション」・「不確実な場合は必ず Hold」)
---

# 仕様書: 約定済み保有の読み取り契約と、サービス間の読み取り契約の走査（#943）

## 起点

- #943（PR #940 の残余）。PR #940 は `/risk-controls/working-entry-orders` に**送り手の本物の型を直列化する契約テスト**（T-10-744）を
  足したが、同じ判断サービスのアダプタ `HttpHeldPositionProvider` が読む **`/risk-controls/open-positions`** には同じ守りが無い。
- 本件の射程: (1) `/open-positions` の契約テスト (2) アダプタの内部 DTO を nullable にし、一致行の識別項目の欠落を**不明**にする
  (3) **他サービスの HTTP 応答を自前の型で読む箇所を全部走査**し、安全に関わる経路で fail-open するものには契約テストを足し、
  残りは追随 issue へ回す。

## 現物で確認した（是正前・`origin/develop` b2d26a67）

| 事実 | 出典 |
| --- | --- |
| `HttpHeldPositionProvider` は `/open-positions` を `private sealed record OpenPositionDto(string Symbol, Market Market, TradeSide Side, int Quantity, decimal? EntryPrice, decimal? StopLossPrice)` で読む。一致は `p.Symbol == symbol && p.Market == market`、一致 0 件は `HeldPosition.None`（保有なし） | `TradeDecisionService/Infrastructure/ExternalServices/HttpHeldPositionProvider.cs` |
| 既存テストは手書き JSON だけ（送り手の型を使っていない） | `HttpHeldPositionProviderTests.cs` の `TwoPositions` |
| 🔴 **送り手 `OpenPositionView.Symbol` を `Ticker` へ改名しても、TradeDecisionService.Tests 751 件・MarketMonitorService.Tests 157 件・ReportService.Tests 1101 件がすべて緑**（実測。RiskManagementService.Tests は `OpenPositionsServiceTests` が `.Symbol` を参照しコンパイル不可＝IDE の改名なら一緒に直る側） | 本作業の変異注入 |
| 送り手は Minimal API の `Results.Ok(...)`。リスク管理・判断・市場監視の Program.cs は JSON 設定を変えていない（web 既定＝camelCase・列挙は数値）。**費用統制と報告書の Program.cs は `JsonStringEnumConverter` を足している** | `grep ConfigureHttpJsonOptions backend/Services/*/Program.cs` |
| 射影は `(銘柄, 市場)` ごとに 1 行、数量は常に正（向きは `Side`）、数量 0 の建玉は出さない | `PortfolioProjection.ProjectOpenPositions`（`pos.Qty == 0` は `continue`・`Math.Abs(pos.Qty)`） |

## 決定（記録は IADR-0390 の 2026-09-25 追記 / #943）

1. **契約テスト（T-10-800）**: 送り手の本物の `OpenPositionView` を web 既定で直列化し、**本番の Program.cs が組み立てた**
   `IHeldPositionProvider`（`RiskManagement:BaseUrl` を与えて DI から解決し、"risk" HttpClient の一次ハンドラだけを差し替える）に読ませる。
2. **DTO を全項目 nullable**（`string? Symbol, Market? Market, TradeSide? Side, int? Quantity, …`）。
   - 銘柄が null／空、または市場が無い**行が 1 つでもあれば応答全体を不明**（null）。その行が判断対象かどうか判らないため。
   - 一致行の方向が無い、数量が無い／正でないなら**不明**（射影の契約「数量は常に正」を破る応答は解釈できない）。
   - 価格 2 項目の欠落は従来どおり「価格だけ不明」（`HeldPosition(qty, null, null)`）。
   - 3 状態の区別: **present**＝一致行あり（`HeldPosition`）／**none**＝全行が識別でき一致なし（`HeldPosition.None`）／
     **unknown**＝非 2xx・例外・打ち切り・不正応答・識別できない行（null）。unknown は IADR-0358 の新規建て見送りへ流れる。
3. **送り手側の固定（T-10-805）**: 受け手の契約テストはどれも「送り手が web 既定のまま出している」ことを前提にする。
   その前提をリスク管理の**本物の Program.cs**（`RiskWorkerWebApplicationFactory`）で固定する —— `/open-positions`・`/sizing-context`・
   `/working-entry-orders` の本文が、同じ DI の `Build()` を web 既定で直列化したものと JSON の木として一致する。
4. **走査の結果、安全に関わる経路で fail-open するもの**（下表の ★）に契約テストを足す: サイジング文脈（T-10-802）・
   市場監視の損切り検知（T-10-803）・日報の建玉（T-10-804）。**実行時の堅牢化（nullable 化）は本件では判断の保有だけ**に留め、
   他は追随 issue #957 へ回す（契約テストが改名のマージを止めるので、残るのは「送り手を先に配備した」窓だけ）。

## 走査（他サービスの HTTP 応答を自前の型で読む箇所）

**母集合の取り方**: `backend/` の非テストの `*.cs` を `ReadFromJsonAsync|GetFromJsonAsync|ReadAsStringAsync|JsonSerializer.Deserialize|JsonDocument.Parse`
と `\.GetAsync\(|PostAsJsonAsync|\.PostAsync|\.SendAsync\(` で引き、呼び先が**本リポジトリ内の別サービス**のものを残した。
除外: 外部 API（BOJ・EDINET・Finnhub・FINRA・FRED・Google News・SEC・Stooq・Discord）、自サービスの永続化の
（逆）直列化（`*Serialization.cs`・`EfBrokerPositionObservationStore`）、LLM の出力の解析（`TradeDecisionParser`）、
送り手が本リポジトリ外（LLM ゲートウェイ `RestLlmCompletionTransport`・知識ベース `HttpKnowledgeBaseSearch` / `Writer`＝基盤リポジトリ）。
Refit のクライアントは 0 件（`[Get(` / `AddRefitClient` の非テスト使用なし）。

「改名の帰結」は、送り手の該当プロパティを改名したとき受け手が実行時にどうなるか（コードで追った。★ は本件で変異注入も実施）。
**fail open**＝安全側の結論（不明・停止・見送り）ではなく「無い／0／通常」を返す。**fail closed**＝不明・未供給・見送りへ倒れる。

| # | 受け手（アダプタ） | 口 | 受け手の型 | 送り手の型 | 契約テスト（本件前） | 改名の帰結 | 本件 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 ★ | 判断 `HttpHeldPositionProvider.GetPositionAsync` | リスク `GET /risk-controls/open-positions` | `OpenPositionDto`（private） | `OpenPositionView` | **無し**（手書き JSON） | `Symbol`→一致 0 件＝`HeldPosition.None`（**保有なし**）／`Market`→全行が 0＝日本に化け、米国株は一致 0 件＝保有なし／`Side`→0＝買い（ショートをロングと読む）／`Quantity`→0＝保有なし。**fail open（保有）** | T-10-800・T-10-801。DTO nullable・識別不能は不明 |
| 2 | 判断 `GetWorkingEntryOrdersAsync` | リスク `/working-entry-orders` | `WorkingEntryOrderDto`（private・nullable） | `WorkingEntryOrderView` | **有り**（T-10-744） | 不明（T-10-745）＝fail closed | 済（PR #940）。送り手側の固定を T-10-805 に含めた |
| 3 ★ | 判断 `HttpSizingContextProvider` | リスク `/sizing-context` | `SizingContext`（判断の自前の record） | `SizingContextView` | **無し**（既存テストは**受け手自身の型**を直列化していた） | `Capital`/残枠→null＝数量 0（fail closed）／**`ConsecutiveLosses`・`DrawdownRatio`→0＝`PositionSizer.GetSizeFactor` の縮小が外れる（fail open・上限）**／`Mode`→0＝`InternalPaper` が `OrderIntent.Mode` に載る／`Limits`→null＝判断の中で NullReferenceException（アダプタの catch の外）／`StopLossMethod`→null＝不明 | T-10-802（本番の配線で）。実行時の nullable 化は追随 |
| 4 | 判断 `HttpWatchlistProvider` | 市場監視 `GET /monitor/watchlist` | `WatchedSymbol`（自前） | `MonitoredSymbol` | 無し | `Symbol`→全行が `IsNullOrWhiteSpace` で落ち**空の watchlist を返す**（フォールバックではない）＝判断が 1 本も走らない（新規建ても判断由来の手仕舞いも止まる。損切り検知は別経路）／`Market`→0＝日本 | 追随 |
| 5 | 判断 `HttpDailyPolicyProvider` | 報告書 `GET /reports/daily-policy` | `ConfirmedDailyPolicyDto`（private） | `ConfirmedDailyPolicy` | 無し | `Summary`→null＝縮退制御なしの構成では**空の方針でプロンプトが組まれる**（FR-07「未確定なら取引しない」の門を素通り＝fail open）、縮退制御ありでは `ScreeningContextAssembler` の `policy.Summary.Length` で例外／`Date`→0001-01-01 | 追随（優先） |
| 6 | 判断・費用統制 `HttpAssumptionsClient` | 構成 `GET /assumptions` | `VersionedAssumptions` | 同じ型（`Shared.Kernel`） | —（共有型） | 改名は両側に同時に効く（コンパイル時に一致） | 対象外 |
| 7 | 情報収集 `HttpCostControlGate` | 費用統制 `GET /costs/state` | `CostStateDto`（private） | `CostControlDecision`（`IsHalted` は計算プロパティ） | 無し | `IsHalted`（または `State`）→false＝**費用上限の停止を無視して収集を続ける（fail open・費用）**／`IntervalMultiplier`→0＝`Math.Max(1, …)` で通常間隔 | 追随（優先。取引の安全ではなく費用の上限） |
| 8 ★ | 市場監視 `HttpPositionStore` | リスク `/open-positions` | `HeldPosition`（市場監視の Domain record） | `OpenPositionView` | **無し**（既存テストは**受け手自身の型**を直列化） | **`StopLossPrice`→0＝ロングは `price ≦ 0` まで発火しない（損切り保護が黙って外れる）**／`Side`→0＝ショートをロングとして判定（下落で誤発火）／`Symbol`→null。**fail open（損切りライン）** | T-10-803。実行時の堅牢化は追随 |
| 9 ★ | 報告書 `HttpOpenPositionSource` | リスク `/open-positions` | `OpenPositionDto`（private） | `OpenPositionView` | 無し | `Symbol`→全行が落ち**空列＝日報 §3「建玉なし」**（未供給ではない）／価格→0。**fail open（保有の表示）** | T-10-804。実行時の堅牢化は追随 |
| 10 | 報告書 `HttpPeriodFillSource` | リスク `/risk-controls/fills` | `LedgerFillDto`（private） | `LedgerFill` | 無し | `Symbol`→全行が落ち約定 0 件の報告書（失敗も空列へ倒す設計）／`Quantity`/`Price`→0 | 追随 |
| 11 | 報告書 `HttpPeriodDriftAdoptionSource` | リスク `/drift-adoptions` | `DriftAdoptionDto`（private） | `DriftAdoptionView` | 無し | `Symbol`→全行が落ち「該当なし」（失敗は null だが、改名は空列になる） | 追随 |
| 12 | 報告書 `HttpBuyInInferenceRecordSource` | リスク `/buy-in-inferences` | `BuyInInferenceQueryDto`（private） | 推定の照会ビュー | 無し | `PeriodCovered`→null＝未供給（fail closed）／`Inferences`→null＝`?? []` で**強制買戻し 0 件**（fail open・表示） | 追随 |
| 13 | 報告書 `HttpOpenDUptimeSource` | リスク `/session-uptime` | `SessionUptimeDto`（private） | `SessionUptimeView` | 無し | `Days`→null＝未供給（fail closed）／`Stage1CumulativeCountedDays`→0 | 追随 |
| 14 | 報告書 `HttpStageProgressSource` | リスク `GET /risk-controls/stage-gate` | `StageGateDto(TradingStage? CurrentStage)` | 段階ゲートの状態 | 無し | null＝未供給（fail closed） | 追随（低） |
| 15 | 報告書 `HttpBorrowFeeRecordSource`・`HttpFxSourceStatusSource`・`HttpLlmUsageRecordSource`・`HttpTradeRationaleSource` | 監査 `GET /audit/events/by-type` | `AuditEntryDto(Guid, string EventType, string Detail)`（各 private） | 監査台帳の行 | 無し | `EventType`→null＝全行が種別不一致で捨てられ**事象 0 件と区別できない**（fail open・表示）／`Detail`→null＝`JsonSerializer.Deserialize(null)` の ArgumentNullException が外側の catch で未供給（fail closed） | 追随 |
| 16 | 通知 `HttpKillSwitchController`・`HttpPauseController`（操作） | リスク `POST kill-switch/*`・`pause`・`resume` | `KillSwitchStateView(bool Engaged)`・`PauseStateView(bool Paused)` | `KillSwitchState`・`PauseState` | 無し | 統制そのものは送り手で成立する。改名で**報告する状態だけ**が false になる（表示の誤り） | 追随 |
| 17 | 通知 `HttpPauseController.GetStatusAsync` | リスク `GET /risk-controls/status` | `RiskStatusView`（internal・15 項目） | `RiskStatusView` | 無し | 真偽値→false＝「kill switch=OFF・新規建て=可能」と表示（統制は効いたまま・表示の誤り） | 追随 |
| 18 | 通知 `HttpGoodFaithViolationController` | リスク `POST good-faith-violations/clear` | `ClearResultView`（internal） | 解除結果 | 無し | `RemainingCount`→0＝「停止は継続します」が出ない（表示の誤り） | 追随 |
| 19 | 通知 `HttpStageGateController` | リスク `stage-gate`・`transition`・`withdrawal/evaluate` | 5 つの internal record | 段階ゲートの各ビュー | 無し（フロント契約の文言だけ） | `Accepted`→false＝**受理された遷移を「拒否」と報告**／`HaltNewEntries`→false＝撤退評価の停止を報告しない（表示の誤り） | 追随 |
| 20 | 通知 `HttpReportReviewController` | 報告書 `review`・`confirm`・`/reports` | `ReviewView`・`ReportListItem`（private） | レビュー局面のビュー | 無し | `Version`→0＝確定は版不一致の 409（fail closed）／`UnsuppliedInputs`→null＝**確定前の未供給の警告が出ない**（fail open・確認の門） | 追随 |
| 21 | BFF `OpendAuthBffEndpoints` | OpenD 認証ゲートウェイ `state` | `UpstreamState`（`JsonPropertyName` 明示） | `OpendAuthState`（`JsonPropertyName` 明示） | 無し | C# 名の改名は通信路の名前を変えない。通信路の名前の変更は null＝`NotSupplied`（fail closed） | 対象外（低） |
| 22 | BFF `Assumptions`・`Monitor`・`RiskControls` の中継 | 各サービス | 本文をそのまま中継 | — | —（フロント契約フィクスチャ） | 受け手の型が無い | 対象外 |

**安全に関わる経路**（保有・注文・上限・資金・損切りライン）で fail open するのは 1・3・8・9。本件で 4 つとも契約テストを足した。
残り（5・7 の fail open は取引の安全の外＝方針の門と費用の上限／4・10〜20 は表示・縮退）は追随 issue #957。

## 受け入れ基準

1. （T-10-800）送り手の本物の `OpenPositionView`（AAPL ロング・7203 ショート）を web 既定で直列化した応答を、**本番の Program.cs が
   組み立てた** `IHeldPositionProvider` が読み、AAPL は `HeldPosition(3378, 337.63, 320.75)`・7203 は `-100`・MSFT は `HeldPosition.None`。
   送り手の `Symbol`・`StopLossPrice` を改名すると赤。
2. （T-10-801）銘柄が無い／null／空／別名、市場が無い、一致行の方向・数量が無い、数量 0、無関係な行の銘柄が無い、`[null]` の応答は、
   `GetPositionAsync`・`GetSignedQuantityAsync` とも **null（不明）**。「保有なし」と読まない。
3. （T-10-802）送り手の本物の `SizingContextView` を直列化した応答を、本番の配線の `ISizingContextProvider` が全項目そのまま読む
   （連敗数 3・DD 0.07・`MoomooSimulate`・上限・`NoProtectiveStop`）。`ConsecutiveLosses`・`DrawdownRatio`・`Mode` の改名でそれぞれ赤。
4. （T-10-803）市場監視 `HttpPositionStore` が送り手の型の本文から損切り判定に使う建玉を読む。改名で赤。
5. （T-10-804）報告書 `HttpOpenPositionSource` が送り手の型の本文から建玉を読む。改名で赤。
6. （T-10-805）リスク管理の本物の Program.cs が返す 3 つの口の本文が、応答型を web 既定で直列化したものと一致する
   （`/open-positions` だけを文字列列挙で返す変異で赤・他の 1913 件は緑）。
7. 既存の `HttpHeldPositionProviderTests`（空配列＝0・非 2xx＝null・価格だけ不明 等）は緑のまま。
8. （T-10-806）変異注入: 上の各テストが、対応する変異（送り手の改名・アダプタの判定の除去・送り手の JSON 設定の変更）で赤になり、
   それ以外のテストは緑のままであることを実測して PR とテスト仕様書に記す。

## 変更しないもの

- 送り手（リスク管理）の応答型・エンドポイント・JSON 設定。
- 市場監視・報告書・サイジング文脈のアダプタの実行時の挙動（契約テストだけを足す。nullable 化と「不明」の扱いは追随 issue で、
  各経路の縮退の向き——市場監視は空列、報告書は未供給、サイジングは残枠 0——に合わせて設計する）。
- 判断の `HeldPosition`・建玉効果の解決・IADR-0358 の見送り（unknown が流れ込む先は既存のまま）。

## 母集合（この変更で追随する記述）

走査語: `OpenPositionDto` / `OpenPositionView` / `open-positions`（`*.cs`・`*.md`。確定済みの `.ai-context/specs`・`superpowers` を除く）。

| 箇所 | 扱い |
| --- | --- |
| `HttpHeldPositionProvider.cs` | 直す（DTO nullable・不明の判定） |
| `HttpHeldPositionProviderTests.cs` / 新 `RiskManagementReadContractTests.cs`（判断） | 足す |
| `HttpPositionStoreTests.cs`（市場監視）/ `HttpOpenPositionSourceTests.cs`（報告書）＋両 `*.Tests.csproj` | 足す（リスク管理のテスト専用参照・extern alias） |
| 新 `ReadContractWireFormatTests.cs`（リスク管理） | 足す |
| `HttpPositionStore.cs` / `HttpOpenPositionSource.cs` / `HttpSizingContextProvider.cs` の「同形」コメント | **変えない**（「同形」は今も正しい。守りが契約テストへ移ったことはテスト側に書いた） |
| `IADR-0390` 本文と索引行 | 日付つき追記（本文末の「残余: `/open-positions` の DTO は契約テストを持たない」が本件で**誤りになる**——規則 10） |
| `docs/tests/FR-10_risk-controls-tests.md` | 節を足す（T-10-800〜T-10-806）・trace ブロックへ本仕様書と #943 |

**テスト ID**: FR-10 の予約ブロック **T-10-800〜T-10-809** のうち 800〜806 を使う（市場監視・報告書の契約テストも、固定する対象は
FR-10 のリスク管理の読み取り口なので同じ系列に置く）。FR-04 は ID 付きのテスト仕様書を持たない（`T-04-` の使用 0 件）。
