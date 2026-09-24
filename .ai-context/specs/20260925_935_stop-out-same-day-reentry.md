---
title: 損切りした銘柄は、その取引日のうちは同じ方向の新規建てを統制側で止める（#935）
type: spec
status: accepted
related_ids: [FR-10, FR-04, FR-19, UC-01, UC-02, ADR-0003, ADR-0009, ADR-0040, IADR-0394, IADR-0246, IADR-0210, IADR-0344, IADR-0132, IADR-0163, IADR-0374, IADR-0358]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 リスク管理)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 差金決済防止＝既存の同日再エントリー統制)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (S0 / S1)
---

# 仕様書: 損切りした銘柄の同日・同方向の新規建てを統制側で止める（#935）

## 起点

- **#935**（decision-needed。2026-09-25 にオーナーが裁定）。裁定の要旨:
  **損切りした銘柄は、その米国東部の取引日のうちは新規建て（同じ方向）をしない。統制側（リスク管理）で機械的に止める。
  LLM の判断には任せない。** 区切りは `TradingDay.Of`。手仕舞い（Close）は決して止めない。
  拒否理由は名前付きで記録し、見送り理由の計器（IADR-0374）に出す。手動の手仕舞い・リスク統制による縮小を含めるかは
  仕様書で明記する。
- 実測（稼働 PoC・2026-09-23）: 13:43:33Z（22:43:33 JST・09:43:33 EDT）に S1 が AAPL 707 株を 337.455 で損切り。
  **3 分後の 13:46:45Z** に判断エンジンが AAPL の新規買い 715 株 @337.63 を出し、買い直した（#935 本文。5 分後にさらに 713 株）。
- 起点 ID: **FR-10**（統制）。判断側（**FR-04**）は変更しない（下「判断側を変えない理由」）。

## 既存の「同日再エントリー」統制が 9/23 に効かなかった理由（コードで確認）

既存の統制は `RiskEvaluator` の `SameDayReentry`（`settings.Guard.PreventSameDayReentry`）である。適用条件は
`AccountTypePolicy.AppliesSameDayReentry(market, effectiveProductType, accountType)` が単一情報源で、
**`現物 && (日本株 || 現金口座)`** のときだけ真になる（`backend/Services/RiskManagementService/Domain/AccountTypePolicy.cs`）。

- 9/23 の AAPL は **米国株・現物・信用口座**である。信用口座であることはコードから導ける ——
  米国株の新規買いが**承認された**以上、`BrokerAccountTypeUnverified`（観測された口座種別が設定値と一致しない・
  観測が無い）は立っておらず、設定値の既定は `AccountType.Margin`（`TradingGuardSettings.ConfiguredAccountType`）。
  仮に現金口座なら `CashAccountSettlementHold` が決済済み資金の供給元不在で買付を常に止める（`RiskEvaluator` 決定4-2 のコメント）。
  よって `AppliesSameDayReentry` は **false** で、既存統制は評価すらされない。
- **既存統制は目的が違う。** 差金決済規制（日本の金商法 161 条の 2）と Good Faith Violation（米国の現金口座）という
  **制度・決済の制約**を守るものであり、入力 `SymbolsTradedToday` は「当日に売買が成立したすべての銘柄」である。
  これを信用口座の米国株へ広げると、**利益確定した銘柄・判断由来で手仕舞った銘柄・新規建てした銘柄の買い増しまで**
  その日は二度と買えなくなる（裁定の射程を大きく超える）。**適用範囲の拡張ではなく、同じ判定コアの隣に
  別の理由で置く**のが正しい場所である（並列の別機構は作らない —— 同じ `RiskEvaluator` ・同じ審査経路・
  同じ拒否イベント・同じ計器を通る）。

## 決定（要旨。詳細は IADR-0394）

1. **何を「損切り」と数えるか**（裁定が仕様書での明記を求めた点）:

   | 手仕舞いの経路 | 数えるか | いつの取引日に数えるか |
   | --- | --- | --- |
   | **S1 ソフトウェア逆指値の成行決済**（`SoftwareStopExecuted` / `ClosePlaced`） | **数える** | 発動（決済レグの承認）の時刻、または約定の時刻のいずれかが当日 |
   | **S0 ブローカー側逆指値の約定**（`ProtectiveStopPlaced` で承認行を持つ決済レグに約定が付いた） | **数える** | 約定の時刻が当日（**発注＝武装しただけでは数えない**） |
   | 保護が成立しないときの成行手仕舞い（`ProtectiveStopCoverageLost`） | 数えない | —（価格が損切りラインに達したのではなく、保護の維持に失敗した対処である） |
   | owner の手仕舞い（`POST /risk-controls/positions/close`） | 数えない | —（人の裁量。止めたいなら pause・禁止銘柄がある） |
   | 維持率割れの自動縮小（`MaintenanceMarginReductionService`） | 数えない | —（口座全体の保証金の事情であり、その銘柄の値動きが損切りラインを割ったのではない） |
   | 判断由来の決済（LLM の反対売買） | 数えない | —（損切りラインの到達ではない） |
   | **由来が記録されていない決済**（本変更より前に記録された承認行） | **不明**として扱う | 承認または約定の時刻が当日 → 同方向の新規建てを**不明**の理由で止める |

2. **「同じ方向」**: 手仕舞った建玉の方向。売りの決済（ロングの損切り）は**買いの新規建て**を、買いの決済
   （ショートの損切り）は**売りの新規建て**を止める。反対方向の新規建ては止めない（裁定が「同じ方向」と限定）。
3. **区切り**: `TradingDay.Of(instant, market)`（米国株は米国東部の暦日。夏時間は `TimeZoneInfo` が吸収）。
4. **拒否理由は 2 つ**（名前が付き、`ast.risk.rejections{reason}` に `reason.ToString()` のまま出る）:
   - `StoppedOutSameDay`（クラス A：統制が設計どおり作動した記録）
   - `StopOutStatusUnknown`（クラス B：確かめられないので止めている状態の記録。`CapitalBaselineUnavailable` と同じ区分）
   序数は末尾 30 / 31。
5. **手仕舞い（Close）は止めない。** 判定は `isEntry` の短絡の内側だけで行い、Close では台帳を読みもしない。
6. **不明 / 無し / 有り を分ける。** 台帳の承認行に**由来の列**（`approved_orders.Source`・null 許容）を足す。
   本変更以後の承認行は書き手 4 経路がすべて由来を明示する。**null は「由来が記録されていない」＝不明**であり、
   「損切りではない」ではない。供給が読めない（台帳の読み取りが例外を投げる）ときは新規建ての審査が例外で終わり
   承認されない（fail-closed）。Close はこの読み取りをしないので影響を受けない。
7. **依存は必須にする**（IADR-0163 決定2）: `OrderScreeningService` の引数に `IPortfolioLedgerStore` を必須で足す。
   `Program.cs` の構築式から外すとコンパイルが通らない。加えて `Program.cs` の実構成で組んだホストに
   損切りイベントを流し、同じ銘柄の買いが名前付きの理由で拒否されることを確かめるテストを置く（下 T-10-778）。

## 判断側を変えない理由（IADR-0374 の計器について）

- 裁定は「統制側で機械的に止める。LLM の判断には任せない」。判断側は損切りの有無を知らず、
  知らせるには新しい跨サービスの照会（リスク管理 → 判断）が要る。**同じ規則を 2 か所に置くと、片方だけが古くなる**。
- `decision_skips{reason}`（IADR-0374）は**判断が自分で見送った**ときの計器であり、本件の判断側は見送らない
  （`TradeDecisionMade` を出し、リスク管理が `OrderRejected` で拒否する）。したがって本件の理由は
  **`ast.risk.rejections{reason="StoppedOutSameDay"}`** に出る。`decision_skips` には出ない。
  🔴 **裁定の文言「見送り理由の計器（IADR-0374）に出す」とは字義どおりには一致しない**——PR 本文と IADR で明示し、
  オーナーの確認を求める（判断側の事前照会を足すかは別 issue の判断とする）。

## 母集合（走査と除外理由）

| 走査 | 結果 | 扱い |
| --- | --- | --- |
| `AppendApproval(` の本番呼び出し（`grep -rn "AppendApproval(" backend/Services/RiskManagementService --include=*.cs \| grep -v /Tests/`） | 4 か所（`OrderApprovedLedgerHandler`・`ProtectiveStopPlacedLedgerHandler`・`SoftwareStopExecutedLedgerHandler`・`ProtectiveStopCoverageLostLedgerHandler`） | 4 か所すべてが由来を明示する |
| `new OrderApproved(` の本番発行元 | 3 か所（`OrderScreeningService`・`PositionCloseService`・`MaintenanceMarginReductionService`） | いずれも `OrderApprovedLedgerHandler` を通り由来 `OrderApproved`＝損切りではない |
| `IPortfolioLedgerStore` の実装 | 本番 2（EF / InMemory）・テスト 3（`OpenPositionsServiceTests` / `ShortSellingStatusServiceTests` の FakeLedger・`PortfolioLedgerConsumersTests` の InFlightProbeLedger。最初の走査は 2 と数え、ビルドで 3 つ目が見つかった） | 5 つとも新しい読み口を実装する |
| `new OrderScreeningService(` | 本番 1（`Program.cs`）・テスト 10 か所（8 ファイル） | すべてに台帳を渡す |
| `RiskEvaluator.Evaluate(` の本番呼び出し | 1（`OrderScreeningService`） | 新しい引数は省略可能（既存のテスト 133 か所を変えない）。本番の唯一の呼び出しが常に供給する |
| 拒否理由の語彙に触れる `docs/` | `FR-10_risk-controls.md`（クラス分類・手仕舞いは止めない節） | 同書へ節を足す。`FR-19_trading-guard.md` の差金決済防止は**変えない**（既存統制の範囲は不変） |
| `SameDayReentry` / 同日再エントリーを述べる `docs/`（7 ファイル） | 既存統制の記述 | 変えない（本件は既存統制の範囲を変えていない） |
| 拒否理由を列挙で写像するコード（通知・監査・フロント） | 0 件（`reason.ToString()` で出す） | 追加の写像は不要 |

## 受け入れ基準（テスト ID は T-10-770〜T-10-779）

1. （T-10-770）S1 が当日に発動した銘柄への**同じ方向**の新規建ては `StoppedOutSameDay` で拒否される。反対方向の新規建ては拒否されない。
2. （T-10-771）S0 の決済レグは**約定が当日**のときだけ数える（武装しただけ・約定が前日は数えない）。
3. （T-10-772）保護喪失の成行手仕舞い・承認経路（owner / 判断 / 自動縮小）の決済は数えない。
4. （T-10-773）由来が記録されていない決済が当日にあれば、同じ方向の新規建ては `StopOutStatusUnknown` で拒否される（無しとして扱わない）。
5. （T-10-774）手仕舞い（Close）は、損切り済み・不明のいずれでも拒否されない。
6. （T-10-775）**9/23 の時系列**: 13:43:33Z の S1 発動 → 13:46:45Z の買い（同じ ET 日）は拒否。JST の日付が変わった 15:30Z（ET はまだ 9/23）も拒否。9/24 04:00Z（ET 00:00）以降は通る。
7. （T-10-776）夏時間の境界: 冬（EST・UTC−5）は 04:30Z が前日の ET 23:30 として拒否され、05:30Z は翌 ET 日として通る。
8. （T-10-777）台帳の読み口（EF / InMemory）が同じ意味論を返す（由来の往復・null の保持・S0 の旧い承認でも当日の約定があれば返す）。
9. （T-10-778）`Program.cs` の実構成で組んだホストに `SoftwareStopExecuted` を流すと、同じ銘柄の買いの審査が `StoppedOutSameDay` で拒否され、`ast.risk.rejections{reason="StoppedOutSameDay"}` にその名前が出る。
10. （T-10-779）変異注入: ①方向の判定を反転 ②由来の null を「損切りではない」に倒す ③ S0 を武装で数える ④ `Program.cs` で台帳の代わりに空の台帳を渡す、の各々で少なくとも 1 件が赤になる（実測を PR に記す）。

［2026-09-25 追記 / #935］監査（PR #949）で、⑤「手仕舞いの審査も台帳の決済を読む」⑥「台帳の決済の読み取りの失敗を握りつぶして『損切りなし』に倒す」の 2 変異を赤くするテストが 1 件も無いと分かった。受け入れ基準を 1 つ足す（T-10-770〜T-10-779 は埋まっているので T-10-816 を使う）:

11. （T-10-816）`Program.cs` の実構成で、台帳の決済の読み取り（`GetCloseApprovals`）だけを例外にすると、手仕舞い（Close）は**それでも承認され**、新規建て（Open）は審査が例外で終わり **`OrderApproved` が出ない**。読み取りが成功する状態では同じ新規建てが承認されることを対照として先に確かめる。⑤⑥の各変異で赤になる。

## 変更しないもの

- 既存の `SameDayReentry` の適用範囲・入力（`SymbolsTradedToday`）・既定値。
- `PortfolioProjection` / `PortfolioState` / `PortfolioSnapshot`（損切りの供給は `buyInBan` と同じく審査サービスが別に組む）。
- 判断側（`TradeDecisionService`）・`decision_skips` の語彙。
- 発注執行・市場監視（損切りの実行そのもの）。

## 作業手順

1. 失敗するテストを先に書く（9/23 の時系列が承認されてしまうことの再現）。
2. 契約: 拒否理由 2 種・クラス分類・序数表。
3. 台帳: 由来の列（EF マイグレーション）・書き手 4 経路・読み口。
4. 純関数 `StopOutProjection` → `RiskEvaluator` の判定 → `OrderScreeningService`（必須依存）→ `Program.cs`。
5. `dotnet build` / `dotnet test`（RiskManagement・Contracts）/ `dotnet format --verify-no-changes` / `node scripts/check-*.js`。
