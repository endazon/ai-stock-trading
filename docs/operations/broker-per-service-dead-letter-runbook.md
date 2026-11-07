---
title: 運用 Runbook — ブローカのデッドレターをサービスごとにする（適用の順序・旧の共有のデッドレターの扱い・戻し方）
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
---
<!-- trace:
ids: [NFR-18]
adrs: [ADR-0013]
iadrs: [IADR-0635, IADR-0129, IADR-0628, MSP:IADR-0699, MSP:IADR-0694]
specs: [20261107_broker-per-service-dead-letter]
issues: []
-->

# 運用 Runbook: ブローカのデッドレターをサービスごとにする

> **運用仕様書（[operations.md](operations.md)）の下位にあたる手順書である。** クラウドへの移行の全体は[クラウド移行 Runbook](cloud-single-node-migration-runbook.md)にある。
>
> 🔴 **本書の手順はすべてオーナーが行う。** 配備・クラスタの操作は、AI（コーディングエージェント）は行わない。
>
> **状態を `draft` に留めている理由**: クラウドのノードでまだ流していない。

## 何が変わるか

| | 変更の前 | 変更の後 |
| --- | --- | --- |
| 受信キューのデッドレター | `<queue>_error`（例 `ai-stock-trading.audit-service.OrderApproved_error`） | 同じ |
| サービスの既定のデッドレター（受信キュー以外の受け皿） | 全サービスで共有の `wolverine-dead-letter-queue`（キュー・同名の exchange） | サービスごとの `<ServiceName>.dead-letter`（例 `ai-stock-trading.audit-service.dead-letter`） |
| ブローカの権限（基盤の `rabbitmq-permissions.yaml`） | 全 11 利用者が共有のデッドレターに configure・write・read を持つ | 共有のデッドレターの権限は誰にも無い。各利用者は自分の接頭辞（`<ServiceName>.`）の中のデッドレターだけ |

- 取引ユニットのイメージ（共通の配線）と、基盤の権限の表の 2 つの変更からなる。**取引ユニットのイメージを先に、基盤の権限を後に**当てる。
- 受信キューの宣言（引数 `x-dead-letter-exchange`）は変わらない。新しいイメージの Pod は起動で `<ServiceName>.dead-letter` を新たに宣言するだけである。

## 1. 取引ユニットのイメージを先に当てる（全 11 サービス）

1. 取引ユニットの変更（デッドレターをサービスごとにする）が入ったイメージを、移行の手順書の §4 のとおり digest で固定して配備する。
   - このとき基盤の権限は**まだ旧のまま**（共有のデッドレターの権限がある）。新しいイメージは共有の名前を宣言しないが、自分の接頭辞の中の
     `<ServiceName>.dead-letter` は旧の権限でも宣言できる。旧のイメージの Pod が残っていても、共有の名前の宣言は旧の権限で通る。
   - ローリングの更新で旧・新の Pod が並んでも、受信キューは同じ名前・同じ引数なので、メッセージは取り残されない（同じキューを順に読む）。
2. 全 11 サービスが新しいイメージで動いていることを確かめる（**1 つでも旧のイメージなら 2 へ進まない**）。

   ```sh
   kubectl -n ai-stock-trading get pods -o jsonpath='{range .items[*]}{.metadata.labels.app}{"\t"}{.spec.containers[0].image}{"\n"}{end}' | sort
   ```

3. 各サービスの既定のデッドレターが宣言されていることを、**読みだけで**確かめる（名前と件数・consumer の数を一覧するだけ。何も変えない）。

   ```sh
   kubectl -n platform-infra exec deploy/rabbitmq -- rabbitmqctl -q list_queues name messages consumers | grep -E '\.dead-letter\b' | sort
   ```

   - `ai-stock-trading.<サービス>.dead-letter` が 11 本あること。足りないサービスは新しいイメージで起動していない（1 へ戻る）。

## 2. 旧の共有のデッドレターの件数を読みだけで確かめる

```sh
kubectl -n platform-infra exec deploy/rabbitmq -- rabbitmqctl -q list_queues name messages consumers | awk '$1 == "wolverine-dead-letter-queue"'
```

| 結果 | 次 |
| --- | --- |
| 行が無い | 旧の共有のキューは作られていない。3 へ |
| `messages` が 0・`consumers` が 0 | 3 へ（旧のキューは 4 で消す） |
| `messages` が 1 以上 | **消さない。** 5（空でないとき）へ。3 は先に進めてよい（権限を外してもキューと中身は残る） |
| `consumers` が 1 以上 | 旧のイメージがまだ動いている。1 の 2 へ戻る |

## 3. 基盤の権限を当てる（基盤の手順）

基盤（MSP）の `deploy/cloud/infra/rabbitmq-permissions.yaml`（共有のデッドレターを外した版）を当て、ブローカを作り直す。
手順は基盤の `deploy/cloud/README.md` の「段 2 の切り替え」（ConfigMap を当てて `rollout restart deploy/rabbitmq`）。

- ブローカの作り直しの間、各サービスの接続は切れ、Wolverine の再接続で戻る（権限の表の他の変更と同じ）。
- 作り直しの後、各サービスのログに `ACCESS_REFUSED`（403）が無いこと、受信キューに consumer が付いていることを確かめる。

  ```sh
  kubectl -n platform-infra exec deploy/rabbitmq -- rabbitmqctl -q list_queues name consumers | grep '^ai-stock-trading\.' | grep -v -E '_error|\.dead-letter' | awk '$2 == 0'
  ```

  何も出なければよい（consumer 0 の受信キューが無い）。

## 4. 旧の共有のデッドレターを消す（空のときだけ）

2 で空だったときだけ、空でなければ消さない・使われていれば消さない指定で消す（`--if-empty --if-unused`。2 の後に流入があれば失敗して残る）。

```sh
kubectl -n platform-infra exec deploy/rabbitmq -- rabbitmqctl delete_queue wolverine-dead-letter-queue --if-empty --if-unused
```

- 同名の exchange は消さない（メッセージを持たず、3 の後は誰の権限にも当たらない）。
- 消した後に旧のイメージを起動すると、共有の名前の宣言が 403 で拒まれて起動できない（それが正しい振る舞い。戻すときは下の「戻し方」）。

## 5. 空でないとき

旧の共有のデッドレターに残っているのは、取引ユニットのいずれかのサービスが受信キュー以外の経路で退避したメッセージである
（受信キューの失敗は `<queue>_error` へ行くので、通常は溜まらない）。

1. **消さない・purge しない。** 3 の後もキューと中身はブローカに残る（権限は外れるが、ブローカの管理者は読める）。
2. 中身を見て、どのサービスの何のメッセージかを確かめる。管理画面の Get messages を **requeue（戻す）** で使い、取り出したメッセージは必ず戻す。
   ヘッダ（`x-death` の `queue`・Wolverine の `source`・`message-type`）から、元のサービスと型を読む。
3. 扱いは型ごとに決める（[旧キュー削除 Runbook](wolverine-queue-cleanup-runbook.md)の ② と同じ考え方）。
   - 監査サービスの台帳と突き合わせ、取りこぼしが無いかを見る。必要なら**新しいイベントとして発行し直す**（再投入ではなく再発行）。
   - 不要と判断したものは、件数と判断を運用の記録に残してから捨てる。
4. 空になったら 4 へ。

## 戻し方

| 戻すもの | 手順 | 注意 |
| --- | --- | --- |
| 基盤の権限だけ | 基盤の `rabbitmq-permissions.yaml` を前の版へ戻してブローカを作り直す | 共有のデッドレターの権限が戻る。取引ユニットのイメージはそのままでよい（新しいイメージは共有の名前を使わない） |
| 取引ユニットのイメージ | **先に基盤の権限を前の版へ戻す**。その後に旧のイメージへ戻す | 権限を戻さずに旧のイメージへ戻すと、旧のイメージは共有の名前の宣言が 403 で拒まれ、起動できない |

- 新しいイメージが宣言した `<ServiceName>.dead-letter` は、戻した後も残る（空なら `--if-empty --if-unused` で消してよい）。

## 確かめる試験（手元・CI）

- 単体: PlatformShim.Tests の `PerServiceDeadLetterQueueTests`（全 11 サービスのデッドレターが自分の接頭辞の中・共有の名前を宣言しない）。
- 実ブローカ: Integration の `BrokerPerServiceDeadLetterE2ETests`（共有の権限が無い利用者で起動し、失敗したメッセージが `<queue>_error` へ移る）。
- 基盤: `scripts/rabbitmq-permissions.test.js`（どの利用者も共有のデッドレター・他のサービスのデッドレターに権限を持たない）。
