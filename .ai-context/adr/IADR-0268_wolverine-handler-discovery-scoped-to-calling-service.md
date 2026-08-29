---
title: IADR-0268 Wolverine のハンドラ探索は呼び出し元サービスのアセンブリへ固定する（同居プロセスでの越境を断つ）
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0129, IADR-0259, IADR-0263, IADR-0266, IADR-0267, IADR-0049, IADR-0050]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
---

# IADR-0268: Wolverine のハンドラ探索は呼び出し元サービスのアセンブリへ固定する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: Accepted
- 日付: 2026-08-29
- 決定者: Claude Code（`Integration E2E` の 5 連続 failure の原因調査で起案）

## 起点・関連

- 起点 ID: **`NFR`（無採番）**。メッセージング共通配線の是正＝横断的な統制作業であり、
  `.claude/rules/traceability.md`「起点 ID の種別」の無採番許容ケース 2 に当たる。**環流はしない。**
- 関連する実装仕様書:
  [20260829_e2e-order-approved-consumption](../specs/20260829_e2e-order-approved-consumption.md)
- 上流: [IADR-0129](IADR-0129_wolverine-messaging-topology.md)（Wolverine のトポロジと生成コード。
  **決定 4「トポロジは共通ヘルパに閉じる」・決定 11「生成失敗は 1 通目の受信時に無言で起きる」**を継承）・
  [IADR-0259](IADR-0259_single-project-vsa-structure.md)（1 サービス = 1 プロジェクト）・
  [IADR-0049](IADR-0049_integration-e2e-foundation.md) / [IADR-0050](IADR-0050_e2e-multiservice-and-auth.md)
  （複数サービスの Worker を**同一プロセスへ同居**させる実基盤 E2E）

## コンテキスト（実測）

`Integration E2E`（`.github/workflows/integration.yml`）が develop で 5 連続 failure。
`Total tests: 13 / Passed: 10 / Failed: 3` で、3 件とも
「**発注執行サービスが `OrderApproved` を処理せず、実 Postgres に発注結果が残らない**」であった。

原因は**共通配線の側にあり、発注執行サービスのコードは 1 行も変わっていない**。
最初に赤くなったコミットは `276bfce`（リスク管理サービスの VSA 移送）に一意に確定する
（`git rev-list --count c9d3d5a..276bfce` = 1）。

### 何が起きていたか

`WolverineExtensions.UseAiStockTradingRabbitMq` は `options.Discovery.IncludeAssembly(...)` で
**追加**のアセンブリを足すだけで、Wolverine の**既定のハンドラ探索（application assembly の走査）を
閉じていなかった**。application assembly は明示しなければ実行時に推論され、
**同一プロセスで 2 つ目以降に起動したホストは、先に起動したホストが確定させたものを引き継ぐ**。

その結果、リスク管理ホストを先に起動したプロセスで、後続の発注執行ホストが
リスク管理のハンドラを自分のハンドラグラフへ取り込んでいた（発注執行ホストのログで実測）:

```
System.NotSupportedException: Handler type RiskManagementService.Infrastructure.Steps.OrderApprovedActivityHandler
  does not have a suitable, public constructor for Wolverine or is missing registered dependencies
   at Wolverine.Runtime.Handlers.HandlerChain.DetermineFrames(...)
   at Wolverine.Runtime.Handlers.HandlerGraph.HandlerFor(Type messageType)
   at Wolverine.Runtime.HandlerPipeline.InvokeAsync(Envelope envelope, ...)
```

取り込んだ側にはリスク管理の依存が DI に無いためチェーンの組み立てが失敗する。
**失敗するのは起動時ではなく 1 通目の受信時**であり、
起動・ヘルスチェック・キュー宣言（AutoProvision）・consumer 接続がすべて成功したまま、
**メッセージだけが無言で処理されない**。IADR-0129 決定 11 が記録した失敗様式と同型である。
別のテストでは発注執行ホストが `TradeDecisionMadeHandler` で落ちており、
**本来購読しない `TradeDecisionMade` の受信口まで作っていた**ことも確認した。

### なぜ移送まで表に出なかったか

**欠陥は移送前から存在した。** 表に出なかったのは、相手側の application assembly が
たまたまハンドラを含まなかったからにすぎない。

- 移送前: リスク管理の application assembly は `RiskManagementService.Api` で、
  **ハンドラを 1 つも含まなかった**（ハンドラは `RiskManagementService.Infrastructure` にあり、
  明示の `IncludeAssembly` でだけ足されていた）。引き継いでも**拾うものが無く無害**。
- 移送後: `RiskManagementService` が **`Program` と全ハンドラを同一アセンブリに持つ**ようになり、
  引き継いだ側がリスク管理の全ハンドラを discovery してしまう。

**1 サービス 1 プロセスの本番では顕在化しない。** 複数サービスを同居させる実基盤 E2E
（IADR-0049 / IADR-0050）だけが観測できる位置にあった。

### 切り分け（ローカル実測。CI と同一の失敗を再現したうえで）

| 実行内容 | 結果 | 判ること |
| --- | --- | --- |
| `OrderExecutionPipelineE2ETests` 単独 | Total: 1, Failed: **0** | 発注執行のコードは健全 |
| `PositionDrift…` ＋ `OrderExecutionPipeline…` | Total: 4, Failed: **0** | 相手アセンブリを **load するだけ**では壊れない |
| `TradeExecutionPipelineE2ETests` 単独 | **Failed** | **相手の Wolverine ホストが先に起動している**ことが条件 |

## 決定

### 決定 1: 共通配線が application assembly を呼び出し元サービスへ固定する

`UseAiStockTradingRabbitMq` の中で `options.ApplicationAssembly = Assembly.GetCallingAssembly()`
を設定し、**推論と引き継ぎの余地を無くす**。サービス側の `Program.cs` は 1 行のままで変更しない
（IADR-0129 決定 4「トポロジの選択肢をサービス側に持たせない」を維持する）。

- `GetCallingAssembly` を使うため、メソッドへ **`[MethodImpl(MethodImplOptions.NoInlining)]`** を付ける。
  インライン化されると呼び出し元がひとつ上へずれ、**固定先を取り違える**。
- `handlerAssemblies`（可変長引数）の意味は「**自アセンブリ以外**にハンドラを置く場合の追加」へ変わる。
  自分自身は常に走査されるため渡す必要はない（渡しても重複は無害で、既存の呼び出しは無改修で正しく動く）。

### 決定 2: `Discovery.DisableConventionalDiscovery()` は採らない

全走査を止める案は、`IncludeAssembly` で足した分まで効かなくなる恐れがあり、
10 サービス分の配線を一度に危険へ晒す。**application assembly の固定のほうが射程が狭く、効果は同じ**である。

### 決定 3: 回帰は Docker 不要の既定 CI で止める

この欠陥は **Docker を要する nightly の `Integration E2E` でしか観測できなかった**
（PR の必須チェックは全部緑のまま赤が積み上がった）。同型の再発を早く捕まえるため、
不変条件を `WolverineTopologyTests` に固定する（実ブローカ不要）:

1. 先に起動した別サービスが確定させた application assembly を**引き継がない**。
2. 固定先が **shim 自身ではなく呼び出し元**である（shim へ固定すると、
   越境しない代わりに**自分のハンドラも拾わなくなる**）。

## 帰結

- 同居プロセスでサービス境界が守られる。**サービスは自分のハンドラだけを持つ。**
- 本修正だけでは E2E は完全には緑にならなかった。**同じ 3 件の裏にもう 1 つ別の原因が隠れていた**
  （情報収集の縮退による新規建て停止の fail-closed・[IADR-0267](IADR-0267_information-degradation-state-heartbeat-and-fail-closed.md)）。
  こちらは**統制が正しく働いていた**ものであり、E2E 側が前提条件を本番と同じ経路
  （現況観測イベントの発行）で整えることで解いた。詳細は作業仕様書に記録した。
- **残余リスク**: `GetCallingAssembly` は「共通配線を**サービスの Program.cs から直接呼ぶ**」ことを前提とする。
  ヘルパをさらにラップする層を挟むと固定先がその層になる。決定 3 のテスト 2 がこれを検出する。
