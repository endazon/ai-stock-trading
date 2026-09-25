---
title: MMApiMoomooTradeClientReconnectTests の並列・高負荷での不安定を、応答と打ち切りを競走させない形で根から直す（#981）
type: spec
status: accepted
related_ids: [FR-05, FR-11, ADR-0002, IADR-0327, IADR-0379]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05 発注と注文状態の追跡・FR-11 時系列ログによる監査)
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# 仕様書: MMApiMoomooTradeClientReconnectTests の不安定の根本原因と是正（#981）

## 起点

- **#981**（#979 の作業報告の残余リスク）。`dotnet test backend/Services/OrderExecutionService/Tests -- xUnit.MaxParallelThreads=16`
  を CPU 負荷 6 本（4 コア）・他の並行ビルドの下で連続実行すると、
  `MMApiMoomooTradeClientReconnectTests.接続に失敗した次の試行は接続オブジェクトを作り直して接続できる` が **24 回中 8 回赤**
  （同クラスの他のメソッドも散発）。通常負荷の CI では未観測。
- 🔴 skip・無効化・再試行の追加・待ち時間を延ばすだけの修正は禁止。本番のバグなら本番を、試験の同期なら試験を直す。
- 関連: #732 / IADR-0327（接続オブジェクトの作り直し。本試験が固定する性質）、IADR-0379（壁時計どうしの競争で合否が決まる試験の判別軸）。

## 再現（是正前・実測）

| 条件 | 結果 |
| --- | --- |
| `--filter FullyQualifiedName~MMApiMoomooTradeClientReconnect -- xUnit.MaxParallelThreads=16` × 10 回、CPU を回し続ける負荷 6 本（4 コア） | 10/10 緑（自然再現せず） |
| `dotnet test backend/Services/OrderExecutionService/Tests -- xUnit.MaxParallelThreads=16`（全 854 件）× 8 回、同じ負荷 6 本＋他エージェントの並行ビルド（load average 60〜70） | 8/8 緑（自然再現せず。1 回は 9 分 13 秒かかった） |
| フェイクの**成功側の応答**（`Task.Run` で返す `OnInitConnect` / `OnReply_*`）の配送を 300 ms 遅らせる（応答待ち 200 ms を超える。スレッドプールが塞がった状態の模擬） | **作り直しの 2 件が決定的に赤**。文面 `BrokerUnavailableException : OpenD への接続を確立できませんでした（未発注）。 ---- System.TimeoutException : The operation has timed out.` |
| 同じ注入を同一プロジェクトの `MMApiMoomooTradeClientPositionCollectionTests` の照会経路 2 件へ 6000 ms（応答待ち 5 秒を超える）で入れる | **2 件とも決定的に赤**（同じ文面） |
| 固着側（`NeverCompletes`）の `InitConnect` を 4 秒遅くする（処理系が遅い日の模擬） | `接続できないままなら毎回_BrokerUnavailable_で据え置きハングしない` が赤。文面 `Expected elapsed.Elapsed to be less than 10s, but found 12s, 608ms` |

- 自然再現は取れていない（IADR-0379「理由」と同じ扱い: **再現できなかったから欠陥ではない、とは読まない**）。根拠は
  ①下の機序、②遅延注入で「応答が打ち切りに負ければ判定が変わる」ことの実測、③#981 の起票者の観測（24 回中 8 回）である。

## 根本原因（仮説ごとの判定）

### ① 壁時計の待ち（応答待ち 200 ms と、スレッドプールで配送される応答の競走）——**これが原因（試験側）**

- 試験は `ReplyTimeout = 200 ms` の 1 つのクライアントで「1 回目は失敗（固着＝応答待ちの打ち切り）→ 2 回目は回復（応答が返る）」を通す。
  2 回目は `OnInitConnect`・`GetAccList`・`GetPositionList`（US / JP の 2 回）の **4 つの応答**が、それぞれ 200 ms の打ち切りに勝つ必要がある。
- フェイクは実 SDK と同じく応答を `Task.Run`（スレッドプール）で返す。本体の待ちは `TaskCompletionSource(RunContinuationsAsynchronously)` の
  `WaitAsync(_replyTimeout)` で、応答による完了の継続も、打ち切りのタイマーのコールバックも**どちらもスレッドプールが配送する**。
  プールが塞がると、空いた瞬間には応答（まだ走っていない `Task.Run`）も 200 ms の打ち切りも**両方とも期限切れ**で、順序は保証されない
  （IADR-0379 のコンテキストが計装で確定させた機序と同じ）。**比を広げても消えない**（同じ形の照会経路の試験は 5 秒でも注入で赤）。
- IADR-0379 決定 1 の判別軸（合否を決める 2 つの時刻がどちらも壁時計か・肯定形の表明か）に当たる。検査器
  `scripts/check-wall-clock-timeout-tests.js` は `ReplyTimeout =` を語境界で見ない（IADR-0379 決定 4 の追記②）ため、素通りしていた。

### ② 接続オブジェクトの作り直しの同期 ——**原因ではない**

- 作り直し（`RecreateConnection`）は `_connectGate` の内側で同期に完了し、`factory.Create()` も同期である。`factory.Behavior` の切替は
  試験のスレッドで 2 回目の呼び出しの前に行い、`await` の前後で happens-before が成り立つ。フェイクの振る舞いは生成時に固定される。
- 旧接続からの遅れたコールバック（IADR-0327 案 C が扱う問題）は、固着側（`NeverCompletes`）がコールバックを返さないので起きない。
- 裏付け: 作り直しを止める変異（`RecreateConnection()` をコメントアウト）で、是正後の 3 件が**速く・決定的に**赤（下の「検証」）。
  作り直しの同期が揺れているなら、この変異と無関係な赤が是正後にも出るはずだが、是正後の連続実行で 0 件（下の表）。

### ③ 共有状態 ——**原因ではない**

- 各テストは自分の `FakeConnectionFactory` / `RecordingLogger` / クライアントを作る（クラス間で共有する可変状態は無い）。
- `RecordingLogger.Records`（`List<T>`）へプールのスレッドから書くのは `OnInitConnect` のログだけで、その後の `TrySetResult` /
  `TrySetException` を試験側の継続が待つため happens-before が成り立つ。`FakeTradeConnection` の採番は `_sendGate` の内側、
  `factory.Created` への追加は `_connectGate` の内側で、同時には書かれない。

### ④ 静的状態 ——**原因ではない**

- 本体の静的状態は `MMAPI.Init()` を 1 度だけ呼ぶ `InitGate` / `_apiInitialized` だけで、`lock` で守られている。フェイクは SDK の通信を使わない。

### ⑤ 陰性対照の壁時計の上限（`elapsed < 10 秒`）——**同型（IADR-0379 決定 1）。あわせて直す**

- `接続できないままなら毎回_BrokerUnavailable_で据え置きハングしない` は「200 ms × 3 の打ち切りが 10 秒未満で終わる」を表明していた。
  打ち切りの配送もスレッドプールを待つので、所要はプールの混み具合で伸びる（上限で倒れる表明＝決定 1 が危ういとする形）。上の再現表の 5 行目で赤。
- この上限が押さえたかったのは「無限ループ・指数的な再試行になっていない」ことで、**それは回数（接続オブジェクト 3 本・各 InitConnect 1 回）が
  既に押さえている**。ハングは上限なしでは「黙って固まる」ので、Guard（下）で理由つきの赤にする。

## 是正（試験だけ。本番コードは変えない）

1. **回復（応答が返る）を表明する試験に、有限の応答待ちを置かない。** `接続に失敗した次の試行は…` と `接続オブジェクトを作り直したことが…` は
   `ReplyTimeout = Timeout.InfiniteTimeSpan` とし、1 回目の失敗は**打ち切りではなく接続拒否の通知**（`FakeConnectBehavior.Refuses`＝
   `OnInitConnect(errCode=-1, "Connection refused")` を別スレッドから返す）で起こす。完了の口が 1 つずつしか無いので競走が無い。
   1 本目は拒否を返し続けるので、作り直さなければ 2 回目も拒否で赤くなる（作り直しの検出力は保つ）。
2. **打ち切りで据え置くことは、打ち切りだけが完了の口である試験で固定する。** `接続できないままなら…`（`NeverCompletes` × 3）と
   `接続できない間は発注要求が…` は `ReplyTimeout = 200 ms` のまま（応答と競走しない）。前者は「打ち切りで失敗した試行の後も作り直す」
   （接続オブジェクト 3 本）を固定し、**応答ではなく打ち切りで終わったこと**を `InnerException is TimeoutException` で観測する（IADR-0379 決定 2）。
   壁時計の上限（10 秒未満）は外し、回数の表明に寄せる（上の ⑤）。
3. **Guard（30 秒）で呼び出しを包む。** 合否の基準ではない——応答が返らなくなったとき（回復の口が壊れたとき）に黙って固まる代わりに
   `TimeoutException` で赤くするための上限（IADR-0379 決定 2）。
4. **同じ形の照会経路の試験（`MMApiMoomooTradeClientPositionCollectionTests` の 2 件）も同じく直す**（下の母集合。応答待ち 5 秒 → 無期限＋Guard）。

### 検討して採らなかった案

| 案 | 評価 |
| --- | --- |
| 応答待ちを延ばす（200 ms → 数秒） | **採らない。** 禁止事項（待ちを延ばすだけ）であり、比を広げても順序は保証されない（照会経路の 5 秒でも注入で赤） |
| 本番に `TimeProvider` を注入し、試験は手動の時計で打ち切りを進める | **採らない。** 打ち切りの配送を完全に決定的にできるが、試験の都合で本番の構造（コンストラクタ）を変える（IADR-0379 案 C の理由と同じ）。試験側だけで競走を消せるので要らない |
| 1 回目も固着（`NeverCompletes`）のまま、キャンセルで失敗させる | **採らない。** 失敗の分類が `OperationCanceledException` になり、`BrokerUnavailableException` の表明を失う。拒否の通知のほうが本番の失敗の口（`OnInitConnect` の errCode）をそのまま通る |

## 🔴 母集合（規則 9〜11。走査したファイルと除外理由）

走査に使った語（誤りの側の文字列）: `ReplyTimeout = TimeSpan.From` / `ReplyTimeoutSeconds = ` /
`Task.Run(() => _trdCallback` / `Task.Run(() => _connCallback` / `Task.Run(() => _qotCallback` / `Task.Run(reply)` /
`MMApiMoomooTradeClientReconnect`（`git grep`・`backend/**/*.cs` と追跡ファイル全件。`CHANGELOG.md` を除く）。

| ファイル | 扱い |
| --- | --- |
| `backend/Services/OrderExecutionService/Tests/Infrastructure/ExternalServices/MMApiMoomooTradeClientReconnectTests.cs` | **直す**（上の 1〜3） |
| `backend/Services/OrderExecutionService/Tests/Infrastructure/ExternalServices/MMApiMoomooTradeClientPositionCollectionTests.cs` | **直す**（同じ形・同じプロジェクト。照会経路の 2 件は応答 5 秒待ち＋`Task.Run` の応答で、注入で決定的に赤） |
| `backend/Services/OrderExecutionService/Infrastructure/ExternalServices/MoomooBrokerOptions.cs` | 直さない（本番の既定値。`ReplyTimeout` の定義であって試験ではない） |
| `backend/Services/BacktestService/Tests/Infrastructure/ExternalServices/MMApiMoomooHistoryKLineClientReconnectTests.cs` | **直さない（残余・同型）**。応答待ちは `ReplyTimeoutSeconds = 1`（**整数秒**）で、本番 `MMApiMoomooHistoryKLineClient` は `TimeSpan.FromSeconds(int)` にしか写さないため、試験側だけでは無期限を表せない（本番の構成の変更が要る）。別サービスでもあり本件の射程外。追随 issue が要る |
| `backend/Tests/AiStockTrading.IntegrationTests/MoomooAdapterFakeOpenDIntegrationTests.cs` | **直さない（残余・同型の疑い）**。応答待ち 500 ms で、応答は偽 OpenD の `Task.Run(reply)`。陰性対照（OpenD が応答しない）と同じクライアント構成を共有し、IntegrationTests（本環境で実行できない）に属する。本件の射程外。追随 issue が要る |
| `.ai-context/specs/20260910_732_*` / `20260911_743_*` / `20260911_754_*` / `20260918_827_*`・`docs/tests/FR-15_backtest-tests.md` | 直さない（確定済みの凍結記録／試験の番号と意図は変わらない。本件で試験 ID は振らない） |
| `.ai-context/adr/IADR-0327_*` / `IADR-0379_*` | 直さない（決定は変わらない。本件は IADR-0379 決定 1・2 の適用） |

- 規則 10（この変更で新たに誤りになる自分の記述）: 試験の旧コメント「応答待ちは 200ms × 3。無限ループ・指数的な再試行になっていないことを上限で押さえる」は
  上限を外したため誤りになる → 「回数で押さえる」へ改めた。1 回目の失敗の説明（「接続完了が返ってこない」）も「接続の失敗が通知される」へ改めた。
  `using System.Diagnostics;`（Stopwatch 専用）を外した。
- 規則 11（窓）: 窓は「応答の登録」から「応答の配送」まで。プローブは
  増える側＝成功側の応答の配送を遅らせる（300 ms / 6000 ms。是正後は 2000 ms / 6000 ms。応答はいずれ返るので**緑が正しい**）／
  減る側＝成功側の応答（`GetPositionList`）を返さない（**赤が正しい**。回復していない）、あわせて作り直しを止める本番の変異（**赤が正しい**）。
  形は 3 通り（前の端＝応答の登録、後の端＝応答の配送）。升目は「期待どおりか」。

  | 形 \ プローブ | 応答の配送を遅らせる（緑が正） | 応答を返さない（赤が正） | 作り直しを止める（赤が正） |
  | --- | --- | --- | --- |
  | 前の端だけ（登録から固定の予算〔200 ms / 5 秒〕で打ち切り、それを合否にする＝有限の応答待ち。**是正前**） | ✗ 赤（4 件とも決定的。実測） | ✓ 赤（4 件とも。実測） | ✓ 赤（#732 の PR で確認済み。本件では再測せず） |
  | 後の端だけ（配送を無期限に待つ。Guard なし） | ✓ 緑（下の採用形と同じ待ち方なので同じ結果） | ✗ 赤にならず固まる（実測: 120 秒で打ち切るまで終わらない） | ✓ 赤（推論: 1 本目が拒否を返し続けるので採用形と同じく赤。本件では再測せず） |
  | **両端**（配送を無期限に待ち、登録から Guard〔合否の基準ではない〕を超えたら理由つきで赤。1 回目は拒否の通知で起こす。**採用**） | ✓ 緑（実測） | ✓ 赤（30 秒の Guard で `TimeoutException`。実測） | ✓ 赤（3 件。回数の表明・拒否の再発。実測） |

## 検証

- 遅延注入・変異（是正後・`--filter` で 2 クラス 18 件）:
  - 成功側の応答を 2000 ms（作り直し）／6000 ms（照会経路）遅らせる → 18/18 緑（是正前は 4 件赤）。
  - 固着側の `InitConnect` を 4 秒遅くする → 18/18 緑（是正前は陰性対照が壁時計の上限で赤）。
  - 成功側の `GetPositionList` の応答を返さない → 4 件が Guard の `TimeoutException` で赤（固まらない）。
  - 本番の `RecreateConnection()` を止める → 作り直しの 3 件が赤（是正後も作り直しの検出力を保つ）。
- 並列・高負荷の連続実行（CPU を回し続ける負荷 6 本〔4 コア〕・`-- xUnit.MaxParallelThreads=16`・他エージェントの並行ビルドあり。develop 202fc00a へ rebase 後）:
  - `--filter "FullyQualifiedName~MMApiMoomooTradeClientReconnect|FullyQualifiedName~MMApiMoomooTradeClientPositionCollection"`（18 件）× 25 回 → **25/25 緑**。
  - `dotnet test backend/Services/OrderExecutionService/Tests`（全 867 件）× 8 回 → **8/8 緑**。
  - 是正前は同条件で自然再現せず（上の再現表）。是正の効果の根拠は回数ではなく、遅延注入で「是正前は決定的に赤・是正後は緑」を示したことにある。
- `dotnet build backend/backend.slnx`（0 警告）・`dotnet format --verify-no-changes`・`node scripts/check-wall-clock-timeout-tests.js` ほか文書検査器。

## 射程外

- 本番コードの変更（不要と判断。上記①〜④）。
- BacktestService・IntegrationTests の同型（上の母集合の「残余」2 行）。
- 検査器 `check-wall-clock-timeout-tests.js` を `ReplyTimeout` の形へ広げること（有限の応答待ちと `Task.Run` の応答の組は、陰性対照〔応答しない上流〕
  と見分けるのに表明まで読む必要があり、IADR-0379 決定 1「grep の行だけで性質を推定しない」に当たる。同型の事故が再発したら検討する）。
