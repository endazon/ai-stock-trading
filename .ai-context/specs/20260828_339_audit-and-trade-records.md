---
title: 監査・取引記録の再実装 — 経費区分 7 種の建玉単位紐づけ
type: spec
status: approved
related_ids: [FR-09, FR-11, UC-07, ADR-0016, NFR-08, NFR-10, NFR-11]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
---

# 仕様書: 監査・取引記録（経費区分 7 種の建玉単位紐づけ）

起点 issue: #339（親 #344 フェーズ 3）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-11**（全イベントの時系列監査ログ／取引記録の経費区分 7 種／建玉単位の紐づけ／配当相当額を配当と混同しない／後から集計可能な粒度）・FR-09（Discord 通知）
- ユースケース: UC-07（取引履歴・判断根拠の参照）
- 非機能要件: **NFR-08**（重複排除ストア 既定 90 日・下限 7 日）・**NFR-10**（費用台帳・発注履歴・監査ログは 7 年保持でパージ対象外）・**NFR-11**（パージは既定で無効）
- 計画 ADR: **ADR-0016 決定 15**（経費区分 `Realized` / `BorrowFee` / `MarginInterest` / `DividendInLieu` / `Commission` / `Fee` / `FxCost`）・ADR-0027 決定 2（建玉の一次識別子は (銘柄, 市場)。銘柄・口座へは合算で導出する）

## 1. ギャップ分析（現状 → 要求 → 差分）

**本 issue はゼロからの再実装ではない。** 11 サービスは実装済みであり、監査サービスは追記専用台帳・冪等・
相関照会・期間照会まで完成している。以下は**コードを実際に読んで**確認した現状である（推測ではない）。

| # | issue の要求 | 現状（実測） | 差分（本作業で作るもの） |
| --- | --- | --- | --- |
| 1 | 全イベント（収集・判断・発注・通知）の時系列監査ログ | **ほぼ達成。** 契約イベント **33 種**すべてに監査ハンドラがあり（走査で一致を確認・§2）、`AuditConsumerCoverageTests` が Wolverine の実行時発見結果で追随漏れを CI で止める。`EfAuditEventStore`（`audit_events`・追記専用・`Id`=Envelope.Id で冪等）・`AuditQueryEndpoints`（相関／直近／種別×期間） | **取引サイクル 1 周の完全性テストが無い。** また `AuditEntryFactory` の写像が全数そろっていることは**ハンドラ側でしか**担保されていない（写像の欠落は検査されない） |
| 2 | 経費区分 7 種を**建玉単位**で紐づける | **存在しない。** `TradeExpenseCategory` に相当する型はコード全体に無い（`git grep TradeExpense` は 0 件）。近いものは 2 つだけ: ① `BorrowFeeAccrual`（借株料**のみ**・(銘柄, 市場, 取引日)）② `CostCalculator.EstimateOneWayCost`（**概算見積り**＝手数料＋為替スプレッド。区分を持たず、実費でもない）。`PnlAggregator` は費用を `totalCost` の**一括値**に畳んでおり区分が復元できない | **新規**: 経費区分 enum（7 種・序数固定）・区分の性質（実現損益 / 譲渡費用）・経費 1 行の型・**建玉単位の集計純関数**・**経費台帳イベント**・**監査結線**（建玉ごとの相関） |
| 3 | `DividendInLieu` を配当と混同しない | 型が無いため**表現不能**（混同を防ぐ手段が存在しない） | **新規**: 区分の性質写像（`DividendInLieu` → 譲渡費用）・要約の否定形注記・**否定形テスト群**（§5） |
| 4 | FR-18 は Won't だが後から集計可能な粒度 | — | 建玉 × 区分 × 発生日 × 発生元で 1 行。集計は純関数で導出（**7 区分すべてを常に返し、0 円と未計上を件数で区別する**） |
| 5 | 台帳・監査証跡は 7 年でパージ対象外／重複排除は既定 90 日・下限 7 日／パージ既定オフ | **構造的には満たしている**（パージは `processed_messages` と `order_dispatch_reservations` の 2 経路だけで、`audit_events` ほか 32 テーブルはどのパージ経路にも載っていない。`RetentionOptions.Enabled` 既定 `false`）。**しかしそれを固定する宣言も検査も無い**——新しいパージ経路を足しても何も赤くならない | **新規**: `RetentionScope`（パージ可のストアを**閉じた列挙**で宣言し、それ以外はすべて 7 年保持＝fail-safe）と、既存 2 経路からの `EnsurePurgeable` 呼び出し（宣言を**load-bearing** にする） |
| 6 | Discord 通知連携と秘匿情報（#313 / #318 の Webhook URL 露出）の構造的解消 | **解消済み**（#289）。`DiscordWebhookHttpClientExtensions` が Webhook 専用クライアントの既定ロガーを `RemoveAllLoggers()` で外し、`RedactedUriHttpClientLogger` が `scheme://host/***` だけを出す。回帰テスト 4 本が固定済み | **監査台帳側の否定形が無い。** 監査台帳は**契約イベント全量を JSON で 7 年保持する**ため、契約イベントに秘匿情報の項目が 1 つでも生まれたら 7 年残る。**契約イベントのプロパティ名を全数走査する構造テスト**を新設する |

### 1.1 供給（実際に値を積み始めること）は本作業の範囲外である

**7 区分のうち、実費が供給されている区分は 1 つも無い。** 実測:

- `OrderExecuted` は**手数料を運ばない**（`DecisionId` / `OrderId` / `Status` / `FilledQuantity` / `AveragePrice` / `ExecutedAt` / `Provider` のみ）。`BrokerOrder` も同じ。→ `Commission` / `Fee` / `FxCost` の実費供給なし
- `BorrowFeeAccrualService` は**本番の呼び出し元を持たない**（`Program.cs` の DI 登録だけ）。ADR-0027 決定 6 が ADR-0026 の PoC 項目 9（`ShortFeeRate` の単位確定・期限 2026-08-31）を前提に供給を意図的に遮断している。→ `BorrowFee` の供給なし
- `MarginInterest` / `DividendInLieu` はブローカ照会の実装自体が無い

したがって本作業は **ADR-0027 §結果「実装は記録側を先行して設計してよい」と同じ遮断**に従い、
**記録側（型・区分・イベント・監査結線・集計）だけを作る。** 供給を発明して概算値を実費として
積むことはしない——`CostCalculator` は概算であり、それを経費台帳へ入れると
**「表示されている数字が何を意味するか誰も答えられない」状態**（ADR-0027 が塞いだもの）へ戻る。

### 1.2 なぜ新しい永続テーブルを作らないか

**経費台帳の 7 年保持の実体は既存の `audit_events` である。** 監査台帳はイベント全量を JSON で保持し
（`AuditEntry.Detail`）、期間・種別の照会（`GetByTypesInPeriod`）と相関照会（`GetByCorrelation`）を持つ。
報告サービスは既にこの経路で期間集計を行っている（`HttpFxSourceStatusSource`）。
新テーブルを足すと**同じ事実の権威が 2 つ**になり、片方だけ更新される事故を作る。
建玉単位の照会は、借株料と同じ作法（`borrow-fee:{Symbol}:{Market}`）で
**相関 `trade-expense:{Symbol}:{Market}`** を用いて成立する。

## 2. 母集合の引き直し（`traceability.repo.md` 規則 9・10）

### 2.1 「必須イベント」の全数走査（記憶で挙げない）

契約イベントの母集合は **`backend/Shared/AiStockTrading.Shared.Contracts/Events/` の record 型**である
（`EventTypeDiscovery.GetEventTypes()` と同じ定義）。走査コマンドと結果:

```
grep -l "^public record\|^public sealed record" backend/Shared/AiStockTrading.Shared.Contracts/Events/*.cs
  → 33 件
grep -o "class \([A-Za-z]*\)AuditHandler" .../AuditEventHandlers.cs
  → 33 件
diff（両者の型名リスト） → IDENTICAL（差分ゼロ）
```

除外したファイルと理由（黙って落とさない）:

| ファイル | 除外理由 |
| --- | --- |
| `AuditDetailJson.cs` | `static class`（イベントではなく JSON 設定の単一情報源） |
| `EventTypeDiscovery.cs` | `static class`（母集合を返す補助） |

**33 種の全数と、取引サイクル 1 周における位置づけ**（計画 `04_workflows/01_scheduled-trading-cycle.md`
のフロー図・シーケンス図から機械的に割り当てた）:

| 区分 | イベント | サイクル 1 周の必須か |
| --- | --- | --- |
| 収集 | `InformationCollected` | **必須**（フロー C） |
| 判断 | `TradeDecisionMade` | **必須**（フロー D） |
| 発注審査 | `OrderApproved` / `OrderRejected` | **必須**（フロー G。承認と拒否は排他） |
| 発注・約定 | `OrderExecuted` | **必須**（フロー H・I。承認側の分岐でのみ発生） |
| 前提チェックの中断 | `DailyPolicyUnconfirmed` | 条件付き（フロー B の NG 分岐） |
| 市場トリガ | `PriceMovementDetected` / `StopLossTriggered` | 条件付き（`02_event-driven-trading` の起点。定時サイクルでは発生しない） |
| 注文の後続操作 | `OrderModified` / `OrderCancelled` | 条件付き（訂正・取消が起きたときだけ） |
| 統制・観測・報告・費用・為替（23 種） | `AssumptionsChanged` / `ReportConfirmed` / `ReportDraftPresented` / `CostThresholdReached` / `LlmCostIncurred` / `StageTransitioned` / `WithdrawalTriggered` / `BacktestEvaluated` / `GoodFaithViolationRecorded` / `GoodFaithViolationsCleared` / `BorrowFeeAccrued` / `BorrowFeeAccrualUnavailable` / `BuyInInferred` / `BrokerAccountObserved` / `BrokerAvailabilityObserved` / `BrokerPositionsObserved` / `PositionReconciliationDrift` / `PositionCloseRequested` / `MaintenanceMarginReductionExecuted` / `FxRateSourceFellBack` / `FxRateSourcePrimaryRestored` / `FxRateStale` / `PositionClosedWithStaleFxRate` | 非必須（サイクルとは独立の事象。監査記録の対象ではあるが「1 周で必ず出る」ものではない） |

> **内訳の検算**: 必須・条件付きの各行が 10 種、最終行が 23 種、合計 33 種（走査結果と一致）。
> **必須集合は 5 種**（`InformationCollected` → `TradeDecisionMade` → `OrderApproved` → `OrderExecuted`、
> または `OrderRejected` で終わる拒否経路）であり、テストはこの 2 経路を両方通す。

### 2.2 本変更で新たに誤りになる自分の記述（規則 10）

イベントを 1 種増やすため、「購読対象は N 種」と書いた**導出値**が誤りになる。誤りの側（数詞）で全走査した:

```
git grep -nE "[0-9]+ *(イベント|種)" -- backend docs scripts .github .claude | grep -iE "監査|購読|audit"
```

| 箇所 | 記述 | 実測 | 対応 |
| --- | --- | --- | --- |
| `AuditService.Infrastructure/Composable/Steps/AuditEventHandlers.cs:15` | 「22 イベントそれぞれに 1 本ずつキューを持つ」 | **既に誤り**（33） | **数詞を落とす**（「契約イベントの全数」へ）。**再発しない形にする**——数詞は増設のたびに腐る導出値であり、`AuditConsumerCoverageTests` が全数一致を機械で保証している |
| `AuditService.Api/Program.cs:44` | 「購読対象は AuditEventHandlers.cs の 21 種」 | **既に誤り**（33） | 同上 |

除外したもの（理由つき）:

| 対象 | 除外理由 |
| --- | --- |
| `.ai-context/adr/**` `.ai-context/specs/**` の数詞（`IADR-0037` の「10 イベント」等） | **凍結記録**。当時の事実であり本文プロズを後から書き換えない（`.ai-context/README.md`） |
| `NotificationService` の「11 種」「10 種」 | 通知サービスの購読集合であり、本作業は通知の購読を増やさない（監査のみ） |
| `docs/blocked-tasks.md` の借株料の行 | 数詞ではなく供給状況の記述。本作業は供給を開始しないため内容が変わらない |

**導出値は走査ではなく計算し直した**（33 = 走査結果。本作業後は 34 になるが、**どこにも書かない**）。

## 3. 対象範囲

- **対象**: 経費区分の契約型・分類・建玉単位の集計純関数／経費台帳イベントと監査結線／保持区分の宣言と
  既存パージ 2 経路への強制／監査完全性・写像全数・秘匿情報非出力の各テスト／データ仕様書
- **対象外**: 実費の**供給**（§1.1）／FR-18 の集計機能・画面・報告書への表示／新しい永続テーブル（§1.2）／
  Discord Webhook の秘匿対策そのもの（#289 で解消済み。本作業は監査台帳側の否定形を足すだけ）

## 4. 設計

### 4.1 経費区分（`Shared.Contracts.Trading`）

```
TradeExpenseCategory : Realized=0, BorrowFee=1, MarginInterest=2, DividendInLieu=3,
                       Commission=4, Fee=5, FxCost=6      ← ADR-0016 決定 15 の列挙順
TradeExpenseNature   : RealizedProfitAndLoss, TransferCost
TradeExpenseClassification.NatureOf(category)
    Realized → RealizedProfitAndLoss ／ 残り 6 種 → TransferCost
```

- 序数は**整数として往来し得る**ため `RejectionReason` と同じ規律で固定し、専用テストが全メンバを表で押さえる。
- 🔴 **本 enum に「受取配当」を表す区分は存在しない。** `DividendInLieu` は**支払**であり、税務上は譲渡費用に
  近い扱いである。メンバ名走査のテストが `Dividend` を含むメンバを `DividendInLieu` **ただ 1 つ**に固定する
  （`Dividend` / `DividendIncome` を後から足すと赤くなる）。

### 4.2 経費 1 行と集計

```
TradeExpense(Symbol, Market, Category, AmountUsd, OccurredOn, SourceId, RecordedAt)
TradeExpenseCategoryTotal(Category, AmountUsd, LineCount)   // LineCount で「0 円」と「未計上」を区別する
PositionExpenseSummary(Symbol, Market, Totals)              // Totals は常に 7 区分ぶん・enum 順
TradeExpenseLedger.SummarizeByPosition(expenses) / SummarizePosition(expenses, symbol, market)
```

- 建玉の一次識別子は **(Symbol, Market)**（ADR-0027 決定 2）。**銘柄別・口座全体は合算で導出する**——
  別に積むストアは作らない。
- `PositionExpenseSummary.TotalExpensesUsd` は **`Realized` を含まない**（実現損益を費用へ混ぜない）。
  `DividendInLieu` は**含む**（譲渡費用に近い扱い）。この 2 点が「配当と混同しない」の実体である。
- 符号の約束: `TransferCost` の 6 区分は**費用額を正で**持つ。`Realized` のみ符号付き（損失は負）。

### 4.3 経費台帳イベントと監査結線

```
TradeExpenseRecorded(TradeExpense Expense)
  → AuditEntryFactory.From: 相関 = AuditCorrelation.From($"trade-expense:{Symbol}:{Market}")
                            Symbol = 銘柄, OccurredAt = Expense.RecordedAt
  → TradeExpenseRecordedAuditHandler（AuditEventHandlers.cs の**末尾へ追加**）
```

要約の書式（🔴 区分ラベルを先頭側に置き、切り詰めで注記が落ちないようにする）:

```
経費計上 配当相当額の支払い（**配当の受取ではありません**／譲渡費用に近い扱い） AAPL/UnitedStates 12.34 USD（計上日 2026-08-28・発生元 …）
```

### 4.4 保持区分（`Shared.Contracts.Operations.RetentionScope`）

```
PurgeableStores = ["processed_messages", "order_dispatch_reservations"]   // 閉じた列挙
IsPurgeable(store) / EnsurePurgeable(store)   // 列挙外は InvalidOperationException
```

- **既定は「パージしない」。列挙に無いストアはすべて 7 年保持**（NFR-10）とする閉世界の規則にする——
  7 年側を列挙すると**テーブルが増えるたびに腐り、漏れた側が黙ってパージ可になる**（fail-open）。
  閉世界なら未知は必ず非パージ側へ倒れる（fail-safe）。
- 宣言を飾りにしないため、既存の 2 経路（`ProcessedMessageRetentionService.PurgeOnceAsync` /
  `OrderReservationRetentionService.PurgeOnceAsync`）が削除前に `EnsurePurgeable` を呼ぶ。
- `order_dispatch_reservations` は**終端行だけ**がパージ対象である（NFR-09。未確定予約は無期限保持）。
  この区別は既存のストア実装（`PurgeCompletedBefore`）が持つ。

## 5. 受け入れ基準（issue の退行防止 → テスト写像）

- [ ] **経費 7 区分の網羅**: enum が 7 メンバちょうど・序数が表と一致・`NatureOf` が 7 区分すべてで定義される
- [ ] **否定形: `DividendInLieu` が配当扱いにならない** — ① `NatureOf` が `RealizedProfitAndLoss` ではない
      ② 費用合計に**含まれる** ③ 実現損益の合計を**動かさない** ④ enum に `Dividend` を含むメンバが 1 つしか無い
      ⑤ 監査要約に「配当の受取ではありません」が出る
- [ ] **建玉への紐づけ整合**: 同一 (銘柄, 市場) は 1 つの `PositionExpenseSummary` に集まり、市場違い・銘柄違いは分かれる。
      監査台帳では建玉ごとの相関で引ける／別建玉の行が混ざらない
- [ ] **取引サイクル 1 周で必須イベントがすべて記録される**: 承認経路（収集→判断→承認→約定）と拒否経路
      （収集→判断→拒否）の 2 経路。時系列（`OccurredAt` 昇順）・同一 `DecisionId` 相関
- [ ] **写像の全数**: `AuditEntryFactory` の `From` 過負荷の引数型集合が契約イベントの全数と一致する
- [ ] **7 年対象がパージされない**: `audit_events` ほか業務台帳は `IsPurgeable` が false・`EnsurePurgeable` が投げる。
      パージ経路 2 本は宣言済みストアしか受け付けない
- [ ] **パージ既定オフ**: `RetentionOptions.Enabled` 既定 false（既存テストが担保。本仕様で参照する）
- [ ] **否定形: 秘密情報の非出力**: 契約イベントのプロパティ名に秘匿情報を示す語が 1 つも無い／
      経費イベントの監査要約・payload に Webhook URL 形の文字列が現れない

## 6. テスト方針（統制系 3 点セット）

| 3 点セット | 本作業での実体 |
| --- | --- |
| **境界値テーブル** | `[Theory] [InlineData]` で 7 区分 × 序数、7 区分 × 性質を**全メンバ表**で固定（表に無いメンバがあれば失敗する） |
| **プロパティベース** | 全区分に対し `NatureOf` が例外を投げない／`IsExpense` が `Realized` のみ false／集計は入力順序に依存しない／空入力でも 7 区分ぶんのキーが返る |
| **否定形** | 配当混同 5 本・パージ 3 本・秘匿情報 2 本（上記受け入れ基準） |

## 7. 計画書との差異

- 差異: **なし**（計画の定義どおり 7 区分・(銘柄, 市場) 単位）。ただし**供給が未着手**である点は
  ADR-0027 §結果が明示的に許した「記録側の先行」であり、計画違反ではない。`docs/blocked-tasks.md` へ
  供給の未着手を追記する。

## 8. 未決事項

- 実費の供給元（手数料・信用金利・配当相当額の照会）は moomoo PoC（#342 / ADR-0026 項目 9）の成立待ち。
  供給が始まるまで、経費台帳は**行が 1 件も無い**（0 円ではなく未計上である。`LineCount` で区別する）。

## 9. 検証結果（実測・2026-08-28）

| 検証 | 結果 |
| --- | --- |
| `dotnet build backend/backend.slnx` | 0 Error / 0 Warning |
| `dotnet test backend/backend.slnx` | **3,926 passed / 8 failed / 0 skipped**（51 アセンブリ）。失敗はすべて `AiStockTrading.IntegrationTests` で、**Docker が無い環境の既知の制約**（`Failed to connect to Docker endpoint at 'unix:///var/run/docker.sock'`）。CI 相当の `--filter "Category!=Integration"` では **3,917 passed / 0 failed** |
| `dotnet format backend/backend.slnx --verify-no-changes` | 差分なし（exit 0） |
| カバレッジ（Release・`Category!=Integration`・レポート 51 件） | **79.46%（14,190/17,859 行）/ floor 79.00%** — exit 0。**床は下げていない** |
| 文書・トレーサビリティ検査 | `check-doc-links` / `check-trace-blocks` / `check-cross-repo-refs` / `check-test-traceability` / `check-banned-libraries` / `check-banned-settled-cash-sources` / `check-consumer-endpoint-names` / `check-plan-id-qualification` / `check-reading-budget` / `check-adr-index-sync` / `check-action-versions` / `check-ai-workflow-config` / `check-workflow-job-refs` / `gen-knowledge-graph --check` がいずれも exit 0 |
| `scripts.test.js` | 267 tests passed |

### カバレッジについての注意（次に触る人へ）

**同一のソースファイルが、カバレッジレポートの `filename` の根（root）ごとに別のキーとして分母へ入る。**
`check-coverage.js` は `filename` をそのまま集計キーにしており、正規化しない。実測では
`Shared.Contracts` の 1 ファイルが 3 つの根で現れた。

| 根 | 産出元 | 例 |
| --- | --- | --- |
| `Trading/...` | `AiStockTrading.Shared.Contracts.Tests`（対象アセンブリが Contracts のみ） | `Trading/TradeExpense.cs` |
| `AiStockTrading.Shared.Contracts/...` | `AiStockTrading.Shared.Infrastructure.Tests`（対象が `backend/Shared/` 配下） | — |
| `Shared/AiStockTrading.Shared.Contracts/...` | 各サービスのテスト（対象が `backend/` 配下） | — |

したがって **`Shared.Contracts` へ 1 行足すと分母は最大 3 行増える。**
本作業では、契約側の新規行を Contracts テストとサービステストの 2 面から被覆し、
さらに**契約イベントの全数を実走させるテスト**（`契約イベントの全数が監査台帳へ記録される`）で
既存の未被覆ハンドラ・写像を被覆して床を回復した。**この実走テストは床対策ではなく、
issue の受け入れ基準「全イベントの時系列監査ログ」そのものである。**
