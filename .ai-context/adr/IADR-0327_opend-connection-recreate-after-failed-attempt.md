---
title: IADR-0327 OpenD 接続は試行が失敗するたびに接続オブジェクトを作り直し、SDK に差し替え口を設ける
type: impl-adr
status: Accepted
related_ids: [FR-05, FR-11, FR-15, UC-01, UC-02, ADR-0002, IADR-0016, IADR-0060, IADR-0153, IADR-0157, IADR-0211]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# IADR-0327: OpenD 接続は試行が失敗するたびに接続オブジェクトを作り直す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- issue: [#732](https://github.com/endazon/ai-stock-trading/issues/732)
  （OpenD 停止中に起動した order-execution が、OpenD 復旧後も再接続せず発注経路が死んだままになる）
- 仕様書: [`.ai-context/specs/20260910_732_opend-reconnect-after-refused.md`](../specs/20260910_732_opend-reconnect-after-refused.md)
- 計画: FR-05（moomoo への発注と注文状態の追跡）／FR-11（時系列ログによる監査）／ADR-0002（ブローカ選択）
- 関連 IADR: [IADR-0016](IADR-0016_safe-broker-execution.md)（SIMULATE 限定）／
  [IADR-0060](IADR-0060_opend-production-cutover-gates.md)（OpenD 本番化の前提・preflight）／
  [IADR-0153](IADR-0153_broker-account-type-supply-and-fail-closed.md)（**差し替え口が無いという残余リスク**の記録）／
  [IADR-0211](IADR-0211_opend-unavailable-forgo-without-queueing.md)（不達は見送り・待ち行列を作らない）

## 背景・課題

**OpenD が落ちている間に order-execution-service が起動すると、OpenD が復旧しても二度と接続できない。**
`kubectl rollout restart` で Pod を入れ直すまで発注経路が死ぬ（2026-09-10 実測。issue #732）。

起動時に 1 度だけ `Connection refused`（`MMAPI_Conn.OnTcpConnect`）が出て、以後は照会のたびに
`OpenD へ接続します` → 15 秒後に `BrokerUnavailableException`（`TimeoutException`）を繰り返す。
このとき **TCP ソケットは 1 本も作られていない**——クライアント側の `/proc/net/tcp` ＋ `tcp6` を 200 秒
サンプリングして `SYN_SENT` すら現れない一方、同時刻に別 Pod からは TCP で繋がる。

つまり `MMAPI_Conn` は最初の接続失敗以降、`InitConnect` を呼んでも TCP を張り直さない状態に固着する。
`InitConnect` は `true` を返すため、**アプリ側は戻り値では異常を判定できない。**

**OpenD は有人認証（SMS 検証コード）を要するため、起動順序として「OpenD が後」になるのが常態である。**
その並びでは order-execution が必ずこの固着に入る。しかも監視上は fail-safe な「ブローカへ到達できません」に
見えるため、**復旧可能な障害と区別できない。**

構造上の問題として、`MMApiMoomooTradeClient` は `MMAPI_Trd` を `readonly` フィールドで直接生成しており、
**作り直すことも、テストで差し替えることもできなかった**（IADR-0153 が残余リスクとして記録済み）。

## 検討した選択肢

| 案 | 評価 |
| --- | --- |
| A. `InitConnect` の戻り値で異常を判定する | ❌ 実測で `true` を返しており、判定材料にならない |
| B. **接続試行が失敗したら接続オブジェクトを作り直してから次の `InitConnect` へ臨む** | ✅ 入れ直し（＝新しいプロセスで新しい `MMAPI_Trd`）で即回復した実測と整合する。SDK 内部の固着を外から直せない以上、**捨てて作り直す**のが唯一の手 |
| C. コールバックの `MMAPI_Conn client` 引数で世代を突き合わせ、古い接続からのコールバックを捨てる | ❌ **SDK が `client` に何を渡すかを実 OpenD 無しに確認できない。** 取り違えると正当なコールバックを捨てて恒久的に接続不能になり、いま直そうとしている事故より悪い |
| D. 接続不能を検知したらプロセスを落として k8s に再起動させる | ❌ 発注実行中のプロセスを落とす副作用が大きい。OpenD の停止時間は有人認証の都合で長く、再起動ループになる |
| E. `IMoomooTradeClient` 自体を DI で作り直す（singleton をやめる） | ❌ 発注時に控えた市場キャッシュ（`_orderMarket`）を巻き添えで失う。接続だけ作り直せば足りる |

## 決定

### 1. SDK の接続オブジェクトに薄いシームを挟む（`IMoomooTradeConnection` / `IMoomooTradeConnectionFactory`）

`Moomoo.OpenApi.MMAPI_Trd` は sealed ではないが**メソッドが 1 つも virtual でない**（リフレクションで実測）ため、
継承では差し替えられない。よって `MMApiMoomooTradeClient` が実際に使うメンバだけを写したインターフェースを挟み、
本番実装 `MMApiTradeConnectionFactory` が `new MMAPI_Trd()` を包む。

🔴 **これは SDK 非依存のポートではない**——protobuf 型はそのまま通す。SDK 非依存の境界は既存の
`IMoomooTradeClient` が持っており、**二重に作らない**。面は使っているメンバだけに限る
（使っていない SDK メソッドを「将来のために」足さない）。

コンストラクタ引数は**任意**（既定＝本番実装）であり、`Program.cs` の DI 登録は 1 行も変えていない。

### 2. 接続試行が失敗したら、次の `InitConnect` の前に接続オブジェクトを作り直す

`EnsureConnectedAsync` は `_connectGate` の内側で、`_connectionStale` が立っていれば
旧オブジェクトを `Close()` → `Dispose()` し、新しく生成して配線し直してから `InitConnect` する。

`_connectionStale` は **`finally` で「`_connected` が立っていなければ立てる」**——
`InitConnect` が `false` を返した経路（`BrokerUnavailableException` を直接投げるため `catch` フィルタを
通らない）もキャンセルも、これで一様に拾う。`OnDisconnect` でも立てる（一度つながった後に OpenD が
落ちた場合も、同じ固着に入り得るため）。

RSA 秘密鍵はコンストラクタで 1 度だけ読んで内容を保持し、作り直しのたびに再適用する
（作り直しのたびにファイルを読み直すと、鍵の差し替え中に失敗する経路が増える）。

### 3. 作り直しを区別できるログを 1 行足す（FR-11）

作り直しの直後に `Warning` を 1 行出す（ホスト・ポート・通算作り直し回数のみ。**秘匿情報は出さない**）。
従来は「タイムアウト」としか出ず、切り分けに 200 秒のソケットサンプリングを要した。この 1 行があれば
「作り直しても繋がらない（＝OpenD が本当に落ちている）」と「作り直しに入っていない（＝別の欠陥）」を
ログだけで区別できる。

### 4. fail-safe は変えない

OpenD が本当に不達の間は**従来どおり `BrokerUnavailableException` を投げ続ける**（IADR-0211 の見送り）。
作り直しは接続確立の前段でのみ起き、**発注そのものの再試行は 1 度も増やさない**。`_connected` が立つのは
接続完了通知と `GetAccList` の成功が揃ったときだけであり、これは従来と同じである。
作り直しの回数は**呼び出し回数を超えない**（1 呼び出しにつき高々 1 回）。

## 理由

入れ直し（＝新しいプロセスで新しい `MMAPI_Trd`）で即座に回復した実測が、
「固着するのは接続オブジェクトであってプロセスではない」ことを示している。SDK の内部状態は外から
触れないので、**同じ効果をプロセスを落とさずに得る最小の手段が「接続オブジェクトの作り直し」**である。

シームを挟むのは作り直しの前提であり、副産物として IADR-0153 が残余リスクとして挙げていた
「実 OpenD 無しでは接続まわりを単体テストで固定できない」を解消する。

## 結果

- 良い影響
  - OpenD が後から起動する常態でも、**Pod を入れ直さずに**発注経路が復旧する。
  - 接続まわりが実 OpenD 無しで単体テストできるようになった（本 IADR と同じ PR で 4 件を固定）。
  - 固着と不達がログで区別できる。
- 悪い影響・トレードオフ
  - 接続オブジェクトの生成が SDK 直呼びから 1 段深くなる（面は 14 メンバの機械的な委譲）。
  - 古い接続オブジェクトからのコールバックが新しい試行の `_connectTcs` を完了させ得る（上の選択肢 C を採らないため）。
    その場合も続く `GetAccList` が失敗して `BrokerUnavailableException` へ倒れる＝**収束し fail-safe を破らない**が、
    1 回分の無駄な試行になる。
  - 🔴 **接続オブジェクトが可変になったことで、進行中の送信と作り直しが競合し得る**（AI レビュー指摘・2026-09-11）。
    差し替えは `_connectGate` の内側だけで起きるが、送信側はその外側で読む。窓は
    「`_connected` の速いパスを抜けた直後に `OnDisconnect` が発火し、別スレッドが作り直す」瞬間に限られる。
    **完全な相互排他は採らない**——送信は `await` を跨ぐため、ロックを操作全体に掛けると
    接続と送信が直列化し、`_connectGate` と `_sendGate` の間で待ち合わせの向きが交差する。
    代わりに窓を狭める 3 点を採った: (1) フィールドを `volatile` にして差し替えを読み手へ確実に見せる、
    (2) 1 操作の中では**ローカルへ受けてから使う**（採番と送信が別インスタンスへ跨がらない）、
    (3) **先に新しい接続を見せてから**古い方を `Close` / `Dispose` する。
    残る窓で起きるのは、既に切断されている接続への送信が `TimeoutException` ではなく
    `ObjectDisposedException` で落ちることであり、**どちらも「発注送信後の不明な失敗」**として
    従来と同じ扱い（例外を伝播し、予約とリコンサイル〔IADR-0057 / IADR-0092〕が守る）に収まる。
- フォローアップ
  - 🔴 **実 OpenD での再現・検証は本 PR では行っていない**（稼働クラスタへ触らない制約）。次回の配備で確認する。
  - `MMApiMoomooHistoryKLineClient`（バックテストの相場取得）に**同型の欠陥が残る**
    （`MMAPI_Qot` を直接生成し、同じ `_connectTcs` 待ち）。発注しない経路であり #732 の射程外のため、別 issue で追う。
    - ［2026-09-11 追記 / [#743](https://github.com/endazon/ai-stock-trading/issues/743)］
      **この追随を消化した。** 本 IADR の決定 1〜4 を相場（Qot）経路へ同型に適用し、
      `IMoomooQotConnection` / `IMoomooQotConnectionFactory`（BacktestService.Infrastructure.ExternalServices）と
      `RecreateConnection()` を置き、偽 OpenD による陽性・陰性の試験 4 件で固定した
      （仕様書 [`.ai-context/specs/20260911_743_qot-reconnect-after-refused.md`](../specs/20260911_743_qot-reconnect-after-refused.md)）。
      **新しい決定は起こしていない**——決定は本 IADR が持ち、#743 は適用先を広げただけである。
      シームの実体は発注経路と共有していない（サービス境界を跨ぐ横断参照になるため）。
      **上の「実 OpenD での検証は未実施」は相場経路でも同じく未実施である。**

## 関連

- Supersedes: なし
- Superseded by: なし
