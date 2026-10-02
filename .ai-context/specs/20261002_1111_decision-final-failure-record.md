---
title: 取引判断の最中の例外を、再試行の後の最終の失敗だけ 1 件、型名で監査台帳へ残し、夜間の要約で数える（#1111）
type: spec
status: accepted
related_ids: [NFR, FR-04, FR-11, FR-02, UC-01, UC-02, IADR-0483, IADR-0462, IADR-0129, IADR-0395, IADR-0023, IADR-0163, IADR-0079, IADR-0254]
author: claude (Claude Code)
created: 2026-10-02
updated: 2026-10-02
plan_refs: []
---

# 取引判断の最中の例外の最終の失敗を台帳へ残す（#1111）

## 背景と裁定

#1092 段 2（AST#1110・IADR-0462 決定5）は「判断中の例外」を台帳の対象から外した。ログにしか残らず、Pod の再起動で消える。
#1111 の裁定（2026-10-02・オーナー）は **最小限で残す**:

- 再試行の後の**最終の失敗だけ**を、監査台帳に 1 件残す。再試行のたびには数えない。
- 載せるのは**例外の型名・発生源（定時か価格変動か）・銘柄・時刻だけ**。メッセージとスタックは秘密情報を含み得るので載せない。
- 発行は、ハンドラの例外で捨てられない形にする（**ランタイムの MessageBus** から出す）。
- 夜間の要約で件数を数えられるようにする。

起点は NFR（運用。無採番のメタ作業）で、FR-11（判断のイベントの記録）・FR-04（判断）の記録を足す。計画書（02_requirements の FR-11）を読み、
計画 ADR の新たな制約は無いことを確かめた（監査台帳の保持は非機能要件の 7 年保持。秘密を載せない理由になる）。

## 前提（読んだ制約）

- IADR-0462 決定5: 別件とした理由（経路で意味が違う・再試行ごとに数えない設計・語彙の決定が要る）。
- IADR-0129 決定5: 共通の失敗規則は再試行 2s/10s/30s → `<queue>_error`。🔴 ハンドラが例外で終わると、その処理中に発行したメッセージは捨てられる。
- IADR-0395: 配送回数（`Envelope.Attempts`。受信のたびにハンドラの前で 1 増える・1 始まり）を `WolverineExtensions.MaxDeliveryAttempts`（再試行間隔の配列から導出）と `>=` で比べて「最後の配送か」を決める。
- IADR-0023: 定時は銘柄ごとに捕まえて次の銘柄へ進む（再配送すると発行済みの銘柄を重複発注し得る）。
- IADR-0163 決定2: 配線の抜けが試験で見えないので、ハンドラの依存は必須にする。
- IADR-0079: イベント契約は追加のみ。baseline へ登録する。
- IADR-0254 決定3: 期間の集計は台帳が権威（プロセスごとの積み上げをしない）。

## 構成の確かめ（新旧の配置）

取引判断のサービスは新樹形（`backend/Services/TradeDecisionService/` 直下の単一プロジェクト・`Features/` `Infrastructure/` `Tests/`）である。
監査サービスも新樹形（`Domain/` `Infrastructure/Steps/` `Tests/`）。既存の配置に合わせて、ポートは `Features/TradeDecision/`、実装は `Infrastructure/ExternalServices/`、
ハンドラは `Infrastructure/Steps/` に置く。

## 母集合（規則 9〜11。origin/develop 10a32a8e で走査）

### 判断の最中の例外が出る口（取引判断のサービスのハンドラと常駐）

```bash
grep -rn "public .*Task Handle(\|public void Handle(" backend/Services/TradeDecisionService --include=*.cs | grep -v "/Tests/\|/obj/"
grep -rn "catch (Exception" backend/Services/TradeDecisionService --include=*.cs | grep -v "/Tests/\|/obj/"
```

| # | 口 | 例外の扱い（現状） | 最終の失敗の点 | 扱い |
| --- | --- | --- | --- | --- |
| 1 | `InformationCollectedHandler.Handle`（定時） | 銘柄ごとに `catch (Exception) when (!停止)` でログを出して次へ | その `catch`（同じ巡回で同じ銘柄をやり直さない） | **出す**（`scheduled`） |
| 2 | `PriceMovementDetectedHandler.Handle`（価格変動） | 握らない → 再試行 → `<queue>_error` | 配送回数が最大配送回数に達した配送の失敗 | **出す**（`price-movement`・投げ直す） |
| 3 | `TradeDecisionAppService` の `catch (Exception` 17 か所（`git grep -c`）（`...SafeAsync`・`Skip`・発行の失敗） | 例外を fail-safe の値へ倒す（例外にならない） | — | 対象外（判断の失敗ではなく、縮退。既存の見送り・照会の状態の記録が台帳に出す） |
| 4 | `Stage0RecordingService`（常駐。Stage 0 の記録） | `catch (Exception)` でログ | — | 対象外（取引判断のサイクルではなく、記録の再生。裁定の「定時か価格変動か」に入らない） |
| 5 | 判断の下位の供給口（`Http*Provider`・`HttpLlmCompletionClient` 等の `catch`） | 失敗を null・Hold へ倒す | — | 対象外（3 と同じ） |

ハンドラは 2 つだけ（上の grep の結果 2 行）。1・2 が裁定の「定時か価格変動か」の全部である。

### 追随の母集合（規則 9: 誤りの側の文字列で走査）

```bash
git grep -n -E "判断中の例外|判断の途中の例外|台帳に記録が無い" -- ':!.ai-context/specs' ':!CHANGELOG.md'
```

| 場所 | 誤りになる記述 | 対応 |
| --- | --- | --- |
| `scripts/nightly-ledger-summary.sh` の冒頭 | 「分からない（台帳に記録が無い）: 判断中の例外」 | §14 を足したと書き換え |
| `docs/operations/nightly-ledger-summary-runbook.md` §台帳に無いもの | 「判断の途中の例外（ログにしか残らない…）」 | 本文と途中の再試行だけが無い、と書き換え。§出力の読み方に 14 の行 |
| `docs/tests/FR-10_risk-controls-tests.md` の #1092 段 2 の残余 | 「判断中の例外は台帳へ出していない」 | 後の節で出すようにしたと書き換え（生きた文書） |
| `.ai-context/adr/IADR-0462` 決定5・残余、索引の行 | 「本決定では台帳へ出さない」「別件とする」 | 凍結記録なので本文は変えず、日付つき追記で IADR-0483 を指す（決定5 と索引の行）。残余の行は決定5 を引いているので追記で足りる |
| `docs/data/audit-events.md` | （記述なし） | 事実の説明を足す |
| `docs/api/events-and-ports.md` | （記述なし） | イベントの行を足す |
| `scripts/README.md` | 試験の件数 29 / 43（既に古い。規則 10 で数え直し） | §14 と、件数を 31 / 50（本変更の後の実測）へ |

### 規則 10（この変更で新たに誤りになる自分の記述）

- 価格変動のハンドラの引数に `Envelope` を足した → ハンドラを直接呼ぶ既存の試験は無い（呼び出しは Wolverine の生成コードだけ。`git grep -n "\.Handle(" origin/develop -- backend/Services/TradeDecisionService/Tests` の 4 件はすべて `RiskReadStubHost` のスタブの別物）。
- 両ハンドラの必須依存を足した → ハンドラを組む Wolverine の試験ホスト 2 つ（`InformationCollectedConsumerTests`・`PriceMovementDetectedConsumerTests`。どちらも同じアセンブリの両ハンドラを発見する）に報告口を登録。Program.cs を組む試験は Program.cs の登録で足りる。
- 新イベント → `EventMessageTypeNameTests`・`event-schemas.baseline.json`・`AuditCycleCompletenessTests`（全イベントの監査）・監査のハンドラ。通知は購読しない（通知の網羅試験は購読するものだけを見る）。
- 夜間の要約の出力の順が 12 → 14 → 13 になる → 手順書の 14 の行に書いた。

### 規則 11（窓）

本件の「窓」は 2 つ。どちらも 3 通りの形をプローブで確かめた（試験の ID は下）。

**A. 再試行の窓（1 通のメッセージの配送 1〜4 回目）**。増える側＝最後の配送の失敗が数えられること、減る側＝途中の配送の失敗が数えられないこと。

| 形 | 途中（1〜3 回目）で出さない | 最後（4 回目）で出す | 戻したメッセージ（5 回目）で出す | 1 通の 1〜4 回目で合計 1 件 |
| --- | --- | --- | --- | --- |
| 後の端だけ（`attempts >= Max`。採用） | ○ | ○ | ○ | ○ |
| 前の端だけ（`attempts == 1` で出す） | ×（1 回目で出す） | ×（出さない） | × | ○（ただし再試行で成功した失敗まで数える） |
| 両端を突き合わせる（`attempts == Max` だけ） | ○ | ○ | ×（出さない） | ○ |

前の端だけの形は、再試行で回復した一過性の失敗を最終の失敗として数える（裁定に反する）。`== Max` は退避先から戻したメッセージの最後の失敗を数えない
（Wolverine はそのメッセージも再び退避先へ送るので、最終の失敗である）。プローブは T-10-2180 の理論試験（1〜5）と連続配送の試験。

**B. 夜間の要約の窓（`[from, to)`）**。増える側＝窓の頭ちょうどを含む、減る側＝窓の尻ちょうど・窓の前を含まない。

| 形 | 頭ちょうど（20:00）を数える | 尻ちょうど（08:00）を数えない | 窓の前（19:59:59）を数えない |
| --- | --- | --- | --- |
| `>= from AND < to`（採用。他の節と同じ） | ○ | ○ | ○ |
| `> from AND < to` | × | ○ | ○ |
| `>= from AND <= to` | ○ | × | ○ |

プローブは T-10-2185 の実 PostgreSQL の試験（頭ちょうどの META を数え、尻ちょうどの `TimeoutException`・窓の前の `KeyNotFoundException` を数えない）。

## 設計（IADR-0483）

1. 事実 `TradeDecisionFailed(EventId, Symbol, Market, CycleTrigger, ExceptionType, OccurredAt)`。メッセージ・スタック・内側の例外の欄を持たない。
   型名は名前空間つき・総称型は定義の名前（`Ns.Foo`1`）。
2. 定時は銘柄ごとの `catch` で 1 件。価格変動は `catch (Exception) when (!停止 && Envelope.Attempts >= MaxDeliveryAttempts)` で 1 件出して投げ直す。
3. `ITradeDecisionFailureReporter`（両ハンドラの必須依存）。本番は singleton の `PublishingTradeDecisionFailureReporter`。Program.cs が
   `new MessageBus(IWolverineRuntime).PublishAsync` を委譲で渡す（ランタイムの発行口）。例外を投げない。ログにも型名だけ。
4. 監査: `AuditEntryFactory.From`・`TradeDecisionFailedAuditHandler`。相関は `EventId`。通知しない。
5. 夜間の要約 §14（起点 × 型名 × 件数・銘柄）。

## 受け入れ基準 → 試験（FR-10 のテスト仕様書。T-10-2180〜T-10-2188）

並行の作業（#1156 が T-10-2160〜T-10-2172 を使う）と衝突しないよう、T-10-2180 から振った（T-10-2040〜T-10-2159 は他の進行中の作業が取り得るため避けた）。

| ID | 基準 | 置き場所 |
| --- | --- | --- |
| T-10-2180 | 価格変動: 1〜3 回目の失敗は出さず投げ直す・4（と 5）回目は 1 件出して投げ直す・1 通の 1〜4 回目で合計 1 件・成功は出さない・停止は出さない・判定の理論試験。ハンドラが失敗で終わっても、ランタイムから `TradeDecisionFailed` が外へ出る（共有 fanout exchange 宛て）。事実とログに秘密・口座 ID・スタックが無い | `TradeDecisionService/Tests/Infrastructure/Steps/TradeDecisionFailureRecordTests.cs` |
| T-10-2181 | 定時: 失敗した銘柄だけ 1 件（`scheduled`・銘柄・市場・型）、他の銘柄は判断を続ける。全部成功なら出さない。本番と同じ発行の実装で `TradeDecisionFailed` が 1 件外へ出て、秘密を持たない | 同上 |
| T-10-2182 | 報告口: 6 欄だけ・型名・起点・銘柄・時刻（時計）。メッセージ・スタック・内側の例外・秘密・口座 ID が事実とログに無い（陰性）。総称型は定義の名前。発行の失敗（同期の例外・失敗した ValueTask・取り消された ValueTask）で例外を投げず、型名だけを警告のログに残す | 同上 |
| T-10-2183 | 監査: 種別・`EventId` 相関・銘柄・要約・Detail に型名と起点があり、メッセージ・スタックの欄が無い。全イベントの監査の完全性・メッセージの型名 | `AuditEntryFactoryTests`・`AuditCycleCompletenessTests`・`EventMessageTypeNameTests` |
| T-10-2184 | 組み立て: 報告口は Program.cs で singleton の発行の実装として 1 つだけ。両ハンドラの必須依存（抜けは `CompositionWiringGuardTests` が拾う） | `TradeDecisionFailureReporterRegistrationTests` |
| T-10-2185 | 夜間の要約 §14: 起点 × 型名で数え、銘柄を並べる。窓の頭ちょうどを含み、尻ちょうど・窓の前を含まない。SQL はメッセージ・スタックの欄を読まない。§1 の種類別の件数にも出る | `scripts/nightly-ledger-summary.test.sh`・`.pg.test.sh` |
| T-10-2186 | 独立監査 🟡1: 外側と内側の例外の `Data` と内側の例外の本文に秘密を持つ例外でも、事実の型名・直列化した事実・報告口のログ・監査の要約と Detail（`AuditEntryFactory.From`）に `Data` のキーと値・内側の例外・秘密・口座 ID が無い | `TradeDecisionFailureRecordTests`（監査の記録を作るため、テスト専用に AuditService を別名 `AuditWorker` で参照） |
| T-10-2187 | 独立監査 🟡2: Program.cs の組み立てから解決した報告口で報告すると、`TradeDecisionFailed` がメッセージバスへ 1 件発行される（Wolverine の追跡。外部の送信先は無効。Docker 不要） | `TradeDecisionFailureReporterRegistrationTests` |
| T-10-2188 | 独立監査 🟡3（IADR-0483 決定4「通知はしない」）: 通知サービスのどの型のメソッドも `TradeDecisionFailed` を引数に取らず、本番と同じ発見範囲の Wolverine は実行器を「ハンドラ無し」と答える（対照 `OrderExecuted` はあり） | 通知の `TradeDecisionFailedIsNotNotifiedTests` |

## 検証

実測（2026-10-02）:

- `dotnet build backend/backend.slnx`: 警告 0・エラー 0。
- `dotnet test backend/backend.slnx`: 失敗 0 のプロジェクトは全部（TradeDecision 1366・Audit 253・Shared.Contracts 523・OrderExecution 1466・Report 1513・RiskManagement 2131・
  Notification 854・Architecture 199〔skip 1〕ほか）。`AiStockTrading.IntegrationTests` だけ 14 件が失敗し、14 件すべて
  `Failed to connect to Docker endpoint at 'unix:///var/run/docker.sock'`（この環境に Docker が無い。本変更と無関係）。
- `dotnet format backend/backend.slnx --verify-no-changes`: 差分なし（終了コード 0）。
- `bash scripts/nightly-ledger-summary.test.sh`: 31 件通過。`bash scripts/nightly-ledger-summary.pg.test.sh`: 50 件通過（PostgreSQL 16）。
- node の検査器（check-test-traceability・check-trace-blocks・check-doc-links・check-adr-index-sync・check-adr-index-addendum-loss・check-cross-repo-refs・
  check-plan-id-qualification・check-observability-assets・check-consumer-endpoint-names・check-reading-budget・check-banned-libraries・gen-knowledge-graph --check）が通過。
  `node scripts/scripts.test.js` はコミットの後に流す（追跡外のファイルがあると census の試験〔#775〕が差を出すため）。

## 自己変異の確認（2026-10-02。当てて対象の試験を流し、退避した写しで戻して `touch` した）

窓の表（規則 11）の 3 通りの形は、変異 M1〜M3 として実測した（採用した形だけが全部緑）。

| 変異 | 落ちた試験 |
| --- | --- |
| M1 価格変動で最後の配送の判定を外す | T-10-2180（4 件） |
| M2 判定を `== Max`（両端） | T-10-2180（2 件: 5 回目の配送・判定の理論試験） |
| M3 判定を初回（前の端） | T-10-2180（7 件） |
| M4 型名の代わりに `ex.ToString()` | T-10-2180・2181・2182（7 件） |
| M5 型名に Message を足す | T-10-2180・2181・2182（7 件） |
| M6 総称型を定義の名前にしない | T-10-2182（1 件） |
| M7 発行の失敗を投げ直す | T-10-2182（3 件） |
| M8 報告のログに元の例外を渡す | T-10-2180・2182（2 件） |
| M9 定時で報告しない | T-10-2181（2 件） |
| M10 定時の起点を price-movement | T-10-2181（2 件） |
| M11 価格変動で停止も報告する | T-10-2180（1 件） |
| M12 価格変動で投げ直さない | T-10-2180（4 件） |
| M13 Program.cs で報告口を scoped | T-10-2184（1 件） |
| M14 Program.cs で報告口を登録しない | T-10-2184・CompositionWiringGuard |
| M15 監査の相関を EventId にしない | T-10-2183（1 件） |
| M16 監査のハンドラを外す | 監査の網羅・完全性の既存試験（2 件） |
| S1 §14 の窓の尻を `<=` | T-10-2185（pg 1 件） |
| S2 §14 の窓の頭を `>` | T-10-2185（pg 1 件） |
| S3 §14 を銘柄ごとに分ける | T-10-2185（pg 1 件） |
| S4 §14 でメッセージの欄を読む | T-10-2185（スタブ 2 件・pg 2 件） |

［2026-10-02 追記 / #1111・独立監査］独立監査が生き残りを示した 3 つの変異（監査の番号で M3・M6c・M4。上の表の M 番号とは別）を塞ぐ試験
T-10-2186〜T-10-2188 を足し、変異を当てて赤を確かめ、退避した写しで戻して `cmp` で一致を確かめた。

| 変異（独立監査） | 落ちた試験 |
| --- | --- |
| M3 型名へ `Exception.Data` の値を足す（`ExceptionTypeName` の末尾に連結） | T-10-2186（1 件。既存の T-10-2180〜2182 は緑のまま） |
| M6c Program.cs の発行の委譲を `e => ValueTask.CompletedTask` にする | T-10-2187（1 件。既存の T-10-2184・CompositionWiringGuard は緑のまま） |
| M4 通知サービスに `TradeDecisionFailedNotificationHandler` を足す | T-10-2188（2 件。通知の既存の 854 件は緑のまま） |

- `dotnet build backend/backend.slnx`（警告 0）・`dotnet test backend/backend.slnx`。
- `dotnet format backend/backend.slnx --verify-no-changes`。
- `bash scripts/nightly-ledger-summary.test.sh`・`bash scripts/nightly-ledger-summary.pg.test.sh`・`node scripts/scripts.repo.test.js` と文書系の検査器。

## 残余

- 定時の購読そのもの（銘柄ごとの捕捉の外）が巡回の途中で投げて再試行されると、それまでの銘柄の失敗が重ねて出る（市場カレンダーの判定が投げたときだけ。停止は出さない）。
- 退避先から手で戻したメッセージが再び最後の配送で失敗すると、もう 1 件出る。
- 発行の失敗した最終の失敗は台帳に無い（警告のログだけ。価格変動のメッセージは退避先に残る）。
- 型名は外側の例外だけ（`AggregateException` 等で包まれると中身は分からない。本文はログ）。
- 定時の経路のログ（`LogError` の例外の本文）は従来どおり残る（ログは Pod の再起動で消える一時の記録で、裁定の対象は台帳）。
