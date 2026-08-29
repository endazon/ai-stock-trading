---
title: 同居プロセスで Wolverine のハンドラ探索が他サービスへ漏れる欠陥を塞ぐ（Integration E2E の 3 件赤）
type: spec
status: approved
related_ids: [NFR, IADR-0129, IADR-0259, IADR-0263, IADR-0266, IADR-0268]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs: []
---

# 仕様書: Wolverine ハンドラ探索のサービス境界を明示的に閉じる

## 起点

- 起点 ID: **`NFR`（無採番）**。メッセージング基盤の配線是正＝横断的な統制作業であり、
  `.claude/rules/traceability.md`「起点 ID の種別」の無採番許容ケース **2** に当たる。
  計画側の非機能要件表に「サービス境界を跨ぐハンドラ探索の禁止」に当たる番号は無い。
- 直接の契機: `Integration E2E` ワークフロー（`.github/workflows/integration.yml`・ジョブ
  `integration-e2e`）が develop で 5 回連続 failure。実測 `Total tests: 13 / Passed: 10 / Failed: 3`。

## 事実（実測）

### 失敗している 3 件

いずれも「発注執行サービスが `OrderApproved` を処理せず、実 Postgres に `ExecutionRecord` が残らない」。

1. `TradeExecutionPipelineE2ETests.取引判断が承認され発注執行まで複数サービスを跨いで流れる`
2. `TradeExecutionPipelineE2ETests.同一イベントは購読する全サービスへ届く_fan_outがcompeting_consumerへ退行していない`
3. `OrderExecutionPipelineE2ETests.承認注文を実RabbitMQへ発行するとペーパー執行され実Postgresへ永続される`

### いつから壊れたか（CI ログを自分で数え直した）

| run | head | PR | 結果 | 実際の失敗内容 |
| --- | --- | --- | --- | --- |
| 87 | `5ec764a` | #587 | failure | **E2E 13/13 は全て緑。** `TradeDecisionService.Api.Tests.LlmPurposeWiringTests.費用計上イベントも層別の用途で発行される` の単体テスト 1 件（#598 で是正済み） |
| 89 | `58854e3` | #591 | success | — |
| 91 | `bd39cbd` | #593 | failure | E2E 1 件のみ。`fan_out` テストの **リスク管理側台帳**（`ledgered`）の表明。**発注執行側は緑**。run 94 が同一コードで緑のため flake と判断 |
| 94 | `c9d3d5a` | #599 | success | — |
| 95 | `276bfce` | #600 | **failure** | **上の 3 件（初出）** |
| 96–99 | `a7cbd5d` / `145446a` / `c6b3ece` / `ea8ab91` | #602–#605 | failure | 同一の 3 件 |

親から渡された表と**結果欄は一致した**。ただし**内訳は一致しない** —— 87 と 91 の失敗は
今回の 3 件とは別物であり、「移送波の途中から断続的に壊れていた」ではない。
**`git rev-list --count c9d3d5a..276bfce` = 1**。最初に壊れたコミットは **`276bfce`（#600）に一意に確定**する。

### 根本原因（ローカル再現で確定）

この環境で `dockerd` を起動でき、**CI と同一の失敗をローカルで再現**した
（`Total: 13, Failed: 3`・同じ 3 件）。ホストの Serilog 出力を採取して原因を直接観測した。

3 件すべてで、**発注執行サービスのホスト内**（`Content root path: .../Services/OrderExecutionService`）で
次の例外が出ている:

```
System.NotSupportedException: Handler type RiskManagementService.Infrastructure.Steps.OrderApprovedActivityHandler
  does not have a suitable, public constructor for Wolverine or is missing registered dependencies
   at Wolverine.Runtime.Handlers.HandlerChain.DetermineFrames(...)
   at Wolverine.Runtime.Handlers.HandlerGraph.HandlerFor(Type messageType)
   at Wolverine.Runtime.HandlerPipeline.InvokeAsync(Envelope envelope, ...)
```

**発注執行のホストが、リスク管理サービスのハンドラを自分のハンドラグラフに取り込んでいる。**
リスク管理の依存は発注執行の DI に登録されていないためチェーンの組み立てが失敗し、
`OrderApproved` が**一通も処理されない**。テスト 1 では同じホストが
`RiskManagementService.Infrastructure.Steps.TradeDecisionMadeHandler` で落ちており、
**発注執行が本来購読しない `TradeDecisionMade` の受信口まで作っている**ことも確認できる。

これは [IADR-0129](../adr/IADR-0129_wolverine-messaging-topology.md) 決定 11 が記録した
「**起動・ヘルスチェック・キュー宣言・consumer 接続がすべて成功したまま、1 通目の受信時の
ハンドラ生成が失敗してメッセージだけが無言で処理されない**」失敗様式そのものである。
E2E がトポロジの表明（キュー存在・consumer ≥ 1・DLQ）を通過してから
`record.Should().NotBeNull()` で落ちるのは、この様式と厳密に一致する。

### 切り分け（ローカル実測）

| 実行内容 | 結果 | 判ること |
| --- | --- | --- |
| `OrderExecutionPipelineE2ETests` 単独 | **Total: 1, Failed: 0**（17.9s） | 発注執行のコード自体は健全 |
| `PositionDriftStateConcurrencyE2ETests` ＋ `OrderExecutionPipelineE2ETests` | **Total: 4, Failed: 0** | リスク管理アセンブリを **load するだけ**では壊れない |
| `TradeExecutionPipelineE2ETests` 単独 | **Failed** | **リスク管理の Wolverine ホストが先に起動した同一プロセス**で壊れる |
| 全 13 件 | **Total: 13, Failed: 3** | CI と一致 |

⇒ 汚染の条件は「アセンブリが読み込まれていること」ではなく
**「同一プロセスで別サービスの Wolverine ホストが先に起動していること」**である。

### なぜ #600 で初めて出たか

`WolverineExtensions.UseAiStockTradingRabbitMq` は `options.Discovery.IncludeAssembly(...)` で
**追加**のアセンブリを足すだけで、Wolverine の**既定のハンドラ探索（application assembly の走査）を
閉じていない**。同一プロセスで 2 つ目以降に起動したホストは、先に起動したホストが確定させた
application assembly を引き継ぐ。

- **#600 より前**: リスク管理ホストの application assembly は `RiskManagementService.Api` であり、
  **ハンドラを 1 つも含まなかった**（ハンドラは `RiskManagementService.Infrastructure` にあり、
  明示の `IncludeAssembly` でだけ足されていた）。発注執行がこれを引き継いでも**拾うものが無く無害**だった。
- **#600 以後**: 単一プロジェクト＋VSA 移送により `RiskManagementService` が
  **`Program` と全ハンドラを同一アセンブリに持つ**ようになった。引き継いだ側が
  リスク管理の全ハンドラを discovery してしまう。

つまり **#600 は「潜在していた配線の欠陥を可視化した」変更**であり、
移送そのものは誤っていない。**発注執行のコードは 1 行も変わっていない**
（`git diff --stat c9d3d5a..276bfce -- backend/Services/OrderExecutionService backend/Shared backend/TestSupport`
は `backend/backend.slnx` の 1 ファイルのみを返す）。

**本番では 1 サービス 1 プロセスのため顕在化しない。E2E だけが観測できる位置にある。**

### もう 1 つの原因（探索の是正だけでは緑にならなかった）

**探索の是正を入れたところ、`fan_out` テストと `OrderExecutionPipelineE2ETests` は緑になったが、
`取引判断が承認され発注執行まで複数サービスを跨いで流れる` だけが落ち続けた。**
今度はリスク管理ホストのログに理由が出た:

```
[WRN] 注文拒否: DecisionId=... 理由=InformationSourceDegraded
```

**これは統制が正しく働いている姿である。** [IADR-0267](../adr/IADR-0267_information-degradation-state-heartbeat-and-fail-closed.md)
決定 2 は「**有効な現況観測が無い（未観測・失効）＝不明**は新規建てを止める」と定めた（fail-closed）。
本 fixture にはリスク管理と発注執行しか居らず、収集サービスが発行する現況観測
（`InformationSourceStateObserved`）が**永久に届かない**ため、`TradeDecisionMade` は必ず拒否される。

**この 2 つ目の原因はハンドラ探索の欠陥に隠れていた**（探索が壊れている間は
そもそも `OrderApproved` が出る前に発注執行側で止まっていたため、拒否理由が表に出なかった）。

#### 対処: 前提条件を本番と同じ経路で整える（統制は迂回しない）

`取引判断が承認され…` の中で、**収集サービスが毎巡回発行するのと同じ現況観測イベントを
実ブローカへ発行**し、「止めるカテゴリは無い」を正規の経路で届ける。適用完了は
リスク管理の `IInformationDegradationStore.BlocksNewEntries` が下りるまで待って確かめる
（発行の完了と適用の完了は別であり、待たないと競合する）。

- **統制の無効化・迂回・設定での抜け道は使わない。** 副次的に**観測経路そのものも E2E を通る**ようになる。
- `fan_out` テストは `OrderApproved` を直接発行してスクリーニングを経由しないため、前提条件は不要である
  （実際、探索の是正だけで緑になった）。

## 決定と変更内容

**サービスの Wolverine ホストは、自分のハンドラだけを探索する。** 共通ヘルパで
`options.ApplicationAssembly` を**呼び出し元サービスのアセンブリへ明示的に固定**し、
同居プロセスでの引き継ぎを構造的に断つ（トポロジの単一の出所である
`WolverineExtensions` に閉じる —— IADR-0129 決定 4 の方針を維持し、サービス側は 1 行のままにする）。

- 変更ファイル:
  - `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Extensions/WolverineExtensions.cs`
    （探索範囲の固定）
  - `backend/Tests/AiStockTrading.IntegrationTests/TradeExecutionPipelineE2ETests.cs`
    （現況観測による前提条件の確立。上の「もう 1 つの原因」）
- 回帰テスト: 同居を再現する単体テストを `AiStockTrading.TestSupport.PlatformShim.Tests` に追加する
  （実ブローカ不要。`StubAllExternalTransports`）。**Docker が無い既定 CI でも落ちる**ようにする
  ことが要点である —— この欠陥は今まで nightly の E2E でしか観測できなかった。
- 実装ADR: **IADR-0268** を新設し、`.ai-context/adr/README.md` の索引へ追記する。

### やらないこと

- **テストの skip・無効化・削除はしない。** 待ち時間（30 秒）も延ばさない —— 原因は待ち不足ではない。
- #600 の差し戻しはしない（移送は正しい。壊れていたのは共通配線側である）。
- `Discovery.DisableConventionalDiscovery()` は採らない。全走査を止めると
  `IncludeAssembly` で足した分まで効かなくなる恐れがあり、11 サービス分の配線を一度に危険へ晒す。
  **application assembly の固定のほうが射程が狭く、効果は同じである。**

## 母集合の引き直し（規則 9・10）

**記憶で挙げず、誤りの側の文字列で走査してから挙げた。**

- `grep -rl "UseAiStockTradingRabbitMq" backend --include=*.cs` → **40 ファイル**
  （うち Program.cs 10 本＝全サービス、テスト 21 本、共通ヘルパ 1 本、コメントのみの言及 8 本）。
  **共通ヘルパ 1 箇所を直せば 10 サービスすべてに効く**ため、Program.cs 側は無改修とする。
- `grep -rl "IncludeAssembly\|ApplicationAssembly\|ハンドラ探索\|handlerAssemblies"` → 実装 25・
  文書 6（IADR-0263 / 0264 / 0266 / README / 作業仕様書 2 本）。
- **規則 10（是正で新たに誤りになる自分の記述の引き直し）**: `WolverineExtensions.cs` の
  XML doc `handlerAssemblies`（「エントリアセンブリ以外は明示が要る」）は、
  application assembly を固定する本変更で**意味が変わる**ため書き換える。
  IADR-0129 の本文は凍結記録のため書き換えず、IADR-0268 から参照して差分を示す。

### 除外したものと理由

| 除外 | 理由 |
| --- | --- |
| 各サービスの `Program.cs`（10 本） | 共通ヘルパ 1 箇所で閉じるため改修不要。触ると 10 本に同じ誤りを撒く危険が増える |
| `.ai-context/specs/`・`.ai-context/superpowers/` | point-in-time の凍結記録（`traceability.repo.md`「凍結の射程」） |
| `docs/operations/wolverine-queue-cleanup-runbook.md` | キュー名の運用手順であり、探索範囲の記述を持たない（走査で確認済み） |

## 受け入れ基準

1. `TradeExecutionPipelineE2ETests` 単独実行（同居の最小再現）が緑になる。
2. `AiStockTrading.IntegrationTests` の全 13 件が緑になる（`Failed: 0`）。
3. 追加した回帰テストが、**修正前のヘルパでは落ち、修正後は通る**ことを実測で確かめる。
4. `dotnet build backend/backend.slnx` が 0 Warning / 0 Error。
5. `dotnet format` 差分なし。既存の Docker 不要テストが全て緑。
6. カバレッジ床（`coverage-floor.json` の 0.83）を下げない。

## 実測結果（ローカル。Docker を起動できたため CI と同一の経路で確認した）

| 検証 | 結果 |
| --- | --- |
| `AiStockTrading.IntegrationTests` 全 13 件（修正前） | `Total: 13, Errors: 0, Failed: 3` —— CI と同一の 3 件 |
| **同（修正後）** | **`Total: 13, Errors: 0, Failed: 0, Time: 153.081s`** |
| 回帰テストの**負の対照**（探索固定の 1 行だけ外す） | `Failed: 2, Passed: 50` —— 追加した 2 件が**確かに落ちる** |
| 同（元に戻す） | `Failed: 0, Passed: 52` |
| `dotnet build backend/backend.slnx` | `Build succeeded. 0 Warning(s) / 0 Error(s)` |
| `dotnet format --verify-no-changes` | exit 0（差分なし） |
| `node scripts/scripts.test.js` | `✓ 303 tests passed`（exit 0） |
| 文書系検査 6 種 ＋ `gen-knowledge-graph --check` ＋ `check-reading-budget` | すべて exit 0 |

**カバレッジ床は変更していない**（`coverage-floor.json` は無改修）。
