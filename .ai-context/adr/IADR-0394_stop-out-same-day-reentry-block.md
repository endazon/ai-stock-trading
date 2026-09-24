---
title: IADR-0394 損切りした銘柄は、その取引日のうちは同じ方向の新規建てを統制側で止める。承認行に由来を持たせ、由来の無い当日の決済は不明として止める
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-04, FR-19, UC-01, UC-02, ADR-0003, ADR-0009, ADR-0040, IADR-0246, IADR-0210, IADR-0344, IADR-0132, IADR-0163, IADR-0374, IADR-0358, IADR-0134]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 差金決済防止)
  - planning:projects/ai-stock-trading/07_adr/ADR-0040_simulate-stop-loss-method-is-selectable.md (S0 / S1)
---

# IADR-0394: 損切りした銘柄の同日・同方向の新規建てを統制側で止める

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: オーナー裁定（[#935](https://github.com/endazon/ai-stock-trading/issues/935) の 2026-09-25 のコメント）を claude が実装。
  裁定が仕様書での明記を委ねた点（何を損切りと数えるか・不明の扱い）は本 IADR が決め、PR でオーナーの確認を受ける。

## 起点・関連

- 関連する計画書 ID: **FR-10**（統制）。FR-04（判断）は**変えない**（決定 8）。
- 対象 Issue: [#935](https://github.com/endazon/ai-stock-trading/issues/935)
- 関連する実装仕様書: [20260925_935_stop-out-same-day-reentry](../specs/20260925_935_stop-out-same-day-reentry.md)
- 関連 IADR: [IADR-0132](IADR-0132_product-type-tri-state-and-guard-scope.md) 決定 5（差金決済防止の適用範囲）、
  [IADR-0246](IADR-0246_trading-day-boundary-by-market.md)（取引日は市場の現地取引日）、
  [IADR-0210](IADR-0210_broker-side-stop-loss-unification.md) / [IADR-0344](IADR-0344_s1-software-stop-loss.md)（S0 / S1 の承認行）、
  [IADR-0163](IADR-0163_allow-list-and-required-dependency-scope.md) 決定 2（不在が統制の無効を意味する依存は必須にする）、
  [IADR-0374](IADR-0374_decision-skip-reasons-and-first-alert-rule.md)（見送り理由の計器）、
  [IADR-0134](IADR-0134_rejection-reason-ordinal-and-plan-registry-transcription.md) 決定 2（拒否理由は末尾へ足す）。

## コンテキストと課題

2026-09-23（稼働 PoC）、**13:43:33Z** に S1（ソフトウェア逆指値）が AAPL 707 株を 337.455 で損切りした
**3 分後の 13:46:45Z**、判断エンジンは同じ AAPL の新規買い（715 株 @337.63）を出し、買い直した（#935 本文。5 分後にさらに 713 株）。

既存の「同日再エントリー」の統制（`RejectionReason.SameDayReentry`）が効かなかった理由は**適用範囲**である。

- 適用条件の単一情報源は `AccountTypePolicy.AppliesSameDayReentry` で、**`現物 && (日本株 || 現金口座)`** のときだけ真。
- 9/23 の AAPL は**米国株・現物・信用口座**。信用口座であることはコードから導ける ——
  米国株の新規買いが承認された以上 `BrokerAccountTypeUnverified` は立っておらず（観測された口座種別＝設定値）、
  設定値の既定は `AccountType.Margin`。現金口座なら `CashAccountSettlementHold` が決済済み資金の供給元不在で
  買付を常に止めている（`RiskEvaluator` のコメント「現時点で本値の供給元は無く、現金口座の買付は常に止まる」）。
- さらに**目的が違う**。既存統制は差金決済規制（日本）と Good Faith Violation（米国の現金口座）という
  **制度・決済の制約**を守るもので、入力 `SymbolsTradedToday` は「当日に売買が成立したすべての銘柄」である。
  信用口座の米国株へ広げると、利益確定・判断由来の決済・買い増しの後まで止まり、裁定の射程を大きく超える。

**損切りの後の買い直しを止める規則は、コードにも計画にも存在しなかった。** オーナーは 2026-09-25 に
「損切りした銘柄は、その米国東部の取引日のうちは新規建て（同じ方向）をしない。統制側で機械的に止める。
LLM の判断には任せない」と裁定した。

## 検討した選択肢

| 案 | 判定 |
| --- | --- |
| A. 既存の `SameDayReentry` の適用範囲を信用口座の米国株へ広げる | **不採用**。目的（制度・決済）と入力（当日に売買したすべての銘柄）が違う。裁定の射程を超えて止める |
| B. 判断側（FR-04）で損切りを知って見送る | **不採用**。裁定が「統制側で機械的に」「LLM に任せない」と明示。判断側へ知らせるには新しい跨サービスの照会が要り、同じ規則を 2 か所に置くと片方だけが古くなる |
| C. 損切りの記録を別テーブル（損切りイベントの購読）で持つ | **不採用**。本変更の前に起きた当日の損切りは新しいテーブルに無く、**不明を「無し」として扱う**ことになる。S0 は「武装」と「約定」の相関が要り、結局は台帳の承認行と約定を引くことになる |
| D. 台帳の承認行に**由来**を持たせ、同じ `RiskEvaluator` に**別の理由**で判定を足す | **採用** |

## 決定

### 決定 1: 何を「損切り」と数えるか（裁定が仕様書での明記を委ねた点）

| 手仕舞いの経路 | 承認行の由来（`ApprovalSource`） | 数えるか | 当日の判定 |
| --- | --- | --- | --- |
| S1 の成行決済（`SoftwareStopExecuted` / `ClosePlaced`） | `SoftwareStopS1` | **数える** | 承認（＝発動）**または**約定の時刻が当日 |
| S0 の決済レグ（`ProtectiveStopPlaced`） | `ProtectiveStopS0` | **数える** | **約定**の時刻が当日（**武装しただけでは数えない**） |
| 保護喪失の成行手仕舞い（`ProtectiveStopCoverageLost`） | `ProtectionLostClose` | 数えない | — |
| `OrderApproved` 経由（判断由来の決済・owner の手仕舞い・維持率割れの自動縮小） | `OrderApproved` | 数えない | — |
| 由来が記録されていない決済（本列の追加前の行） | `null` | **不明** | 承認または約定の時刻が当日 |

- **S1 は発動で数え、約定を待たない。** 9/23 は発動から 3 分で買い直した。約定が台帳へ届く（約定追跡の巡回）より
  買いの審査が先に来ても止まるようにする。発動は「損切りラインへ到達した」事実そのものである。
- **S0 は約定で数える。** S0 の承認行はエントリーと同時の**武装**で書かれるため、承認時刻で数えると
  「建てた日は同じ銘柄を二度と買えない」になる。損切りの成立は逆指値の約定である。
- **数えないものの根拠**:
  - 保護喪失の成行手仕舞い —— 価格が損切りラインへ達したのではなく、**保護の維持に失敗した対処**である。
  - owner の手仕舞い —— **人の裁量**。止めたいなら pause・禁止銘柄がある。数えると利益確定の後の買い直しまで止まる。
  - 維持率割れの自動縮小 —— **口座全体の保証金の事情**であり、その銘柄が損切りラインを割ったのではない。
  - 判断由来の決済 —— LLM の反対売買であり、損切りラインの到達ではない。

### 決定 2: 「同じ方向」は建玉の方向

売りの決済（ロングの損切り）は**買いの新規建て**だけを、買いの決済（ショートの損切り）は**売りの新規建て**だけを止める。
反対方向の新規建ては止めない（裁定が「同じ方向」と限定した）。

### 決定 3: 区切りは市場の現地取引日

`TradingDay.Of(instant, market)`（米国株は米国東部の暦日。夏時間は `TimeZoneInfo` が吸収）。
JST で区切ると米国セッションの途中（ET 10〜11 時）で解ける（#249 / IADR-0246 と同じ誤り）。

### 決定 4: 拒否理由は 2 つ（名前付き・末尾追加）

| 理由 | 序数 | クラス | 意味 |
| --- | --- | --- | --- |
| `StoppedOutSameDay` | 30 | **A**（統制の正常作動） | 当日に損切りした銘柄への同方向の新規建て |
| `StopOutStatusUnknown` | 31 | **B**（止めている状態の記録） | 当日に損切りしたかを確かめられない |

`ast.risk.rejections{reason}` は `reason.ToString()` をそのままタグにするため、追加の写像なしで両名が出る
（`BusinessMetrics.RecordOrderScreening`）。通知・監査も理由を `ToString()` で出しており、列挙の写像を持つ箇所は無い。

### 決定 5: 手仕舞い（Close）は止めない

判定は `RiskEvaluator` の `isEntry` の短絡の内側だけで行う。`OrderScreeningService` は**新規建てのときだけ**台帳を読む
（Close の審査は損切りの読み取りをしないので、その読み取りの失敗にも巻き込まれない）。

### 決定 6: 不明・無し・有りを分ける

- `approved_orders` に **`Source`（int・null 許容）** を足す（EF マイグレーション `AddApprovedOrderSource`）。
  **既存行は埋め戻さない。** 本変更以後は書き手 4 経路（`OrderApprovedLedgerHandler`・
  `ProtectiveStopPlacedLedgerHandler`・`SoftwareStopExecutedLedgerHandler`・`ProtectiveStopCoverageLostLedgerHandler`）が
  すべて由来を明示する。`IPortfolioLedgerStore.AppendApproval` の `source` の既定は `null`（＝不明）であり、
  **書き忘れは「損切りではない」ではなく「不明」＝止める側へ倒れる。**
- 射影（`StopOutProjection`・純関数）は方向ごとに `None` / `StoppedOut` / `Unknown` を返す。
  `null` の由来の当日の決済は `Unknown` になり、同方向の新規建ては `StopOutStatusUnknown` で止まる（#865 / #877 と同じ向き）。
  同じ方向に両方が並べば `StoppedOut` を採る。
- 🔴 **供給元が読めないとき**（台帳の読み取りが例外）は、新規建ての審査が例外で終わり（`OrderScreeningService.Screen` から
  `TradeDecisionMadeHandler` へ伝播する）、**`OrderApproved` は発行されない**（fail-closed）。
  名前付きの理由にはならない —— 同じ審査の中で台帳（`GetFills`）を
  既に読んでおり、この読み取りだけが独立に失敗する経路を想定した捕捉は置かない（起こり得ない場合への防御的実装をしない）。

### 決定 7: 台帳は必須依存

`OrderScreeningService` に `IPortfolioLedgerStore` を**必須引数**で足す（IADR-0163 決定 2）。`Program.cs` の構築式から
外すとコンパイルが通らない。加えて、`Program.cs` の実構成（`RiskWorkerWebApplicationFactory`）で
`SoftwareStopExecuted` を流し、同じ銘柄の買いが `StoppedOutSameDay` で拒否され、`ast.risk.rejections` に
その名前が出ることを確かめる（`StopOutReentryWiringTests`。構築式で空の台帳を渡す変異で赤になることを実測）。
`RiskEvaluator.Evaluate` の `stopOuts` 引数は省略可能（既存の 133 か所のテストを変えない）で、`null` は
「この呼び出し元は供給していない」＝評価しない。本番の唯一の呼び出し元は新規建てで**常に**供給する。

### 決定 8: 判断側は変えない —— `decision_skips` には出ない

判断側は損切りを知らず、`TradeDecisionMade` を出す。拒否はリスク管理の `OrderRejected` であり、
計器は **`ast.risk.rejections{reason="StoppedOutSameDay"}`** である。`ast.trade_cycle.decision_skips`（IADR-0374）は
**判断が自分で見送った**ときの計器であり、本件では増えない。

🔴 **裁定の文言「見送り理由の計器（IADR-0374）に出す」とは字義どおりには一致しない。** 名前付きの理由が
拒否の計器に出ることで趣旨（理由別に数えて見える）は満たすと判断したが、判断側の見送りとしても数えるべきかは
PR でオーナーの確認を求める（足すなら判断側へ損切りの照会を足す別 issue になる）。

## 理由

- **同じ判定コア・同じ審査経路・同じ拒否イベント・同じ計器**を通るため、並列の別機構にならない。
  既存の差金決済防止とは入力も目的も違うので、**理由を分ける**のが監査・計器の読み手にとって正しい。
- **由来を承認行に持たせる**のは、損切りの成立が「承認（S1 の発動）」と「約定（S0）」の 2 種類の時点を持ち、
  どちらも台帳が既に持っている事実だからである。新しいイベントの購読やテーブルを足すと、足す前の当日の損切りが
  「無し」に見える（不明を無しとして扱う）。

## 結果

- 良い影響: 9/23 の形（S1 の損切りの 3 分後の買い直し）が統制で止まる。理由が名前で計器・監査・通知に出る。
- 悪い影響・トレードオフ:
  - **配備当日の過剰拘束。** 配備前に記録された当日の決済は由来が `null` であり、その銘柄の同方向の新規建ては
    `StopOutStatusUnknown` でその取引日のうち止まる（判断由来・owner の手仕舞いを含む）。翌取引日には解ける。
  - **S0 の約定は約定追跡の巡回で台帳へ届く。** 届く前の審査は S0 の損切りを知らない（S1 と違い発動時点の記録が無い）。
    S0 の約定からその銘柄の買いの審査までが巡回間隔より短いと、1 本は通り得る。
  - 取引日の区切りは暦日であり、**時間外取引**は同じ ET 暦日に属する（プレマーケットの損切りはその日の本場に及ぶ）。
- フォローアップ:
  - 判断側の見送りとしても数えるかのオーナー確認（決定 8）。
  - 計画（FR-10 / 05_trading-assumptions §5）にこの統制が載っていない。計画側への記録は planning への issue で行う。

［2026-09-25 追記 / #935］PR #949 の監査で、上の記述と PR 本文に不正確な点が 3 つ見つかった。本文は書き換えず、ここで訂正する。

1. **登録行を消す変異の赤は 1 件ではなく 202 件である。** PR 本文は「`Program.cs` から `IPortfolioLedgerStore` の登録行を消す」変異で
   赤になるのを「1 件（`StopOutReentryWiringTests` のみ）」と書いたが、それは本 PR の新規テストだけを数えた値だった。
   RiskManagementService.Tests 全体（1,954 件）で実走すると **202 件が赤**（本番の構成を起動するテストが依存の解決で落ちる）。
   構築式で空の台帳を渡す変異（決定 7）は起動が通るので、全体で実走しても赤は `StopOutReentryWiringTests` の中だけである
   （T-10-816 を足した後で 2 件〔T-10-778・T-10-816〕、1,954 件中）。
2. **配備当日の過剰拘束には S0 の武装の行も入る。** S0 の武装（`ProtectiveStopPlacedLedgerHandler`）は決済（Close）の承認行として
   武装時刻で書かれる。配備前にその日に書かれた武装の行は由来が `null` で承認時刻が当日なので、`StopOutProjection` は
   `Unknown` と判定する。つまり**配備前にその取引日に S0 つきで新規建てしたすべての銘柄**で、損切りしていなくても
   同じ方向の新規建てが `StopOutStatusUnknown` でその日のうち止まる（上の「判断由来・owner の手仕舞いを含む」より広い）。
   配備前の武装の行がのちの取引日に約定した場合も、`StoppedOut` ではなく `Unknown` として止まる（止める向きは同じ）。
   → 配備は取引時間の外（セッションの間）に行う（PR 本文の Deploy 節）。
3. **武装から 24 時間を超えて約定した S0 は数えられない（既存の欠落）。** 約定追跡（`OrderFillPoller`）は
   記録から `FillPolling:MaxTrackingHours`（既定 24 時間）を過ぎた非終端の注文を照会せず（`FindPendingSince(now - maxTracking)`）、
   S0 のレグの記録時刻は武装の時刻である。`OrderExecuted` を発行するのは発注時と約定追跡だけなので、その約定は台帳へ届かず、
   決定 1 の「S0 は約定で数える」が働かない。本 PR で生じた欠落ではない。是正は #958 で行う。

加えて、決定 5・決定 6 の「台帳の読み取りが失敗しても手仕舞いは巻き込まれない／新規建ては `OrderApproved` を出さない」を
赤くするテストが無かった（「手仕舞いも読む」「読み取りの失敗を無しに倒す」の 2 変異が生き残っていた）。
`StopOutReentryWiringTests` に T-10-816 を足し、各変異で赤になることを実測した。

## 関連

- [#935](https://github.com/endazon/ai-stock-trading/issues/935)（裁定）
- 実装: `Domain/StopOutReentrySupply.cs`・`Domain/RiskEvaluator.cs`・`Features/RiskManagement/StopOutProjection.cs`・
  `Features/RiskManagement/ApprovalSource.cs`・`Features/RiskManagement/OrderScreeningService.cs`・
  `Infrastructure/Persistence/{Ef,InMemory}PortfolioLedgerStore.cs`・`Infrastructure/Steps/{OrderApproved,ProtectiveStop}LedgerHandler(s).cs`・
  `Program.cs`（いずれも `backend/Services/RiskManagementService/` 配下）
- テスト: T-10-770〜T-10-779（`docs/tests/FR-10_risk-controls-tests.md`）
