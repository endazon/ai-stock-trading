---
title: 最小の名目額（equity の 1%）に満たない新規建てを見送り、判断由来の決済（利確）の後は同じ取引日のうち同じ方向の新規建てを統制で止める（#1176）
type: spec
status: accepted
related_ids: [FR-10, FR-04, FR-11, UC-01, UC-02, ADR-0003, ADR-0009, ADR-0018, IADR-0495, IADR-0394, IADR-0463, IADR-0471, IADR-0003, IADR-0017, IADR-0130, IADR-0374, IADR-0462, IADR-0134, IADR-0163, IADR-0246]
author: claude (Claude Code)
created: 2026-10-07
updated: 2026-10-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 リスク統制・「手仕舞い（Close）と損切りは止めない」・「生成AIはこれらを上書きできない」)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 リスク統制・取引ガードの既定値。本件の 2 統制の行は無い)
---

# 最小の名目額と、判断由来の決済の後の同日・同方向の再エントリー禁止（#1176）

## 背景（issue の観測）

- 2026-10-05 US: NVDA を利確で全量売却（17:53:39 UTC・1049 株）した 5 分後に、同じ NVDA を段階資金の残枠（約 $2.5k）で 10 株（約 $2.4k）新規に買った。
- 2026-10-07 02:34:50 JST: AMZN を利確で全量売却（970 株 @255.65）した 5 分後の 02:39:56 に、同じ AMZN を 951 株 @255.63 で買い直した（取得単価が付け替わり、次の +3% の基準が上がる。費用のある本番では往復ごとに損になる）。
- 2026-10-07 04:47 JST: AAPL を 13 株 @334.11（約 $4.3k・equity の約 0.45%）で新規に建てた。段階資金の残枠が約 $4.3〜4.7k まで減っていたため。
- 現状の統制: 損切り後の同日同方向の再エントリーの禁止（`StoppedOutSameDay`）は利確には効かない（`EntryStateBlockers.StopOut`・`StopOutProjection` が `OrderApproved` 由来の決済を数えない）。差金決済防止は米国では現金口座だけ（`AccountTypePolicy.AppliesSameDayReentry`）。最小の名目額は計画にもコードにも無い。

## 裁定（オーナー・2026-10-07。issue の最後のコメント）

1. **最小の名目額で見送る**: サイジングの結果が equity の 1% 未満の新規建ては見送る（建玉枠・承認・LLM 費用を消費しない）。しきい値は構成で持つ。
2. **利確後に間隔を置く**: 利確（判断由来の決済）で手仕舞った銘柄は、同じ取引日（その市場の現地取引日）のうちは同じ方向の新規建てをしない（損切り後の `StoppedOutSameDay` と同じ形の統制）。
3. 実装は統制（リスク管理の審査またはサイジング）側で行い、方針の文言に頼らない。

## 計画の確認（pp-main 280f04f・読み取り専用）

- **FR-10**（リスク統制）が起点。commit の起点 ID は **FR-10**。FR-04（判断）はサイジングと LLM の前の見送りの地点として触るが、規則は統制の語彙で持つ。
- 05_trading-assumptions §5 には最小の名目額の行も、損切り・利確の後の同日再エントリー禁止の行も無い（損切りの方も #935 の裁定で入った実装側の統制で、計画に載っていない＝IADR-0394 §フォローアップ）。→ 計画へ記録を求める issue の下書きを PR に添える（起票はしない）。
- FR-10「既定値は全体前提条件に持ち、変更は方針確定プロセスまたは利用者の設定変更のみ」「生成 AI は上書きできない」: しきい値は取引判断サービスの構成（環境変数）で持ち、既定値は `TradingDefaults` に置いて試験で固定する（生成 AI は触れない）。

## 実測（origin/develop 9d74d128）

- サイジングは取引判断サービス（`TradeDecisionAppService.DecideAsync`）が **LLM の後**に行う。株数＝`PositionSizer.CalculateCappedQuantity`（リスク基準＝equity × 1% ÷ 1 株の損切り幅、を `min(1 注文上限, min(段階残枠, 日次残枠))` ÷ 価格で上から抑える）。損切り幅は LLM の出力に下限を掛けた幅なので、**株数は LLM の後でしか決まらない**。
- ただし名目額（株数 × 価格）は常に `min(1 注文上限, 段階残枠, 日次残枠)` 以下である（`CalculateCappedQuantity` の金額キャップ）。したがって **この上限そのものが equity × しきい値 を下回れば、LLM の結論に依らず新規建ては必ず最小の名目額を割る** —— LLM の前に計算できる下界である（AAPL の 0.45% はまさにこの形: 残枠 $4.3k ＜ equity 約 $970k の 1% ≈ $9.7k）。
- LLM の前の見送りは #1113（IADR-0463）の関門と同じ線引きで行える: 保有が既知で 0・未約定の新規建てが既知で空の銘柄では、LLM の結論は「新規の買い（必ず最小を割る）」「売り（裸の新規売りとして必ず見送り）」「Hold」しか無い。保有中・不明の銘柄では決済の判断を残すため省かない。
- `TradeDecisionMade` の発行元は取引判断サービスだけで（`TradeDecisionAppService` の 2 か所）、リスク管理の `OrderScreeningService.ScreenAsync` がそれを審査して `OrderApproved` を出す。owner の手仕舞い（`PositionCloseService`）・維持率の自動縮小（`MaintenanceMarginReductionService`）は審査を通らず `OrderApproved` を直接出す。台帳の承認行は 3 経路とも `OrderApprovedLedgerHandler` が `ApprovalSource.OrderApproved` で書くため、**台帳の上では判断由来の決済と owner の手仕舞い・自動縮小を区別できない**。`OrderApproved.CycleTrigger` は判断由来だけが持つが、IADR-0307 が「統制の判定には一切使わない」と定めた観測の値である。
- 判断由来の決済は保有の全量（`effect.CloseQuantity`）。部分的な決済は判断からは出ない（約定が部分で終わることはある）。
- 損切りの統制の入力（`IPortfolioLedgerStore.GetCloseApprovals`・`StopOutProjection.Lookback`＝2 日）は EF（PostgreSQL）の台帳であり、**再起動で消えない**。
- 審査は数量を減らさない（`OrderScreeningResult.Approved(quantity)` は意図の数量のまま）。判断が決めた名目額がそのまま発注される。
- `RejectionReason` の末尾は `StopOutStatusUnknown`（序数 31）。`ApprovalSource` の末尾は `ProtectionLostClose`（3）。`DecisionSkipReason` は 15 値、`DecisionForgoneBeforeLlmReason` は 5 値。gRPC の `EntryBlocker` は 1〜7。
- 採番: origin/develop の T-10 の最大は 2287、IADR の最大は 0493。並行の #1189 が **IADR-0494** と **T-10-2288〜T-10-2293** を先に使い（先にマージされる）、#1190 が T-10-2300 以降を使うため、本件は **IADR-0495**・**T-10-2310〜T-10-2322** を使う（衝突したら最大＋1 へ改番する。欠番受容の規則）。

## 設計

### A. 最小の名目額（サイジング側・取引判断サービス）

| 対象 | 変更 |
| --- | --- |
| `RiskManagementService.Domain.TradingDefaults` | `MinEntryNotionalRatio = 0.01m`（equity の 1%）を足す。`TradingDefaultsTests` で固定する |
| `RiskManagementService.Domain.MinimumEntryNotional`（新規・純関数） | `IsBelow(notional, equity, ratio)`（`ratio > 0` かつ `notional < equity × ratio`。**ちょうど 1% は通す**）／`CapacityCannotReach(equity, maxOrderAmount, availableCapital, ratio)`（`min(maxOrderAmount, availableCapital) < equity × ratio`）／`Validate(ratio)`（`0 ≤ ratio ≤ 0.25` 以外は例外）。`PositionSizer` と同じ置き場（判断サービスが extern alias で読む共有の統制の語彙） |
| `TradeDecisionService` の構成 `Sizing:MinEntryNotionalRatio` | `MinimumEntryNotionalOptionsLoader.FromConfiguration`: 未設定・空は既定（`TradingDefaults.MinEntryNotionalRatio`）。**読めない値・範囲外は例外**（`Program.cs` は構築時に読むので起動が止まる＝fail-fast）。`0` は統制を外す（明示の運用判断でだけ使う） |
| `TradeDecisionAppService` | 省略可能引数 `MinimumEntryNotionalOptions?`。**未指定は既定（1%）で効く**（不在を「統制なし」にしない。IADR-0163 決定2 の規律）。① **LLM の前**: 保有が既知で 0・未約定が既知で空・資金と両残枠が既知（null でない）・`CapacityCannotReach` が真 → `SkipBeforeLlmAsync(EntryCapacityBelowMinimumNotional)`。#1113 の照会（ネットワーク）より先に置く（手元の値だけで決まるため）。② **サイジングの後**: 数量 > 0 で `数量 × 参照価格（基準通貨）` が `IsBelow` → `SkipJudgedAsync(SizedBelowMinimumNotional)`。採算ゲートより前 |
| `DecisionForgoneBeforeLlmReason` | 末尾に `EntryCapacityBelowMinimumNotional` |
| `DecisionSkipReason` | 末尾に `EntryCapacityBelowMinimumNotional`（LLM の前。上と同名）・`SizedBelowMinimumNotional`（LLM の後） |
| `TradeDecisionAppService.ToSkipReason` | 新しい値の写像 |

- 対象は**新規建て（Open）だけ**。決済は名目額を見ない（FR-10「手仕舞いは止めない」）。保有中の買い増し・売り増しも新規建てなので②が効く。
- ①が効かない経路（保有中・不明・残枠が未供給・価格に依存して割る場合）は②が止める。①は費用の最適化、②が統制の本体。
- しきい値の範囲の上限 0.25 は 1 注文あたりの上限の既定（equity の 25%）であり、これを超えると新規建てが構造的に成立しない（誤設定として起動を止める）。

### B. 判断由来の決済の後の同日・同方向の再エントリー禁止（審査側・リスク管理）

| 対象 | 変更 |
| --- | --- |
| `OrderApproved`（共有契約） | 末尾に `bool FromTradeDecision = false`。**審査（`OrderScreeningService`）が判断を承認したときだけ true**。owner の手仕舞い・自動縮小・発注執行の内部の構築は既定（false）のまま。旧いメッセージは false として読まれる |
| `ApprovalSource` | 末尾に `TradeDecision = 4`（判断由来の承認行）。`OrderApproved = 0` の意味は「判断を経ない承認（owner の手仕舞い・自動縮小）、および本値の追加前の判断由来」へ改める |
| `OrderApprovedLedgerHandler` | `message.FromTradeDecision ? TradeDecision : OrderApproved` で由来を書く |
| `DecisionExitReentrySupply`（新規・`StopOutReentrySupply` と同じ形） | 方向ごとの bool（ロング＝売りで決済した建玉／ショート＝買いで決済した建玉）・`ForEntry(side)`・`NoneToday` |
| `DecisionExitProjection`（新規・純関数。`StopOutProjection` と同じ形） | 由来が `TradeDecision` の決済で、**承認または約定の時刻が当日**（`TradingDay.Of(・, market)`）なら、その決済の方向に立てる。別市場は数えない。`Lookback` は `StopOutProjection.Lookback` を共有 |
| `RejectionReason` | 末尾に `DecisionExitSameDay`（序数 32・**クラス A**） |
| `RiskEvaluator.Evaluate` | 省略可能引数 `decisionExits`。新規建てで同じ方向に立っていれば `DecisionExitSameDay`（`StoppedOutSameDay` の直後） |
| `EntryStateBlockers` | `DecisionExit(supply, side)` を足し、`Determinable` と `Determine` に加える（`Determine` は必須引数。審査と同じ位置・同じ述語） |
| `OrderScreeningService` / `EntryBlockersService` | 台帳の決済の読み取りを 1 回にし、`StopOutProjection` と `DecisionExitProjection` の両方へ渡す（新規建てだけ読む規律は不変） |
| gRPC `EntryBlocker` / `RiskReadWireMapping` / `GrpcEntryBlockersProvider` | `ENTRY_BLOCKER_DECISION_EXIT_SAME_DAY = 8` を足し、名前で写す（baseline を `--update`） |
| `TradeDecisionPromptBuilder.EntryBlockerLabel` | 「本日この方向で判断由来の決済（利確）済み」 |

- **数えるもの**: 判断由来の決済（LLM の Sell によるロングの全量決済・Buy によるショートの全量決済）。利益・損失を問わない（裁定が「利確（判断由来の決済）」と括った）。
- **数えないもの**: 損切り（S0 / S1。`StoppedOutSameDay` が別の理由で止める＝理由を混ぜない）・保護喪失の成行手仕舞い・owner の手仕舞い（人の裁量）・維持率の自動縮小（口座全体の事情）。
- **反対方向は止めない**（裁定「同じ方向」）。
- **承認だけで数える**（約定を待たない・S1 と同じ）。判断の決済は承認から約定まで数秒だが、約定が台帳へ届く前に次の判断の審査が来得る。承認されたが約定しなかった決済（ブローカーの拒否・見送り）でも、その日の同じ方向の新規建て（実質は買い増し）は止まる —— 判断が手仕舞うと決めた日に同じ方向へ積み増すことはしない（保守側）。
- **部分約定の決済**: 承認で数えるので、部分約定でも全量でも同じ扱い（その日の同じ方向は止まる）。
- **不明**: 由来が記録されていない（null）当日の決済は、すでに `StopOutStatusUnknown` が同じ方向を止めている。本統制は不明の値を持たない（同じ行を 2 つの理由で数えない）。
- **配備当日の取りこぼし（fail-open・1 取引日だけ）**: 配備前に書かれた当日の判断由来の決済は由来が `OrderApproved` であり、owner の手仕舞いと区別できないので数えない。翌取引日から効く。配備はセッションの外で行う。
- **再起動**: 入力は EF の台帳（`approved_orders.Source`）であり、再起動後も同じ答えになる（`StoppedOutSameDay` と同じ）。DB のマイグレーションは不要（`Source` は int で、値の追加だけ）。

### 選択肢（IADR-0495 に詳述）

- A の置き場: 審査（リスク管理）で名目額を見る案は、しきい値を 2 サービスに持つか、LLM の後にしか止められない。サイジング（判断側）に置けば LLM の前の下界も使える。→ **サイジング側**。
- B の識別: `CycleTrigger` の有無で判断由来を見分ける案は IADR-0307（観測の値を統制に使わない）に反する。台帳の承認行を審査ハンドラが別に書く案は、同じ DecisionId の冪等（先勝ち）と競合する。→ **契約に明示の印を足す**。
- B の形: `StopOutProjection` を一般化して 1 つの供給にする案は、損切りの 3 値（不明を持つ）と本件の 2 値を混ぜ、既存の試験（T-10-770〜）の意味を変える。→ **同じ形の別の射影・別の理由**。

## 走査した母集合（規則 2・6・9・10）

走査語（誤りの側を含む）: `StoppedOutSameDay|StopOutReentrySupply|StopOutProjection|ApprovalSource|EntryStateBlockers|Determinable|EntryBlocker|EntryBlockedByRiskControls|AddOnBlockedByRiskControls|DecisionForgoneBeforeLlmReason|DecisionSkipReason|SizingZeroQuantity|CalculateCappedQuantity|new OrderApproved\(|判断由来の決済|7 理由|15値|15 種|5 つ`（`git grep`。`.ai-context/specs/` は point-in-time の記録のため除外）。

| ファイル | 扱い |
| --- | --- |
| `backend/Services/RiskManagementService/Domain/{EntryStateBlockers,RiskEvaluator,StopOutReentrySupply,TradingDefaults}.cs` | 変更（新しい述語・引数・既定値）。`StopOutReentrySupply` は据え置き（参照のみ） |
| `backend/Services/RiskManagementService/Features/RiskManagement/{ApprovalSource,OrderScreeningService,StopOutProjection,RiskReadWireMapping}.cs`・`GetEntryBlockers/EntryBlockersService.cs` | 変更（由来の値・読み取りの共有・写像）。`StopOutProjection` は射影の規則を変えない（注記だけ） |
| `backend/Services/RiskManagementService/Infrastructure/Steps/OrderApprovedLedgerHandler.cs` | 変更（由来の分岐） |
| `backend/Services/RiskManagementService/Features/RiskManagement/{MaintenanceMarginReductionService,ClosePosition/PositionCloseService}.cs` | 据え置き（`OrderApproved` を既定＝判断を経ない承認で出す。正しい） |
| `backend/Services/OrderExecutionService/.../OrderExecutionAppService.cs`（`new OrderApproved` 1 か所） | 据え置き（発行しない内部の値。保護レグの構築にだけ使う） |
| `backend/Shared/AiStockTrading.Shared.Contracts/{Events/OrderApproved,Events/TradeDecisionForgoneBeforeLlm,Observability/DecisionSkipReason,Trading/RejectionReason,Trading/RejectionReasonClassification}.cs` | 変更（末尾追加・分類は既定のクラス A のまま注記） |
| `backend/Shared/AiStockTrading.Shared.Grpc/Protos/.../risk_controls_read.proto`・`scripts/proto-contract-baseline.json` | 変更（enum 値の追加。非破壊） |
| `backend/Services/TradeDecisionService/Features/TradeDecision/DecideTrade/{TradeDecisionAppService,TradeDecisionPromptBuilder}.cs`・`Infrastructure/ExternalServices/{GrpcEntryBlockersProvider,MinimumEntryNotionalOptionsLoader}.cs`・`Program.cs` | 変更・新規 |
| `backend/Services/TradeDecisionService/Infrastructure/ExternalServices/HttpEntryBlockersProvider.cs` | 据え置き（理由を数値で往復し `Enum.IsDefined` で判定。新しい値は既知になる） |
| `backend/Services/TradeDecisionService/Features/TradeDecision/RecordStage0Decisions/Stage0DecisionRecorder.cs` | **据え置き**（理由は IADR-0495 §結果。Stage 0 は AI の判断の記録であり、本番の統制を全部は再現していない〔審査・損切りの再エントリー禁止も無い〕。名目額の床だけを足すと記録の指紋が変わる） |
| `backend/Services/AuditService` | 据え置き（理由を名前の文字列で残す。列挙の写像を持たない） |
| `backend/Services/NotificationService` | 据え置き（拒否理由を `ToString()` で出す。写像を持たない） |
| `scripts/nightly-ledger-summary.sh` | 据え置き（§5・§11 は理由を名前で数える。新しい名前は自動で出る） |
| `deploy/observability/dashboards/ai-stock-trading-business.json` | 据え置き（理由はタグ値で、列挙しない。説明文は既存の 2 理由の読み方だけ） |
| `docs/functional/FR-10_risk-controls.md` | 追記（2 統制の節。「数えない決済」の行に判断由来の決済は別の理由で止まると注記。対象の理由の行に `DecisionExitSameDay`） |
| `docs/tests/FR-10_risk-controls-tests.md` | 節を追加（T-10-2310〜） |
| `docs/data/audit-events.md`・`docs/api/events-and-ports.md`・`docs/observability/observability.md`・`docs/operations/nightly-ledger-summary-runbook.md` | 追記（理由の列挙・`OrderApproved` の項目・数の更新「5 つ」→「6 つ」・「15 種」→「17 種」） |
| `.ai-context/adr/IADR-0394`・`IADR-0463`・`IADR-0471`・`IADR-0003`・`IADR-0017` | 日付つき追記（本文は書き換えない）。索引 `README.md` に IADR-0495 の行と追記の注記 |
| `deploy/helm/ai-stock-trading/values.yaml` | 注記だけ（既定は既定値で効くため env は足さない。変えるときのキー名を書く） |

規則 10（この変更で新たに誤りになる自分の記述）: `ApprovalSource.OrderApproved` の要約（「判断由来の新規建て・決済」を含む）、`EntryStateBlockers` 冒頭の「7 理由」、`StopOutProjection` の表の `OrderApproved` 行の注記（判断由来）、`TradeDecisionForgoneBeforeLlm` の「5 値」、`DecisionSkipReasonTests` の「15 値」、`HeldAddOnBlockersTests` の「7 理由」、プロンプトの口の 7 理由の注記、`TradeDecisionAppService.ToSkipReason` の「5 値」。

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験 |
| --- | --- | --- |
| 1 | 名目額が equity の 1% 未満は見送り、ちょうど 1% と超過は通す（純関数） | T-10-2310 `MinimumEntryNotionalTests` |
| 2 | 既定値は 1%（`TradingDefaults`）。構成の未設定は既定、0〜0.25 は採用、読めない値・負・0.25 超は起動を止める（例外） | T-10-2311 `TradingDefaultsTests`・`MinimumEntryNotionalOptionsLoaderTests` |
| 3 | 判断（端から端）: サイジングの名目額が 1% 未満なら発注意図を作らず `SizedBelowMinimumNotional`（判断後の見送り）。ちょうど 1% は発注意図を作る。AAPL 13 株（約 $4.3k・equity $970k）の例 | T-10-2312 `MinimumEntryNotionalDecisionTests` |
| 4 | LLM の前: 保有 0・未約定なし・`min(1 注文上限, 残枠)` が 1% 未満なら LLM を呼ばず `EntryCapacityBelowMinimumNotional`。保有中・残枠の未供給・しきい値 0 なら LLM を呼ぶ | T-10-2313 同上 |
| 5 | 決済は名目額で止めない（保有 13 株の利確の売りは通る） | T-10-2314 同上 |
| 6 | 判断由来の決済の当日・同方向（ロング → 買い、ショート → 売り）は `DecisionExitSameDay`。反対方向は止めない。翌取引日は止めない。別市場は数えない | T-10-2315 `DecisionExitProjectionTests`・`DecisionExitReentryEvaluationTests` |
| 7 | 区切りは市場の現地取引日（米国東部。夏時間の開始・終了、JST の日付変更で解けない。東証は JST） | T-10-2316 `DecisionExitProjectionTests` |
| 8 | 損切り（S0 / S1）・保護喪失・owner の手仕舞い（`OrderApproved`）・由来なしは本統制では数えない（損切りは `StoppedOutSameDay`、由来なしは `StopOutStatusUnknown` のまま） | T-10-2317 同上・`StopOutProjectionTests` の既存 |
| 9 | 承認だけ（約定なし）・部分約定でも数える | T-10-2318 同上 |
| 10 | 口（`EntryStateBlockers.Determine`）は審査と同じ答え（組み合わせの全数に判断由来の決済の次元を足す）。gRPC・REST の写像 | T-10-2319 `EntryStateBlockersTests`（T-10-1782 の拡張）・`EntryBlockersEndpointTests`（T-10-1786 の全写像）・`GrpcEntryBlockersProvider` の試験 |
| 11 | 本番構成（`Program.cs`）: 判断由来の決済の `OrderApproved` を流すと台帳に `TradeDecision` の由来で残り、同じ銘柄の買いが `DecisionExitSameDay` で拒否され計器に出る。owner の手仕舞いの `OrderApproved` では止まらない。AMZN の例（02:34:50 JST の利確の 5 分後の買い） | T-10-2320 `DecisionExitReentryWiringTests` |
| 12 | 再起動: 別の DbContext（＝別プロセス相当）で読んでも由来 `TradeDecision` が残り、同じ答えになる | T-10-2321 `LedgerCloseApprovalsTests` の拡張 |
| 13 | 序数（`RejectionReason` 32・`ApprovalSource` 4）・分類（クラス A）・見送りの語彙の数（17 値・6 値）・LLM の前の見送りの写像 | T-10-2322 既存の序数・分類・語彙の試験の更新 |
| 14 | `dotnet build` 警告 0・関係するサービスの試験が緑・`dotnet format --verify-no-changes`・CI の node 検査緑 | 実測（PR 本文） |

### 変異（主要な分岐）

実装をコミットした後、各変異を当てて関係する試験スイートを走らせ、`git checkout` で戻す。結果は試験仕様書と PR 本文へ。

| # | 変異 |
| --- | --- |
| M1 | 名目額の比較を `<=` にする（ちょうど 1% を見送る） |
| M2 | サイジングの後の名目額の判定を外す |
| M3 | LLM の前の下界を外す |
| M4 | LLM の前の下界で保有 0 の条件を外す（保有中でも省く） |
| M5 | 構成の範囲検査を外す |
| M6 | 判断由来の決済の射影で方向を反転する |
| M7 | 当日の判定を UTC の日付にする |
| M8 | 審査で判断由来の決済の理由を評価しない |
| M9 | 口（`Determine`）に判断由来の決済を足さない |
| M10 | 台帳の由来を常に `OrderApproved` で書く（印を無視） |
| M11 | 審査が `FromTradeDecision` を立てない |
| M12 | 射影が `OrderApproved`（owner の手仕舞い）も数える |
| M13 | 射影が約定だけで数える（承認を見ない） |
| M14 | 口が判断由来の決済を供給しない |
| M15 | 審査が判断由来の決済を供給しない |

実測（コミット 3562f081 に当て、リスク管理 2,211 件・取引判断 1,522 件を全件実行。M1・M5・M9 は両方）: **15 本すべて赤（生存 0）**。
変異ごとの赤の内訳は試験仕様書 FR-10 の本件の節。最少は M11・M14・M15（各 1 件・本番構成の配線の試験 T-10-2320）。
（試験の ID は実測の後に並行 PR との衝突を避けて T-10-2310〜へ改番した。試験の中身は同じ。）

## 残余

- 配備当日に配備前の判断由来の決済は数えない（1 取引日だけ）。
- Stage 0 の記録・バックテストは本件の 2 統制を再現しない。
- しきい値は equity 比の 1 値（市場・段階で分けない）。日本株（単元 100 株）では 1% を割りにくい。
- 稼働での確認（NVDA・AMZN・AAPL の形が見送り・拒否に変わり、計器と監査台帳に名前で出る）は PoC セッションで行う。
