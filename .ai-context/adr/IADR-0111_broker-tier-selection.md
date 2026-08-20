---
title: IADR-0111 ブローカー選択は provider × environment の直交 2 軸で表現し、実弾は解禁ゲート 1 点に集約する
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-12, FR-20, ADR-0002]
author: endazon (with Claude Code)
created: 2026-07-29
updated: 2026-07-29
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/03_moomoo-integration.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# IADR-0111: ブローカー選択は provider × environment の直交 2 軸で表現する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-29
- 決定者: endazon（利用者・階層化方針の指示と設計承認）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-05（発注執行）、FR-12（ペーパートレード）、FR-20（段階ゲート）、
  ADR-0002（計画リポ）（証券会社連携・**Proposed**）
- 対象 Issue: [#267](https://github.com/endazon/ai-stock-trading/issues/267)
- 関連する実装仕様書: [20260729_broker-tier-selection](../specs/20260729_broker-tier-selection.md)
- 関連 IADR: [IADR-0016](IADR-0016_safe-broker-execution.md)（安全既定 paper・実弾防止の二重ゲート）、
  [IADR-0056](IADR-0056_moomoo-simulate-poc-complete-real-gated.md)（SIMULATE 限定・**§3 実弾解禁前提**）、
  [IADR-0060](IADR-0060_opend-production-cutover-gates.md)（OpenD 本番化・決定 5＝第三の閂）、
  [IADR-0092](IADR-0092_reservation-broker-probe-moomoo.md)（moomoo リコンサイルプローブ）
- 運用手順書: [live-trading-cutover-runbook](../../docs/operations/live-trading-cutover-runbook.md)

## 背景・課題

ブローカー選択は `Broker:Provider` の `paper` / `moomoo` 2 分岐であり、Helm は `moomoo.enabled`（bool）で
その 2 値を切り替える。一方で `TrdEnv` は 4 層（ヘッダ固定・config 拒否・SIMULATE 口座採用・provider ゲート）で
SIMULATE に固定されている。

ここでは「**どの証券会社に出すか**（provider）」と「**どの取引環境に出すか**（sim / 実弾）」という
本来直交する 2 軸が、単一の bool・単一の文字列に潰れている。帰結は 3 つある。

1. 運用の本番近接順（**paper ＜ シム ＜ 実弾**）という段階が、設定の語彙として存在しない。
   利用者は「今どの階層で走っているのか」を provider 文字列と `Broker:Moomoo:TrdEnv` の組から推論するしかない。
2. ADR-0002 は「立花証券 e支店 API を日本株の冗長系として将来追加する価値がある（`IBrokerAdapter` で
   抽象化済みのため追加は容易）」と明記しているが、現状の表現では証券会社を足すたびに provider 文字列が増殖し、
   各社ごとに sim / 実弾の区別を別の場所で持つことになる。
3. 実弾解禁時に「どこを開けるのか」が `Broker:Provider` と `Broker:Moomoo:TrdEnv` に分散し、
   **解禁という決定の単一責務が定まらない**。IADR-0056 §3 は解禁に「別 IADR ＋ 明示 config」を要求しているが、
   その「明示 config」がどこに現れるべきかは未定義だった。

## 検討した選択肢

1. **単一キー `Broker:Tier`（`paper` / `moomoo-sim` / `moomoo-live` …）** — 設定面は最も単純。ただし
   provider 軸は将来増え environment 軸は 2 で固定なので、値は直積の列挙になる。証券会社を足すたびに
   parse とテストが `n × 2` で増え、「moomoo と立花で同じ environment」という関係を型が表現できない。
2. **`moomoo.enabled` に加えて `moomoo.live` のような bool を足す** — 最小差分だが、証券会社ごとに
   bool が 2 本ずつ増え、排他関係（複数 provider の同時 true）が型で表現できない。現状の潰れをさらに悪化させる。
3. **provider × environment を独立した型で表現し、Helm 面だけ単一 tier に畳む** — アプリ内は 2 軸のまま、
   運用者が触るスイッチは 1 つ。証券会社の追加は enum 1 値で済む。

## 決定

**選択肢 3 を採用する。** 加えて、実弾の可否を**単一の解禁ゲートに集約**する。

1. **型**: `BrokerProvider`（`Paper` / `Moomoo`）と `BrokerEnvironment`（`Simulated` / `Live`）を独立した
   enum とし、`BrokerSelection` レコードが両者を保持する。正準名 `Tier`（`paper` / `moomoo-sim` / `moomoo-live`）が
   そのまま本番近接順を表し、introspection の自己申告とログに用いる。paper は environment 非該当
   （内蔵擬似）として扱い、`Tier` は常に `paper`。
2. **設定キー**: アプリは `Broker:Provider`（既存キー据置・既定 `paper`）＋ `Broker:Environment`（新規・既定 `sim`）の
   2 キー。Helm は単一 value `broker.tier`（既定 `paper`）で、template が 2 つの環境変数へ展開する。
   直交軸をアプリ内で潰さず、運用者が触るスイッチは 1 つに保つ。
3. **`moomoo.enabled` は非推奨エイリアスとして温存**する。`broker.tier` が既定のままで `moomoo.enabled=true` なら
   `moomoo-sim` として描画し、矛盾指定は描画時 `fail` で止める。既定（`moomoo.enabled=false`）の本番描画は
   **バイト等価**であり、`helm.yml` がこれを検査する。
4. **fail-safe はすべて発注抑止側**: 未設定は `paper` / `sim`。未知の provider・未知の environment・
   `paper`＋`live` の矛盾は、いずれも**起動時停止**とする。黙って sim へ倒すと誤設定が隠れ、
   黙って paper へ倒すと「実弾のつもりで擬似発注」が生まれる。既存 `EnsureSimulate` と同じ流儀で、
   「安全側へ黙って倒す」ではなく「誤認を起動時に表面化させる」を選ぶ。
5. **実弾は `LiveTradingGate` 1 点に集約し、本 IADR では解禁しない**。
   `LiveTradingGate.LiveTradingReleased` は `const bool` の `false` であり、`BrokerEnvironment.Live` が
   選択された時点で IADR-0056 §3 の前提を列挙して起動時停止する（**閂 0**）。
   加えて Helm は `broker.tier=moomoo-live` を**描画時に `fail`** させ、誤設定がクラスタへ届かないようにする（**外周の閂**）。
   **既存の閂 1〜4（provider ゲート・`SetTrdEnv(Simulate)` 固定・`EnsureSimulate`・SIMULATE 口座採用）は
   一行も変更しない。** したがって live は「型として表現できるが到達不能」である。
6. **将来の解禁は `LiveTradingReleased` を `true` にする 1 ファイルの変更に集約される**。それには
   別 IADR（IADR-0056 §3 の前提充足を根拠づけるもの）を要し、その IADR が閂 2・3 の緩和も併せて扱う。

## 理由

- **2 軸の分離が、証券会社追加のコストを線形に保つ**。立花証券を足す差分は「enum 1 値 ＋ アダプタ ＋
  `BrokerFactory` の switch 1 腕 ＋ Helm の tier 値」であり、environment 側のロジック・テストは共有される。
  選択肢 1 では追加のたびに sim / live の 2 値を parse とテストへ展開する必要がある。
- **解禁を 1 点へ集約したことが、本変更の安全上の主目的**である。従来「実弾に近づく設定」は
  provider 文字列と `Broker:Moomoo:TrdEnv` に分散していた。`LiveTradingGate` は grep 可能な単一の
  述語であり、「解禁されているか」をテストで固定できる（`LiveTradingReleased` が false であること自体を
  テストが主張する）。これは閂を**増やす**変更であって、既存の閂を弱める変更ではない。
- **Helm 側を単一 tier に畳んだのは運用者の誤設定を減らすため**。2 つの value を別々に設定させると
  「provider は moomoo に変えたが environment を戻し忘れる」形の事故が起こり得る。tier は 1 語で階層を表す。
- **`paper`＋`live` を拒否する**のは、この組み合わせを黙認すると「実弾のつもりで擬似発注していた」という
  最悪の誤認が成立するため。paper が environment 非該当であることは、黙殺ではなく**明示的拒否**で表す。

## 影響

- **肯定的**: 3 階層が設定の語彙になり、introspection が `Tier` を自己申告するため「今どの階層か」が
  実行時に確認できる。証券会社の追加が最小差分になる。実弾解禁の決定点が 1 箇所に定まる。
- **制約**: `Broker:Environment` という設定キーが増える（既定 `sim` のため未設定でも現行挙動）。
  `moomoo.enabled` は非推奨となり、将来削除する際に別途 chore を要する。
- **不変**: 実弾は本 IADR で解禁されない。既存の閂 2〜4 は無改変であり、その証拠として
  `MoomooBrokerOptionsTests` 等の既存テストが差分ゼロのまま緑であることを PR 本文で提示する。
  本番 `values.yaml` の描画はバイト等価。

## 備考

`TradeMode`（`Paper` / `Live`・`OrderIntent` と段階ゲートが持つ概念）とは別物である。`TradeMode` は
「その注文が実弾相当の統制下にあるか」という**リスク統制側の段階**であり、`BrokerEnvironment` は
「実際にどの取引環境へ送るか」という**執行先**である。段階ゲートが `TradeMode.Live` を許可していても、
`BrokerEnvironment.Simulated` であれば SIMULATE に出る。両者を統合しないのは、段階の進行（FR-20）と
執行先の切替（FR-05）が別の権限・別の手順で動くべきだからである。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [ADR-0002], [IADR-0016](IADR-0016_safe-broker-execution.md), [IADR-0056](IADR-0056_moomoo-simulate-poc-complete-real-gated.md), [IADR-0060](IADR-0060_opend-production-cutover-gates.md)
