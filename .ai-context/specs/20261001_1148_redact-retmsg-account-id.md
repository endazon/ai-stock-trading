---
title: OpenD の retMsg に現れる口座 ID を、例外を作る 1 か所（EnsureSucceeded）で伏せる（#1148）
type: spec
status: accepted
related_ids: [FR-11, FR-10, FR-05, IADR-0476, IADR-0473, IADR-0347, IADR-0300, IADR-0458, IADR-0117]
author: claude (Claude Code)
created: 2026-10-01
updated: 2026-10-01
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements (FR-11: 運用の観測。ログに機密を残さない)
---

# OpenD の retMsg に現れる口座 ID を伏せる（#1148）

## 背景

- #1135（IADR-0473）で、発注執行が自分で組み立てるログ（接続完了・口座選択・口座の食い違い）の口座 ID は末尾 2 桁以外を伏せた。
- 同 PR の独立監査が残余として起票した（#1148）: OpenD が返す生の `retMsg` は、`MMApiMoomooTradeClient.EnsureSucceeded` が `MoomooTradeRequestException` の例外文と `RetMsg` にそのまま載せる。これがアダプタのログ・例外の連鎖・監査イベントへ伏せずに流れる。伏せているのは検証口（probe）の出力だけだった。

## 範囲

1. `EnsureSucceeded`（`MoomooTradeRequestException` を作る唯一の本番の口）で `retMsg` の口座 ID を伏せてから例外を作る。例外文・`RetMsg` の両方が伏せた値になる。
2. 伏せる値の集合は「口座一覧（`Trd_GetAccList`・`userID=0`＝ログイン中のユーザーの全口座）で見た口座 ID」。口座一覧を一度も読めていない間（接続時の口座一覧の照会の失敗）は、検証口と同じ「6 桁以上の数字の並び」を伏せる。
3. IADR-0476・試験（T-10-2000〜T-10-2008）・自己変異。

範囲外: 市況（Qot）の応答（`MMApiMoomooKLineProbeClient`・バックテストの `MMApiMoomooHistoryKLineClient`）は口座を扱わない。検証口の最終段（`IProbeOutputRedactor`）は既存のまま。

## 母集合（規則 9。origin/develop 58730639）

引き方: `git grep -n -E "EnsureSucceeded\(|rsp\.RetMsg|\.RetMsg\b|ex\.Message|MoomooTradeRequestException\(" -- backend ':!*Tests*'` と、例外をログへ渡す口 `git grep -n -E "Log(Warning|Error|Information|Debug|Critical)\(ex" -- backend/Services/OrderExecutionService ':!*Tests*'`（48 か所）。

### 生の retMsg が入る口（源）

| 位置（是正前） | 中身 | 是正 |
| --- | --- | --- |
| `MMApiMoomooTradeClient.EnsureSucceeded`（`PlaceOrder`・`GetPositionList`・`GetOrderList` ×2・`GetHistoryOrderList` ×2・`CancelOrder`・`GetAccList`・`GetFunds`・`GetMarginRatio` の 11 か所から呼ばれる） | `MoomooTradeRequestException(op, retType, retMsg)`。例外文 `moomoo {op} が失敗しました（retType=…）: {retMsg}` と `RetMsg` | **ここで伏せる**（本件の唯一の是正点） |
| `MMApiMoomooTradeClient.QueryOrderFeeAsync`（`rsp.RetMsg`） | 検証口の結果 | 既存で `RedactAccountId(_simAccId)` 済み（変更なし） |
| `MMApiMoomooKLineProbeClient`（`rsp.RetMsg` ×2） | 市況の枠・日足の照会（口座を扱わない）。検証口の出力の最終段も通る | 対象外 |
| バックテスト `MMApiMoomooHistoryKLineClient`（例外文に `rsp.RetMsg`） | 市況の履歴 K 線（口座を扱わない） | 対象外 |
| 試験の偽クライアント（`new MoomooTradeRequestException(...)`） | 試験だけ | 対象外 |

本番で `MoomooTradeRequestException` を作るのは `EnsureSucceeded` だけ（`git grep -n "new MoomooTradeRequestException" -- backend ':!*Tests*'` → 1 件）。

### 生の retMsg が流れる先（ログ・例外・通知・監査イベント・HTTP 応答）

| 流れる先 | 位置 | 是正前 | 是正後 |
| --- | --- | --- | --- |
| ログ（Warning・本文に `retMsg=` と例外） | `MoomooBrokerAdapter.PlaceWithRejectionDetailAsync`（確認できた拒否） | 全桁 | 伏せた値（源で伏せる） |
| 例外文（`BrokerDispatchIndeterminateException` の本文に `ex.Message`）とログ（Error・例外） | 同（届いたか不明） | 全桁 | 伏せた値 |
| 監査イベント `AlternativeProtectiveStopAttempted.RejectReasonMessage` と S3 のログ（`OrderApprovedHandler`） | アダプタの戻り値 `ex.RetMsg` | 全桁 | 伏せた値（理由の文は残る） |
| 監査イベント（見送り・拒否の理由 `ex.Message`） | `OrderExecutionAppService`（届いたか不明の経路の `rejectReasonMessage: ex.Message`） | 全桁 | 伏せた値 |
| ログ（Warning・例外） | `MoomooBrokerAdapter.GetOrderAsync`・`QueryPositionsAsync`・`GetAccountStateAsync` | 全桁 | 伏せた値 |
| ログ（Error・例外） | `BrokerAvailabilityProbeService`（巡回の失敗） | 全桁 | 伏せた値 |
| 例外の伝播（`CancelOrderAsync` 等の呼び手が受けてログへ出す） | 例外をログへ渡す 48 か所 | 全桁 | 伏せた値（例外そのものが伏せた値を持つため、受け手を 1 か所ずつ直さない） |
| 判定に使う箇所 | `MoomooPositionQueryClassifier`（`RetMsg` を頻度制限の語で引く。`frequen|times per|频率|頻度`） | — | 語は数字を含まないので分類は不変（T-10-2003 で固定） |
| 判定に使う箇所 | `MoomooTradeRequestException.IsConfirmedFailure`（`RetType` だけ） | — | 不変 |
| HTTP 応答・通知 | `git grep -n -E "RetMsg|RejectReasonMessage" -- ':(glob)backend/Services/*/Features/**' ':(glob)backend/Services/*/Hosted/**' ':(glob)backend/Services/*/Api/**' ':(glob)backend/Services/NotificationService/**' ':!*Tests*'` → 発注執行の `OrderExecutionAppService`（監査イベントへ渡す）と検証口（既存で伏せる）だけ。通知・HTTP 応答へ `retMsg` を直接載せる口は無い | — | 監査イベント経由のものは上で伏せた値になる（監査サービスの `AuditEntryFactory` は受け取った文を要約・payload に載せる） |

**作る側で伏せる理由**: 流れる先は 48 か所以上あり、受け手を 1 か所ずつ直すと次に足した受け手が漏れる。例外文・`RetMsg` を判定に使う箇所は上の 2 つだけで、どちらも口座 ID の数字に依存しない。

### 追随する文書（規則 9・10。誤りの側の文字列で引く）

`git grep -n -i -E "retMsg" -- docs deploy`:

| 文書 | 是正前 | 是正 |
| --- | --- | --- |
| `docs/operations/broker-execution-paths-runbook.md`（`moomoo 発注を拒否されました ... retMsg=...` の行） | 「`retMsg` が理由」（伏せる旨なし） | 口座 ID は伏せて出る旨を足す |
| `docs/data/audit-events.md`（S3 の試行の記録） | 拒否理由（`retType` / `retMsg`）を残す | 口座 ID は伏せて残る旨を足す |
| `docs/tests/FR-10_risk-controls-tests.md` | — | 新節（T-10-2000〜T-10-2008） |

除外: `docs/api/events-and-ports.md`・`docs/functional/FR-10_risk-controls.md`・`docs/tests/FR-10_risk-controls-tests.md` T-10-360（「拒否理由を残す」は伏せても真）、検証口の 2 本の Runbook（検証口は従来から伏せている）。`.ai-context/` の凍結記録（IADR-0347・IADR-0473 の残余の行）は書き換えず、IADR-0476 で解消を記録する。

### 規則 10（この変更で新たに誤りになる自分の記述）

- `IMoomooTradeClient.cs` の `RetMsg` の要約「ブローカーが返した拒否理由の原文」→ 原文ではなくなる（口座 ID を伏せた文）。直す。
- `MMApiMoomooTradeClient` の `IProbeOutputRedactor.Redact` の注記「EnsureSucceeded の例外文は生の retMsg を含む」→ 誤りになる。直す。
- `MMApiMoomooTradeClient` の `EnsureSucceeded` の注記「従来のメッセージ文字列は不変」→ 口座 ID の部分だけ変わる。直す。
- IADR-0473 の残余（#1148 は射程外）→ 凍結記録のため本文は書き換えない。IADR-0476 が解消を記録する。

## 規則 11（窓）

本件は時間差の窓を扱う是正ではない（伏せるか否かは「口座 ID の集合を知っているか」で決まり、時刻の端を突き合わせない）。ただし「口座一覧を読む前」と「読んだ後」の 2 つの局面があるため、両方を試験で固定する（読む前＝T-10-2004、読んだ後＝T-10-2000〜T-10-2003・T-10-2005）。

## 設計（IADR-0476）

- `MMApiMoomooTradeClient` に「口座一覧で見た口座 ID の集合」を持つ（減らさない。口座一覧を読むたびに足す）。口座一覧は接続時と可用性の巡回（既定 5 分）ごとに読まれる。
- `EnsureSucceeded` を instance にし、`RedactRetMsg(retMsg, 集合)` を通してから例外を作る。
  - 集合が空でなければ、集合の各 ID（大きい順＝桁の多い順）を既存の `RedactAccountId`（末尾 2 桁以外を伏せる）で置き換える。注文 ID・日付など他の数字は残す（拒否理由の読みやすさ）。
  - 集合が空（口座一覧を読めていない）なら、検証口と同じ `OrderFeeProbeCommand.MaskLongDigitRuns`（6 桁以上の数字の並び）で伏せる。2 通り目の伏せ方を作らない。
- `MoomooTradeRequestException` の型・`RetType`・分類は変えない。

## 試験

`docs/tests/FR-10_risk-controls-tests.md` の末尾に新節（T-10-2000〜T-10-2008）。偽 OpenD（`backend/Tests/AiStockTrading.IntegrationTests/MoomooAdapterFakeOpenDIntegrationTests.cs`）に「操作ごとに非成功の応答を返す」口を足し、実物の `MMApiMoomooTradeClient` と `MoomooBrokerAdapter` を通す。純関数は `backend/Services/OrderExecutionService/Tests/Infrastructure/ExternalServices/MoomooRetMsgRedactionTests.cs`（T-10-2006）。

| ID | 内容 |
| --- | --- |
| T-10-2000 | 確認できた拒否（-1）: アダプタのログ（本文・例外）と戻り値に全桁が出ない。伏せた末尾 2 桁と理由の文は残る |
| T-10-2001 | 届いたか不明（-100）: 伝播する例外の連鎖とログに全桁が出ない |
| T-10-2002 | S3 の拒否: 監査へ運ぶ `RejectReasonMessage` に全桁が出ない（理由の文は残る） |
| T-10-2003 | 建玉照会の失敗: ログに全桁が出ず、頻度制限の分類は変わらない |
| T-10-2004 | 口座一覧の照会の失敗（口座が未確定）: 例外の連鎖に全桁（SIMULATE・実弾）が出ない |
| T-10-2005 | 取消・口座照会の失敗: 呼び手へ伝播する例外・ログに全桁が出ない（実弾口座の ID を含む） |
| T-10-2006 | 純関数: 既知の集合があれば集合の ID だけ伏せ、他の数字は残す。集合が空なら 6 桁以上の並びを伏せる |
| T-10-2007 | 偽 OpenD のすべての非成功の経路で、例外文・`RetMsg` に全桁が出ない（否定形の総当たり） |
| T-10-2008 | 口座が入れ替わっても前に見た口座 ID を伏せ続ける（集合を減らさない） |

## 自己変異の結果

変異はスクラッチの専用ディレクトリへ `cp` で退避し、変異を当てて試験を走らせ、`cp` で戻した後に `cmp` で一致を確かめた（7 個すべて一致）。試験は結合（`MoomooAdapterFakeOpenD`）と発注執行の単体（`MoomooRetMsgRedaction`・`OrderFeeProbe`・`PositionQuery`・`MoomooBrokerAdapter`・`MoomooPlaceOrderRetType`）を走らせた。

| # | 変異 | 赤になった試験 |
| --- | --- | --- |
| M1 | `EnsureSucceeded` が生の retMsg のまま例外を作る（是正前） | T-10-2000・2001・2002・2003・2004・2005・2007（6 経路すべて）・2008 |
| M2 | 口座一覧を読めていないときに伏せない（パターンの代替を外す） | T-10-2004・2006 |
| M3 | 口座一覧の口座 ID を覚えない（常にパターンへ落ちる） | T-10-2005（注文 ID まで伏せる） |
| M4 | 大きい順に並べない（短い ID を先に伏せる） | T-10-2006 |
| M5 | 覚えるのを SIMULATE 口座だけにする（実弾口座を伏せない） | T-10-2000・2001・2002・2003・2005・2007（6 経路すべて） |
| M6 | 常にパターン（6 桁以上）で伏せる | T-10-2005・2006（注文 ID まで伏せる） |
| M7 | 口座一覧を読むたびに集合を置き換える（減らす） | T-10-2008 |

いずれも戻した後に緑。

## 検証

コミット前に実行（ローカルは `dotnet` の実体をホームのインストール先から呼んだ）。

| コマンド | 結果 |
| --- | --- |
| `dotnet build backend/backend.slnx -warnaserror` | 成功・警告 0 |
| `dotnet format <変更したプロジェクト> --verify-no-changes`（OrderExecution 本体・試験、IntegrationTests） | 差分なし |
| `dotnet test`（OrderExecution / Architecture、IntegrationTests は `--filter "Category!=Integration"`） | 1,464 / 199（skip 1）/ 48 件すべて成功 |
| `node scripts/scripts.test.js` | 490 / 490 成功 |
| node の検査器（trace-blocks・test-traceability・knowledge-graph --check・cross-repo-refs・plan-id-qualification・doc-links・adr-index-sync・adr-index-addendum-loss・reading-budget・commit-messages --range origin/develop..HEAD） | すべて OK |

## 残余リスク

- 口座一覧に出ない口座の ID（別ログインの口座など）が retMsg に現れれば伏せない（集合があるときはパターンを掛けないため）。OpenD は接続中のログインの口座しか扱わない。
- 口座一覧を読めていない間は 6 桁以上の数字の並びをすべて伏せる（5 桁以下の口座 ID は伏せない。検証口と同じ）。
- 口座 ID を区切り文字入りなど別の表記で返されれば一致しない。実機で口座 ID を含む retMsg は未実測（偽 OpenD の文言は模したもの）。
- 監査台帳の S3 の拒否理由は、口座 ID の部分が `****<末尾 2 桁>` になる（既存の記録は書き換えない）。

［2026-10-01 追記 / #1148］独立監査（GO・🟡2・🟢2）の是正。母集合表の「検証口（`QueryOrderFeeAsync`）＝既存で伏せ済み（変更なし）」は実弾の口座については不正確だった（発注口座だけを伏せていた）。検証口の `retMsg` も `RedactRetMsg`（既知の全口座＋発注口座）へ通し、結合試験「照会口の結果の retMsg は実弾口座の ID も伏せる」を足した。並びの試験に文字列順と数値順で結果が変わる組（724808・1724808）を足した。部分一致で注文 ID 等の一部が伏せられる点は IADR-0476 の残余へ記録した。変異: 検証口を発注口座だけに戻す・並びを辞書順にする、の 2 個とも赤。
