---
title: リスク管理の現在値の補充を閉場中は止め（閉場ごとに 1 回だけ引いて開場まで有効とする）、発注執行の口座選択と通貨近似のログを初回だけ Information にして口座 ID を伏せる（#1131・#1135）
type: spec
status: accepted
related_ids: [FR-01, FR-10, FR-11, FR-03, ADR-0031, ADR-0043, IADR-0473, IADR-0066, IADR-0380, IADR-0437, IADR-0434, IADR-0354, IADR-0373, IADR-0469]
author: claude (Claude Code)
created: 2026-10-01
updated: 2026-10-01
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0031 (決定 2〜4: Finnhub の要求量の統制)
  - planning:projects/ai-stock-trading/07_adr/ADR-0043 (決定 3: 1 日の巡回回数は開場中の巡回で数える)
---

# 閉場中の現在値の補充を止める（#1131）／口座選択と通貨近似のログを静かにし口座 ID を伏せる（#1135）

## 背景

- **#1131（FR-01・低）**: リスク管理の `QuoteRefreshService` が、引け後（2026-09-30 20:01–22:50 UTC）も Finnhub `/quote` を約 3 回/分（保有 3 銘柄 × 60 秒巡回）呼び続けた（約 510 回）。同じ時間帯の市場監視は 0 回。`QuoteRefreshService` は開場判定を持たない。
- **#1135（FR-11・低）**: 発注執行が 5 分ごと（可用性 probe の巡回）に Information で「SIMULATE 口座を選びました accId=…」（口座 ID の全桁）と「口座照会の応答が通貨を明示していないため、要求した通貨…を前提として…（近似）」を出す（一晩に各約 106 行）。

## 範囲

1. リスク管理の現在値の補充を、市場監視と同じ開場判定（共有カーネル `MarketHours`）で市場ごとに止める。閉場中に読む側（審査・手仕舞いの参照価格・実DD のサンプリング・取り込み）が価格を失わないこと。
2. 発注執行の口座選択・通貨近似のログを、プロセスで初回（または口座・前提が変わった直後）だけ Information、以後は Debug にする。口座 ID を出すログはすべて伏せる。ログ以外の振る舞いは変えない。
3. IADR-0473・試験（T-10-1960〜T-10-1974）・自己変異。

範囲外: 市場監視・取引判断・報告書の現在値の取得（いずれも本件と無関係に開場判定を持つか、要求ごとに引く）・Finnhub の日次上限・計画 ADR の改訂。

## 母集合（規則 9。origin/develop 0c4f461c）

### #1131: 閉場中も外部へ取りに行く巡回・現在値を読む側

- `git grep -n -E "GetLatestQuoteAsync|IMarketDataSource" -- backend ':!*/Tests/*'` → リスク管理で現在値を**引く**のは `QuoteRefreshService` だけ（`IMarketDataSource` の利用者はリスク管理では同サービスのみ）。
- `git grep -n -E "ICurrentPriceSource|IPortfolioStateProvider|QuoteCache" -- backend/Services/RiskManagementService ':!*/Tests/*'` → 現在値を**読む**側:

| 読む側 | 閉場中に呼ばれるか | 価格が無いときの挙動 | 是正後に価格は読めるか |
| --- | --- | --- | --- |
| `LedgerPortfolioStateProvider`（審査の含み損益・DD。`EnableMarkToMarket` 時） | 呼ばれ得る（判断は場中だけだが、手仕舞い・維持率の審査は閉場中も来る） | 含み 0 | 読める（閉場中に引いた値を開場まで有効とする） |
| `ObservedDrawdownRefreshService`（実DD のサンプリング。営業日は閉場中も巡回） | 呼ばれる | 含み 0（latch は単調なので下がらない） | 読める |
| `PositionCloseService.ResolvePrice`（手仕舞いの参照価格） | 呼ばれ得る（利用者の手仕舞い） | 平均取得単価へ倒す | 読める |
| `PositionDriftAdoptionService`（取り込みの参照価格） | 呼ばれ得る | 既存の扱い | 読める |
| `ShortSellContextSupplier`・`ShortSellingStatusService` | 呼ばれ得る | 既存の扱い | 読める |

- 開場判定の実体: `git grep -n "MarketHours\.\(IsOpen\|NextOpen\)" -- backend` → 共有カーネル `MarketHours`（市場監視の `MarketHoursSchedule`・取引判断の `MarketCalendar` はどちらも構成の注入点だけを持つ薄い包み）。**リスク管理は同じ `MarketHours` を直接引く**（新しい判定を作らない）。臨時休場の構成（`Monitor:Holidays` 等）は `deploy/` に 0 件（`git grep -n "Holidays" -- deploy`）。
- 鮮度（staleness）: `MarketData:MaxQuoteStalenessSeconds`（既定 300 秒）。`QuoteCache.GetFresh` は「取得時刻からの経過」で判定する。**ここが本件の要**: 補充を止めるだけだと、引けの 5 分後から朝まで手元の値がすべて「取得不可」になる（下の窓の表）。
- 日次要求量の見積り: `git grep -n "EvaluateDailyVolume\|EstimateDailyVolume" -- backend/Services` → リスク管理は既定の 24 時間で数えている（`Program.cs` 2 か所）。**本件で 24 時間は誤りになる**（規則 10）→ 市場監視と同じ `MarketSessions.RegularSessionMinutes(Market.UnitedStates)` へ。

### #1135: 口座 ID を出すログ・初回だけにするログ

- `git grep -n -i -E "accid|acc_id|accountid" -- backend/Services/OrderExecutionService ':!*/Tests/*'` → ログで口座 ID を出すのは `MMApiMoomooTradeClient` の 3 か所:

| 行（是正前） | ログ | 水準 | 頻度 | 是正 |
| --- | --- | --- | --- | --- |
| `OpenD 接続完了・SIMULATE 口座 accId={AccId} 種別={AccType}` | 接続のたび | Information | 接続ごと（稀） | 伏せる（水準は据え置き） |
| `SIMULATE 口座を選びました accId={AccId} accType=… trdMarketAuthList=…` | 口座一覧の照会のたび | Information | 接続時＋probe ごと（5 分） | 伏せる・初回／変化時だけ Information、以後 Debug |
| `照会した SIMULATE 口座 accId={FetchedAccId} が発注先 accId={OrderAccId} と異なる…` | 口座の食い違い | Warning | 異常時のみ | 伏せる（両方。水準は据え置き） |

- 伏せ方は既存の `MMApiMoomooTradeClient.MaskAccountId`（末尾 2 桁以外を伏せる。検証口の出力が使う）を再利用する（2 通りの伏せ方を作らない）。
- 通貨近似のログ: `git grep -n "要求した通貨" -- backend` → 1 か所（`GetAccountEquityInBaseAsync`）。
- 追随する文書（規則 9・10。誤りの側の文字列で引く）: `git grep -n -E "accId=|要求した通貨|毎回" -- docs deploy`

| 文書 | 是正前の記述 | 是正 |
| --- | --- | --- |
| `docs/operations/broker-execution-paths-runbook.md` | `OpenD 接続完了・SIMULATE 口座 accId=<数値>` | 伏せた形（`accId=****<末尾 2 桁>`）へ |
| `docs/operations/capital-baseline-seed-runbook.md` | 近似の行が出ているときは供給されている | 初回だけ情報の水準・以後はデバッグの水準である旨を足す |
| `docs/tests/FR-10_risk-controls-tests.md` T-10-621 | 「Information で毎回残り」 | 初回は Information・以後 Debug（新節を参照） |
| `deploy/helm/ai-stock-trading/README.md`（grep の型 `OpenD 接続完了・SIMULATE 口座 accId=`） | — | 前方一致のため据え置きで一致する（変更不要） |

除外: `docs/blocked-tasks.md` の `accId=724808`（ログではなく過去の実測の記録。表記は本件の外）・`docs/operations/live-trading-cutover-runbook.md` / `LiveTradingGate.cs` の旧メソッド名 `FetchSimulateAccIdAsync`（本件で誤りになるものではない）・`.ai-context/` の凍結記録（IADR-0434 の「24 時間巡回」・IADR-0437 の「既定の 24 時間のまま」。本文は書き換えず IADR-0473 で前提が変わったことを記録する）。

## 設計（IADR-0473）

### #1131: 閉場ごとに 1 回だけ引き、その値を次の開場まで有効とする

純関数 `QuoteSessionFreshness`（`Features/RiskManagement`）を 1 か所に置き、補充側と読む側が同じ規則を見る。

- **有効の起点** `FreshFrom(market, fetchedAt)` = 取得時に開場していれば取得時刻、閉場していれば**その閉場の次の開場時刻**（`MarketHours.NextOpen`。見通せなければ取得時刻＝従来どおり）。閉場中は価格が動かないため、閉場中に引いた値は「開場の瞬間に引いた値」と同じだけ新しい。
- **読む側** `CachedCurrentPriceSource`: `now - FreshFrom ≤ MaxQuoteStalenessSeconds` なら読める。閉場中は常に読め、開場後は開場から 300 秒で切れる（その前に開場後の最初の補充が上書きする）。場中に引いた値は従来どおり取得から 300 秒。
- **補充側** `QuoteRefreshService.RunOnceAsync`: 銘柄（の市場）ごとに `ShouldRefresh(market, now, 手元の取得時刻)` = 開場中、または手元に無い、または手元の値の `FreshFrom ≤ now`（＝この閉場の中で引いた値ではない）。閉場中の 2 回目以降は引かない。取得できなければ（null）次の巡回でもう一度引く。
- **開場の直後**: 巡回は従来の間隔のまま（既定 60 秒）。開場後の最初の巡回で引く（閉場中に引いた値は開場から 300 秒まで有効なので、巡回間隔が 300 秒未満なら価格が途切れない）。
- **市場ごと**: 判定は建玉の市場で行う（米国・東証の昼休みを含む）。
- `QuoteCache` に取得時刻を返す読み出し `GetEntry` を足す（追加のみ。既存の `GetFresh` は報告書・判断の `LastKnownQuoteSource` が使うので変えない）。
- 日次要求量の見積りは市場監視と同じく米国の場中 390 分で数える（閉場ごとの 1 回 × 銘柄数は見積りに入れない＝過小は 1 日あたり銘柄数ぶん）。

### #1135: 初回だけ Information、口座 ID は伏せる

- 口座選択: 前回 Information で出した「口座 ID・種別・取扱市場」の組と同じなら Debug、違えば（初回を含む）Information。
- 通貨近似: 同じ口座で近似を採ったことを報告済みなら Debug。近似でない応答（通貨の明示・反証）を受けたら報告済みを解き、次に近似へ戻ったら Information。口座が変わっても Information。
- 状態はプロセス内（`MMApiMoomooTradeClient` は singleton）。並行に呼ばれても `Interlocked.Exchange` で 1 回に寄せる（最悪でも Information が 1 行増えるだけで、黙ることはない）。

## 窓の表（規則 11。端＝引け（閉場の始まり）と寄り付き（開場））

プローブ（増える側＝閉場中の要求数／減る側＝閉場中・開場直後に価格を読めるか）:

- P1: 引け → 翌朝の寄り付き前まで 1 分ごとに巡回したときの要求数（期待: 銘柄ごとに 1）
- P2: 引けの 3 時間後に価格を読めるか（期待: 読める）
- P3: 寄り付きの 30 秒後（開場後の巡回の前）に価格を読めるか（期待: 読める）
- P4: 寄り付きから 301 秒後、補充なしで閉場中の値を読めるか（期待: 読めない＝夜の値を開場後に信じ続けない）
- P5: 場中 10:00 に引いた値を 10:05:01 に読めるか（期待: 読めない＝場中の鮮度は従来どおり）
- P6: 寄り付き後の最初の巡回で引くか（期待: 引く）

| 形 | P1 | P2 | P3 | P4 | P5 | P6 |
| --- | --- | --- | --- | --- | --- | --- |
| 後の端（引け）だけ: 閉場ごとに 1 回引くが、読む側は鮮度を取得時刻から数える（変異 M2） | ✅ 1 | ❌ | ❌ | ✅ | ✅ | ✅ |
| 前の端（寄り付き）だけ: 鮮度の起点を常に次の開場にする（変異 M3） | ❌ 0（場中の値を「この閉場の値」と読み、引けの後に引かない） | ✅ | ✅ | ✅ | ❌ | ✅ |
| 閉場中は一切引かない（読む側は採用の形。変異 M4） | ❌ 0 | ❌ | ❌ | ✅ | ✅ | ✅ |
| **両端（採用）**: 閉場ごとに 1 回引き、閉場中の値は次の開場から鮮度を数える | ✅ 1 | ✅ | ✅ | ✅ | ✅ | ✅ |
| 是正前: 閉場に関係なく引く（変異 M1） | ❌ 銘柄ごとに約 1,050 | ✅ | ✅ | ✅ | ✅ | ✅ |

各形で最初に破れる升目は試験で実測した（P1＝T-10-1961、P2〜P4＝T-10-1963、P5＝T-10-1964、P6＝T-10-1962。下の自己変異の結果）。同じ試験の中で後に続く升目（例: M2 の P3）は、値からの導出である。

## 試験

`docs/tests/FR-10_risk-controls-tests.md` の末尾に新節（T-10-1960〜T-10-1974）。T-10-621（発注執行の通貨近似のログ）も同書にあるため、#1135 の試験も同書に置く。

| ID | ファイル |
| --- | --- |
| T-10-1960〜T-10-1968 | `backend/Services/RiskManagementService/Tests/Hosted/QuoteRefreshClosedMarketTests.cs` |
| T-10-1969 | 同上（日次見積りの分数） |
| T-10-1970〜T-10-1974 | `backend/Tests/AiStockTrading.IntegrationTests/MoomooAdapterFakeOpenDIntegrationTests.cs`（偽 OpenD。`Category=Integration` を付けない既存の方針のまま既定の CI で走る） |

既存の `MarketDataWiringTests.保持期限を超えた前回値は取得不可として落とす` / `保持期限内の前回値は読み出される` は、時刻が米国の閉場中（2026-07-17 03:00 UTC＝前日 23:00 ET）だった。本件で閉場中の値は開場まで有効になるため、**場中の鮮度を確かめる試験として時刻を場中へ移す**（意図は不変）。

## 自己変異の結果

変異はスクラッチの複製から戻し、戻した後にファイルが複製と一致することを `cmp` で確かめた。

| # | 変異 | 赤になった試験 |
| --- | --- | --- |
| M1 | `ShouldRefresh` を常に true（是正前の形） | T-10-1961・1962・1966・1967・1968 |
| M2 | 読む側を取得時刻からの経過に戻す（`now - FetchedAt <= maxStaleness`） | T-10-1963・1965・1966・1968 |
| M3 | `FreshFrom` を常に次の開場にする（取得時の開場判定を消す） | T-10-1961・1964・1965・1966・既存 `保持期限を超えた前回値は取得不可として落とす` |
| M4 | 閉場中は一切引かない（`ShouldRefresh` を開場判定だけに） | T-10-1961・1962・1963・1965・1966・1967・1968 |
| M5 | `ActiveMinutesPerDay` を 1,440 | T-10-1969 |
| M6 | 口座選択のログを常に Information | T-10-1970・1971・1974 |
| M7 | 通貨近似のログを常に Information | T-10-1972・1973 |
| M8 | 近似でない応答で報告済みを解かない | T-10-1973 |
| M9 | 口座選択の行で口座 ID を伏せない | T-10-1974 |
| M10 | 食い違いの Warning で口座 ID を伏せない | T-10-1974 |
| M11 | 接続完了の行で口座 ID を伏せない | T-10-1974 |

いずれも戻した後に緑。

## 検証

コミット前に実行（ローカルは `~/.dotnet/dotnet`）。

| コマンド | 結果 |
| --- | --- |
| `dotnet build backend/backend.slnx -warnaserror` | 成功・警告 0 |
| `dotnet format <変更したプロジェクト> --verify-no-changes`（RiskManagement 本体・試験、OrderExecution、IntegrationTests、Shared.Infrastructure） | 差分なし |
| `dotnet test`（RiskManagement / OrderExecution / Shared.Infrastructure / Architecture、IntegrationTests は `--filter "Category!=Integration"`） | 2,122 / 1,419 / 343 / 199（skip 1）/ 29 件すべて成功 |
| `node scripts/scripts.test.js` | 490 / 490 成功 |
| node の検査器（trace-blocks・test-traceability・knowledge-graph --check・cross-repo-refs・plan-id-qualification・doc-links・adr-index-sync・adr-index-addendum-loss・reading-budget・observability-assets・commit-messages --range origin/develop..HEAD） | すべて OK |

## 残余リスク

- 引けの直後（最初の閉場の巡回、既定 60 秒以内）に引いた値が公式の終値（クロージング・クロス）を反映していなければ、その値が翌朝まで残る。Finnhub の `/quote` の終値の確定時刻は実測していない。
- 臨時休場（構成で足す日）はリスク管理には入らない（規則の休場日だけ）。臨時休場日は開場と読んで従来どおり巡回する（要求が増えるだけで、価格を失う向きには倒れない）。
- 寄り付きの直後は、開場後の最初の巡回まで（既定 60 秒以内）前日の終値で含み損益を数える（是正前も同じ）。
- 日次見積りは閉場ごとの 1 回を数えない（銘柄数 × 1 回/日の過小）。
- 口座選択のログは Debug へ下がるため、既定のログ水準では 2 回目以降が見えない（初回と変化は見える）。
