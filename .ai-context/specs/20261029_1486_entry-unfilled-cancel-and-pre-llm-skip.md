---
title: 判断由来の新規建ての指値の未約定時の取消を既定無効で置き（計画の裁定待ち）、保有 0 で未約定の新規建てがある銘柄は LLM を呼ぶ前に見送る
type: spec
status: done
related_ids: [FR-04, FR-10, FR-19, ADR-0003, ADR-0067, ADR-0072, IADR-0119, IADR-0390, IADR-0463, IADR-0536, IADR-0539, IADR-0462, IADR-0428, IADR-0594]
author: claude (Claude Code)
created: 2026-10-29
updated: 2026-10-29
---

# 仕様書: 新規建ての未約定の取消と LLM の前の見送り（#1486）

> 本仕様書は実装着手前に作成した（着手 2026-10-29。AST `origin/develop` `520dbeca`）。

## 起点（トレーサビリティ）

- 起点: #1486（稼働 PoC 2026-10-27。TSLA の新規建て 115 株 @397.07 が閉場まで約定も取消もされず、同じ銘柄の判断 68 回のうち 67 回が「未約定の買い注文がある」で Hold。LLM 約 30 円）。
- 計画: FR-10・FR-19、ADR-0067（判断由来の**決済**の指値の未約定時の取消。**射程は決済だけ**）、ADR-0072（PoC の経路で決済の取消を有効化）、05_trading-assumptions §5 の 2 行（決済の N・回数）。
- 🔴 **計画の裁定待ち**: 新規建ての取消には計画の規則も値も無い。planning#852 を起票した（射程に足すか・N・回数・PoC で有効にするか。2026-10-29）。オーケストレーターの承認（2026-10-29）により、
  仕組みを**既定無効**で置き、有効時の値は決済側の定数を**暫定の初期値**として共用する。**新規建ての値は planning#852 の裁定待ちであり、有効化は裁定の後とする。**
- 前提の IADR: IADR-0539（決済の取消の実装）、IADR-0536（未約定の決済の警告）、IADR-0390（未約定の新規建てを判断の前提に入れる。決定6「未約定が在っても新規建てはコードでは止めない」）、
  IADR-0119（保有 0 の売りは裸の新規売りとして見送る）、IADR-0462（LLM の前の見送りの記録）、IADR-0463（保有 0・未約定なしで省く線引き）、IADR-0428（保護の巡回はエントリーの状態を確かめる）。
- 本件の判断: IADR-0594（新規）。

## 原因

- 発注執行の取消（`DecisionCloseUnfilledCanceller`）の問い合わせは `PositionEffect.Close` に限る（ADR-0067 決定4 のとおり）。新規建ては DAY の失効まで板に残る。
- 取引判断は、保有 0 で未約定の新規建てが既知でも LLM を呼ぶ（IADR-0390 決定6）。結論は Hold・重ね買い・裸の売り（必ず見送り）しか無い。

## 母集合（規則 9・10）

```bash
git grep -n "DecisionCloseUnfilledCancel\|FindUnfilledDecisionCloses\|CountUnfilledCancelRequests" -- backend docs deploy
git grep -n "DailyReportNoResponseHalted" -- backend docs   # LLM の前の見送りの語彙の追随先（直近に足した値で走査）
git grep -n "working: true\|WorkingEntryOrders(\[" -- backend/Services/TradeDecisionService/Tests
```

| 箇所 | 扱い |
| --- | --- |
| 発注執行の取消の本体・構成・問い合わせ（EF・インメモリ・既定の実装）・常駐 | 本体と構成の検証を決済・新規建てで共有し、新規建ての取消を足す（決済の振る舞いは変えない） |
| `TradingDefaults`（リスク管理）・Shared.Kernel の既定 | 新規建ての既定（無効・暫定の初期値）を足し、試験で固定する |
| 見送りの語彙（`DecisionSkipReason`・`DecisionForgoneBeforeLlmReason`・写像・語彙の試験 3 本） | 末尾に `EntryOrderWorking` を足す |
| 保有 0・未約定ありで LLM を呼ぶことを期待していた試験（`EntryBlockersBeforeLlmTests`・`HeldAddOnBlockersTests`・`OneShareCapacityDecisionTests`・`MinimumEntryNotionalDecisionTests`・`TradeDecisionServiceTests`） | 期待を改める（保有中の銘柄の未約定で試す・LLM の前の見送りを確かめる） |
| 文書（FR-10 の機能・テスト仕様書、運用仕様書、イベント・監査・可観測性・夜間の要約の手順） | 追随 |
| `deploy/helm/.../values*.yaml` | **変えない**（有効化しない） |
| `.ai-context/` の過去の記録 | point-in-time の記録（書き換えない） |

## 設計

1. **見送り（既定で効く）**: `TradeDecisionAppService` は保有と未約定を読んだ直後、`held == 0`（既知）かつ未約定の新規建てが既知で 1 件以上なら `SkipBeforeLlmAsync(EntryOrderWorking)`。
   定時・価格変動とも。未約定が不明・保有中・保有が不明では省かない。IADR-0390 決定6 を保有 0 について改める（保有中の買い増しは従来どおりコードでは止めない）。
   未約定の方向は問わない（空売りの新規建てが残る場合も、保有 0 の売りは裸の新規売りとして必ず見送られ〔IADR-0119〕、買いは板の空売りと反対方向の新規建てを並べるだけで意味を持たない）。
   ［PR #1490 のレビューで追記］
4. **残余**: 取消を有効にしても、回数の上限（暫定 1 回）に達した後の新規建ては取り消さず、DAY の失効まで滞留し得る（planning#852 の論点）。
2. **取消（既定は無効）**: 発注執行の取消の本体を `UnfilledDecisionOrderCancellation` に切り出し、決済（従来）と新規建て（`DecisionEntryUnfilledCanceller`）が建玉効果を渡して呼ぶ。
   構成の検証は共通の基底（`UnfilledDecisionOrderCancelOptions`）。新規建ての構成は節 `DecisionEntryUnfilledCancel`（既定は無効・N 15 分・回数 1 回・範囲は決済と同じ）。
   問い合わせ・数えは建玉効果を引数に取る形を足し、決済の口はそれへ委ねる。回数は新規建て（銘柄・市場・新規建ての方向）で数え、決済の取消とは別に数える。
3. **保護の記録**: 新しい経路は作らない。取り消した新規建てが約定 0 で終端したことを保護の巡回がブローカーの照会で確かめ、逆指値を取り消して記録を完了する（IADR-0428 の既存の分岐）。

## 受け入れ基準（→ 試験）

- [x] 保有 0 で未約定の新規建て（買い・空売りとも）がある銘柄は、LLM を呼ぶ前に見送る（T-10-3078。是正前に赤）。
- [x] 新規建ての取消は既定で無効。暫定の初期値は決済側と同じ（T-10-3079・T-10-3080）。
- [x] 有効時、15 分以上未約定の判断由来の新規建てが取り消され、置き直さない。決済の取消とは別に数える（T-10-3080）。
- [x] 取り消した新規建ての保護の記録（未確定）も閉じる（T-10-3081）。

## 検証

- `dotnet build backend/backend.slnx`・OrderExecutionService / TradeDecisionService / RiskManagementService / Architecture / Shared.Contracts / Audit / Report の試験・`dotnet format --verify-no-changes`・文書検査・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`。

## 配備

- 取引判断（見送り）と発注執行（取消の仕組み。無効のまま）を上げる。設定の追加は無い。有効化は planning#852 の裁定の後。
