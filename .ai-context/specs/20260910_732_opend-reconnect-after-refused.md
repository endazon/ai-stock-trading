---
title: OpenD 接続失敗後に接続オブジェクトを作り直し、入れ直さずに再接続できるようにする
type: spec
status: draft
related_ids: [FR-05, FR-11, UC-01, UC-02, ADR-0002, IADR-0016, IADR-0153, IADR-0211, IADR-0327]
author: endazon (with Claude Code)
created: 2026-09-10
updated: 2026-09-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# 仕様書: OpenD 接続失敗後の接続オブジェクト再生成（#732）

## 起点

- issue #732（`fix(FR-11,ADR-0002)`）。**OpenD が落ちている間に order-execution-service が起動すると、
  OpenD が復旧しても二度と接続できない。** `kubectl rollout restart` で入れ直すまで発注経路が死ぬ。
- 起点となる計画書
  - FR-05（moomoo 証券へ発注し、注文状態を追跡できる）: 発注経路そのもの。
  - FR-11（収集・判断・発注・通知のイベントを時系列ログとして記録し、後から監査できる）:
    「固着していた」ことを後から区別できるログを残す部分。
  - UC-01 / UC-02（発注・注文追跡）。
  - ADR-0002（ブローカ選択＝moomoo / OpenD 経由）。
- 関連する実装 ADR: IADR-0016（SIMULATE 限定）・IADR-0211（OpenD 不達は見送りにして待ち行列を作らない）・
  IADR-0153（`GetAccountTypeAsync` の再照会。**差し替え口が無い**という残余リスクの記述を持つ）。

## 実測（2026-09-10・issue #732 より）

起動時（OpenD 停止中）に 1 度だけ次が出る。

```
[11:51:57 ERR] OpenD 接続失敗 errCode=25769803776 desc=System.Net.Sockets.SocketException (111): Connection refused
  at Moomoo.OpenApi.MMAPI_Conn.OnTcpConnect(IAsyncResult ar)
```

以後、OpenD が復旧して LISTEN していても照会のたびに次を繰り返す。

```
[13:52:42 INF] OpenD へ接続します opend:11111 encrypt=True
[13:52:57 WRN] moomoo 建玉照会に失敗したため不明（null）を返します。
  BrokerUnavailableException: OpenD への接続を確立できませんでした（未発注）
  ---> System.TimeoutException: The operation has timed out.
```

| 検査 | 結果 |
| --- | --- |
| クライアント側 `/proc/net/tcp` ＋ `tcp6` を 200 秒サンプリング（宛先 11111） | **SYN_SENT すら 1 度も現れない**（観測機構が生きていることは全ソケット 18 行で確認） |
| OpenD 側 `/proc/net/tcp` を 100 秒サンプリング | 11111 は LISTEN のみ。確立された接続なし |
| 別 Pod からの TCP 接続（`/dev/tcp/opend/11111`） | **OK**（TCP・DNS ともに正常） |
| order-execution-service だけを入れ直す | **即座に回復**（`OpenD 接続確立 connID=...`） |

**結論**: `MMAPI_Conn` は最初の `Connection refused` 以降、`InitConnect` を呼んでも TCP を張り直さない。
`InitConnect` は `true` を返すため、アプリ側（`EnsureConnectedAsync`）は異常と判定できず、
`_connectTcs` を `ReplyTimeoutSeconds`（既定 15 秒）待って落ちる状態に固着する。

**本作業では実 OpenD を使った再現・検証は行わない**（稼働クラスタへは触らない。コードとテストのみ）。
SDK 内部の固着そのものは外から直せないため、**固着した接続オブジェクトを捨てて作り直す**方針を採る。

## 設計

### 1. SDK の接続オブジェクトに差し替え口を設ける（`IMoomooTradeConnection` / `IMoomooTradeConnectionFactory`）

`MMApiMoomooTradeClient` は `MMAPI_Trd` を `readonly` フィールドで直接生成しており、
**作り直すことも、テストで差し替えることもできない**（IADR-0153 が残余リスクとして記録済み）。

`Moomoo.OpenApi.MMAPI_Trd` は sealed ではないが**メソッドが 1 つも virtual でない**（リフレクションで実測）ため
継承では差し替えられない。よって**薄いシーム**を挟む。

- `IMoomooTradeConnection`: `MMApiMoomooTradeClient` が実際に使うメンバだけを持つ
  （`SetClientInfo` / `SetConnCallback` / `SetTrdCallback` / `SetRsaPrivateKey` / `InitConnect` / `Close` /
  `NextPacketId` / `GetAccList` / `PlaceOrder` / `ModifyOrder` / `GetOrderList` / `GetHistoryOrderList` /
  `GetPositionList` / `Dispose`）。**SDK 非依存のポートではない**——protobuf 型はそのまま通す。
  SDK 非依存の境界は既存の `IMoomooTradeClient` が持っており、二重に作らない。
- `IMoomooTradeConnectionFactory.Create()`: 本番実装は `MMApiTradeConnectionFactory`（`new MMAPI_Trd()` を包む）。
- `MMApiMoomooTradeClient` のコンストラクタに**任意引数**として差す（既定 = 本番実装）。
  `Program.cs` の登録は変更しない。

### 2. 接続試行が失敗したら、次の `InitConnect` の前に接続オブジェクトを作り直す

```
_connection      : 現在の接続オブジェクト（作り直しで差し替わる）
_connectionStale : 直前の接続試行が失敗した／切断された＝作り直しが要る
```

`EnsureConnectedAsync`（`_connectGate` の内側）:

1. `_connectionStale` なら `RecreateConnection()`（旧 `Close()` → `Dispose()` → 新規生成 →
   `SetClientInfo` / `SetConnCallback` / `SetTrdCallback` / RSA 鍵の再適用）。
2. `_connectTcs` を張り直して `InitConnect`。
3. `finally` で **`_connected` が立っていなければ `_connectionStale = true`**、`_connectTcs = null`。
   `InitConnect` が `false` を返した経路（`BrokerUnavailableException` を直接投げるため
   `catch when` フィルタを通らない）も、キャンセルも、これで一様に拾う。

`OnDisconnect` も `_connectionStale = true` を立てる（issue の方針 2）。
一度つながった後に OpenD が落ちた場合も、同じ固着に入り得るためである。

RSA 秘密鍵は**コンストラクタで 1 度だけ読んで文字列で保持**し、作り直しのたびに再適用する
（作り直しのたびにファイルを読み直さない。鍵の内容はログに出さない）。

### 3. 固着を区別できるログを足す（FR-11）

作り直しの直前に **`Warning`** で 1 行出す。

```
OpenD 接続オブジェクトを作り直しました（直前の接続試行が失敗／切断されたため）。opend:11111 通算作り直し=1
```

現状は「タイムアウト」としか出ず、切り分けに 200 秒のソケットサンプリングを要した。この 1 行があれば
「作り直しても繋がらない（＝OpenD 側が本当に落ちている）」と「作り直しに入っていない（＝別の欠陥）」を
ログだけで区別できる。**秘匿情報（RSA 鍵・口座番号）は出さない**（ホスト・ポート・回数のみ）。

### 4. fail-safe の維持

OpenD が本当に不達の間は**従来どおり `BrokerUnavailableException` を投げ続ける**
（IADR-0211 決定＝見送り・待ち行列を作らない）。作り直しは**接続確立の前段**でのみ起き、
発注そのものの再試行は 1 度も増やさない。`_connected` が立つのは
`InitConnect` の完了通知 ＋ `GetAccList` の成功の**両方**が揃ったときだけであり、これは従来と同じである。

### 採らなかった案

| 案 | 却下理由 |
| --- | --- |
| `InitConnect` の戻り値だけで判定する | 実測で `true` を返しているため判定材料にならない（issue の実測） |
| コールバックの `MMAPI_Conn client` 引数で世代を突き合わせ、古い接続からのコールバックを捨てる | **SDK が `client` に何を渡すかを実 OpenD 無しに確認できない**。取り違えると正当なコールバックを捨てて**恒久的に接続不能**になり、いま直そうとしている事故より悪い。古いコールバックが新しい `_connectTcs` を完了させても、続く `GetAccList` が新しい（まだ未接続の）オブジェクトで失敗し `BrokerUnavailableException` へ倒れる＝**収束し fail-safe を破らない**ので採らない（残余リスクへ記載） |
| プロセスを落として k8s に再起動させる | 発注実行中のプロセスを落とす副作用が大きい。OpenD は有人認証が要るため落ちている時間が長く、再起動ループになる |
| `IMoomooTradeClient` 自体を作り直す（DI を singleton から factory へ） | 発注時に控えた市場キャッシュ（`_orderMarket`）を巻き添えで失う。接続だけを作り直せば足りる |

## 走査した母集合（`.claude/rules/traceability.md` 規則 1〜10）

追跡下の全ファイルを軸ごとに走査（`.git` / `node_modules` を除外・拡張子で絞らない）。

| 軸 | 検索語 | ヒット |
| --- | --- | --- |
| 1 | `MMAPI_Trd` / `MMAPI_Qot` / `MMAPI_Conn` / `MMAPI.Init` | 4 ファイル（コード 2・記録 2） |
| 2 | `InitConnect` | 9 ファイル |
| 3 | `EnsureConnectedAsync` | 8 ファイル |
| 4 | `_connectTcs` | 2 ファイル（コード 2） |
| 5 | `差し替え口`（規則 10: 本変更で新たに誤りになる自分の記述） | 1 ファイル |

| 箇所 | 扱い |
| --- | --- |
| `backend/Services/OrderExecutionService/Infrastructure/ExternalServices/MMApiMoomooTradeClient.cs` | **変更**（本件の実装点） |
| `backend/Services/OrderExecutionService/Infrastructure/ExternalServices/IMoomooTradeConnection.cs` | **新規**（シーム＋本番実装＋ファクトリ） |
| `backend/Services/OrderExecutionService/Tests/Infrastructure/ExternalServices/MMApiMoomooTradeClientReconnectTests.cs` | **新規**（再接続の陽性・陰性） |
| `.ai-context/adr/IADR-0327_*` ＋ `.ai-context/adr/README.md` | **新規／索引追記**（内部設計の決定）。🔴 **当初は `IADR-0326` で起草したが、[#737](https://github.com/endazon/ai-stock-trading/pull/737)（BFF の 401→502 写像）が先に develop へ入って同番号を確保したため、先着尊重で `IADR-0327` へ改番した**（本リポは欠番を許し、後発はその時点の新たな最大番号＋1 へ進む。IADR-0280）。**プッシュ済みコミット 2 件の件名は `IADR-0326` を名乗ったまま残る**——force push 禁止のため遡及修正できない |
| `.ai-context/adr/IADR-0153_*`（`差し替え口が無い`）＋索引行 | **追記**（規則 10。`［2026-09-11 追記 / #732］`。**結論〔再照会の挙動は単体テストで固定できていない〕は変えない**——シームはできたが、そのテストは本作業では書いていない） |
| 🔴 `backend/Services/BacktestService/Infrastructure/ExternalServices/MMApiMoomooHistoryKLineClient.cs` | **対象外（同型の欠陥が残る）**: `MMAPI_Qot` を直接生成し、同じ `_connectTcs` 待ちで**同じ固着に入り得る**。ただし #732 の射程は発注経路であり、バックテストの相場取得は発注しない（fail-safe の重みが違う）。**同型として別 issue で追う**。本 PR では触らない |
| `backend/Services/BacktestService/Program.cs`（`EnsureConnectedAsync` の語） | **据え置き**: 上記クライアントの呼び出し側であり、本変更の対象ではない |
| `backend/Shared/AiStockTrading.Shared.Contracts/Ports/BrokerUnavailableException.cs` | **据え置き**: 分類の意味は変えない（接続前の失敗は本変更でも `BrokerUnavailable` のまま） |
| `backend/Shared/AiStockTrading.Shared.Contracts.Tests/ProtectiveStopEventPayloadTests.cs` | **据え置き**: コメント中の語に当たっただけで、接続の張り直しとは無関係 |
| `deploy/opend/k8s/rsa-secret.example.yaml` | **据え置き**: OpenD 側の鍵配置手順。接続の張り直しとは無関係 |
| `.ai-context/specs/20260715_13_*` / `20260805_342_*` / `20260828_331_*` | **除外**: 凍結記録（point-in-time） |
| `.ai-context/adr/IADR-0211_*` / `IADR-0157_*` | **据え置き**: 決定（見送り／SDK 隔離）は本変更で変わらない |

## 受け入れ基準

- [ ] 接続試行が失敗した後、次の `EnsureConnectedAsync` が**新しい接続オブジェクト**を生成し、
      フェイクの接続完了で**接続に成功する**（＝入れ直さずに回復する）
- [ ] 陰性対照: 接続が失敗し続ける間は `BrokerUnavailableException` を投げ続け、
      **無限ループにならず**、発注は 1 件も出ない
- [ ] 作り直しを区別できるログ（`Warning` 1 行）が出る。秘匿情報を含まない
- [ ] 既存テストが緑のまま（`dotnet test backend/backend.slnx`）
- [ ] 変異検査: 修正を戻すと陽性テストが落ちる

## テスト方針

xUnit v3 ＋ AwesomeAssertions。`IMoomooTradeConnectionFactory` にフェイクを差す（実 OpenD 不使用）。

| 種別 | 内容 |
| --- | --- |
| 陽性 | 1 回目の `InitConnect` はコールバックを返さない（＝タイムアウト）→ `BrokerUnavailableException`。2 回目は**別インスタンス**が生成され、接続完了通知 ＋ 口座一覧応答（SIMULATE 口座）で**成功**する |
| 陰性 | コールバックを一切返さないフェイクで 3 回連続呼び出し。毎回 `BrokerUnavailableException` で、**呼び出し回数分だけ**作り直しが起き、ハングしない |
| ログ | 2 回目の試行で「作り直しました」の `Warning` が 1 行出る |
| 発注 | 接続できない間に発注を要求しても `BrokerUnavailableException` で、フェイクへ発注要求が 1 件も届かない |

`ReplyTimeout` はテストで 200ms 程度へ縮める（`MoomooBrokerOptions.ReplyTimeout` は `init` で差せる）。

## 計画書との差異

- 差異: なし。FR-05 / FR-11 の範囲内の欠陥修正であり、計画の変更を要さない。

## 未決事項・残余リスク

- 🔴 **実 OpenD での再現・検証は本作業では行っていない**（稼働クラスタへ触らない制約）。
  SDK が「作り直せば TCP を張る」ことは、**入れ直しで即回復した実測**（プロセス起動＝新しい
  `MMAPI_Trd` で繋がった）から導いた推論である。稼働環境での確認は次回の配備で行う。
- 古い接続オブジェクトからのコールバックが新しい試行の `_connectTcs` を完了させ得る（上記「採らなかった案」）。
  収束し fail-safe は破らないが、1 回分の無駄な試行になる。
- 進行中の送信と作り直しの競合（AI レビュー指摘・2026-09-11）。`volatile` 化・1 操作 1 インスタンス・
  「先に差し替えてから解放」で窓を狭めたが、完全な相互排他は採っていない（`await` を跨ぐロックになるため）。
  残る窓で起きるのは `TimeoutException` が `ObjectDisposedException` に替わることだけで、
  いずれも「発注送信後の不明な失敗」として従来どおりリコンサイルが守る。詳細は IADR-0327 の残余リスク。
- `MMApiMoomooHistoryKLineClient`（バックテストの相場取得）に同型の欠陥が残る（上表）。
