---
title: IADR-0476 OpenD の retMsg に現れる口座 ID を、例外を作る 1 か所（EnsureSucceeded）で口座一覧の全口座 ID について伏せる
type: impl-adr
status: Accepted
related_ids: [FR-11, FR-10, FR-05, IADR-0473, IADR-0347, IADR-0300, IADR-0458, IADR-0117]
author: claude (Claude Code)
created: 2026-10-01
updated: 2026-10-01
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements (FR-11: 運用の観測。ログに機密を残さない)
---

# IADR-0476: OpenD の retMsg に現れる口座 ID を伏せる（#1148）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-01
- 決定者: Claude Code（実装）。統制の値・発注の分類は変えない

## 起点・関連

- 関連する計画書 ID: FR-11（運用の観測）・FR-10（S3 の拒否理由の監査）・FR-05（発注）
- 起票: [#1148](https://github.com/endazon/ai-stock-trading/issues/1148)（PR #1147 の独立監査が残余として起票）
- 関連する実装仕様書: [`.ai-context/specs/20261001_1148_redact-retmsg-account-id.md`](../specs/20261001_1148_redact-retmsg-account-id.md)
- 前提:
  - [IADR-0473](IADR-0473_quote-refresh-closed-market-and-account-log-demotion.md) 決定 4: 発注執行が組み立てるログの口座 ID は `MaskAccountId`（末尾 2 桁以外を伏せる）。残余に「`retMsg` を含む例外・ログは射程外（#1148）」
  - [IADR-0347](IADR-0347_alternative-broker-order-types-for-simulate.md): `MoomooTradeRequestException` は retType / retMsg を構造として保ち、S3 の拒否理由を監査台帳へ運ぶ
  - [IADR-0300](IADR-0300_trade-expense-source-port-and-negative-recording.md)（2026-09-29 追記）: 検証口の出力は口座 ID を伏せる（既知の口座 ID の置換と、口座が未確定のときの 6 桁以上の数字の並びの伏せ）
  - [IADR-0458](IADR-0458_guard-position-query-classified-single-retry.md) 決定 2: 建玉照会の失敗の分類は retMsg の頻度制限の語を引く

## 背景

OpenD の生の `retMsg` は `MMApiMoomooTradeClient.EnsureSucceeded` が `MoomooTradeRequestException` の例外文と `RetMsg` へそのまま載せていた。
例外はアダプタのログ（確認できた拒否の Warning・届いたか不明の Error・照会の失敗の Warning）・可用性の巡回の Error・
`BrokerDispatchIndeterminateException` の本文（監査の見送り理由にもなる）・S3 の拒否理由（`AlternativeProtectiveStopAttempted.RejectReasonMessage`）へ流れる。
伏せていたのは検証口の出力だけで、`retMsg` に口座 ID が入れば全桁で残り得た。

## 決定

### 決定 1: 伏せるのは作る側の 1 か所（`EnsureSucceeded`）

`EnsureSucceeded` を instance メソッドにし、`RedactRetMsg` を通してから `MoomooTradeRequestException` を作る。例外文と `RetMsg` の両方が伏せた値になる。
本番で `MoomooTradeRequestException` を作るのは `EnsureSucceeded` だけである（11 か所から呼ばれる）。

根拠: 流れる先は例外をログへ渡す口だけで発注執行に 48 か所あり、受け手で伏せると次に足した受け手が漏れる。
作る側に置いてよいかを先に確かめた——`retMsg` を判定に使うのは建玉照会の失敗の分類（頻度制限の語 `frequen|times per|频率|頻度`）だけで、語は数字を含まないので分類は変わらない。
`IsConfirmedFailure` は `RetType` だけを見る。

### 決定 2: 伏せる値は「口座一覧で見た全口座 ID」。口座一覧を読めていなければ 6 桁以上の数字の並び

- `MMApiMoomooTradeClient` は口座一覧（`Trd_GetAccList`・`userID=0`＝ログイン中のユーザーの**全口座**。実弾を含む）を読むたびに、全口座の ID を集合へ足す（減らさない。入れ替わった前の口座も伏せ続ける）。
  口座一覧は接続時と可用性の巡回（既定 5 分）ごとに読まれる。OpenD がその接続で扱える口座は一覧に出る口座だけなので、集合は接続中のログインについて閉じている。
- 集合があれば、集合の各 ID（大きい順＝桁の多い順。長い ID の中の短い ID の部分一致で長い方の頭を残さない）を既存の `RedactAccountId`（末尾 2 桁以外を伏せる）で置き換える。**注文 ID・日付・回数などの他の数字は残す**（拒否理由として読めること＝IADR-0347 の目的を崩さない）。
- 集合が空（接続時の口座一覧の照会そのものの失敗）なら、伏せる値が分からないため、検証口と同じ `OrderFeeProbeCommand.MaskLongDigitRuns`（6 桁以上の数字の並びを末尾 2 桁以外伏せる）を掛ける。2 通り目の伏せ方は作らない。
- 検証口（注文費用の照会口）の `retMsg` も同じ `RedactRetMsg` で伏せる。集合に発注口座（SIMULATE）を足して渡す（従来は発注口座だけを伏せており、実弾の口座 ID が検証口の出力に全桁で出得た。独立監査 🟡 2026-10-01）。

### 決定 3: 採らなかったもの

- **受け手（ログ・監査の組み立て）で伏せる**: 受け手が 48 か所以上あり、新しい受け手が漏れる（決定 1）。
- **常に数字の並び（6 桁以上）で伏せる**: 口座 ID 以外の数字（注文 ID 〔10 桁〕・日付 〔8 桁〕）まで伏せ、拒否理由・監査台帳の読みやすさを落とす。口座一覧という正の集合が手に入るので、パターンは集合が無いときだけに使う。
- **発注に使う口座（`_simAccId`）だけを伏せる**: `retMsg` は実弾口座を含む他の口座に触れ得る（口座一覧の先頭は実弾。#342 の実測）。
- **`MoomooTradeRequestException` の側で伏せる**（コンストラクタに集合を渡す）: 試験の偽クライアントが同じ型を作る。作る口が 1 か所なので、そこで伏せれば足りる。

## 結果

- `MoomooTradeRequestException` の例外文・`RetMsg` に、口座一覧で見た口座 ID の全桁が出ない。その例外を運ぶログ・例外の連鎖・監査イベントにも出ない。
- S3 の拒否理由（監査台帳）は口座 ID の部分だけが `****<末尾 2 桁>` になる。理由の文・retType・他の数字は残る。
- 建玉照会の失敗の分類・発注の分類は変わらない。
- 試験 T-10-2000〜T-10-2008。IADR-0473 の残余（#1148）を解消する。

### 残余リスク

- 口座一覧に出ない口座の ID（別ログインの口座など）が `retMsg` に現れれば伏せない（集合があるときはパターンを掛けないため）。OpenD は接続中のログインの口座しか扱わないので、現れる見込みは低い。
- 口座一覧を読めていない間（接続時の口座一覧の照会の失敗）は 6 桁以上の数字の並びをすべて伏せる。5 桁以下の口座 ID は伏せない（検証口と同じ）。
- 口座 ID を区切り文字入り（`7248-08` 等）や別の表記で返されれば一致しない（実機で口座 ID を含む `retMsg` は未実測）。
- 市況（Qot）の応答の `retMsg`（K 線の検証口・バックテストの履歴 K 線）は口座を扱わないため対象外。
- 置換は境界を見ない部分一致（`RedactAccountId` の `string.Replace`）である。口座 ID の数字列を含む別の数字（例: 注文 ID `9000724808001`）は一部が伏せられる（`9000****08001`）。漏らす向きではなく伏せすぎる向きの誤りで、拒否理由の注文 ID が読みにくくなる場合がある（独立監査 🟡 2026-10-01）。
