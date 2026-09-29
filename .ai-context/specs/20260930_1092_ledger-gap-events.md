---
title: 建玉照会の失敗と LLM を呼ぶ前の見送りを監査台帳へ出し、夜間の要約で数える（#1092 段 2）
type: spec
status: accepted
related_ids: [NFR, FR-04, FR-10, FR-11, IADR-0462, IADR-0358, IADR-0374, IADR-0452, IADR-0254, IADR-0118, IADR-0458, IADR-0150, IADR-0129, IADR-0079]
author: claude (Claude Code)
created: 2026-09-30
updated: 2026-09-30
plan_refs: []
---

# 建玉照会の失敗と LLM を呼ぶ前の見送りを台帳へ出す（#1092 段 2）

## 背景

#1092 段 1（`scripts/nightly-ledger-summary.sh` と runbook。作業仕様書 `20260929_1092_nightly-ledger-summary`）は、
既存の監査台帳 `audit_svc.audit_events` だけから夜間を要約する。段 1 の調査で、次は**台帳に無い**（ログかメトリクスだけで、
Pod の再起動で消える）ことが分かった。

- LLM を呼ぶ前の見送り（日報の未確定・現在値なし・換算レートの未解決・換算レートの鮮度切れで保有なし）。
- 保有照会の失敗（取引判断の側）。
- moomoo の建玉照会の失敗（保護逆指値ガード・スナップショット・S1 の据え置きのいずれも記録を残さない）。
- 判断中の例外。

段 1 の runbook §9 は、建玉照会の失敗を `BrokerPositionsObserved` の欠けから**推定**している（Pod の停止と照会の失敗を区別できず、
ガードの 30 秒ごとの照会の失敗は表れない）。段 2 はこれを実測に置き換える。

起点は NFR（運用。無採番のメタ作業）で、FR-04（判断）・FR-10（統制）・FR-11（監査）の記録を足す。計画 ADR の新たな制約は無い。

## 前提（読んだ制約）

- IADR-0358 決定4: 既存イベント（`OrderDispatchForgone` / `TradeDecisionSkipped`）の流用は誤帰属として退けた。
  「語彙を 1 件だけ特別扱いしない。先に全種類を洗い出してから決める」とした。
- IADR-0374: 見送りの語彙（`DecisionSkipReason`）を全件洗い出した。監査台帳・Discord へ載せるかは「観測の結論を見てから」とした（射程外）。
- IADR-0452: LLM が結論を出した**後**の見送りは `TradeDecisionHeld` で台帳へ出る。**LLM を呼ぶ前の 4 地点では出さない**（同 決定1）。
- IADR-0254 決定3: 期間の集計を、プロセスごとの（再起動で消える）購読・積み上げでしない。台帳が権威である。
- IADR-0118: 建玉照会の不明（null）と空列（建玉ゼロ）を取り違えない。
- IADR-0079: イベント契約は追加のみ。新イベントは baseline へ登録する。
- IADR-0129: 発行は Wolverine。🔴 ハンドラが例外で終わると、その処理中に発行したメッセージは捨てられる（外へ出したい記録はランタイムの `MessageBus` から出す）。

## 母集合（規則 9〜11。origin/develop 3ca6f64d を grep で確認）

走査したコマンド（本番コードだけ。テスト・obj を除く）:

```bash
grep -rnE "\.(GetPositionsAsync|QueryPositionsAsync|IsOperationalAsync|GetPositionAsync|GetSignedQuantityAsync|GetWorkingEntryOrdersAsync|GetOpenPositionsAsync)\(" \
  --include=*.cs backend/Services backend/Shared | grep -v "/Tests/\|\.Tests/\|/obj/"
```

### 建玉照会・保有照会の呼び出し元（21 行）

| # | 呼び出し元 | 照会先 | 失敗の現れ方 | 扱い | 発生源（`PositionQuerySource`） |
| --- | --- | --- | --- | --- | --- |
| 1 | `ProtectiveStopGuard.RunOnceAsync`（巡回の先頭。`PositionQueryRetry` 経由を含む） | moomoo 建玉 | null → 巡回ごと据え置き | **出す** | `ProtectiveStopGuard`（失敗の種類つき） |
| 2 | `PositionQueryRetry.QueryAsync`（54・74 行） | moomoo 建玉 | 1 の内部 | 1 に含む（照会し直しの最終結果だけを 1 回の観測として数える） | — |
| 3 | `ProtectiveStopGuard.HoldUnlessPositionGoneAsync`（676 行。建玉 0 の確かめ直し） | moomoo 建玉 | null → その行を据え置き | **出さない**。巡回の中の 2 回目の照会であり、1 と同じ巡回で成功と失敗が並ぶと状態が 1 巡回の中で往復する。建玉 0 の行だけで起き、据え置きへ倒れる（穴を作らない）。1 の状態で健全さは読める | — |
| 4 | `BrokerPositionSnapshotService.PublishOnceAsync` | moomoo 建玉 | null → 観測を発行しない | **出す** | `BrokerPositionSnapshot` |
| 5 | `BrokerAvailabilityProbeService.ProbeOnceAsync`（`IsOperationalAsync` → moomoo では建玉照会） | moomoo 建玉 | false → 稼働を発行しない | **出す** | `BrokerAvailabilityProbe` |
| 6 | `SoftwareStopExecutor.TryCloseCoreAsync`（294 行。S1 の決済。スナップショットを渡されなかったときだけ照会） | moomoo 建玉 | null → Deferred | **出す**（自分で照会したときだけ。ガードから渡された回は 1 が数える） | `SoftwareStopClose` |
| 7 | `OrderExecutionAppService`（138 行。決済のゲート） | moomoo 建玉 | null・例外 → 見送り `BrokerPositionsIndeterminate` | **出す**（見送りは既に台帳に出るが、同じ見送りでも理由が照会の失敗かは読めない。状態の遷移だけ足す） | `OrderDispatch` |
| 8 | `OrderExecutionAppService.HasUnattributedPositionAsync`（586 行。S1 の武装前の確かめ） | moomoo 建玉 | null → 見送り `UnattributedPosition`（照会の失敗と実際の帰属不明が同じ理由に畳まれる） | **出す** | `OrderDispatch` |
| 9 | `ProtectiveStopDriftAdopter.ResolveTargetAsync` | moomoo 建玉 | null・例外 → 例外を投げる（Wolverine の再試行 → `<queue>_error`） | **出さない**。利用者の操作が起点の単発の経路で、失敗は例外として再試行され、使い切ると RabbitMQ の DLQ（永続）に残る。周期の照会ではない | — |
| 10 | `MoomooBrokerAdapter.QueryPositionsAsync`（266 行） | OpenD | アダプタの実装そのもの | 対象外（呼び出し元の側で数える。アダプタで数えると発生源が分からない） | — |
| 11 | `TradeDecisionAppService.GetHeldPositionSafeAsync`（648 行。プロンプト用） | リスク管理の台帳（保有） | 実結線で null・例外 → 不明 | **出す**（実結線〔`IsEnabled`〕のときだけ。NoOp は照会していない） | `TradeDecisionHoldings` |
| 12 | `TradeDecisionAppService.GetSignedHeldQuantitySafeAsync`（629 行。決済の数量の引き直し） | 同上 | 同上 | **出す** | `TradeDecisionHoldings` |
| 13 | `TradeDecisionAppService.GetWorkingEntryOrdersSafeAsync`（667 行） | リスク管理の台帳（未約定の新規建て） | 同上 | **出す** | `TradeDecisionWorkingEntries` |
| 14・15 | `GrpcHeldPositionProvider`（37・62 行） | gRPC | 11〜13 の内部 | 11〜13 に含む | — |
| 16・17 | `MarketMonitorAppService`（48 行）・`WatchlistCycleFitGuard`（29 行） | リスク管理の台帳（保有） | 市場監視の損切りライン判定・巡回の見積り | **出さない**（本件の射程外）。#1092 が挙げた 2 種（moomoo の建玉照会・取引判断の保有照会）に入らない。残余へ書く | — |
| 18・19 | `GrpcPositionStore`（34 行）・`ReportAutoGenerator.SafeOpenPositionsAsync`（609 行） | リスク管理の台帳 | 16・17 の内部／報告書は「不明」と描く | **出さない**（同上。報告書は照会の失敗を本文に描く） | — |
| 20 | `GrpcOpenPositionSource`（23 行） | gRPC | 19 の内部 | 同上 | — |
| 21 | `MMApiMoomooTradeClient`（接続の実装） | OpenD | 10 の内部 | 対象外 | — |

### LLM を呼ぶ前の見送り（`TradeDecisionAppService.DecideAsync`）

`grep -n "Skip(trigger\|SkipJudgedAsync(trigger" TradeDecisionAppService.cs` の 14 行を、LLM の呼び出し（`_orchestrator.DecideAsync`・331 行）の前後で分けた。

| 地点 | 理由 | 前後 | 台帳 |
| --- | --- | --- | --- |
| 219 | `DailyPolicyUnconfirmed` | 前 | **新事実で出す**（`DailyPolicyUnconfirmed` は営業日ごとに 1 回・構成で無効にできるので件数にならない） |
| 237 | `CurrentPriceUnavailable` | 前 | **新事実で出す** |
| 252 | `FxRateUnresolved` | 前 | **新事実で出す** |
| 291 | `FxRateStaleNoHolding` | 前 | **新事実で出す** |
| 368〜542（10 地点） | `LlmHold` ほか | 後 | 既存の `TradeDecisionHeld`（IADR-0452。価格が無い・解析不能は出ない＝残余） |

前の 4 地点は `DecisionSkipReason` の 12 値のうち LLM より前にあるものの**全部**である（語彙を 1 件だけ特別扱いしない。IADR-0358 決定4）。

### 判断中の例外（入れない。理由）

- LLM・保有照会・サイジング文脈・現在値・為替は、いずれも失敗を例外で返さず fail-safe の値（Hold・不明・残枠 0）へ倒す
  （`HttpLlmCompletionClient` の catch → Hold、`GrpcSizingContextProvider` の安全既定など）。残る例外はバグの類である。
- 2 つの経路で意味が違う。定時は銘柄ごとに catch して続ける。価格変動は例外が Wolverine の再試行（2s/10s/30s）→ `<queue>_error`（RabbitMQ の DLQ・永続）へ進む。
  価格変動の経路では**事実は既に永続の DLQ に残る**。記録するなら 1 件の失敗を再試行の回数だけ数えないように、失敗が確定した点（DLQ への移送）で数える設計が要る。
- ハンドラが例外で終わると、その処理中に発行したメッセージは捨てられる（IADR-0129）。記録にはランタイムの発行口と、例外の語彙（型名だけにする・秘密を載せない）の決定が要る。
- よって本段では入れず、残余へ書き、起票を提案する。

## 設計（IADR-0462）

### A. 建玉照会の状態の変化 `PositionQueryStatusChanged`

- 契約（Shared.Contracts）: `Source`（`PositionQuerySource`: `ProtectiveStopGuard` / `BrokerPositionSnapshot` / `BrokerAvailabilityProbe` / `SoftwareStopClose` / `OrderDispatch` / `TradeDecisionHoldings` / `TradeDecisionWorkingEntries`）・
  `Status`（`Healthy` / `Failing`）・`PreviousStatus`（`Unknown` / `Healthy` / `Failing`）・`FailureKind`（失敗の種類。分かるときだけ）・
  `FailingSince`（失敗が始まった時刻）・`FailedQueries`（その失敗の続いた照会の回数）・`OccurredAt`。
- 状態は発生源ごと・プロセスの中（`PositionQueryHealthTracker`。Shared.Infrastructure）。
  - `Unknown`（起動直後）→ 失敗: **出す**（`PreviousStatus=Unknown`）。🔴 再起動で状態が消えても、最初の失敗は必ず出る。
  - `Unknown` → 成功: **出す**（1 プロセス・1 発生源につき 1 回だけ）。前のプロセスで始まった失敗の区間を、再起動の後に閉じるためである（回復の時刻は「不明（再起動の後の最初の成功）」と読む）。
  - `Healthy` → 失敗: 出す。`Failing` → 成功: 出す（`FailingSince`・`FailedQueries` つき）。
  - `Healthy` → 成功、`Failing` → 失敗: **出さない**（平常の周期的な成功・失敗の連続を洪水させない）。失敗の回数は数えて、回復の記録に載せる。
- 発行が失敗したら状態を戻す（次の観測で出し直す）。報告は例外を投げない（照会した側の挙動を変えない）。
- 発行はランタイムの `MessageBus`（発注・S1 のハンドラが例外で終わっても記録が捨てられない）。

### B. LLM を呼ぶ前の見送り `TradeDecisionForgoneBeforeLlm`

- 契約: `EventId`・`Symbol`・`Market`・`Reason`（`DecisionForgoneBeforeLlmReason`: 4 値。名前は `DecisionSkipReason` と同じ）・`OccurredAt`・`CycleTrigger`。
- 出口は `SkipBeforeLlmAsync`（4 地点）。1 回の見送りにつき 1 件（件数を数えるため。抑止しない）。見送りの計上（`Skip`）は従来どおり 1 件。
- 発行の失敗で見送りを壊さない（`SkipJudgedAsync` と同じ規律。キャンセルだけ伝える）。
- ポート `IDecisionForgoneBeforeLlmReporter`（既定 NoOp・本番は `PublishingDecisionForgoneBeforeLlmReporter`）。

### C. 監査と要約

- 監査: `AuditEntryFactory.From` ×2・ハンドラ ×2。相関は、A は発生源ごとの決定的 GUID（`position-query:<Source>`）、B は `EventId`。
- 通知はしない（台帳だけ。状態の変化は既存の通知〔`BrokerPositionsIndeterminate` の見送り等〕と重なる）。
- `scripts/nightly-ledger-summary.sh` に §10（建玉照会の状態の変化・発生源別の区間）と §11（LLM を呼ぶ前の見送り・理由別）を足す。
  runbook の「台帳に無いもの」を実測へ置き換える。§9 の推定は残す（段 2 の配備より前の夜と、Pod の停止の読み分けに使う）。

## 受け入れ基準 → 試験（FR-10 のテスト仕様書。T-10-1767〜）

| ID | 基準 |
| --- | --- |
| T-10-1767 | 状態の遷移: 起動直後の失敗・起動直後の成功は出す、成功の連続・失敗の連続は出さない、回復に失敗の始まりと回数を載せる |
| T-10-1768 | 発生源ごとに独立する（一方の失敗が他方の状態を変えない） |
| T-10-1769 | 発行の失敗で状態を戻し、次の観測で出し直す。報告は例外を投げない |
| T-10-1770 | 発注執行の 6 地点（ガード〔失敗の種類つき〕・スナップショット・稼働 probe・S1・決済のゲート〔例外を含む〕・S1 の武装前）が、正しい発生源で成功・失敗を報告する。ガードに渡されたスナップショットで S1 は報告しない |
| T-10-1771 | 取引判断の保有照会・未約定の照会が発生源を分けて報告する。未結線（NoOp）では報告しない |
| T-10-1772 | LLM を呼ぶ前の 4 地点で 1 件ずつ出す。LLM の後の見送りでは出さない。発行の失敗で見送りを壊さない。理由の名前は `DecisionSkipReason` と一致する |
| T-10-1773 | 監査の記録（種別・相関・要約）と、取引サイクルと同じホストでの記録 |
| T-10-1774 | 両サービスの組み立て（Program.cs）が発行の実装を、業務クラスへ同じ singleton で渡す |
| T-10-1775 | 夜間の要約が §10・§11 を数える（スタブ・実 PostgreSQL） |

## 検証

- `dotnet build backend/backend.slnx`（警告 0）。
- `dotnet test` を OrderExecution・TradeDecision・Audit・Notification・Shared.Contracts・Shared.Infrastructure・Architecture へ。
- `dotnet format backend/backend.slnx --verify-no-changes`。
- `node scripts/scripts.repo.test.js`・文書系の検査器。

実測（2026-09-30）: `dotnet build backend/backend.slnx` は警告 0・エラー 0。`dotnet format backend/backend.slnx --verify-no-changes` は差分なし。
`dotnet test`: OrderExecution 1266・TradeDecision 1068・Audit 246・Notification 850・Shared.Contracts 516・Shared.Infrastructure 335・
Architecture 199（skip 1）・Report・MarketMonitor・RiskManagement がすべて通過。
`bash scripts/nightly-ledger-summary.test.sh`（20 件）・`bash scripts/nightly-ledger-summary.pg.test.sh`（31 件。PostgreSQL 16）が通過。
node の検査器（check-test-traceability・check-trace-blocks・check-doc-links・check-adr-index-sync・check-adr-index-addendum-loss・
check-cross-repo-refs・check-plan-id-qualification・gen-knowledge-graph --check・check-commit-messages・check-observability-assets・
check-consumer-endpoint-names・check-reading-budget）と `node scripts/scripts.repo.test.js` が通過。
- 注意（自己変異の手順）: 変異を戻すとき、ファイルの時刻が変異のビルドより古くなると増分ビルドが戻した後のソースを拾わない。
  変異の後は該当ファイルを `touch` してからビルドし直した（最終の試験はその後に流した）。

## 自己変異の確認（2026-09-30。変異を当てて対象の試験を流し、元へ戻した）

| 変異 | 落ちた試験 |
| --- | --- |
| 失敗の連続も毎回出す | T-10-1767（1 件） |
| 起動直後の成功を出さない | T-10-1767・T-10-1768（2 件） |
| 発生源を区別しない（Observe の鍵を固定） | T-10-1767・T-10-1768（2 件） |
| Revert の鍵を固定 | T-10-1769（1 件） |
| 発行の失敗で状態を戻さない | T-10-1769（1 件） |
| ガードの発生源を BrokerPositionSnapshot にする | T-10-1770（6 件） |
| ガードの失敗の種類を落とす | T-10-1770（2 件） |
| S1 が渡されたスナップショットでも報告する | T-10-1770（1 件） |
| 決済のゲートで報告しない | T-10-1770（3 件） |
| 建玉の定期観測で報告しない | T-10-1770（2 件） |
| Program.cs でガードへ報告口を渡さない | T-10-1774・CompositionWiringGuard（moomoo） |
| Program.cs で報告口を登録しない（keyed へ変える） | T-10-1774（2 件）・CompositionWiringGuard（paper・moomoo） |
| 取引判断: 未約定の照会を保有照会の発生源で報告する | T-10-1771（5 件） |
| 取引判断: 未結線でも報告する | T-10-1771（1 件） |
| 取引判断: 1 地点（換算レート未解決）で事実を出さない | T-10-1772（1 件） |
| 取引判断: LLM を呼ぶ前の見送りで TradeDecisionHeld も出す | T-10-1772（4 件）・既存の DecisionHeldReportTests（1 件） |
| 取引判断: 見送りの発行ポートを登録しない | T-10-1774・CompositionWiringGuard |
| 取引判断: 報告口を scoped にする | T-10-1774（1 件） |
| 監査: 相関を発生源で分けない | T-10-1773（1 件） |
| 要約: 再起動の後の成功の読み分けを外す | T-10-1775（pg） |
| 要約: 窓の前の記録を引かない | T-10-1775（pg） |
| 要約: §10 の窓の尻を `<=` にする | T-10-1775（pg） |
| 要約: §11 に窓の尻ちょうどを含める | T-10-1775（pg） |

## 監査の指摘への対応（PR #1110 の監査・head 4149f1d4）

製品コードは変えず、テストと記録だけで埋めた。テスト ID は既存の T-10-1767〜1775 へ足した（新規の ID は振っていない）。

| 指摘 | 対応 | 足した試験 |
| --- | --- | --- |
| N1（中）: `PositionQueryHealthTracker.Observe` の錠を外しても通る | 16 スレッド × 20,000 回、全発生源へ失敗と成功を位相をずらして交互に報告する並行試験。遷移の順序（内部の版）は試験から見えないので、順序に依らない等式で表明する（起動直後の遷移は発生源ごとに 1 回・失敗の区間の開きと閉じの数が一致・Healthy からの失敗 ＝ Healthy の遷移 − 1・回復に載った回数の合計 ＝ 報告した失敗の数）。所要は 1 秒未満。錠ありで 15 回連続で通り、錠を外すと 15 回連続で落ちた | T-10-1767（並行） |
| N2（中）: キャンセルを失敗として数えないことを固定していない | 取引判断: 保有状況・未約定・決済の数量の 3 つの照会の最中にトークンを取り消して `OperationCanceledException` を投げる → 判断はキャンセルを伝え、失敗の報告は出ない。ガード: 巡回の先頭の照会の最中に取り消す（照会し直しの有無の両方）→ 同上 | T-10-1771・T-10-1770 |
| N3（低）: 報告口の catch を狭めても通る／見送りの発行の catch の OCE の扱い | 報告口: 発行口が `TimeoutException`・`IOException`（同期に投げる）・`TimeoutException`・`OperationCanceledException`（失敗した結果を返す）のどれで失敗しても例外を漏らさず、次の照会で出し直す。見送りの発行: 現行の意図（`SkipJudgedAsync` と同じ規律）どおり、発行口自身の打ち切り（トークンは生きている）では見送りと計上 1 件を保ち、発行の最中にトークンが取り消されたときだけキャンセルを伝えて見送りとして数えない | T-10-1769・T-10-1772 |
| N4（低）: 夜間の要約の窓の端 | 実 PostgreSQL の試験に (a) 窓の頭ちょうど（20:00）に始まった失敗（窓の前に成功の記録あり）が 1 区間だけ出ること、(b) 窓の途中で走らせたとき未回復の区間の終端が現在時刻（±5 分）で切られることを足した。スタブ試験と `scripts.repo.test.js` の登録は変更不要（既存の登録で走る） | T-10-1775 |
| L1（低・記録） | IADR-0462 の残余に、最初の失敗の発行を戻すと出し直しの `FailingSince` が出し直しの時刻へずれることを足した | — |
| L2（低・記録） | runbook の発生源の表に、内蔵のペーパー構成でも稼働の probe が起動ごとに `Unknown→Healthy` を 1 行出すことを足した | — |

変異の確認（2026-09-30。当てて対象の試験を流し、`git checkout` で戻して `touch` した）:

| 変異 | 内容 | 落ちた試験 |
| --- | --- | --- |
| E | `Observe` の錠を外す | T-10-1767（並行。15/15 回） |
| A | 取引判断の照会で `OperationCanceledException` を捕まえて失敗を報告し、投げ直す（保有状況・未約定・決済の数量のそれぞれ） | T-10-1771（各 1 件） |
| L | ガードの先頭の照会で `OperationCanceledException` を捕まえて失敗を報告し、投げ直す | T-10-1770（2 件） |
| P | 報告口の catch を `InvalidOperationException` へ狭める | T-10-1769（4 件） |
| M | 見送りの発行の catch を `ex is not OperationCanceledException` にする（種類だけで伝える）／種類を問わず飲む | T-10-1772（各 1 件） |
| S2 | 要約の「窓の前の最後の記録」を `at <= from` にする | T-10-1775（pg。区間が 2 行） |
| S4 | 要約の未回復の区間の終端を常に窓の終端にする | T-10-1775（pg） |

## 残余

- 取引判断のサービスを複数立てると、状態はプロセスごとに持つ（同じ失敗が台数分出る）。現行の配備は 1 台。
- 再起動をまたいだ失敗は、回復の時刻が「再起動の後の最初の成功」までしか分からない（`PreviousStatus=Unknown` で読む）。
- 市場監視の保有照会（損切りライン到達の判定）・報告書の照会は対象外（#1092 の 2 種に入らない）。別件で扱う。
- 判断中の例外は入れていない（上の理由）。LLM の後の見送りで判断時点の価格が無い・解析不能の回は、引き続き台帳に出ない（IADR-0452 の残余）。
- LLM を呼ぶ前の見送りは 1 回につき 1 件出る。日報が未確定のまま一晩中定時が回ると、銘柄数 × 巡回数の件数になる（件数を数えるための選択）。
