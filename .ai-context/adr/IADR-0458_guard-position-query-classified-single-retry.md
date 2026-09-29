---
title: IADR-0458 保護逆指値ガードの建玉照会は、分類できた一時的な失敗に限り巡回の中で 1 回だけ照会し直す（発注の経路には入れない）
type: impl-adr
status: Accepted
related_ids: [FR-10, UC-02, ADR-0040, IADR-0118, IADR-0144, IADR-0210, IADR-0211, IADR-0117, IADR-0412]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs: []
---

# IADR-0458: 保護逆指値ガードの建玉照会を、一時的な失敗に限って 1 回だけ照会し直す（#1093）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-09-29
- 決定者: Claude Code（実装）。計画の内側で照会の仕方を選ぶ実装判断であり、裁定は要らない

## 起点・関連

- 関連する計画書 ID: FR-10（保護逆指値）・UC-02
- 関連する実装仕様書: [`.ai-context/specs/20260929_1093_guard-position-query-retry.md`](../specs/20260929_1093_guard-position-query-retry.md)
- 前提:
  - [IADR-0210](IADR-0210_broker-side-stop-loss-unification.md) 決定 4(c): 照会不能は据え置き。**本 IADR は変えない。**
  - [IADR-0118](IADR-0118_broker-position-reconciliation.md): 照会不能（null）と空列を区別する。**本 IADR は変えない。**
  - [IADR-0144](IADR-0144_moomoo-short-selling-poc-outcomes.md) 決定 5: 失敗した照会も頻度制限の枠を消費し、素朴な再試行は連鎖失敗を招く。
  - [IADR-0211](IADR-0211_opend-unavailable-forgo-without-queueing.md) 決定 3: 発注の経路に自動の撃ち直しを作らない。

## 背景

PoC の稼働で、moomoo の建玉照会が一時的に 3 回失敗した（返信待ちの打ち切り・再起動の直後）。そのたびに、ガードは巡回（30 秒）ごと全件を据え置いた。
再起動を重ねた直後には、頻度制限にも当たった（#1093）。

アダプタはすべての例外を null に丸めていたので、一時的な失敗と恒久的な失敗を区別できなかった。

## 決定

1. **失敗の種類は、発注執行サービスの中の新しい口（`IClassifiedPositionSource.QueryPositionsAsync`）で運ぶ。** 共有ポート `IBrokerPositionSource` の契約（照会不能は null・例外を投げない）は変えない。
   - `MoomooBrokerAdapter.GetPositionsAsync` は、新しい口の `Positions` を返すだけにする。
2. **分類**（`MoomooPositionQueryClassifier`）:
   - **Transient**: `TimeoutException`、`BrokerUnavailableException`（接続の確立の失敗）、retType -100 / -200 / -400。
   - **RateLimited**: retType -1 のうち、retMsg に `frequen` / `times per` / `频率` / `頻度` を含むもの。
   - **Other**（照会し直さない）: 上記以外。-500（応答の読み損ね）と未定義の値を含む。
3. **ガードの巡回の先頭の建玉照会（1 巡回に 1 回）だけが照会し直す**（`PositionQueryRetry`）。
   - 最大 1 回（設定 `PositionQueryMaxRetries`。0〜1 に収める）。
   - 待ちは、Transient が 2 秒、RateLimited が 10 秒で、±50% の揺らぎを掛ける。
   - 待ちの合計が予算（巡回間隔の半分）を超えるなら、照会し直さずに据え置く。
   - 使い切ったら、従来どおり null で全件を据え置く。
4. 🔴 **発注の経路には入れない。**
   - 決済ゲート・S1 の武装と決済・成行手仕舞い・逆指値の再発注・取消の照会は、従来どおり 1 回だけ照会する。アダプタ層に一律で入れない。
   - ガード内の建玉 0 の確かめ（`HoldUnlessPositionGoneAsync`）も照会し直さない。失敗は据え置きへ倒れるだけで、保護の穴を作らないため。
5. **観測の数え方は変えない。** 照会し直しても、巡回で使うスナップショットは得られた 1 つだけである。
   - IADR-0344 追記(5)・(7) の「群につき 1 巡回 1 回の観測」は変わらない。
   - IADR-0412 の「建玉照会は 1 巡回 1 回のまま」は、成功した巡回の回数として保たれる。一時的な失敗の巡回だけ、要求が最大 2 回になる。

## 採らなかった案

- **null なら分類せずに 1 回照会し直す**: 恒久的な失敗でも、毎巡回で余計な要求を消費する（IADR-0144 決定 5 に抵触）。
- **アダプタで一律に再試行する**: 発注の経路（決済ゲート・S1）にも待ちが入り、承認から発注までの遅延が増える（IADR-0211）。
- **2 回以上の再試行・長い待ち**: 頻度制限の窓（30 秒程度）の中で枠をさらに食い、巡回も遅れる。

## 結果

- 一時的な失敗 1 回で生じていた 30 秒の保護の穴を、多くの場合その巡回の中で塞げる。
- **残余**:
  - 起動直後の照会の集中（ガード・スナップショット・稼働 probe が同時に初回を送る）は、本 IADR では減らさない。#1093 の後続の PR で、起動時の初回をずらす。
  - 頻度制限の文言は、OpenD の版で変わり得る。語の一部で引いているが、外れると Other（照会し直さない）へ倒れる。安全側である。
