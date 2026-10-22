---
title: LLM の前の見送りを 2 か所で足す（新規建ての可否の口に閉場前の締め切りとテーマの上限を足す／判断由来の決済が約定した直後の巡回で出口の判断の LLM を呼ばない）
type: spec
status: done
related_ids: [FR-04, FR-10, FR-19, ADR-0003, ADR-0050, ADR-0070, ADR-0071, IADR-0463, IADR-0471, IADR-0535, IADR-0561, IADR-0562, IADR-0564, IADR-0572]
author: claude (Claude Code)
created: 2026-10-23
updated: 2026-10-23
---

# 仕様書: LLM の前の見送りの是正（#1433・#1434）

> 本仕様書は実装着手前に作成した（着手 2026-10-23。AST `origin/develop` `cacfa796`）。

## 起点（トレーサビリティ）

- 起点: #1433（新規建ての可否の口が閉場前の締め切り・テーマの上限を見ない）、#1434（判断由来の決済の約定直後の巡回で、出口の判断の LLM を呼ぶ）。
  どちらも稼働 PoC（2026-10-21 の米国セッション）の監視で見つかった**費用の無駄**であり、安全上の穴ではない（審査・関所が最終的に止めている）。
- 計画: FR-04（方針の範囲で判断）、FR-10（リスク統制）、ADR-0070（閉場前の新規建ての締め切り）、ADR-0071（テーマごとの同時保有の上限）、
  ADR-0050 決定 1（判断の手仕舞いは処理中の決済を差し引く）、業務フロー 01 §補足（閉場前の統制・テーマの上限）。
- 前提の IADR: IADR-0463（新規建ての可否の口）、IADR-0471（保有中の銘柄の買い増しにも口を当てる）、IADR-0535（処理中の決済が覆うときに LLM を省く）、
  IADR-0561（締め切りの審査）、IADR-0562（テーマの上限の審査）、IADR-0564（効く値の解決）。
- 本件の判断: IADR-0572（新規。0571 は開いている PR #1440 が使用中）。
- 計画との関係: 規則は変えない。強制の場所（審査）も変えない。ADR-0070 の「締め切りの強制の場所」の選択肢 3「LLM の前の口に足す（不採用）」は
  **強制の場所としての不採用**であり、本件は審査を残したまま口にも足す（両端で止める。IADR-0463 決定 1）。食い違いではないが、計画の記述が
  「口には足さない」と読める余地があるので、環流案として報告する（起票はしない）。

## #1433: 原因（コードで確認・develop `cacfa796`）

- `EntryBlockersService.Build` が `EntryStateBlockers.Determine` に渡すのは kill switch・一時停止・当日の損切り・判断由来の決済・日次損失のロックアウトだけ。
- 審査（`OrderScreeningService.ScreenCoreAsync`）は `SessionAndThemeControlResolution.EffectivePreCloseEntryCutoff` / `EffectiveThemeConcentration`（保存値＞構成）で
  効く値を解決し、`PreCloseEntryCutoffSupply(now, MarketHours.SessionCloseOn(market, now), minutes)` と `ThemeConcentrationSettings.Exceeded(symbol, market, snapshot.HeldOrPendingSymbols)` を
  `RiskEvaluator.Evaluate` に渡す。口はこの 2 つを持たない。
- 両理由とも**銘柄（と市場）と状態だけで決まり、注文の数量・価格・商品種別・方向に依らない**（締め切りは新規建てを方向を問わず止める。テーマは方向を問わず銘柄を数える）。
  よって口の対象（状態から確定する拒否）に入れられる。

## #1434: 原因（コードで特定）

- プロンプトの保有（`GetHeldPositionSafeAsync` → `IHeldPositionProvider.GetPositionAsync`）と、LLM の後の Sell の関所（`GetSignedHeldQuantitySafeAsync` → `GetSignedQuantityAsync`）は
  **同じ読み元**（リスク管理の `GET /risk-controls/open-positions`＝台帳の射影。gRPC も同じ口）。HTTP 実装の `GetSignedQuantityAsync` は `GetPositionAsync` の数量を返すだけである。
- ずれは**時点**にある。台帳は発注執行の約定追跡（30 秒周期）が約定を書いて `OrderFilled` を出し、リスク管理が射影するまで約定を知らない。
  HOOD の時系列では:
  1. 巡回の冒頭で台帳を読む → まだ 2339 株（約定は未反映）。プロンプトは保有中として組まれる。
  2. 保有中・買い増しは審査で必ず落ちる（当日の判断由来の決済 `DecisionExitSameDay`）→ `AddOnKnownForgone` が真 → 処理中の決済の照会（IADR-0535）。
  3. 発注執行は記録上まだ非終端の決済（候補あり）があるのでブローカーへ建玉を照会し、**決済方向の建玉 0** を観測（ログ 04:15:38「建玉 0」）。
     処理中の決済は終端（約定）なので数えず、`closable > 0` を満たさないため `NotCovered` を返す。
  4. 取引判断は `NotCovered` を「LLM を呼ぶ」と読み、一次・二次の LLM を呼ぶ（約 15 秒）。
  5. LLM の後の関所で台帳を読み直す → この間に約定が射影されて 0 → `NakedShortOpen`（IADR-0119）で見送り。
- つまり LLM の前に**約定を知っている唯一の読み元**（発注執行がブローカーから得た決済方向の建玉 0）が、応答の `ClosableQuantity` に載っていたのに、
  取引判断の受け皿が読まず捨てていた（REST の DTO にはあるが判定に使わない。gRPC は運ばない）。読み直し（台帳）を LLM の前へ寄せても、台帳がまだ遅れているので止まらない。

## 方式

### #1433

1. `EntryStateBlockers` に 2 つの述語を足す（審査と口が同じ関数を呼ぶ）:
   `PreCloseCutoff(PreCloseEntryCutoffSupply?)`（供給があり `IsWithinCutoff`）、`ThemeConcentration(ThemeConcentrationSettings?, symbol, market, snapshot)`（`Exceeded(...)` が 1 件以上）。
   `RiskEvaluator.Evaluate` の該当 2 か所をこの述語の呼び出しに置き換える（式は同じ）。
2. `Determinable` に `PreCloseEntryCutoff`・`ThemeConcentrationExceeded` を審査の到達順（判断由来の決済の後・GFV の前）で足す（8 → 10 理由）。
   `Determine` に省略可能な引数 `preCloseCutoff` / `themes` + `symbol` / `market` を足す（既存の呼び出しは変わらない）。
3. 締め切りの供給の組み立て（`MarketHours.SessionCloseOn` と効く分数）を `PreCloseEntryCutoffSupply.For(settings, market, now)` に移し、審査（`ScreenCoreAsync`・`ResolveReservedRedelivery`）と口が同じ関数を呼ぶ。
4. `EntryBlockersService` に `ConfiguredSessionAndThemeControls` を注入し、審査と同じ `settingsStore.GetCurrent()` の値から `SessionAndThemeControlResolution` で効く値を解決して `Determine` へ渡す（両方向とも）。
5. 線上: proto `EntryBlocker` に `ENTRY_BLOCKER_PRE_CLOSE_ENTRY_CUTOFF = 11`・`ENTRY_BLOCKER_THEME_CONCENTRATION_EXCEEDED = 12` を足し（非破壊）、送り手 `RiskReadWireMapping.ToProto` と
   受け手 `GrpcEntryBlockersProvider.Reason` を名前で写す。REST は `RejectionReason` の数値で往復するので変更不要（共有契約に既にある値）。
   配備の順序が逆（受け手が古い）でも、未知の番号は不明へ倒れて LLM を呼ぶだけ（従来どおり）。
6. 取引判断のプロンプトの買い増しの塞がりの文言（`EntryBlockerLabel`）に 2 理由の日本語を足す（名前のままだと LLM に意味が伝わらない）。
   判断側の規則は足さない（口の結果を読むだけ）。保有 0 の見送り（`EntryBlockedByRiskControls`）と保有中の買い増しの Hold（`AddOnBlockedByRiskControls`）はそのまま効く。
7. 審査は変えない（両端で止める）。

### #1434

1. 取引判断の `InFlightCloseCoverage` に `BrokerClosableQuantity`（int?・既定 null＝不明）を足す。REST は既存の `ClosableQuantity` を読み、gRPC は proto に
   `optional int32 closable_quantity = 8` を足して運ぶ（送り手 `OrderExecutionReadGrpcService` は照会したときだけ書く）。
2. IADR-0535 の LLM の前の見送りの条件を「処理中の決済が覆う」**または**「発注執行がブローカーの決済方向の建玉を 0 と観測した（`NotCovered` かつ `BrokerClosableQuantity == 0`）」に広げる。
   どちらも `AddOnKnownForgone`（買い増し・売り増しが LLM の後で必ず見送られる）が真のときだけ（従来の前段のまま）。
   建玉 0 なら、決済の結論は発注執行の決済のゲート（`BrokerHeldPositionGate` → `NoPosition`）が見送り、LLM の後の取引判断の関所も台帳が追いつけば `NakedShortOpen` で見送る。
   どの結論も発注にならない。
3. 見送りの理由は新しい値 `BrokerPositionClosed`（`DecisionForgoneBeforeLlmReason`・`DecisionSkipReason` の末尾。名前を一致させる）。
   `InFlightCloseCoversPosition` と分ける（覆うのではなく、既に無い）。ログは「ブローカーの決済方向の建玉が 0 …」で、台帳の保有（held）を併記する。
4. 不明（null・照会の失敗・Unknown・旧い送り手で項目が無い）・建玉が正は従来どおり LLM を呼ぶ（決済の判断を止める向きへは倒さない）。

## 母集合（規則 9: 誤りの側の文字列で走査）

- #1433: `grep -rn "EntryStateBlockers.Determine\|Determinable\|EntryBlocker\." backend --include=*.cs` と proto の `enum EntryBlocker`。
  口の理由を列挙する箇所: `EntryStateBlockers.Determinable`、`RiskReadWireMapping.ToProto(RejectionReason)`、`GrpcEntryBlockersProvider.Reason`、
  `TradeDecisionPromptBuilder.EntryBlockerLabel`、試験 `HeldAddOnBlockersTests`（8 理由を名付ける）・`EntryBlockersEndpointTests`（全理由が線上の値を持つ）、
  文書 `docs/functional/FR-10_risk-controls.md` の「新規建ての可否」の表。いずれも追随する。
  `docs/api/east-west-grpc.md` の `GetEntryBlockers` の行は理由を列挙しないため**該当なし**（`grep -n "DecisionExitSameDay\|8 理由\|closable" docs/api/east-west-grpc.md` が 0 件であることで確認）。
  `PreCloseFlattener` は `AnyOrder` だけを読むので対象外。`BacktestService/RecordedDecisionReplayStrategy` は口を読まない（名前の一致だけ）ので対象外。
- #1434: `grep -rn "InFlightCloseCoverage(\|ClosableQuantity\|closable_quantity" backend --include=*.cs --include=*.proto`。受け皿は `HttpInFlightCloseProvider.Interpret`・`GrpcInFlightCloseProvider`。
  見送りの語彙の列挙: `DecisionForgoneBeforeLlmReason`・`DecisionSkipReason`・`TradeDecisionAppService.ToSkipReason`、試験（末尾の値を固定する `DecisionSkipReasonTests`・`LedgerGapEventsTests`）、
  文書 `docs/api/events-and-ports.md`・`docs/data/audit-events.md`・`docs/observability/observability.md`・`deploy/observability/README.md`・`docs/operations/nightly-ledger-summary-runbook.md`・
  `docs/functional/FR-10_risk-controls.md`（IADR-0535 の表）。

## 受け入れ基準 → 試験

| # | 受け入れ基準 | 試験 |
|---|---|---|
| A1 | 締め切りの窓の中（閉場の 60 分前以降）で口が `PreCloseEntryCutoff` を両方向に返し、同じストアの審査も同じ理由で拒否する | T-10-2985 |
| A2 | 窓の外（閉場の 61 分前）と休場日（土曜）では返さない | T-10-2985 |
| A3 | 画面の保存値（分数）が効く（審査と同じ解決） | T-10-2986 |
| A4 | テーマの集まりの他の銘柄を上限まで保有していれば、集まりの別の銘柄に `ThemeConcentrationExceeded` を返す。同じ銘柄（買い増し）・集まりの外の銘柄には返さない。審査と一致する | T-10-2987 |
| A5 | 述語は審査と同じ関数（`Determine` に供給すれば審査の理由と同じ並びで返る） | T-10-2988 |
| A6 | 2 理由が線上（gRPC）の値を名前で持ち、受け手が名前で読む | T-10-2989 |
| A7 | 取引判断: 保有 0 の銘柄で口が締め切りを返すと LLM を呼ばずに見送る（`EntryBlockedByRiskControls`） | T-10-2990 |
| B1 | 決済の約定直後の巡回（台帳の保有はまだ正・発注執行がブローカーの建玉 0 を観測）で、出口の判断の LLM を呼ばない（`BrokerPositionClosed`） | T-10-2991 |
| B2 | 建玉が正（覆わない）・建玉が不明（null）なら従来どおり LLM を呼ぶ。買い増しが発注になり得るなら照会もしない | T-10-2992 |
| B3 | 受け皿が `ClosableQuantity`（REST）・`closable_quantity`（gRPC）を読み、欠落は null | T-10-2993 |
| B4 | 見送りの語彙は台帳と観測で名前が一致し、末尾に足した | T-10-2994 |

変異: (a) 口の締め切りの供給を外す → A1 赤、(b) テーマの述語の「自分自身を数えない」を外す → A4 赤、(c) `BrokerClosableQuantity == 0` の条件を外す → B1 赤、
(d) 条件を `<= 0 or null` に広げる → B2 赤。

## 影響範囲

- RiskManagementService（口・述語・線上の写像）、TradeDecisionService（受け皿・見送り・文言）、OrderExecutionService（gRPC の 1 項目）、Shared.Contracts（見送りの語彙 2 つ）、Shared.Grpc（proto 2 本）。
- 試験: RiskManagement・TradeDecision・OrderExecution・Shared.Contracts の dotnet test。

## PoC での確認

- #1433: 米国の締め切りの窓（既定 15:00 ET＝夏時間 04:00 JST 以降）で、trade-decision-service のログ
  `新規建てが審査で必ず拒否されるため LLM を呼ばずに見送り（… IADR-0463）: … reasons=PreCloseEntryCutoff` が保有 0 の監視銘柄の巡回ごとに出ること。
  同じ窓の `LLM 費用計上イベント`（一次スクリーニング）が保有中の銘柄の分だけに減ること（2026-10-21 は 54 回）。
- #1434: 判断由来の決済の約定直後の巡回で `ブローカーの決済方向の建玉が 0 …（IADR-0572）: <銘柄> held=<台帳の株数>` が出て、
  同じ銘柄の `LLM 判断:` と `保有建玉が無い、または不明な売り判断のため見送り … held=0` が出ないこと。`ast_trade_cycle_decision_skips_total{reason="BrokerPositionClosed"}` が増えること。
