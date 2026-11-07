---
title: クラウドで購読側のサービスが購読する exchange を宣言しない（受動の宣言）ようにし、購読側の権限を購読に要るものだけへ絞れるようにする
type: spec
status: done
related_ids: [NFR-18, ADR-0013, IADR-0628, IADR-0129, IADR-0568, IADR-0627, MSP:IADR-0691]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-18)
---

# 仕様書: 購読側の exchange の受動の宣言（NFR-18）

> 本仕様書は実装着手前に作成する。実装の判断は IADR-0628。

## 起点となる計画書（トレーサビリティ）

- 非機能要求: NFR-18（通信の保護。ブローカの権限の最小化はクラウドの移行の前の堅牢化の一部）。
- 関連 ADR: ADR-0013（非同期メッセージング）。
- 起点: 所有者の裁定（2026-11-07）「購読側の権限を、購読に要るもの（exchange の read と自分のキューへの束ね）だけへ絞る」。
- 基盤の側: MSP の IADR-0691 §残る制約 2（購読の exchange の configure を外すには、購読側の宣言を受動に替え、exchange を誰が先に宣言するかを決める変更が両ユニットに要る）。

## 現状（着手前に読んだもの）

- 配線の単一の出所: `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Extensions/WolverineExtensions.cs` の
  `UseAiStockTradingRabbitMq`（`UseRabbitMq(...).AutoProvision().UseConventionalRouting(...)`）。全サービスの `Program.cs` が 1 行で呼ぶ。
- Wolverine 6.24.5（`WolverineFx.RabbitMQ`）を逆アセンブルして読んだ:
  - 規約ルーティングは、ハンドラの受ける型ごとにキュー `<ServiceName>.<型名>` を作り、型の名前（`ToMessageTypeName()`＝型の完全名）の
    exchange へ束ねる（`RabbitMqMessageRoutingConvention.ApplyListenerRoutingDefaults` → `RabbitMqQueue.BindExchange`）。
    受ける型は送り先としても事前に登録される（`PreregisterSenders`。同じ exchange の物）。
  - 起動（`BrokerTransport.InitializeAsync`）は接続の後、全ての exchange、次に全てのキューの順で `InitializeAsync` を呼ぶ。
    AutoProvision の exchange は `RabbitMqExchange.DeclareAsync` で**非受動**に宣言する。`DeclarePassive = true` なら
    `ExchangeDeclarePassiveAsync`（在るかの確かめ）だけをする。キューは宣言の後に束ねる（`queue.bind`）。
  - 初期化が例外で失敗すると、5 秒おきに 20 回まで初期化をやり直し、尽きれば `BrokerInitializationException` で起動が失敗する。
    exchange の `_initialized` は成功の後にだけ立つ（失敗した exchange は次の回でまた確かめる）。exchange はキューより先に回るので、
    受動の確かめが通るまでキューの束ねに進まない。
  - 発行だけの型の exchange は、1 通目の発行のときに作られ、非受動で宣言される（遅延）。
- RabbitMQ の権限（公式文書 Access Control の表）: `exchange.declare`＝exchange の configure（非受動は在っても要る）、
  `exchange.delete`＝configure、`queue.declare`＝キューの configure、`queue.bind`＝キューの write と exchange の read、
  `basic.publish`＝exchange の write、`basic.consume`＝キューの read。受動の宣言は、4.3.1 以降は対象への何れかの権限
  （configure / write / read のどれか）で足りる。基盤のブローカは 3.13（受動の宣言は同じく configure を要らない。下の実測）。
- 切り替えの前例: 生成コードの読み込み方式（環境変数 `WOLVERINE_TYPE_LOAD_MODE`・純関数 `ResolveTypeLoadMode`・綴りの誤りは起動で止める）。
- chart: `deploy/helm/ai-stock-trading/templates/deployment.yaml` が全 Worker に `RabbitMq__ConnectionString` を描く。values-cloud は TLS などクラウドの段を持つ。

## 対象範囲

- 対象:
  - 共通配線に「購読する exchange の宣言の仕方」（`Declare`＝既定・従来／`Passive`）を足す。`Passive` では、規約が購読のキューを束ねる
    exchange に `DeclarePassive = true` を付ける。キューの宣言と束ね・デッドレター・発行だけの exchange の宣言は変えない。
  - 切り替え: 環境変数 `WOLVERINE_SUBSCRIBED_EXCHANGES`（既定の入口が読む）と、明示の引数を取る入口（試験の固定用）。サービスの `Program.cs` は変えない。
  - chart: `global.rabbitmq.subscribedExchanges`（既定 `Declare`）。`Passive` のときだけ env を描く。values-cloud だけ `Passive`。
  - 試験: 構成の検査（単体）・chart の切り替え（単体）・実ブローカ（Integration E2E）。helm.yml の描画の検査に 2 行。
- 対象外:
  - 基盤の生成器の変更（購読の exchange の configure を外す）と、exchange の先の宣言（基盤のブローカの起動の直後の初期化）。基盤の PR で行う。
  - 既定の exchange への write（受信キューを持つ利用者の戻し）。受け手での発行元の確かめ（IADR-0627）が守る。

## 起動順の手当て（比べた案）

| 案 | 評価 |
| --- | --- |
| a. 受動の確かめを Wolverine の初期化の再試行で待つ（5 秒おき 20 回、尽きれば起動を失敗させて Pod の再起動に任せる） | 追加の部品が要らない。束ねの無いまま動くことが無い（exchange はキューより先に確かめる）。発行だけの型の exchange は 1 通目の発行まで作られないので、**これだけでは**稀にしか発行しない型の購読側が長く再起動を繰り返す |
| b. 起動を止めずに後から束ねる（背景で確かめて束ねる部品を足す） | Wolverine の外にトポロジの宣言を持つことになり、IADR-0129 の単一の出所を崩す。束ねまでの間の取りこぼしが静かに起きる。採らない |
| c. 発行側が起動時に自分の exchange を宣言する | 発行する型の一覧をサービスごとにコードへ持つ必要がある（今は「作る型」から基盤の生成器が導くだけ）。漏れると a と同じ。採らない |
| **d. 運用が先に宣言する（基盤のブローカの起動の直後の初期化が、購読される exchange を定義ファイルから読み込む）** | 基盤の生成器は既に購読の exchange の一覧を導いている。初期化は利用者の権限を当てる前に走るので、購読側が繋がる時点で exchange は在る。**採用（a を保険として併せる）** |

- 採用: **d を主、a を保険**。基盤の PR が入るまでの間（取引ユニットだけが `Passive` になった間）も、稼働中のブローカには
  従来の購読側が宣言した exchange が残っているので止まらない。新しいブローカで d が無い間は a で待つ（Pod の再起動を繰り返し得る）。

## 受け入れ基準

- [x] `Passive` では、購読のキューを束ねる exchange が受動の宣言になる（キューの束ねは残る）。デッドレターの exchange は受動にしない。
- [x] `Passive` でも、発行だけの型の exchange は非受動で宣言する（発行側は自分の exchange を宣言し続ける）。
- [x] `Declare`（既定）では従来どおり購読の exchange も宣言する。既定の入口は環境変数で切り替わる。綴りの誤り・数は起動で止める。
- [x] chart の既定と values-local は env を描かず、values-cloud だけが `Passive` を描く。
- [x] 実ブローカで、購読の exchange の configure を持たない利用者が、運用の先の宣言の後に起動・束ね・受信できる。その利用者は
  非受動の宣言も削除も 403 で拒まれる。購読側が先に起動したときは exchange が現れるまで起動を終えず、現れた後に束ねて受け取る。
- [x] 修正を外すと赤になる（下の実測）。

## 実測（2026-11-07）

- 単体（`SubscribedExchangeDeclarationTests`）: 14 件緑。`DeclarePassive = true` を `false` に替えると、Passive の 2 件が赤。
  - stub の transport では Wolverine がリスナーの発見を飛ばすので、起動が呼ぶのと同じ `DiscoverListeners` / `PreregisterSenders` を試験から呼ぶ。
- 実ブローカ（`BrokerSubscriberLeastPrivilegeE2ETests`。Testcontainers の `rabbitmq:3.13-management`）: 3 件緑（48 秒）。
  購読側を `Declare` に戻すと 1 件目が赤（`BrokerInitializationException`、内側は 403 `ACCESS_REFUSED - configure access to exchange ... refused`、1 分 47 秒）。
- helm は手元に無い（描画は CI の helm.yml が検査する）。

## 影響範囲

- コード: `WolverineExtensions.cs`（入口 2 つ＋列挙）。サービスの `Program.cs` は変えない（基盤の生成器が読む `UseAiStockTradingRabbitMq(ServiceName` の形も保つ）。
- chart: `values.yaml`・`values-cloud.yaml`・`templates/deployment.yaml`・README。CI: `.github/workflows/helm.yml`。
- 試験: `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim.Tests/SubscribedExchangeDeclarationTests.cs`・
  `backend/Tests/AiStockTrading.IntegrationTests/BrokerSubscriberLeastPrivilegeE2ETests.cs`。

## 段と順序

1. 本 PR（取引ユニット）をマージし、取引ユニットのイメージを更新する（values-cloud で `Passive`）。
2. 基盤の PR（生成器から購読の exchange の configure を外し、exchange の先の宣言を足す）を当てる。
