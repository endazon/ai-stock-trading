---
title: 建玉照会の失敗の分類を全経路で 1 つの口から取り、ガードの照会し直しを台帳で読めるようにする（#1164）
type: spec
status: accepted
related_ids: [FR-10, NFR, IADR-0487, IADR-0458, IADR-0462, IADR-0118, IADR-0144]
author: claude (Claude Code)
created: 2026-10-03
updated: 2026-10-03
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements (FR-10 リスク統制・NFR 可観測性)
---

# 建玉照会の失敗の分類を全経路で 1 つの口から取り、ガードの照会し直しを台帳で読めるようにする（#1164）

## 背景（PoC の実測、2026-10-03 00:28〜00:29 JST。issue 本文の転記）

- 同じ時刻の建玉照会の失敗が、`BrokerPositionSnapshotService` では Transient、`ProtectiveStopGuardService` では Other と読まれた。
- ガードでは一過性の失敗の照会し直し（#1093 / IADR-0458）が効かなかったと報告された。全件が 1 巡回分据え置きになり、30 秒後に回復した。

## 原因（コードと SDK の逆コンパイルで確認。origin/develop f17f1b73）

1. **分類器は 1 つで、入力も同じ形である。** 建玉照会はすべて `MoomooBrokerAdapter.QueryPositionsAsync` を通る。`GetPositionsAsync` はその `Positions` を返すだけである。分類は `MoomooPositionQueryClassifier.Classify(ex)` の 1 か所だけで行われ、入力は `IMoomooTradeClient.GetPositionsAsync` が投げた例外そのものである。Program.cs では、ガードと観測の常駐に同じアダプタのインスタンスが渡る（`IBrokerPositionSource` は `IBrokerAdapter` の singleton を写したもの）。**同じ例外が 2 つの経路で別の分類になる経路は、コードには無い。**
2. **分類の結果が経路ごとに別の場所へ出ていた。2 つの「分類」は比べられる値ではなかった。**
   - ガードは分類を照会の状態の台帳（`PositionQueryStatusChanged.FailureKind`）へ載せる。ただし載るのは**照会し直しの最終結果**の種類である（IADR-0462 決定2 の契約）。
   - 観測の常駐は、`GetPositionsAsync` で分類を捨てる。台帳への報告は種類なし（`null`。監査では「種類 不明」と出る）である。観測の常駐の「Transient」が読めるのは、アダプタの警告ログ（`分類 {Failure}`）だけである。このログは呼び出し元を名乗らない。
   - 決済のゲート・S1 の武装前（`OrderDispatch`）、S1 の決済（`SoftwareStopClose`）、稼働 probe（`BrokerAvailabilityProbe`）も、種類なしで報告していた。
3. **2 つの経路は別々に照会する。同じ時刻でも、分類器への入力は別の失敗である。** SDK（moomoo-api 10.8.6808）を逆コンパイルして確かめた。
   - 未接続の間の送信（`MMAPI_Conn.SendProto`）は serial 0 を返し、返事は来ない。切断（`HandleDisconnect`）は送信済みの要求に返事を合成しない。どちらも本実装の返信待ち（既定 15 秒）が `TimeoutException` で打ち切る（Transient）。
   - OpenD が返事として返す失敗は retType -1 で、文言が頻度制限でなければ Other である（分からないものは Other。IADR-0458）。
   - したがって「同じ時刻の 2 つの照会」は、片方が返信待ちの打ち切り（Transient）、もう片方が OpenD の -1（Other）になり得る。ガードが Transient の後に照会し直し、2 回目が Other で終わった場合も、台帳には最終結果の「Other」だけが載る。照会し直しが効いたかどうかを台帳から読めない。
4. PoC の実ログ（retType・retMsg・例外型）は手元に無い。上の 2 つの形（入力が別の失敗だった／照会し直しの後の失敗だけが見えた）のどちらだったかは確定できない。**本件では、次に同じことが起きたときに台帳だけで区別できるようにする。** 分類の方針（分からないものは Other）は変えない。

## 範囲

1. **分類の口を 1 つにする**: 建玉照会を分類つきで行う共有の入口 `PositionQueries.QueryAsync(IBrokerPositionSource, ct)` を置く。分類つきの口（`IClassifiedPositionSource`）を持つ供給元（moomoo のアダプタ）ならそれを使い、持たない供給元（試験の偽物など）は従来どおり `GetPositionsAsync` を呼んで種類なしとする。台帳へ報告する経路はすべてこの入口を使い、種類を報告する。
2. **台帳へ載せる種類の書き方を 1 か所にする**: `PositionQueryResult.ReportedFailureKind`。成功なら null、分類の無い失敗なら null（従来の「不明」）、照会し直しが無ければ `Transient` / `RateLimited` / `Other`。
3. **ガードの照会し直しを台帳で読めるようにする**: 照会し直しをした巡回の失敗は `<最初の種類>→<最後の種類>`（例 `Transient→Other`）と載せる。照会し直しをしていなければ従来どおり 1 語である。判断は IADR-0487 に残す。
4. 試験（T-10-2210〜T-10-2219）・自己変異。

範囲外:
- 分類の方針（`MoomooPositionQueryClassifier` の表）。分からないもの・-1 の業務上の失敗・-500 は Other のままにする（照会し直しは頻度制限の枠を消費する。IADR-0144 決定5）。PoC の retMsg が手元に無いため、文言を足す根拠も無い。
- 共有ポート `IBrokerPositionSource` の契約（照会不能は null・例外を投げない）。
- 照会の頻度・待ち・予算（IADR-0458）。
- 台帳へ報告しない照会（下の母集合の「対象外」）。分類はアダプタで行われ、アダプタのログには出る。台帳の報告が無いので、種類を運ぶ先が無い。
- #1122 の並行作業のファイル（ExecutionRecord / ExecutedOrderRow・遡及・OrderReservationReconciler）は触らない。

## 母集合（規則 9。origin/develop f17f1b73）

引き方: `git grep -n "GetPositionsAsync\|QueryPositionsAsync" -- backend ':!*Tests*'`（照会の呼び出し元）、`git grep -n "MoomooPositionQueryClassifier\|PositionQueryFailure\.\(Transient\|RateLimited\|Other\)" -- backend ':!*Tests*'`（分類している箇所）、`git grep -n "ReportAsync(PositionQuerySource" -- backend ':!*Tests*'`（台帳へ報告する箇所）、`git grep -n "IsOperationalAsync" -- backend/Services/OrderExecutionService ':!*Tests*'`（建玉照会を流用する probe）。

### 分類している箇所

| 箇所 | 入力 | 是正 |
| --- | --- | --- |
| `MoomooPositionQueryClassifier.Classify(Exception)`（唯一の分類器） | `IMoomooTradeClient.GetPositionsAsync` が投げた例外 | 変えない |
| `MoomooBrokerAdapter.QueryPositionsAsync`（唯一の呼び出し元。`GetPositionsAsync` と `IsOperationalAsync` もここを通る） | 同上 | 変えない |
| `PositionQueryRetry.IsRetryable`（分類の結果で照会し直しを決める） | 分類の結果 | 最初の種類と回数を結果へ載せる |

### OrderExecutionService の建玉照会の呼び出し元

| 呼び出し元 | 発生源（台帳） | 是正前の照会 | 是正前の種類の報告 | 是正 |
| --- | --- | --- | --- | --- |
| `ProtectiveStopGuard.GuardAsync`（巡回の先頭。照会し直しあり） | `ProtectiveStopGuard` | `PositionQueryRetry.QueryWithFailureAsync` | 最終結果の種類だけ | **本件で是正**（照会し直しの経過を載せる） |
| `ProtectiveStopGuard.GuardAsync`（照会し直しを渡されない構成） | `ProtectiveStopGuard` | `GetPositionsAsync` | null | 対象外（本番では使わない。Program.cs は常に照会し直しを渡す。IADR-0458 が「従来の口で 1 回」と定め、既存の試験 `照会し直しを渡さないガードは分類つきの口を使わない` が固定している） |
| `BrokerPositionSnapshotService.PublishOnceAsync` | `BrokerPositionSnapshot` | `GetPositionsAsync` | null | **本件で是正** |
| `OrderExecutionAppService`（決済のゲート） | `OrderDispatch` | `GetPositionsAsync`（例外は不明） | null | **本件で是正**（例外を不明へ倒す扱いは保つ） |
| `OrderExecutionAppService`（S1 の武装前） | `OrderDispatch` | `GetPositionsAsync` | null | **本件で是正** |
| `SoftwareStopExecutor`（自ら照会した回） | `SoftwareStopClose` | `GetPositionsAsync` | null | **本件で是正** |
| `BrokerAvailabilityProbeService.ProbeOnceAsync`（moomoo では建玉照会） | `BrokerAvailabilityProbe` | `IBrokerAvailabilityProbe.IsOperationalAsync` | null | **本件で是正**（probe が分類つきの口も持つときだけ共有の入口を使う。moomoo のアダプタの `IsOperationalAsync` は `GetPositionsAsync() is not null` と同値） |
| `ProtectiveStopGuard.HoldUnlessPositionGoneAsync`（建玉 0 の確かめ直し） | 報告しない（IADR-0462 決定2） | `GetPositionsAsync` | — | 対象外（台帳へ報告しない。null を据え置きへ倒すだけ） |
| `ProtectiveStopDriftAdopter` | 報告しない | `GetPositionsAsync` | — | 対象外（同上） |

### 他サービス

| 箇所 | 対象外の理由 |
| --- | --- |
| `MarketMonitorService` の `HttpPositionStore` / `GrpcPositionStore`（`Classify`） | 名前が同じだけで、リスク管理の台帳の建玉の分類である。moomoo の建玉照会ではない |
| `RiskManagementService` の `OrderDispatchForgoneLifecycle`（コメントの `GetPositionsAsync`） | コメントだけ |
| `TradeDecisionHoldings` / `TradeDecisionWorkingEntries`（台帳の発生源） | 取引判断の保有照会。moomoo の建玉照会ではなく、分類器を通らない |

## 照会し直しの窓（規則 11）

窓 = 巡回の先頭の照会の最初の失敗から、照会し直しの最後の結果までの時間である。台帳に載せる種類は、この窓のどちらの端を見るかで変わる。
- 増える側のプローブ P1: 最初が Transient、照会し直しも失敗（Other）。期待: 台帳から「照会し直しが効いた（したが失敗した）」と読める。
- 減る側のプローブ P2: 最初が Other（照会し直さない）。期待: 台帳から「照会し直していない」と読め、他の経路の同じ失敗と同じ種類が載る。
- 対照 P3: 最初が Transient、照会し直しで成功。期待: 失敗は報告しない（成功 1 回）。

| 形 | P1 | P2 | P3 |
| --- | --- | --- | --- |
| 後の端だけ（最終結果の種類。是正前） | ✕（「Other」だけ。照会し直しの有無が読めない＝#1164 の取り違え） | ○ | ○ |
| 前の端だけ（最初の種類） | ✕（「Transient」だけ。据え置きの原因の Other が消える） | ○ | ○ |
| 両端（照会し直したときだけ `最初→最後`。**採用**） | ○（`Transient→Other`） | ○（`Other`） | ○ |

P1〜P3 は T-10-2213・T-10-2214・T-10-2215 で固定する。

## 受け入れ基準 → 試験

| ID | 受け入れ基準 | 試験 |
| --- | --- | --- |
| T-10-2210 | 同じ失敗（例外の形ごと）を、本物のアダプタ越しに観測の常駐とガードへ渡すと、両経路が同じ種類を台帳へ報告する | `PositionQueryClassificationParityTests` |
| T-10-2211 | 一過性の失敗（Transient）なら、ガードは本物のアダプタ越しに 1 回だけ照会し直す（ブローカへの照会 2 回）。Other なら照会し直さない（1 回） | 同上 |
| T-10-2212 | 共有の入口: 分類つきの口を持つ供給元はそれを使い、持たない供給元は `GetPositionsAsync` を呼んで種類なしとする | 同上 |
| T-10-2213 | P1: 照会し直しても失敗したら `最初→最後` を報告する | `ProtectiveStopGuardTests`（T-10-1770 の理論を改めた） |
| T-10-2214 | P2: 照会し直さない失敗は 1 語で報告する | 同上 |
| T-10-2215 | P3: 照会し直して成功したら成功だけを報告する | 同上 |
| T-10-2216 | 決済のゲート・S1 の武装前（`OrderDispatch`）は分類つきの口の種類を報告する。例外は種類なしの失敗のまま | `PositionQueryClassificationParityTests` |
| T-10-2217 | S1 の決済（`SoftwareStopClose`）は分類つきの口の種類を報告する | 同上 |
| T-10-2218 | 稼働 probe は、probe が分類つきの口も持つときその種類を報告し、持たないときは従来どおり種類なしとする | 同上 |
| T-10-2219 | `ReportedFailureKind`: 成功は null、分類の無い失敗は null、照会し直し無しは 1 語、照会し直しありは `最初→最後` | 同上 |

試験 ID は並行レーン（#1122 が T-10-2189〜T-10-2205 を使用中）との衝突を避けるため帯 T-10-2210〜T-10-2219 を確保した（develop の最大は T-10-2188）。採番行は `docs/tests/FR-10_risk-controls-tests.md` に置く。

## 既存の試験の変更（規則 10）

- `ProtectiveStopGuardTests.T_10_1770_ガードは照会し直しの最終結果を失敗の種類つきで1回だけ報告する`: `[Transient, RateLimited]` の期待を `RateLimited` から `Transient→RateLimited` へ改めた（採用した形の帰結）。
- `PositionQueryStatusChanged` の `FailureKind` の注記と `PositionQuerySource.ProtectiveStopGuard` の注記（共有の契約のコメント）を、照会し直しの経過を載せる書き方へ改めた。型は変えない。
- 自分の記述で新たに誤りになるもの（規則 10）: IADR-0462 決定2 の「最終結果の種類」の記述。凍結記録なので本文は書き換えず、IADR-0487 が改める旨を書く。

## 自己変異（実測 2026-10-03。12 件すべて赤）

対象の試験（`T_10_22*`・`T_10_1770*`・`PositionQueryRetry*`・`ProtectiveStopGuardTests`、111 件）を変異ごとに走らせた（変異は 1 件ずつ当てて戻す）。

| # | 変異 | 赤になった試験 |
| --- | --- | --- |
| M1 | 観測の常駐が種類を捨てる（是正前の形） | T-10-2210（9 件） |
| M2 | ガードの照会し直しの経過を結果へ載せない | T-10-2210（6 件）・T-10-2211（1 件）・T-10-2213（2 件） |
| M3 | 照会し直した失敗に最初の種類だけを載せる | T-10-2210（6 件）・T-10-2211・T-10-2213（2 件）・T-10-2219 |
| M4 | 共有の入口が分類つきの口を使わない | T-10-2210（9 件）・T-10-2212・T-10-2216（5 件）・T-10-2217 ほか（17 件） |
| M5 | 分類器で返信待ちの打ち切りを Other にする | T-10-2210・T-10-2211（3 件）・T-10-2216（2 件）・T-10-2217・T-10-2218・既存の分類の試験（9 件） |
| M6 | 稼働 probe が分類つきの口を使わない | T-10-2218（9 件） |
| M7 | 決済のゲートが種類を捨てる | T-10-2216（3 件） |
| M8 | S1 の武装前が種類を捨てる | T-10-2216（2 件） |
| M9 | S1 の決済が種類を捨てる | T-10-2217（2 件） |
| M10 | 🔴 ガードが照会し直しの最終結果だけを載せる（是正前の形） | T-10-2210（6 件）・T-10-2211（1 件）・T-10-2213（2 件） |
| M11 | 照会し直しの対象から一過性の失敗を外す | T-10-2210（5 件）・T-10-2211（7 件）・既存の照会し直しの試験（10 件） |
| M12 | 照会し直しの回数を 0 と記録する | T-10-2210（6 件）・T-10-2211（1 件）・T-10-2213（2 件） |
