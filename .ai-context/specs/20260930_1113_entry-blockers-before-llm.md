---
title: 新規建てが必ず拒否される銘柄は LLM を呼ぶ前に見送り、可否はリスク管理の審査と同じ述語で判定する（#1113）
type: spec
status: accepted
related_ids: [FR-10, FR-04, FR-11, NFR, UC-01, UC-02, ADR-0003, ADR-0009, IADR-0463, IADR-0394, IADR-0358, IADR-0462, IADR-0374, IADR-0390, IADR-0346, IADR-0420, IADR-0427, IADR-0008, IADR-0119, IADR-0452]
author: claude (Claude Code)
created: 2026-09-30
updated: 2026-09-30
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10, FR-04)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003 (リスク管理の権威と直列の配置)
---

# 新規建てが必ず拒否される銘柄は LLM を呼ぶ前に見送る（#1113）

## 背景

PoC（2026-09-29 夜の台帳）で、`TradeDecisionMade`（Buy/Open）37 件のうち承認は 5 件だった。拒否の理由は
`StoppedOutSameDay` 34 件・`MaxPositionsExceeded` 15 件（重複あり）。**審査で必ず落ちる新規建てのために、
判断は毎サイクル LLM（一次＋本判断）を呼んでいた**（`LlmCostIncurred` 511 件）。月次の LLM 費用の上限に早く達し、
健全な銘柄の判断の頻度まで落ちる。

#1113 のオーナー裁定（2026-09-30）:

1. **案 B**: リスク管理に銘柄単位の「新規建ての可否」の読み取り口を足し、判断側は結果を読むだけ。規則はリスク管理の 1 か所
   （審査と同じ述語）。IADR-0394 決定 8（と案 B の不採用）を新しい IADR で改める。**審査は残す**（両端で止める）。
   照会の失敗・未結線なら LLM を呼ぶ側へ倒す。
2. 省けるのは**保有が既知で 0、かつ未約定の新規建てが既知で空**の銘柄の新規建てだけ。決済の判断は必ず残す。
3. 対象は新規建てが「必ず落ちる」状態すべて（kill switch・一時停止・日次損失のロックアウト等）。口は審査と同じ述語で、
   確定する拒否理由を**方向別**に返す。状態が不明なら省かない。
4. 計器の移動を受け入れる（`ast.risk.rejections` の一部が判断側の見送りへ移る）。月報・ダッシュボードの注記を同じ PR で直す。

## 前提（読んだ制約）

- 計画 ADR-0003: リスク管理は発注前の最終防衛線であり、判断の結論に依らず審査する。→ **審査は外さない**。
- IADR-0394 決定 8: 判断側は損切りを知らない（案 B 不採用）。→ 本件で改める（IADR-0463）。
- IADR-0358 決定 2・IADR-0119: 保有が不明なら新規建てを見送り、決済（Close）は止めない。保有 0 の売りは裸の新規ショートとして見送る。
- IADR-0390: 未約定の新規建て注文は保有と別の第 3 の状態。不明を空に倒さない。
- IADR-0462 決定 4: LLM を呼ぶ前の見送りは `TradeDecisionForgoneBeforeLlm` を 1 回ごとに出す。理由は末尾へ足し、`DecisionSkipReason` と名前を一致させる。
- IADR-0346: 保有建玉数は未約定の新規建てを含めて数える（`OpenPositionCount`）。
- IADR-0008: 日次損失のロックアウトは審査のサービス（`OrderScreeningService`）が保持する（判定コアの外）。
- IADR-0420: 受け手が本文を読む跨サービスの照会は、送り手の本物の型による契約テストを持つ。
- IADR-0427: gRPC の読み取りは REST と同じサービス・純関数を呼ぶ。原則 A（欠落・未指定は不明）。

## 母集合（規則 9。origin/develop 94b073c9）

走査したコマンド:

```bash
grep -n "reasons.Add" backend/Services/RiskManagementService/Domain/RiskEvaluator.cs \
  backend/Services/RiskManagementService/Features/RiskManagement/OrderScreeningService.cs
grep -n "RejectionReason\.\w*" -o backend/Services/RiskManagementService/Domain/ShortSellEvaluator.cs \
  backend/Services/RiskManagementService/Domain/StageProductPolicy.cs | sort -u
```

新規建てを拒否し得る理由のすべて（審査の到達順）。**対象**は「新規建てだけを拒否し、状態から確定する（注文の数量・価格・商品種別に依存しない）、
かつ状態が既知」のもの。

| # | 理由 | 述語の入力 | 判定 | 除外の理由 |
| --- | --- | --- | --- | --- |
| 1 | `CapitalBaselineUnavailable` | 口座照会の資金が null | 除外 | **不明**の状態（照会できていない）。裁定 3「不明なら省かない」 |
| 2 | `BrokerAccountTypeUnverified` | 口座種別の照会結果の欠落・食い違い | 除外 | 不明の状態（照会の欠落）。食い違いは設定の誤りで、別途通知の対象 |
| 3 | `KillSwitchActive` | kill switch | **対象** | — |
| 4 | `TradingPaused` | 一時停止 | **対象** | — |
| 5 | `InformationSourceDegraded` | 収集の縮退**または縮退しているか分からない**（1 ビット） | 除外 | 1 ビットが縮退と不明を混ぜており、既知だけを取り出せない |
| 6 | `StageProhibitsLiveTrading` | 注文の発注先（intent.Mode） | 除外 | 注文の属性に依存し、決済にも掛かる。判断は段階の発注先で注文を作るので立たない |
| 7 | `StageCapitalCapExceeded` | 投入中資金＋**注文額** | 除外 | 金額に依存 |
| 8 | `ProductTypeDisabled` | **実効商品種別**（注文の商品種別・方向） | 除外 | 注文の属性に依存 |
| 9 | `StageProductTypeProhibited` / `StageShortSellReleaseUnmet` | 同上 | 除外 | 同上 |
| 10 | `MarketDisabled` | 取引ガードの市場 | 除外 | 新規建てだけの規則ではない（決済にも掛かる）。「新規建てを確定的に拒否する状態」の範囲外として今回は扱わない（残余） |
| 11 | `BannedSymbol` | 禁止銘柄 | 除外 | 同上（決済にも掛かる） |
| 12 | `SameDayReentry` | 実効商品種別・口座種別・当日の売買 | 除外 | 注文の商品種別に依存（米国株の信用口座では立たない） |
| 13 | `StoppedOutSameDay` | 当日の損切り（方向別） | **対象** | — |
| 14 | `StopOutStatusUnknown` | 由来不明の当日の決済 | 除外 | **不明**。裁定 3 |
| 15 | `CashAccountSettlementHold` | 決済済み資金 vs 当日累計＋**注文額** | 除外 | 金額に依存 |
| 16 | `GoodFaithViolationLimitReached` | 現金口座（照会結果）かつ GFV 件数が停止基準 | **対象（件数が既知のときだけ）** | 件数の未供給（null）でも審査は止めるが、それは不明なので口は返さない |
| 17 | `ManipulativeOrderPattern` | 注文（intent）と注文履歴 | 除外 | 注文に依存し、決済にも掛かる |
| 18 | `PerOrderAmountExceeded` | **注文額** | 除外 | 金額に依存 |
| 19 | `DailyOrderAmountExceeded` | 当日累計＋**注文額** | 除外 | 金額に依存（枠を使い切っていても、0 を超える注文額という前提を足す必要があり、規則の複製になる） |
| 20 | `MaxPositionsExceeded` | 保有建玉数（未約定の新規建てを含む） | **対象** | — |
| 21 | `DailyLossLimitReached`（判定コア） | 実現＋含み損 vs equity 比（equity が既知のとき） | **対象** | — |
| 21' | `DailyLossLimitReached`（ロックアウト。`OrderScreeningService`） | 当日のロックアウトが有効 | **対象** | 同じ理由で返す（審査と同じ） |
| 22 | `MaxDrawdownReached` | DD 比率 | **対象** | — |
| 23 | 空売り専用 9 種（`ShortSellDisabled` ほか） | 空売り文脈・注文価格・注文額 | 除外 | 新規の売り建てだけ・注文や文脈に依存。判断側は保有 0 の売りを見送るので LongSide の判定に要らない |

対象は **7 理由**（3・4・13・16・20・21/21'・22）。

## 設計

### 1. 述語の共有（リスク管理）

- `Domain/EntryStateBlockers.cs`（新設・静的）に対象の述語を 1 つずつ置く（kill switch・一時停止・損切り〔方向〕・GFV・建玉数・日次損失・最大 DD）。
- `RiskEvaluator.Evaluate` は**同じ位置で同じ関数を呼ぶ**（理由の並びは変えない）。
- `EntryStateBlockers.Determine(entrySide, settings, snapshot, stopOuts, lockedOut)` が、既知で確定する理由だけを審査の並びで返す。
- ロックアウトは `OrderScreeningService.IsLockoutActive(lockout, tradingDay)`（静的）を審査と口が共有する（口は掃除〔Clear〕しない）。

### 2. 読み取り口

- REST: `GET /risk-controls/entry-blockers?symbol=<code>&market=<数値|名前>`（`OwnerOrService`。欠落・未定義の市場は 400）。
  応答 `EntryBlockersView(Symbol, Market, LongSide, ShortSide)`（理由は数値の列挙）。
- gRPC: `RiskControlsRead/GetEntryBlockers`（`optional string symbol`・`Market market`。欠落は `INVALID_ARGUMENT`）。
  応答は存在を持つ入れ物 `EntryBlockerReasons`（`long_side` / `short_side`）に `repeated EntryBlocker reasons`。
  `EntryBlocker` は対象 7 理由と `ENTRY_BLOCKER_UNSPECIFIED = 0`。名前で写す。写せない理由は送り手で例外（試験が全対象の写像を固定）。
- `EntryBlockersService`: 設定・スナップショット（審査と同じ `PortfolioSnapshotBuilder`）・当日の損切り（`StopOutProjection.Project`、審査と同じ入力）・
  ロックアウトを読み、`Determine` を買い（LongSide）と売り（ShortSide）で呼ぶ。

### 3. 判断側の関門

- 新しいポート `IEntryBlockersProvider`（`GetAsync(symbol, market)` → `EntryBlockers?`。null＝不明）。実装は Http / Grpc / NoOp（常に null）。
  `Program.cs` は保有照会と同じ選び方（gRPC 宣言 → Grpc、BaseUrl → Http、どちらも無ければ NoOp）。
- `TradeDecisionAppService.DecideAsync` の、保有・未約定の照会と換算レートの鮮度切れの見送りの**後**・RAG の取得の**前**に置く。
  次のすべてを満たすときだけ `SkipBeforeLlmAsync(EntryBlockedByRiskControls)`:
  - 保有が**既知で 0**（`heldPosition is { SignedQuantity: 0 }`）。
  - 未約定の新規建てが**既知で空**（`workingEntries is { Any: false }`）。
  - 可否の照会が**成功**し、**LongSide** が空でない（保有 0 の売りは裸の新規ショートとして LLM の後に必ず見送られるため、ShortSide は見ない）。
- 照会は保有と未約定が上の条件を満たしたときだけ行う（保有中・不明の銘柄では照会しない）。例外は不明へ倒す（キャンセルは伝える）。
- 新しい理由 `EntryBlockedByRiskControls` を `DecisionForgoneBeforeLlmReason` と `DecisionSkipReason` の**末尾**へ足し、`ToSkipReason` に写す。
  `TradeDecisionHeld` は出さない（IADR-0452 決定 1）。

### 4. 計器・文書

- 監査台帳の要約は既存の書式（`LLM を呼ぶ前の見送り（<理由>・<起点>）`）のまま新理由を出す。
- `scripts/nightly-ledger-summary.sh` §11 に「`EntryBlockedByRiskControls` は §5 の審査の拒否から移った分」と注記。2 つの試験（スタブ・実 PostgreSQL）で固定。
- 文書: `docs/data/audit-events.md`・`docs/api/events-and-ports.md`・`docs/api/east-west-grpc.md`・月報とダッシュボードと runbook の読み方の注記・
  `docs/functional/FR-10_risk-controls.md`・`docs/tests/FR-10_risk-controls-tests.md`。

## 窓の表（規則 11）

T0＝判断が可否を読む時点（LLM の前）、T1＝審査の時点（LLM の後）。プローブ:

- **増える側**: T0 では塞がっていない → T0〜T1 に塞がる（損切り・建玉数の上限到達・kill switch の投入）。
- **減る側**: T0 では塞がっている → T0〜T1 に空く（建玉の決済で枠が空く・kill switch の解除・一時停止の解除）。

| 形 | 増える側 | 減る側 |
| --- | --- | --- |
| 後の端だけ（審査だけ＝是正前） | 拒否（正しい） | 承認（正しい）。塞がっている間は毎サイクル LLM を呼ぶ（費用の無駄＝#1113 の症状） |
| 前の端だけ（関門だけ・審査を外す） | **通る（統制の穴）** | 見送り（1 サイクル） |
| **両端（本件）** | 拒否（正しい。審査は不変） | 1 サイクル見送り（安全側。次のサイクルで読み直す） |

現行より緩む升目は無い。代償は、LLM の遅延の間に空いた場合の 1 サイクル分の機会損失だけである。

実測（試験）:

- 両端 × 増える側: 審査は変えていない（`RiskEvaluator` の既存の全試験が緑）。口と審査の一致（T-10-1782・T-10-1783）。
- 両端 × 減る側: 関門は照会の結果だけで見送り、状態を持たない（次の判断で読み直す。T-10-1791 の 2 回目の判断で LLM を呼ぶ）。
- 前の端だけの形は採らない（審査を外す変更は無い）。

## 受け入れ基準

- [ ] 口の答えと審査の拒否が、同じ入力で一致する（建玉数 上限−1/上限/上限+1、損切り None/StoppedOut/Unknown、方向、kill switch・一時停止・日次損失・ロックアウト・DD・GFV の on/off/不明）。
- [ ] 不明（`StopOutStatusUnknown`・資金の未供給・口座種別の未確認・縮退の不明・GFV 件数の未供給）では口は理由を返さない。
- [ ] REST / gRPC の両経路と、判断側の Http / Grpc の写像。失敗・不正な応答は不明（null）。
- [ ] 判断側: 保有 0・未約定なし・LongSide 塞がり → LLM 0 回で `TradeDecisionForgoneBeforeLlm(EntryBlockedByRiskControls)`。
  保有あり（ロング・ショート）・未約定あり・保有不明・未約定不明・照会の失敗・未結線・ShortSide だけ塞がり・空 → LLM を呼ぶ。
  ロング保有での決済は通る。`TradeDecisionHeld` は出さない。
- [ ] 監査台帳の行と夜間の要約 §11。
- [ ] 自己変異で赤になる（述語の共有を外す・保有の条件を外す・不明で省く・方向を取り違える）。
- [ ] build 0 warning・関連の全テスト・`dotnet format --verify-no-changes`・node の検査器。

## 残余リスク

- 可否の照会が 1 判断に 1 回増える（保有 0・未約定なしの銘柄だけ）。照会は 5 秒で打ち切り、失敗は LLM を呼ぶ側。
- `MarketDisabled`・`BannedSymbol` は決済にも掛かる規則として対象から外した（保有 0 の銘柄では新規建ては必ず落ちるが、監視銘柄の設定と重なるため費用の実害は小さい）。
- 口と審査の間でスナップショットの組み立てが別の瞬間になる（窓の表の減る側・増える側。両端で止めるので統制は緩まない）。

## 監査の指摘への対応（PR #1116 の監査）

head 6c52d4e5 に対するフェーズ末監査で、2 つの変異が全テストを緑のまま通った。製品コードは変えず、テストを足して固定した。

| 指摘 | 変異 | 足したテスト | 足した後の実測 |
| --- | --- | --- | --- |
| M6 | `EntryStateBlockers.Determine` の GFV の条件から `snapshot.Account?.AccountType == AccountType.Cash` を外す | T-10-1782 の網羅の GFV 軸に「信用口座（照会済み）＋件数が停止基準以上」（`Gfv.MarginAtLimit`。6,912 → 8,640 通り）。T-10-1784 に同じ升目を名指しし、口が空・審査も `GoodFaithViolationLimitReached` を出さないことを表明 | T-10-1782・T-10-1784 が赤 |
| M5 | `EntryBlockersService.Build` の `TradingDay.Of(now, market)` の市場を取り違える（`Market.Japan` ／ 市場なしの JST 版の 2 通り） | T-10-1783 `JSTの日付が変わっても米国の取引日内ならロックアウトを返す`（ET 9/23 11:30 ＝ JST 9/24 0:30、解除日 9/24 のロックアウト。両方向 `DailyLossLimitReached`、審査と一致。審査側の同型は OrderScreeningServiceTests `米国セッション中にJSTの日付が変わってもロックアウトは解除されない`） | 2 通りとも T-10-1783 の当該ケースだけが赤 |

網羅が穴を持った理由: T-10-1782 の GFV 軸は「現金口座でない」を Account も件数も null の 1 値でしか作っていなかった。口座種別の条件は「件数が既知で基準以上、かつ口座種別が現金でない」の升目でしか効かない。

情報事項（受容として記録。変更しない）:

- **ロックアウトのみの `DailyLossLimitReached` の位置**: 口は `DailyLoss || lockedOut` を日次損失の位置（建玉数の後・DD の前）に置くが、審査はロックアウトを判定コアの外（OrderScreeningService。判定コアの理由の末尾）へ足すため、ロックアウトだけで立つときの並びが審査と異なり得る。判断側の受け手は理由の件数（空か否か）しか見ないので実害は無い。T-10-1783 の一致は単独で立てた場合に限って並びを比べている。
- **`RiskReadWireMapping.ToProto` の例外**: 口の対象外の理由を渡すと例外を投げるが、口（`EntryStateBlockers.Determine`）は `Determinable` の 7 理由しか返さないため到達しない（T-10-1786 が対象外の理由で例外になることと、対象の 7 理由がすべて写ることを固定している）。
