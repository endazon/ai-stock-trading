---
title: 応答待ちの打ち切りと成功応答が競走する試験の残り（BacktestService の再接続・IntegrationTests の偽 OpenD）を、完了の口を 1 つにする形で直す（#988）
type: spec
status: accepted
related_ids: [FR-15, FR-05, FR-10, ADR-0002, ADR-0023, IADR-0327, IADR-0379, IADR-0421]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-15 バックテスト・FR-05 発注と注文状態の追跡・FR-10 リスク統制)
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md
---

# 仕様書: 応答待ちの打ち切りと成功応答が競走する試験の残り（#988）

## 起点

- **#988**（#981 / PR #987 の作業仕様書 `20260925_981_moomoo-reconnect-test-sync` の母集合で「残余」とした 2 行と、検査器の見送り 1 項）。
  1. `backend/Services/BacktestService/Tests/Infrastructure/ExternalServices/MMApiMoomooHistoryKLineClientReconnectTests.cs`
     —— 応答待ちが整数秒の `ReplyTimeoutSeconds = 1`。試験側だけでは無期限を表せない。
  2. `backend/Tests/AiStockTrading.IntegrationTests/MoomooAdapterFakeOpenDIntegrationTests.cs`
     —— 応答待ち 500 ms と偽 OpenD の `Task.Run(reply)`。
  3. `scripts/check-wall-clock-timeout-tests.js` を `ReplyTimeout` の形へ広げるか。
- 🔴 skip・再試行・待ち時間の延長だけの修正は禁止（#988 本文）。手本は #987（成功経路は完了の口を 1 つに、打ち切りの性質は打ち切りしか口の無い陰性対照で確かめる）。
- 関連: IADR-0379 決定 1・2（判別軸・応答しない上流と Guard）、IADR-0327（接続オブジェクトの作り直し）、本件で起こす IADR-0421。

## 🔴 母集合（規則 9〜11。着手前に引いた結果と除外理由）

走査に使った語（誤りの側の文字列）: `ReplyTimeout = TimeSpan.From` / `ReplyTimeoutSeconds = ` / `Task.Run(() => _trdCallback` /
`Task.Run(() => _connCallback` / `Task.Run(() => _qotCallback` / `Task.Run(reply)` / `_replyTimeout`（`git grep -- '*.cs'`）。
軸を変えて `MMSPI_(Qot|Trd|Conn)` を実装するファイル・`new MMApiMoomoo(Trade|HistoryKLine)Client(` の呼び出し元も引いた（規則 5）。
文書側は試験名・クラス名（`MMApiMoomooHistoryKLineClientReconnect` / `MoomooAdapterFakeOpenD` / `据え置きハングしない` / `作り直して取得できる`）で
`backend` と `CHANGELOG.md` を除く追跡ファイル全件を引いた。

| ファイル | 扱い |
| --- | --- |
| `backend/Services/BacktestService/Tests/Infrastructure/ExternalServices/MMApiMoomooHistoryKLineClientReconnectTests.cs` | **直す**（下の是正 1） |
| `backend/Services/BacktestService/Infrastructure/ExternalServices/MMApiMoomooHistoryKLineClient.cs` | **直す**（応答待ちを `TimeSpan` で与える省略可能な引数。IADR-0421） |
| `backend/Tests/AiStockTrading.IntegrationTests/MoomooAdapterFakeOpenDIntegrationTests.cs` | **直す**（下の是正 2） |
| `backend/Services/BacktestService/Infrastructure/ExternalServices/BarDataOptions.cs` | 直さない（構成キー・型・既定を変えないことが IADR-0421 の決定） |
| `backend/Services/BacktestService/Program.cs` | 直さない（DI 登録は 2 引数のまま。新しい引数は渡さない） |
| `backend/Services/OrderExecutionService/Tests/.../MMApiMoomooTradeClientReconnectTests.cs` / `...PositionCollectionTests.cs` | 直さない（#987 で是正済み。`Task.Run` の応答は無期限の応答待ちの試験と、打ち切りだけが口の陰性対照〔200 ms〕にしか無い） |
| `backend/Services/OrderExecutionService/Infrastructure/ExternalServices/MMApiMoomooTradeClient.cs` / `MoomooBrokerOptions.cs` | 直さない（本番。`ReplyTimeout` は既に `TimeSpan` で無期限を表せる） |
| `backend/Services/BacktestService/Tests/.../MMApiMoomooHistoryKLineClientMappingTests.cs` | 直さない（クライアントを接続させない。起動時検査と静的写像だけ） |
| `docs/tests/FR-15_backtest-tests.md`（T-15-95 の行）・`docs/tests/FR-10_risk-controls-tests.md`（`--filter` の記述） | 直さない（性質の記述〔1 回目の接続失敗の後に作り直して成功する／陰性対照は呼び出し回数を超えずハングしない〕とフィルタ文字列は是正後も正しい。規則 10 で読み直した） |
| `.ai-context/specs/20260911_743_*` / `20260911_754_*` / `20260919_897_*` / `20260923_899_*` / `20260918_827_*` / `20260925_981_*`・`.ai-context/adr/IADR-0379_*` / `IADR-0327_*` | 直さない（確定済みの凍結記録。決定は変わらない） |

- 規則 10（この変更で新たに誤りになる自分の記述）:
  - Backtest 試験の旧コメント「不達側は応答待ちが要るため最小の 1 秒にする。復旧側はフェイクが即座に返すため待たない」は、復旧側が無期限になったため誤りになる → 書き直した。
  - 同「応答待ちは 1 秒 × 3。無限ループ・指数的な再試行になっていないことを上限で押さえる」は上限を外したため誤り → 「回数で押さえる」へ。
    `using System.Diagnostics;`（Stopwatch 専用）を外した。1 回目の失敗の説明「接続完了が返ってこない」→「接続の失敗が通知される」。
  - 本体コンストラクタのコメント「Program.cs の登録（2 引数）は変更していない」は引数が 4 つになっても正しい（登録は 2 引数のまま）ので残した。
- 規則 11（窓）: 窓は「応答の登録」から「応答の配送」まで。プローブは
  増える側＝成功側の応答の配送を遅らせる（応答はいずれ返るので**緑が正しい**）／
  減る側＝成功側の応答を返さない（**赤が正しい**。回復していない）、あわせて作り直しを止める本番の変異と、応答待ちを無期限にする本番の変異（陰性対照が**赤になるのが正しい**）。

  | 形 \ プローブ | 応答の配送を遅らせる（緑が正） | 応答を返さない（赤が正） | 作り直しを止める（赤が正） | 本番の応答待ちを無期限にする（陰性対照が赤が正） |
  | --- | --- | --- | --- | --- |
  | 前の端だけ（登録から固定の予算〔1 秒 / 500 ms〕で打ち切り、それを合否にする＝有限の応答待ち。**是正前**） | ✗ 赤（Backtest 2 件・IT 17 件が決定的に赤。実測） | ✓ 赤（打ち切りで。推論: 是正前の形では応答が無ければ打ち切りで例外） | ✓ 赤（#743 の PR で確認済み。本件では再測せず） | ✓ 赤（推論: 陰性対照が固まる） |
  | 後の端だけ（配送を無期限に待つ。Guard なし） | ✓ 緑（採用形と同じ待ち方） | ✗ 固まる（推論。#981 で同じ形を 120 秒まで待って終わらないことを実測済み） | ✓ 赤（推論: 1 本目が拒否を返し続ける） | ✗ 陰性対照が固まる（推論） |
  | **両端**（配送を無期限に待ち、呼び出しごとの Guard〔30 秒・合否の基準ではない〕を超えたら理由つきで赤。1 回目の失敗は拒否の通知。陰性対照は打ち切りだけが口。**採用**） | ✓ 緑（Backtest 6 秒遅延・IT 6 秒遅延とも全件緑。実測） | ✓ 赤（Guard で理由つき。実測） | ✓ 赤（Backtest 3 件。実測） | ✓ 赤（Backtest 陰性対照 2 件・IT 陰性対照 2 件。実測） |

## 再現（是正前・実測）

| 条件 | 結果 |
| --- | --- |
| Backtest: フェイクの成功側の応答（`OnInitConnect` / `OnReply_RequestHistoryKL`）を 1.5 秒遅らせる（応答待ち 1 秒を超える＝スレッドプールが塞がった状態の模擬） | **回復の 2 件が決定的に赤**。文面 `System.TimeoutException : The operation has timed out.` |
| Backtest: 固着側（`NeverCompletes`）の `InitConnect` を 11 秒遅くする（処理系が遅い日の模擬） | 陰性対照 `接続できないままなら毎回失敗し据え置きハングしない` が赤。文面 `Expected elapsed.Elapsed to be less than 30s, but found 36s, 23ms` |
| IT: 偽 OpenD の応答（`OnInitConnect` と `Reply` の全経路）を 1 秒遅らせる（応答待ち 500 ms を超える） | **陰性対照 2 件を除く 17 件がすべて決定的に赤**（19 件中） |

- 自然再現は取れていない（IADR-0379「理由」・#981 と同じ扱い）。根拠は ①#981 / IADR-0379 が計装で確定させた機序、②上の遅延注入で「応答が打ち切りに負ければ判定が変わる」ことの実測である。

## 是正

### 1. BacktestService（`MMApiMoomooHistoryKLineClientReconnectTests`）

- **本番**: `MMApiMoomooHistoryKLineClient` のコンストラクタに省略可能な `TimeSpan? replyTimeout = null` を足す（IADR-0421）。
  null なら従来どおり `TimeSpan.FromSeconds(options.ReplyTimeoutSeconds)`。構成キー・型・既定・DI 登録は変えない。
- **回復の 2 件**（`接続に失敗した次の試行は…` / `接続オブジェクトを作り直したことが…`）: `replyTimeout: Timeout.InfiniteTimeSpan`、
  1 回目の失敗は `FakeConnectBehavior.Refuses`（`OnInitConnect(errCode=-1, "Connection refused")` を別スレッドから返す）で起こす。
  期待する例外は本体が拒否の通知で投げる `InvalidOperationException`。1 本目は拒否を返し続けるので、作り直さなければ 2 回目も赤（検出力を保つ）。
- **陰性対照の 2 件**: 構成の経路（`ReplyTimeoutSeconds = 1`）のまま、`NeverCompletes`（応答しない上流）で打ち切りだけを完了の口にする。
  壁時計の上限（30 秒未満）は外し、回数（接続オブジェクト 3 本・各 `InitConnect` 1 回）で押さえる。
- **Guard（30 秒）は呼び出しごとに、呼び出しへ渡すキャンセルで掛ける**（`WaitAsync(Guard)` にしない）。この試験では応答待ちの打ち切りが
  素の `TimeoutException` を投げるため（発注経路は `BrokerUnavailableException` で包む）、`WaitAsync(Guard)` だと陰性対照で
  「打ち切りで失敗した」と「固まって Guard に切られた」を型で区別できない。キャンセルなら Guard に切られたときは
  `OperationCanceledException` になり、`Assert.ThrowsAsync<TimeoutException>`（完全一致）が赤くなる＝**打ち切りで終わったことの観測**（IADR-0379 決定 2）。
  - 最初は試験全体で 1 つの Guard にしたが、固着側を 11 秒遅くするプローブで 3 回 × 11 秒が 30 秒を超えて赤になった
    （旧来の「全体 30 秒未満」と同じ上限になっていた）。呼び出しごとに改めた（実測で緑）。

### 2. IntegrationTests（`MoomooAdapterFakeOpenDIntegrationTests`）

- 応答が返る 17 件（19 件中）: `Options()` の `ReplyTimeout` を `Timeout.InfiniteTimeSpan` にし、アダプタの呼び出しを
  `.WaitAsync(Guard, ct)`（30 秒）で包む。期待値が例外でない（戻り値の表明）ので `WaitAsync` の `TimeoutException` はそのまま理由つきの赤になる。
- 陰性対照 2 件（`Refusing`＝接続完了通知を返さない偽 OpenD）: `StuckOptions()`（500 ms。応答と競走しない）を使う。
  1 件目は `thrown.InnerException` が `TimeoutException` であること（応答ではなく打ち切りで終わった）を表明に足す。
  いずれも Guard で包む（Guard の `TimeoutException` は期待する `BrokerUnavailableException` と型が違うので区別できる）。
- 🔴 **#988 は「Docker 前提のため CI の IntegrationTests でのみ走る」と書くが、この試験クラスは in-process の偽 OpenD で Docker を要さない**
  （クラス冒頭のコメントどおり `Category=Integration` を付けておらず、既定 CI の単体テストでも走る）。本環境（Docker なし）で
  `--filter "FullyQualifiedName~MoomooAdapterFakeOpenDIntegrationTests"` により**実行できた**ので、遅延注入も含めて実測した。
  同プロジェクトの Docker を要する他クラスは実行していない。

### 3. 検査器（`check-wall-clock-timeout-tests.js`）の拡張 —— **見送る**

`ReplyTimeout` の形を検出する規則を 2 通り試作し、3 つの木で当たりを数えた（`Task.Run(` を含むテストファイルのうち）。

| 規則 | #987 前の木 | develop（#987 後・本件前） | 本件の是正後 |
| --- | --- | --- | --- |
| A: 名前が `…ReplyTimeout` / `…ReplyTimeoutSeconds` の変数・プロパティへ有限の定数を代入（名前つき定数を含む） | 4 件（4 件とも真陽性） | 3 件（真 2・偽 1＝#987 で是正済みの OES） | **3 件（すべて偽陽性）** |
| B: `ReplyTimeout =` / `ReplyTimeoutSeconds =` へ有限の定数をその場で代入（語境界） | 4 件（4 件とも真陽性） | 2 件（2 件とも真陽性） | **1 件（偽陽性。Backtest の陰性対照が構成の経路を通すため）** |

- **是正後の木で的中率が 0 になる。** 危険な組は「同じクライアントが有限の応答待ちを持ち、**かつ**その試験で応答が返ることを表明する」ことで、
  **どのフェイクの振る舞いを渡したかは試験ごと**に決まる。同じファイルの陰性対照が有限の応答待ちを正当に使うため、ファイル単位・行単位の照合では区別できない
  （IADR-0379 決定 1「grep の行だけで性質を推定しない。呼び出し元と表明まで読む」）。
- 規則 B が発注経路・偽 OpenD の是正後を通すのは、有限の値を `StuckReplyTimeout` という**名前つきの定数へ移したから**にすぎない。
  合否が性質ではなく命名で決まる検査は、Backtest の陰性対照を「検査器を黙らせるために名前を付け替える」ことで緑にでき、同じ理由で新しい同型も素通りする。
- 同型の「事故」は #981 の 1 回（24 回中 8 回赤の観測）で、#988 は同じ棚卸しの残り（観測された失敗は無い）である。運用標準の「同型事故 2 回から」にも当たらない。
- 代わりに、応答を `Task.Run` で返すフェイクを持つ 4 ファイル（本リポにある moomoo のフェイクのすべて。#987 の 2 ファイル＋本件の 2 ファイル）に規律をコメントで置いた。
  追跡の issue は起こさない（新しい同型が出たら、その時点で試験の表明まで読む形の検出を検討する）。
- `scripts/` は変更しないので `scripts.test.js` へのテスト追加も無い。

### 検討して採らなかった案

| 案 | 評価 |
| --- | --- |
| 応答待ちを延ばす（1 秒 → 数秒、500 ms → 数秒） | **採らない。** 禁止事項であり、比を広げても順序は保証されない（#981 の照会経路 5 秒でも注入で赤） |
| `ReplyTimeoutSeconds` の 0 以下を無期限と読む／構成を `TimeSpan` 化する | **採らない**（IADR-0421。本番の誤設定を黙ったハングへ変え得る） |
| Backtest の 1 回目も固着（`NeverCompletes`）のまま、キャンセルで失敗させる | **採らない。** 本番の失敗の口（`OnInitConnect` の errCode）を通らず、失敗の分類が変わる。拒否の通知のほうが本番の経路をそのまま通る（#981 と同じ判断） |
| Backtest の陰性対照も新しい引数（200 ms）で速くする | **採らない。** 整数秒の構成（`ReplyTimeoutSeconds` → `TimeSpan`）を通る試験がこの 2 件だけになるため、構成の経路を残す |

## 受け入れ基準

- [x] Backtest: 成功側の応答を 6 秒遅らせても 4 件緑（是正前は 1.5 秒で 2 件赤）。
- [x] Backtest: 固着側の `InitConnect` を 11 秒遅くしても 4 件緑（是正前は陰性対照が壁時計の上限で赤）。
- [x] Backtest: 成功側の K 線応答を返さない → 回復の 2 件が Guard（`TaskCanceledException`・30 秒）で赤（固まらない）。
- [x] Backtest: 本番の `RecreateConnection()` を止める → 3 件赤（作り直しの検出力を保つ）。本番の応答待ちを無期限にする → 陰性対照 2 件が赤（型の不一致＝Guard に切られた）。
- [x] IT: 応答を 6 秒遅らせても 19 件緑（是正前は 1 秒で 17 件赤）。建玉照会の応答を返さない → 該当 1 件が Guard で赤。本番（`MMApiMoomooTradeClient`）の応答待ちを無期限にする → 陰性対照 2 件が赤。
- [x] CPU 負荷 6 本（4 コア）・`-- xUnit.MaxParallelThreads=16` で Backtest 4 件＋IT 19 件 × 10 回 → 10/10 緑。
- [x] `dotnet build backend/backend.slnx`（0 警告 0 エラー）・`dotnet test backend/Services/BacktestService/Tests`（363 件緑）・`dotnet format --verify-no-changes`。

## 射程外

- 履歴 K 線の経路の `ReplyTimeoutSeconds` の値域検査（IADR-0421 の残余）。
- 検査器の拡張（上の 3. で見送り）。
