---
title: 運用 Runbook — Discord を経ない取引の停止（SSH から既存の kill switch を起動する）と、クラスタの外からの検知
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
---
<!-- trace:
ids: [NFR, NFR-03, NFR-06, FR-10, FR-09, UC-06]
adrs: [ADR-0006, ADR-0009, ADR-0024]
iadrs: [IADR-0613, IADR-0062, IADR-0567, IADR-0344]
specs: [20261105_1530_out-of-band-stop-and-incident-runbook]
issues: [#1530, #937, MSP#2402]
-->
<!-- 起点 ID・関連 ADR/IADR・仕様書名・修飾付き issue 参照は本文へ書かず、上の trace ブロックへ入れる（scripts/check-trace-blocks.js が検査する） -->

# 運用 Runbook: Discord を経ない取引の停止と、クラスタの外からの検知

> **運用仕様書（[operations.md](operations.md)）の下位にあたる手順書である。** 状態の単一情報源は運用仕様書に置き、本書は手順に特化する。
> 障害全体の流れ（検知から振り返りまで）は [障害対応 Runbook](incident-response-runbook.md) にあり、本書はその「初動（止める）」の 1 つの手段である。
>
> 🔴 **本書の手順はすべてオーナーが行う。** ノードへの SSH・稼働クラスタへの `kubectl`・外部の監視サービスの登録は、AI（コーディングエージェント）は行わない。

## なぜこの文書があるか

画面を持たず Discord だけで運用すると、新規建てを止める窓口（kill switch）が Discord のボットだけになる。
次のとき、Discord から止められない。

- ボット（notification-service）が落ちている・Discord 側に障害がある。
- Discord のアカウントに入れない、または**アカウントを他人に使われている疑いがある**（このときは Discord から止めても、同じ Discord から解除され得る）。

本書は、ノードへ SSH で入って**既存の kill switch** を起動する経路（`scripts/ast-trading-stop.sh`）と、Discord に頼らずに異常へ気付く手段を定める。

## 仕組み（何が起き、何が起きないか）

| 操作 | 起きること | 起きないこと |
| --- | --- | --- |
| `hold` | 名前空間に Secret `ast-operator-hold` を作り、risk-management の Pod を作り直す。新しい Pod は起動時に kill switch を起動する（actor は `operator-hold`。設定の変更履歴に残る）。**保持の間は Discord・画面からの解除が 409 で拒まれる** | 手仕舞い・損切りは止まらない（kill switch と同じ）。Deployment の spec は変えない（helm upgrade・ArgoCD と食い違わない）。既に起動していた kill switch の主体・理由は上書きしない |
| `release` | Secret を消し、risk-management の Pod を作り直す | **取引は再開しない**（kill switch は起動したまま残る）。再開は従来どおり Discord の解除（確認ボタン＋確認フレーズ）で行う |
| `isolate-discord` | notification-service を 0 台にする。Discord からの操作と、Discord への業務通知が止まる | 基盤の警報（Alertmanager）・kill switch・損切りは影響を受けない |
| `restore-discord` | notification-service を 1 台に戻す | — |
| `status` | 保持の有無・主要な Deployment の台数・保持の印のログを表示する | 何も変えない。kill switch の状態そのものは表示しない（本スクリプトは所有者のトークンを持たない） |

- Secret が無いときは env も無く、risk-management は従来どおり動く（chart は optional の参照で読む）。
- Secret は chart・ESO・ArgoCD の管理の外にある。ArgoCD の自動同期で消えることはない。
- Reloader を有効にした構成では、Secret の作成・削除で risk-management が自動でも再起動する（二重の再起動は無害）。

## この手順を実行する条件（いつ走らせるか）

| 場面 | 行う節 |
| --- | --- |
| Discord から `/killswitch` を打てない・応答が無い | 手順 1・2 |
| Discord のアカウントを他人に使われている疑いがある | 手順 1・2・3（その後 [障害対応 Runbook](incident-response-runbook.md)） |
| 想定しない発注・約定を証券会社のアプリで見た | 手順 1・2（Discord が使えても、先に SSH で止めてよい） |
| 原因が分かり、取引を再開してよいと判断した | 手順 5 |
| 初回（ノードの準備） | 「前提」と「クラスタの外からの検知」 |

## 前提

| 項目 | 内容 |
| --- | --- |
| 必要な権限 | ノードへの SSH（鍵認証）。ノードの上の kubeconfig（k3s なら `k3s kubectl`。root か sudo が要る） |
| 必要なツール | リポジトリのクローン（ノードの上か、kubeconfig を持つ手元の端末）。`bash`・`kubectl`（または `k3s kubectl`） |
| 所要時間の目安 | 止める: 3 分（Pod の作り直しを含む）。外す: 2 分 |

> 🔴 **SSH の鍵を失うと、この経路も失う。** 鍵は Discord と別の端末・別の保管先（パスワードマネージャ等）に置き、
> 予備の鍵を 1 本登録しておく。VPS の管理画面のコンソール（ブラウザ）からも入れることを、一度確かめておく。

## 手順

### 1. 状態を見る（何も変えない）

```bash
ssh <ノード>
cd <リポジトリのクローン>
KUBECTL="k3s kubectl" bash scripts/ast-trading-stop.sh status
```

### 2. 止める（保持を入れる）

```bash
KUBECTL="k3s kubectl" bash scripts/ast-trading-stop.sh --live hold --reason "想定しない約定を調査中"
```

- 最後に `OK: 保持が効きました` が出れば成功である（risk-management の新しい Pod のログに印 `operator-kill-switch-hold: active` を確かめている）。
- 理由は 200 文字まで・1 行。空でもよい（既定の理由が入る）。理由は変更履歴とログに残るので、秘密の値を書かない。

### 3. Discord のボットを止める（アカウントを他人に使われている疑いがあるとき）

```bash
KUBECTL="k3s kubectl" bash scripts/ast-trading-stop.sh --live isolate-discord
```

- 🔴 **ArgoCD の自動同期（selfHeal）が有効なら、台数は宣言（1 台）へ戻される。** スクリプトは ArgoCD の Application を見つけると警告する。
  止め続けるには、先に自動同期を止める:

  ```bash
  k3s kubectl -n argocd patch application ai-stock-trading --type merge -p '{"spec":{"syncPolicy":{"automated":null}}}'
  ```

  戻すときは、`deploy/argocd/application.yaml` を `kubectl apply` し直す（自動同期の宣言が戻る）。
- ボットの資格情報（Bot のトークン・所有者の機密クライアントの secret）は、別途 [障害対応 Runbook](incident-response-runbook.md) §資格情報の入れ替え で作り直す。台数を 0 にしても、漏れた資格情報は失効しない。

### 4. 止まったことを別の経路で確かめる

- 証券会社のアプリで、新しい新規建ての注文が出ていないことを見る（保持の後に出た新規建てがあれば、[障害対応 Runbook](incident-response-runbook.md) の初動へ）。
- ボットを止めていなければ、Discord の `/status` で kill switch が「起動」・優先中の統制が kill switch と表示されることを見る（表示だけで、何も変えない）。

### 5. 外す（再開してよいと判断したとき）

```bash
KUBECTL="k3s kubectl" bash scripts/ast-trading-stop.sh --live release
KUBECTL="k3s kubectl" bash scripts/ast-trading-stop.sh --live restore-discord   # 手順 3 を行ったときだけ
```

- **これだけでは取引は再開しない。** kill switch は起動したままである。原因を確かめたうえで、Discord の `/killswitch off`（確認ボタン＋確認フレーズ）で解除する。
- 手順 3 で ArgoCD の自動同期を止めたなら、戻す。

## クラスタの外からの検知

停止した系は自分の停止を通知できない。Discord への通知はクラスタの中から出るので、**クラスタ・ノードが止まると Discord にも何も届かない**。
基盤側は、クラスタの中から外の監視へ定期的に知らせる形（heartbeat）と、警報（Alertmanager）の Discord への通知を用意している（基盤の作業。本リポジトリの外）。
AST 側は次を決める。

| 何を | 誰が見る | 通知の経路 | 備考 |
| --- | --- | --- | --- |
| クラスタ全体の生存（heartbeat が途絶えた） | 外部の監視サービス（基盤の作業で選ぶ） | **Discord 以外を 1 つ入れる**（メール・スマートフォンの通知） | Discord が使えないとき・アカウントを使われているときにも届くように、受け手を Discord だけにしない |
| AST の Pod の生存（risk-management・order-execution・market-monitor・opend） | 基盤の Prometheus（Pod の Ready・再起動の回数） | Alertmanager → Discord（＋上と同じ別の経路） | AST の警報ルールは `deploy/observability/alerts/` にある。業務の警報（損切りの入力の劣化・発注の発行の滞留など）もここ |
| 業務の異常（統制の発動・発注・約定） | 通知サービス | Discord（業務通知） | Discord が止まると届かない。**証券会社のアプリの約定通知を有効にしておく**（系の外の、独立した検知になる） |
| 夜間の欠け | オーナー（翌朝） | [夜間の台帳の要約 Runbook](nightly-ledger-summary-runbook.md) | ログに頼らない台帳からの要約 |

- 外部の監視サービスに Discord 以外の通知先を登録するのは**オーナーの操作**である（登録の仕方は基盤側の手順書に従う）。
- 自宅のホストで動かしていたときの死活監視（[ホスト側の死活監視 Runbook](host-liveness-monitor-runbook.md)）は、クラウドの単一ノードでは使わない（ホストの PowerShell 前提）。役割は上の heartbeat が引き継ぐ。

## 確認（この手順が成功したと言える条件）

- `hold` の最後に `OK: 保持が効きました` が出た。
- `status` で「保持: 入っている」と表示される。
- Discord から `/killswitch off` を試すと失敗する（保持の間）。※ 確かめるときは、保持を入れたままにしておく。
- 保持の後に、新しい新規建ての注文が証券会社に出ていない。

## 失敗したときの分岐

| 症状 | 原因の候補 | 次の手 |
| --- | --- | --- |
| `hold` が 3 で終わる | `--live` を付けていない | 確かめたうえで `--live` を付ける |
| `hold` が 1 で終わる（印が出ない） | risk-management の Pod が起動しない（DB・ブローカに届かない・イメージが取れない）。Secret の名前が chart と違う | `k3s kubectl -n ai-stock-trading get pods -l app=risk-management-service` と `describe` を見る。**起動しない間、発注執行は新規建てを送らない**（送る直前の停止の照会が不明になり、送らない側へ倒れる）。急ぐなら下の「最後の手段」 |
| `kubectl` が届かない | k3s が止まっている・ノードの負荷 | VPS の管理画面のコンソールから入る。ノードが応答しないなら下の「最後の手段」 |
| Discord から解除できてしまう | 保持が入っていない（Secret が無い・Pod が古い） | `status` を見る。`hold` をやり直す |

### 最後の手段（kill switch に頼らない止め方）

kill switch が効いたと確かめられないときは、**証券会社への経路を断つ**。

1. 証券会社のアプリで、未約定の新規建ての注文を取り消す（アプリの操作。本システムを経ない）。
2. 発注執行を 0 台にする:

   ```bash
   k3s kubectl -n ai-stock-trading scale deployment order-execution-service --replicas=0
   ```

   🔴 **これは損切りも止める。** ソフトウェアの逆指値（系の側で検知して成行で決済する手法）は発注執行が出すので、0 台の間は動かない。
   ブローカー側の逆指値（証券会社が持つ）は残る。建玉があるなら、証券会社のアプリで逆指値を置くか手仕舞う。
3. それでも止まらない（ノードに入れない等）なら、証券会社のアプリでログインのパスワードを変える（OpenD は次のログインから失敗する。動いている接続がその場で切れるかは証券会社の仕様に依存し、確かめていない。戻すには OpenD の再認証が要る。[障害対応 Runbook](incident-response-runbook.md) §OpenD と証券口座）。

## 記録

- 実施日時・実施者・理由・結果（`hold` の出力）を、[障害対応 Runbook](incident-response-runbook.md) §記録 の様式で残す。
- kill switch の起動・解除は設定の変更履歴（actor `operator-hold`）に自動で残る。

## 限界（この手順で担保できないこと）

- **SSH とノードが生きていることが前提である。** ノードごと止まっている・SSH の鍵を失ったときは使えない（「最後の手段」の 1・3 は証券会社のアプリだけで行える）。
- **保持の間も、Discord から締める向き以外の操作はできる**（例: 段階の昇格・報告書の確定）。kill switch が新規建てを止めるので取引には効かないが、保持を外した後に効く。アカウントを使われている疑いがあるときは手順 3 でボットを止める。
- risk-management が起動しない間は保持も起動しない。そのときの新規建ての停止は、発注執行の「送る直前の停止の照会が不明なら送らない」に頼る。
- 外部の監視サービスへの登録は基盤の作業とオーナーの操作に依存する。**登録が済むまでは、クラスタの停止に気付く手段は証券会社のアプリの通知と、オーナーが定期的に `status` を見ることだけである。**
