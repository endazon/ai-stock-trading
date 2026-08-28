---
title: 取引サイクル・スクリーニング層の再実装 — 前提チェック 3 統制・米国市場の時刻構造・コンテキスト超過時の縮退順序
type: spec
status: approved
related_ids: [FR-02, FR-03, FR-04, FR-10, UC-01, UC-02, ADR-0003, ADR-0009, ADR-0020, ADR-0022, IADR-0245, IADR-0246, IADR-0247, IADR-0248, IADR-0249]
author: claude
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/04_workflows/01_scheduled-trading-cycle.md
  - planning:projects/ai-stock-trading/04_workflows/02_event-driven-trading.md
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
related_specs:
  - 20260828_336_information-collection-tiers-and-degradation.md
---

# 仕様書: 取引サイクル・スクリーニング層の再実装（#337）

## 起点

- 起点 issue: **#337**（親 #344。既存 #249〔取引日境界〕・#290〔解析失敗の区別〕を吸収）
- 起点 ID: **FR-02** / FR-03 / **FR-04** / FR-10 / **UC-01** / UC-02 / **ADR-0003** / ADR-0009
- 実測時点: 本リポ `claude/ast-implementation-issues-rzkoxb-w3` = `6197d69` / 計画リポ隣接クローン `666965a`
- 計画書の一次情報: `04_workflows/01_scheduled-trading-cycle.md`（fixed）・`04_workflows/02_event-driven-trading.md`（fixed）・
  `06_technical/01_architecture-overview.md`「判断の二段化」（fixed・2026-08-02 の縮退順序の確定を含む）
- 先行 PR からの引き継ぎ拘束: **PR #556（#336）の仕様書が「`BlocksNewEntries` の下流結線は #337 の射程」と明記**
  （`InformationSourceDegraded.BlocksNewEntries` の参照は下流 3 サービスで 0 件・実測済み）。

## 課題（ギャップ分析）

**11 サービスは実装済みであり、本作業はゼロからの再実装ではない。** 現行の
`TradeDecisionService` / `RiskManagementService` / `MarketMonitorService` / `InformationCollectionService` を
実測して issue 記載の要求との差分を取った。

| # | 要求（issue #337 / 計画書） | 現状（実測） | 差分（本 PR で実装） |
| --- | --- | --- | --- |
| 1 | 前提チェック 3 統制（kill switch・日次損失ロックアウト・一時停止）。いずれか成立で**新規建てのみ停止**・手仕舞い/損切りは進む | **実装済み。** `RiskEvaluator`（isEntry 短絡）＋ `OrderScreeningService`（ロックアウト維持）＋ `TradingControlPriorityTests`（8 通り境界・プロパティ・否定形の 3 点セット） | **実装差分なし**（既存テストを受け入れ基準へ対応付けて確認）。3 統制に加わる第 4 の停止要因（情報収集の縮退＝行 6）を同じ位置・同じ isEntry 短絡で足す |
| 2 | 米国東部時間ベースの市場時刻判定（9:30–16:00 ET・半日取引日 13:00 ET 終了・休場日・DST）。東証は前場/後場 | `MarketCalendar.IsOpen` は**曜日と休場日しか見ない**（時間帯・半日の概念なし。正午でも昼休みでも開場扱い） | `MarketSessions`（Domain 純関数）を新設し `MarketCalendar` を場中判定へ拡張。半日取引日は構成（`TradeCycle:HalfDays:<Market>`）で注入 |
| 3 | 取引日境界の市場別解釈（#249 吸収: JST 固定をやめ**現地取引日**で日次統制・集計） | `PortfolioProjection.TradeDate` が**固定 +9（JST）**。ロックアウトの当日判定・解除日も JST（`clock.Today`）。ET 10:00（JST 0:00 跨ぎ）に日次境界が走り、**同一 US セッション中にロックアウトが解除され得る** | `TradingDay.Of(instant, market)` を新設し、`PortfolioProjection`（当日損益・日次発注枠・同日再エントリー）・`PeriodFillQuery`（期間集計）・ロックアウトの当日/解除日判定を**約定/注文の市場の現地取引日**へ移行 |
| 4 | 縮退順序: ① 銘柄分割 → ② RAG 削除 → ③ ニュース/開示を古い順・関連度低い順に削除。**日報方針・当日市況は削らない**。上位モデル退避は採らない。分割と切り詰めを分けて数え記録・月報へ | **概念が無い。** 一次スクリーニングのプロンプトは方針＋銘柄のみ（参考情報を載せない）。超過時の縮退・記録は存在しない | `ScreeningContextPlanner`（Domain 純関数）を新設。予算構成時はスクリーニング入力（方針・市況＝保護、RAG・ニュース＝削減可）を計画し、発生を `ScreeningContextReduced` イベントで記録（監査台帳の種別 × 期間照会が月報の集計経路） |
| 5 | LLM 構造化出力の**解析失敗**時の挙動（#290 吸収: 解析不能と見送りを区別して記録） | `TradeDecisionParser` は**すべて Hold へ潰す**（`LlmDecision.Hold` の根拠文字列が「解析不能**または**見送り」＝区別しない当のもの） | `ParseDetailed`（失敗種別つき）を新設し、オーケストレータ・FR-11 ログで解析不能と見送りを区別して記録。安全既定（Hold＝取引しない）は不変 |
| 6 | 情報収集の縮退（`BlocksNewEntries`）が**新規建てを止める**ところまで結線。決済は止まらない（否定形で固定） | **結線ゼロ**（下流 3 サービスで参照 0 件・実測）。KB 文言による LLM の自制頼み | `RiskManagementService` が `InformationSourceDegraded`/`Recovered` を購読し状態を保持。`RiskEvaluator` が新規建てのみ `InformationSourceDegraded`（新設・クラス B・序数 28）で拒否。手仕舞いは isEntry 短絡で構造的に通る |
| 7 | 価格変動トリガー ±3%（既定・設定変更可）・クールダウン・即時起動 | **実装済み**（`MonitorSettings.MovementThresholdRatio` / `Cooldown`・Ef 永続化・`PriceMovementDetectedHandler` 合流） | 実装差分なし |
| 8 | 判断根拠の必須記録（FR-04）・方針/リスク制約の範囲内でのみ判断 | **実装済み**（プロンプト構造・FR-11 ログ・`TradeDecisionMade.Rationale`） | 実装差分なし（行 5 の区別記録で強化） |

## 対象範囲

- **対象**: 上表 2〜6。`TradeDecisionService`（Domain / Application / Infrastructure / Api）、
  `RiskManagementService`（Domain / Application / Infrastructure / Api）、`Shared.Contracts`（新規イベント 1 種
  `ScreeningContextReduced`・拒否理由 1 種）、`AuditService`（新規イベントの台帳記録）。
- **対象外**:
  - **月報テンプレートへの縮退件数の描画。** #336 が同じ形（欠測の期間集計）で確定した線引きに従い、
    本 PR の到達点は**監査台帳の種別 × 期間照会まで**とする（`ScreeningContextReduced` が `Split`/`Truncated` を
    分けて持ち、期間で数えられる）。`ReportService` の描画は #338（報告サイクル）の射程。
  - **`MarketMonitorService` の `IMarketSchedule`（平日のみの近似）の差し替え。** ポートのコメントどおり
    #21 系の関心事。監視の閉場ガードは近似のままでも安全側（余分にポーリングするだけで発注に直結しない）。
  - **多銘柄バッチのスクリーニング呼び出しへの再配線。** 現行は銘柄ごとに独立呼び出し（＝縮退順序 ① の
    「分割」が構造的に常時成立）。プランナは多銘柄の分割を実装・テストするが、呼び出し形の変更は行わない
    （IADR-0247 に判断を記録）。
  - **`InformationCollectionService` の `DegradationStateTracker` の永続化**（再起動で縮退継続中の再発行が
    出ない残余リスク。IADR-0249 の残余リスク欄に記録し、解消は別 issue）。
  - moomoo 可用性（`BrokerAvailabilityObserved`）の収集側への引き込み（#336 仕様書の残件）。本 PR は
    `BlocksNewEntries` の結線（リスク管理側）に限る。理由: サイクル中止（AbortCycle）は収集側で既に
    `InformationCollected` を発行しない形で効いており、リスク側の結線と独立。

## 設計

### 1. 市場時刻構造（表 2・IADR-0245）

- `TradeDecision.Domain.MarketSessions`（純関数）: 市場 × 現地時刻 × 半日フラグ → 場中か。
  - 米国: 通常 [9:30, 16:00) ET・半日 [9:30, 13:00) ET（終了時刻は排他＝Closing Cross 時刻で閉場扱い）。
  - 東証: 前場 [9:00, 11:30) ∪ 後場 [12:30, 15:30)。半日取引日なし（フラグは無視。計画の対比表どおり）。
- `MarketCalendar`（Infrastructure）: 週末 → 休場日 → 半日判定 → `MarketSessions` の順。TZ 変換は従来どおり
  `TimeZoneInfo`（DST を吸収。固定オフセット換算をしない＝計画の明文）。半日集合は
  `TradeCycle:HalfDays:<Market>` から注入（休場日と同じ書式）。

### 2. 取引日境界（表 3・IADR-0246）

- `TradingDay.Of(DateTimeOffset, Market)` を新設（US=America/New_York・JP=Asia/Tokyo。変換は 1 か所）。
- `PortfolioProjection`: `TradeDate(instant)`（JST 固定）を**削除**し `TradeDate(instant, market)` へ置換
  （呼び出し残しをコンパイルエラーで捕まえる fail-loud）。`Project` は `DateOnly today` に代えて
  `DateTimeOffset now` を受け、約定ごとに `TradeDate(f.ExecutedAt, f.Market) == TradeDate(now, f.Market)` で
  当日判定する。
- `PeriodFillQuery`: 取引日の解釈を同じ関数へ追随（「日次上限が見ている 1 日」と「日報が集計する 1 日」の
  一致という既存の設計意図を、市場別解釈のまま保つ）。
- ロックアウト: `OrderScreeningService` の当日判定・解除日算出を**注文の市場の現地取引日**基準へ
  （`TradingDay.Of(clock.UtcNow, intent.Market)`）。`RiskStatusService`（表示専用）は**最も遅い市場の現地取引日**
  （サポート市場の現地日付の最小値）で判定し、表示が実際の統制より先に「解除」と見えないようにする。
- **JST のまま残すもの（意図的な除外）**: `SystemClock.Today`・観測到達（`IPositionObservationArrivalStore`）・
  買戻し推定の期間（IADR-0181 が JST 一本化を確定済み。FR-21 の突合構造に触れない）・
  `ObservedDrawdownRefreshService` / `WithdrawalEvaluationService` の営業日ゲート（日次バッチの起動条件であり
  日次統制の境界ではない）・報告書の生成タイミング（`ReportSchedule`。閉場後 JST 起動は生成の都合であり
  集計境界は `PeriodFillQuery` 側で市場別になる）。

### 3. スクリーニング入力の縮退（表 4・IADR-0247）

- `TradeDecision.Domain.ScreeningContextPlanner`（純関数）:
  - 入力: 共有保護サイズ（日報方針等）・銘柄ごとの保護サイズ（当日市況・価格）・削減可能材料
    （`RagReference` / `NewsDisclosure`、サイズ・発行時刻・関連度つき）・予算（文字数）。
  - 手順（計画の表を写像）: ① 銘柄を分割（材料は減らさない）→ ② RAG を関連度の低い順に削る →
    ③ ニュース/開示を**古い順・関連度の低い順**に削る（発行時刻不明は最古扱い＝先に削る保守側）。
  - **保護対象（方針・市況）は削減可能集合に入らない＝型として削れない**（`CollectionDegradation` の
    `ClosesAllowed` と同じ構造防御）。全段を使っても収まらない場合は `UnresolvableOverflow` を立てて
    **削らずに**返す（上位モデルへの退避は採らない＝計画の明文。プランナはモデルという概念を持たない）。
  - 出力: バッチ列・`SplitOccurred` / `TruncationOccurred` / `DroppedRagCount` / `DroppedNewsCount`。
    **分割と切り詰めは別のカウンタ**（計画「分けて数える」）。
- 文字数は 200K トークンの近似プロキシ（予算は構成値 `TradeCycle:Decision:ScreeningContextBudgetChars`）。
  未構成（null）＝縮退制御なし＝現行プロンプト・現行挙動（安全な既定・オプトイン）。
- 構成時のスクリーニングプロンプト: 方針（保護）＋対象銘柄＋現在値（保護）＋縮退後の参考情報
  （`TradeDecisionPromptBuilder.BuildScreening` 拡張。データ/命令の構造分離は本判断と同じ 1 件 1 行 JSON
  フェンス方式を再利用）。RAG/ニュースの分類は `RetrievedContext.Tags` で行う
  （`report`＝RAG 過去判断 → ②、ニュース/開示/マクロ源 → ③、市況源・`collection-status`＝保護）。
- 記録: `Shared.Contracts.Events.ScreeningContextReduced`（発生した判断ごと）。`AuditService` が台帳へ記録
  （全イベント購読のカバレッジテストが強制）。通知は**追加しない**（`InformationSourceDegraded` と同じ扱い。
  計画の要求は「記録し月報に件数」であり、警告通知は求めていない）。

### 4. 解析不能と見送りの区別（表 5・IADR-0248）

- `TradeDecisionParser.ParseDetailed` → `ParsedTradeDecision(Decision, Failure?)`。失敗種別:
  空出力 / JSON 抽出不能 / JSON 不正 / action 不明 / 数値不正。既存 `Parse` は `ParseDetailed(...).Decision` の
  互換ラッパとして残す（安全既定 Hold は不変）。
- `DecisionOrchestrator`: スクリーニング・各票の解析結果を区別してログし、`OrchestratedDecision` に
  `ScreeningUnparseable` / `UnparseableVotes` を載せる。`TradeDecisionService` の FR-11 ログ行にも出す
  （Hold はイベントを発行しないため FR-11 ログが唯一の監査記録＝IADR-0104 決定 6 の既存判断に従う）。

### 5. 縮退状態の結線（表 6・IADR-0249）

- `RiskManagement.Application.Ports.IInformationDegradationStore`（カテゴリ集合を保持。
  `BlocksNewEntries=true` の Degraded で追加・Recovered で除去・残があれば停止）＋ InMemory 実装。
- `RiskManagement.Infrastructure` に Wolverine ハンドラ 2 種（Degraded / Recovered）。
- `PortfolioSnapshot.InformationDegradedBlocksNewEntries` を追加し、`PortfolioSnapshotBuilder` の
  **必須依存**として合成（IADR-0163 決定 2「不在が統制の無効を意味する依存は必須にする」）。
- `RiskEvaluator`: `isEntry && snapshot.InformationDegradedBlocksNewEntries` で
  `RejectionReason.InformationSourceDegraded`（**末尾追加・序数 28・クラス B**）。手仕舞いは isEntry 短絡で
  構造的に通る（否定形テストで固定）。

## 母集合の引き直し（`.claude/rules/traceability.md` 規則 1〜10・`traceability.repo.md` 規則 9・10）

走査はすべて `grep -rn`（パス除外は `obj/` のみ・拡張子で絞らない〔規則 3〕・行フィルタなし〔規則 4〕）。
数値はいずれも**本仕様書コミット前**の実測である（規則 8。本書自身が `BlocksNewEntries` 等の検索語を含むため、
コミット後の同一走査は本書の分だけ増える）。

| 軸 | 検索語 | 結果 | 判断 |
| --- | --- | --- | --- |
| 1 | `BlocksNewEntries` | 8 ファイル 15 行（InformationCollection の Domain/Infra/tests・AuditService・Shared.Contracts） | 下流（TradeDecision / RiskManagement / OrderExecution）は 0 件 ＝ 引き継ぎ拘束のとおり未結線。本 PR で RiskManagement へ結線 |
| 2 | `TradingDayOffset` / `TradeDate(` | src 2 ファイル（`PortfolioProjection` 4 行・`PeriodFillQuery` 1 行）＋ tests。Backtest の 1 件は moomoo API の `OnReply_RequestTradeDate`（無関係・除外） | 両方を市場別解釈へ移行。ReportSchedule の `JstOffset` は生成タイミング（境界ではない）のため除外（設計 §2） |
| 3 | `FromHours(9)`（固定 JST オフセットの別表記・規則 2） | 17 行。RiskManagement では `PortfolioProjection` と tests のみ。他は情報源コネクタの発行時刻解釈（EDINET/BOJ＝日本の公的情報源の現地時刻＝正しい）と FX の AsOf（ADR-0022 の管轄） | `PortfolioProjection` のみ対象。コネクタ・FX は現地時刻の解釈として正しいため除外 |
| 4 | `clock.Today` / `Clock.Today`（RiskManagement src） | 12 行 | `OrderScreeningService`（4 行）・`RiskStatusService`（1 行）・`LedgerPortfolioStateProvider`（2 行）を移行。`BuyInInferenceService` / `ShortSellingStatusService`（買戻し推定・IADR-0181 の JST 確定）・`ObservedDrawdownRefreshService` / `WithdrawalEvaluationService`（日次バッチ起動ゲート）は意図的に除外（設計 §2） |
| 5 | `解析不能または見送り` | 1 行（`LlmDecision.Hold`） | この文字列自体が #290 の「区別しない」当の実装。`Parse` の呼び出し元は `DecisionOrchestrator` のみ（screening 1・votes 1） |
| 6 | `.IsOpen(`（別軸: `IMarketCalendar` 実装・スタブ） | src 3 呼び出し（TradeDecision ハンドラ 2・MarketMonitor 1）＋ tests | TradeDecision 側を場中判定へ。MarketMonitor は対象外（前掲） |
| 7 | `new PortfolioSnapshotBuilder(` | 16 か所 9 ファイル（すべて RiskManagement の src/tests） | 必須依存の追加に全数追随 |
| 8 | `PortfolioProjection.Project(`（規則 10: 是正で新たに誤りになる自分の記述の引き直し） | src 1 ファイル（`LedgerPortfolioStateProvider` 2 か所）＋ `PortfolioProjectionTests` 26 か所 | シグネチャ変更に全数追随（旧シグネチャは残さない＝fail-loud） |

- **除外したものと理由**は各行の「判断」列に記した（規則 6）。
- 導出値（拒否理由の序数 28・イベント基準ファイル）は走査でなく**再計算・再生成**する（規則 10:
  序数表は `Enum.GetValues` 網羅テストが、イベント基準は `UPDATE_EVENT_BASELINE=1` の再生成が固定する）。

## 受け入れ基準（issue #337 §退行防止）と対応テスト

| 受け入れ基準 | テスト |
| --- | --- |
| 3 統制成立時にサイクルが新規建てへ進まない（手仕舞い・損切りは進む） | 既存 `TradingControlPriorityTests`（8 通り境界＋プロパティ＋否定形）で充足済みであることを確認。第 4 の停止要因（情報縮退）は `RiskEvaluatorTests` / `InformationDegradationTests`（新設）で同型の 3 点セット |
| 市場時刻のテーブルテスト（DST 切替日・半日取引日・休場日・ET 基準） | `MarketSessionsTests`（境界テーブル）＋ `MarketCalendarTests`（DST 切替日 2026-03-08 / 2026-11-01 前後の UTC→ET 写像・半日 13:00 ET 終了・休場日・東証昼休み） |
| 縮退順序 ①→②→③ の順で発動・保護対象が削られない否定形・分割/切り詰め別カウントの正確性 | `ScreeningContextPlannerTests`（順序の境界テーブル・プロパティベース〔保護対象は常に全バッチに残る/カウンタ＝実削除数〕・否定形〔全段導入でも保護対象を削る計画を返さない・上位モデル概念を持たない〕）＋ `TradeDecisionServiceTests`（縮退時もプロンプトに方針・現在値が残る否定形と、残る肯定形の対） |
| 解析失敗時の挙動（解析不能と見送りを区別して記録） | `TradeDecisionParserTests`（失敗種別テーブル）＋ `DecisionOrchestratorTests`（解析不能票の計数・スクリーニング解析不能の区別。見送り Hold は Failure なし＝対の肯定形） |
| 縮退状態の結線（新規建て停止・決済は止まらない否定形） | `RiskEvaluatorTests` 追加分＋ `InformationDegradationStoreTests`＋ Wolverine ハンドラテスト（Degraded→ 新規建て拒否 / Close 承認 / Recovered→ 解除 / `BlocksNewEntries=false` は止めない） |
| 取引日境界の市場別解釈 | `PortfolioProjectionTests` 追加分（ET 深夜跨ぎの約定が同一 US 取引日に載る/JST 境界では別日になっていた反例）＋ `OrderScreeningServiceTests`（US セッション中に JST 日付が変わってもロックアウトが解除されない否定形） |

## 実環境待ち・残件

- スクリーニング予算の実値（`claude-haiku-4-5` の 200K トークンに対する文字数プロキシの係数）は実測待ち。
  既定は未構成（縮退制御オフ）であり、有効化は運用構成で行う。
- `RetrievedContext` に発行時刻が無いため、③「古い順」は現状**関連度順のみ**が実効（発行時刻は null＝最古扱い）。
  KB 検索ヒットへの発行時刻の伝搬は KB 契約（`KnowledgeHit`）の拡張であり別作業（IADR-0247 に記録)。
- 縮退状態（RiskManagement 側）は InMemory であり、リスクサービス再起動で消える（fail-open 側の残余リスク。
  IADR-0249 に記録。収集側 tracker も InMemory で同型）。
- 月報テンプレートへの件数描画は #338（前掲「対象外」）。
