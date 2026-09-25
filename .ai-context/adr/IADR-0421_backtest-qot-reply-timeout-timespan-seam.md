---
title: IADR-0421 履歴 K 線クライアントの応答待ちは構成（整数秒）を変えず、TimeSpan を直接与える口をコンストラクタへ足す
type: impl-adr
status: Accepted
related_ids: [FR-15, ADR-0002, ADR-0023, IADR-0157, IADR-0327, IADR-0379]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-15 バックテスト)
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md (決定5 moomoo 履歴 K 線)
---

# IADR-0421: 履歴 K 線クライアントの応答待ちは構成を変えず、TimeSpan を直接与える口をコンストラクタへ足す

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 #988。利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: #988（応答待ちの打ち切りと成功応答が競走する試験の残り。#981 / PR #987 の母集合の「残余」）。
- 関連する実装仕様書: [20260925_988_reply-timeout-race-remaining](../specs/20260925_988_reply-timeout-race-remaining.md)
- 関連 IADR: [IADR-0379](IADR-0379_wall-clock-timeout-test-discriminator-and-never-responding-upstream.md) 決定 1・2
  （壁時計どうしの競争で合否が決まる試験の判別軸・応答しない上流と Guard）。
  [IADR-0327](IADR-0327_opend-connection-recreate-after-failed-attempt.md)（接続オブジェクトの作り直し。本件の試験が固定する性質。
  差し替え口 `IMoomooQotConnectionFactory` もコンストラクタの省略可能な引数として開けた前例）。
  [IADR-0157](IADR-0157_moomoo-history-kline-adapter.md)（moomoo 履歴 K 線アダプタ）。

## コンテキストと課題

- `MMApiMoomooHistoryKLineClientReconnectTests` の回復を表明する 2 件は、応答待ち 1 秒の 1 つのクライアントで
  「1 回目は打ち切りで失敗 → 2 回目は応答が返って回復」を通していた。応答は実 SDK と同じく `Task.Run`（スレッドプール）で返り、
  応答待ちの打ち切りもスレッドプールが配送する。プールが塞がると、空いた瞬間には両方とも期限切れで順序は保証されない
  （IADR-0379 決定 1 の形。#981 が発注経路で同じ機序を確認した）。実測: 応答を 1.5 秒遅らせると 2 件が決定的に赤。
- 発注経路（#987）は `MoomooBrokerOptions.ReplyTimeout`（`TimeSpan`・`init`）へ `Timeout.InfiniteTimeSpan` を与えて直した。
  🔴 **履歴 K 線の構成 `MoomooBarDataOptions.ReplyTimeoutSeconds` は `int`（整数秒）で、本体は `TimeSpan.FromSeconds(int)` にしか
  写さない。**試験側だけでは無期限を表せない（`-1` 秒は `WaitAsync` が `ArgumentOutOfRangeException`、`0` は即時の打ち切り）。
- 構成は `IOptions<BarDataOptions>` の束縛（`Backtest:BarData:Moomoo:ReplyTimeoutSeconds`）で読む。発注経路と違い、
  値域の検査（1〜600 秒）は持たない。

## 検討した選択肢

1. **`ReplyTimeoutSeconds` の 0 以下を無期限と解釈する** —— 本番の誤設定（`0`・負値）が、今は「毎回即座に失敗する／例外で落ちる」
   という**目に見える失敗**なのに対し、**黙って固まる**（バックテストの取得が応答待ちで永久に止まる）へ変わる。
   試験の都合で fail-safe の向きを反転させる。**却下**。
2. **構成を `TimeSpan` 化する（`ReplyTimeout: "00:00:30"`）** —— 構成キーと書式が変わる。束縛の `TimeSpan` は `"30"` を
   **30 日**と読む（整数秒のつもりの設定が桁違いの待ちになる）罠があり、`"-00:00:00.001"` で無期限も書けてしまう（案 1 と同じ反転）。
   **却下**。
3. **`MoomooBarDataOptions` に `TimeSpan?` の上書き用プロパティを足す** —— 公開の setter / `init` を持つプロパティは
   構成の束縛の対象になり、同じ意味の構成キーが 2 つになる。`internal` にするには本サービスに無い `InternalsVisibleTo` を足す必要がある。**却下**。
4. **`TimeProvider` を注入し、試験は手動の時計で打ち切りを進める** —— IADR-0379 案 C・#981 と同じ理由（試験の都合で本番の構造を
   大きく変える。`WaitAsync(TimeSpan, TimeProvider, …)` への書き換えが接続・送信の両経路に及ぶ）。**却下**。
5. **コンストラクタへ省略可能な `TimeSpan? replyTimeout` を足す**（**採用**）。null（既定）なら従来どおり構成の整数秒。

## 決定

1. **`MMApiMoomooHistoryKLineClient` のコンストラクタへ省略可能な `TimeSpan? replyTimeout = null` を足す。**
   `_replyTimeout = replyTimeout ?? TimeSpan.FromSeconds(options.ReplyTimeoutSeconds)`。
   **構成キー・型（整数秒）・既定 30 秒・束縛・`Program.cs` の DI 登録（2 引数）は変えない。**本番の挙動は不変である。
2. **回復（応答が返る）を表明する試験は `replyTimeout: Timeout.InfiniteTimeSpan` を与え、1 回目の失敗は接続拒否の通知
   （`OnInitConnect(errCode=-1)`）で起こす。**完了の口が 1 つずつしか無いので競走が無い（#987 と同じ作法）。
3. **打ち切りで失敗し続けること（陰性対照）は、構成の経路（`ReplyTimeoutSeconds = 1` → `TimeSpan`）をそのまま通す。**
   完了の口は打ち切りだけ（応答しない偽 OpenD）。整数秒の写しを通る試験を残すため、ここでは新しい口を使わない。
4. 口の値域は検査しない（`WaitAsync` が `Timeout.InfiniteTimeSpan` 以外の負値を `ArgumentOutOfRangeException` で拒む。
   本番は渡さないので起こり得ない防御を足さない）。

## 理由

- 試験が必要とするのは「無期限」を表せることだけで、構成の意味を変える必要は無い。案 1・2 はいずれも本番の誤設定を
  黙ったハングへ変え得る。案 5 は本番の経路に一切触れず（null のとき式は従来と同一）、差し替え口の前例（IADR-0327 の
  `connectionFactory`）と同じ形である。
- 実測（作業仕様書）: 是正前は応答を 1.5 秒遅らせると回復の 2 件が決定的に赤、是正後は 6 秒遅らせても緑。
  作り直しを止める本番の変異では是正後も 3 件が赤（検出力を保つ）。

## 結果

- 良い影響: 履歴 K 線の再接続試験から、応答と打ち切りの競走が消える。陰性対照の壁時計の上限（旧: 30 秒未満）も外れ、
  再試行していないことは回数（接続オブジェクト 3 本・各 `InitConnect` 1 回）で押さえる。
- 代償: 本番クラスの公開コンストラクタに、本番が使わない引数が 1 つ増える（`connectionFactory` と同じく試験の差し替え口）。
- 🔴 残余: `ReplyTimeoutSeconds` の値域検査（発注経路の 1〜600 秒・範囲外は起動時停止）は履歴 K 線の経路に無い。
  本件は構成の意味を変えないことを決定としたため、値域の追加は射程外（未起票。必要なら別 issue）。
