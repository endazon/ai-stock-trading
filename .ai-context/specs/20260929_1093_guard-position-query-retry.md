---
title: 保護逆指値ガードの建玉照会を、分類できた一時的な失敗に限り巡回の中で 1 回だけ照会し直す（#1093 段 1）
type: spec
status: accepted
related_ids: [FR-10, UC-02, ADR-0040, IADR-0458, IADR-0118, IADR-0144, IADR-0210, IADR-0211, IADR-0344, IADR-0412]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs: []
---

# 保護逆指値ガードの建玉照会の照会し直し（#1093 段 1）

## 背景

#1093 は、PoC セッションの 2026-09-28〜29 の報告である。
- moomoo の建玉照会が 3 回失敗した（返信待ちの打ち切り、19:29 UTC、再起動直後）。
- そのたびに保護逆指値ガードが全件を 1 巡回（30 秒）据え置いた。
- 再起動を重ねた直後には、頻度制限に 2 回当たった。

起点は FR-10（保護逆指値）である。計画 ADR の新たな制約は無い。決定は IADR-0458 に記す。

## 段の分け方

- **段 1（本 PR）**: ガードの巡回の先頭で行う建玉照会だけを、分類できた一時的な失敗に限って 1 回照会し直す。
- **段 2（別 PR）**: 起動時の初回の照会をずらし、同時に集中しないようにする。対象はスナップショット（BrokerPositionSnapshotService）と稼働 probe（BrokerAvailabilityProbeService）。

## 母集合（規則 9: 建玉照会の呼び出し元を全部挙げ、照会し直しを入れるかを決める）

`git grep -n "GetPositionsAsync" -- backend/Services/OrderExecutionService`（テストを除く）で走査した。

| 呼び出し元 | 用途 | 照会し直し |
| --- | --- | --- |
| `ProtectiveStopGuard.RunOnceAsync`（巡回の先頭） | 巡回で使う建玉 | **入れる**（本 PR） |
| `ProtectiveStopGuard.HoldUnlessPositionGoneAsync` | 建玉 0 の確かめ | 入れない。失敗は据え置きへ倒れ、穴を作らない |
| `OrderExecutionAppService`（決済ゲート・S1 の武装） | 発注の経路 | 入れない（IADR-0211。承認から発注までを遅らせない） |
| `SoftwareStopExecutor`（S1 の決済） | 発注の経路 | 入れない（同上） |
| `ProtectiveStopDriftAdopter` | 乖離の取り込み | 入れない（範囲外） |
| `BrokerPositionSnapshotService`（10 分ごと） | 観測の発行 | 入れない（次の巡回で足りる） |
| `MoomooBrokerAdapter.IsOperationalAsync`（稼働 probe） | 到達性 | 入れない。照会し直すと、到達性の判定が変わる |

「1 巡回 1 回」を前提にしている記述も走査した（`git grep -n "1 巡回.*1 回"`）。
- ガードの注記は「1 つのスナップショットを使う」へ直した。
- `ProtectiveStopNetting` の「群につき 1 巡回 1 回の観測」は変わらない。照会し直しても、使うスナップショットは 1 つだけだからである。
- IADR-0412 の「建玉照会は 1 巡回 1 回のまま」は、建玉観測の常駐（BrokerPositionSnapshotService）の巡回についての記述である。本 PR は常駐に触れないので、そのまま真である（当初はガードの巡回と読み違えていた。監査の指摘で正した）。

## 設計（IADR-0458）

- `IClassifiedPositionSource.QueryPositionsAsync` → `PositionQueryResult(Positions?, Failure)`。
  - moomoo のアダプタだけが実装する。`GetPositionsAsync` は、その `Positions` を返す。共有ポートの契約は変えない。
- `MoomooPositionQueryClassifier` で分類する。
  - Transient: `TimeoutException`、`BrokerUnavailableException`、retType -100 / -200 / -400。
  - RateLimited: retType -1 で、retMsg が頻度制限の語を含むもの。
  - Other: 上記以外。
- `PositionQueryRetry` の動き。
  - 最大 1 回照会し直す。待ちは Transient が 2 秒、RateLimited が 10 秒で、±50% の揺らぎを掛ける。
  - 待ちの合計が予算（巡回間隔の半分）を超えるなら、照会し直さずに null を返す。
  - 待ちと乱数は注入できる（試験は壁時計を使わない）。
- 設定は `ProtectiveStopGuard:PositionQueryMaxRetries`（0〜1）・`PositionQueryTransientRetryDelay`・`PositionQueryRateLimitedRetryDelay`・`PositionQueryRetryJitter`（0〜1）。
- `Program.cs` で、ガードへ `PositionQueryRetry` を渡す（moomoo 構成のみ）。

## 受け入れ基準 → 試験

`PositionQueryRetryTests`（27 件）:
- 成功なら 1 回で待たない。
- Transient の後は 2 秒待って成功を返す。RateLimited の後は 10 秒待つ。
- Transient が続けば 2 回で null を返す。
- Other / None では照会し直さない（呼び出しは 1 回）。
- 予算を超えるなら照会し直さない。ちょうど予算なら照会し直す。
- 揺らぎの両端（1 秒・3 秒）。回数を 0〜1 に収める。取り消されたら中断する。
- 分類の表（13 通り）。

`MoomooBrokerAdapterTests`（8 件）:
- 分類つきの照会は失敗の種類を運び、同じ失敗で `GetPositionsAsync` は null を返す（契約は不変）。
- 成功は None。
- 取り消しは分類せず、両方の口から伝播する（監査の生存変異 M19 を殺す）。

`ProtectiveStopGuardTests`（3 件）:
- 一時的な失敗の後に照会し直して成功すれば、その巡回で評価する（Unknown 0）。
- Other は照会し直さず、全件を据え置く（取消も再発注もしない）。
- 照会し直しを渡さないガードは、分類つきの口を使わない。

テスト仕様書 `docs/tests/FR-10_risk-controls-tests.md` に節を足し、T-10-1737（照会し直しの部品）・T-10-1738（分類）・T-10-1739（ガード）を採番した（採番の最大値 T-10-1736 の次）。

## 検証

- `dotnet test backend/Services/OrderExecutionService/Tests`: 1192/1192 成功・警告 0。
- `dotnet format --verify-no-changes`（OrderExecutionService）: 差分なし。

## 残余

- 起動時の集中は段 2 で扱う。
- 予算が数えるのは待ちだけで、照会の所要時間は含まない。打ち切りが 2 回続く最悪では、次の巡回が最大で約 18 秒遅れる（IADR-0458 決定 3）。
- 頻度制限の後の照会し直しは、発注の経路と同じ枠を消費し得る（IADR-0458 §結果）。
- 頻度制限の文言は、語の一部で引いている。外れたら Other（照会し直さない）へ倒れる。安全側である。
