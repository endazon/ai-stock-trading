---
title: 起動の再試行で RabbitMQ の接続が漏れる根本原因（RabbitMQ.Client 7.1.2 の開き損ねた接続の自動回復）を特定し、7.2.2 へ上げて塞ぐ
type: spec
status: done
related_ids: [NFR, ADR-0013, IADR-0129, IADR-0591, IADR-0595]
author: claude (Claude Code)
created: 2026-10-29
updated: 2026-10-29
---

# 仕様書: 起動の再試行での RabbitMQ の接続の漏れ（#1482）

> 本仕様書は実装着手前に作成した（着手 2026-10-29。AST `origin/develop` `09e95635`）。

## 起点（トレーサビリティ）

- 起点: #1482（稼働 PoC 2026-10-26 19:24 JST。Rancher Desktop の起動直後にブローカの準備が整う前に Worker が起動し、Wolverine がブローカの開始を
  再試行した〔`Error trying to start message broker rabbitmq on Attempt N of 20`〕。準備が整った後、AST の接続が 121 本〔平常 22 本〕。
  各サービス 10〜13 本で、1 本だけにチャネルがあり、残りはチャネル 0 の `running`。`connected_at` はどれも同じ数十ミリ秒の間）。
- 計画: NFR（運用の資源）・ADR-0013（メッセージングは Wolverine）。計画の非機能要件表に当たる番号は無い（資源の漏れの是正）。
- 前提の IADR: IADR-0129（Wolverine の共通配線）・IADR-0591（Wolverine の停止を 1 本に束ねる）。
- 本件の判断: IADR-0595（新規。0594 は開いている PR #1490 が使用中）。

## 根本原因（逆コンパイルで確認）

1. Wolverine 6.24.5 の `BrokerTransport.InitializeAsync` は失敗を 5 秒ごとに 20 回まで再試行し、毎回 `RabbitMqTransport.ConnectAsync` を呼ぶ。
   `ConnectAsync` は失敗すると接続の参照を null にして投げる（作り直す）。Wolverine は接続の自動回復を必ず有効にする（`configureDefaults`）。
2. RabbitMQ.Client 7.1.2 の `AutorecoveringConnection.CreateAsync` は、回復の購読（`ConnectionShutdownAsync += HandleConnectionShutdownAsync`）を
   した後に内側の接続を開く。開くのに失敗すると内側の接続は Library・541 "FailedOpen" で閉じられ、その shutdown が**自動回復を起動する**。
   例外は呼び出し元へ伝わるが、`AutorecoveringConnection` は閉じられず破棄もされない。
3. よって、ブローカが TCP を受けるがハンドシェイクを終えられない間（起動の途中・利用者や vhost の準備前など）、**再試行のたびに捨てられた接続が 1 本ずつ
   裏で繋ぎ直しを続け**、準備が整った回復の巡回で一斉に繋がる。チャネルを持たない（Wolverine は参照を捨てている）。観測（試行の回数 ≈ 余分の本数・同時刻の
   `connected_at`・チャネル 0）と一致する。
4. RabbitMQ.Client 7.2.2 の `CreateAsync` は、開き損ねたら `AutorecoveringConnection` を閉じて（541 FailedOpen）破棄する（逆コンパイルで確認）。

MSP の 7 サービスが同じ起動で 2〜3 本に収まった理由は、失敗の形（TCP の拒否なら接続の組み立ての前に落ちて漏れない）の違いと考える
（MSP も RabbitMQ.Client 7.1.2 で同じ潜在の欠陥を持つ。本件の範囲外で、報告に書く）。

## 再現（是正前に赤）

`PlatformShim.Tests` の `RabbitMqFailedOpenConnectionLeakTests`: AMQP のプロトコルヘッダを受けたら切る偽のブローカへ、自動回復を有効にして接続する。
7.1.2 では、開くのに失敗した後 1.5 秒（回復の間隔 100 ms）で偽のブローカへの接続の試みが 15 回になった（赤）。7.2.2 では 1 回のまま（緑）。

## 母集合（規則 9・10）

```bash
git grep -n "RabbitMQ.Client" -- . ; git grep -n "7\.1\.2" -- . ':!*.json'
```

| 箇所 | 扱い |
| --- | --- |
| `Directory.Packages.props` の `RabbitMQ.Client`（CPM の推移ピン留め。全サービスへ効く） | 7.1.2 → 7.2.2 |
| `AiStockTrading.IntegrationTests.csproj` の直接参照 | 版は CPM から取るので変えない |
| `.ai-context/specs/20260803_354_wolverine-migration.md` の 7.1.2 の記述 | point-in-time の記録（書き換えない） |
| docs / deploy / scripts | 該当なし |

## 設計

- RabbitMQ.Client を 7.2.2 へ上げる（WolverineFx.RabbitMQ 6.24.5 の依存は 7.1.2 以上）。Wolverine の版は MSP と揃えたまま（6.24.x）変えない。
- 却下: ブローカの準備を待ってから Wolverine を開始する（起動の待ち合わせ）。準備の判定（ハンドシェイクが通るか）は結局接続を開くことになり、
  同じ欠陥の上に乗る。運用中の再接続（自動回復）でも初回の開き損ねは起き得るため、ライブラリの欠陥そのものを外すのが根本である。

## 変更履歴・互換性・基盤との揃え（オーケストレーターの確認依頼 2026-10-29）

- 上流の修正: 7.2.0 の rabbitmq-dotnet-client#1857（issue rabbitmq-dotnet-client#1856「Possible connection leak when trying to create first connection」）。
- 7.2.0〜7.2.2 の変更履歴に破壊的変更は無い（公開 API の追加と不具合の是正）。nuspec の依存の組は同じ。アセンブリの版は 7.0.0.0 のまま。
  Wolverine.RabbitMQ 6.24.5 が参照する RabbitMQ.Client の型・メンバ 142 件を 7.2.2 に対して解決し、欠けは無い（検査用のコンソールで、
  ASP.NET の共有フレームワークにある System.Threading.RateLimiting を読めない 1 件を除く。7.1.2 も同じ依存を持つ）。
- 基盤（MSP）は `dotnet list package --include-transitive` で RabbitMQ.Client 7.1.2（WolverineFx.RabbitMQ 6.24.4 の推移）。**基盤と食い違う**ので、
  IADR-0595 で受容し、`Directory.Packages.props` に注記した。MSP も同じ欠陥を持つ。
- 推移ピン留めの理由は不具合の是正であり CVE ではない（`Directory.Packages.props` と IADR-0595 に記載）。
- Integration E2E（実ブローカ）は CI で確かめる。

## 受け入れ基準（→ 試験）

- [x] 開き損ねた接続は、裏で自動回復を続けない（偽のブローカへの接続の試みは 1 回）。是正前は赤。
- [x] 全サービスの解決版が 7.2.2 になる（推移ピン留め。`project.assets.json` で確認）。全単体試験・Architecture が緑。
- [ ] 実ブローカの遅れた起動での本数（平常 22 本）: CI の Integration E2E と PoC の再起動で確かめる（本 PR では実機の再現環境が無い）。

## 配備

- 全サービスを上げる（ライブラリの版の変更）。設定の追加は無い。PoC では、ブローカより先に Worker が起動する再起動の後に
  `rabbitmqctl -q list_connections user channels` で AST の本数が 22 本前後に収まることを確かめる。
