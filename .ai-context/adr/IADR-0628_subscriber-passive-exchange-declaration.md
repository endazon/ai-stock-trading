---
title: IADR-0628 クラウドでは購読側のサービスは購読する exchange を宣言せず、受動の宣言（在るかの確かめ）だけをする。キューの宣言と束ねは続け、exchange は基盤の起動の直後の初期化が先に宣言し、無い間はブローカの初期化の再試行で待つ
type: impl-adr
status: Accepted
related_ids: [NFR-18, ADR-0013, IADR-0129, IADR-0568, IADR-0627, MSP:IADR-0691, MSP:IADR-0681, MSP:IADR-0689]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-18)
---

# IADR-0628: 購読側の exchange の受動の宣言

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07: 購読側の権限を購読に要るものだけへ絞る）／ Claude Code（起案・実装）

## 起点・関連

- 非機能 NFR-18（通信の保護）。前提: IADR-0129（Wolverine の配線の単一の出所）・IADR-0568（ブローカのサービスごとの利用者）。
- 基盤の側: MSP の IADR-0691 §残る制約 2（購読の exchange の configure を外すには、購読側の宣言を受動に替え、exchange を誰が先に宣言するかを決める変更が両ユニットに要る）。
  権限の生成は MSP の IADR-0681（配線から導く生成器）・IADR-0689 決定 12（クラウドの写し）。
- 実装仕様書: [`.ai-context/specs/20261107_broker-subscriber-passive-exchanges.md`](../specs/20261107_broker-subscriber-passive-exchanges.md)

## コンテキストと課題

- 購読側の Wolverine は、起動時に購読する exchange を非受動で宣言する（規約ルーティングと AutoProvision）。RabbitMQ は在る exchange の
  非受動の宣言にも configure を求め、configure は削除も許す。このため基盤は購読側の利用者に、購読する（自分が発行しない）exchange の
  configure を与えている。
- 購読側に要るのは、exchange の read（`queue.bind`）と自分のキューの権限だけである（RabbitMQ の権限の表。受動の宣言は configure を要らない）。
- 受動の宣言は exchange が無いと 404 で失敗する。購読側が発行側より先に起動する順序の手当てが要る。発行だけの型の exchange は、
  発行側の 1 通目の発行のときに作られる（遅延）。

## 検討した選択肢

| 案 | 評価 |
| --- | --- |
| 購読側は宣言しない（`DisableAutoProvision` / 外部所有） | 在るかを確かめずに束ねへ進み、束ねの失敗が初期化の印（キューは失敗でも初期化済みになる）で隠れる。採らない |
| **購読側は受動で宣言する（採用）** | 起動は exchange をキューより先に確かめるので、束ねの無いまま動かない |
| 起動順: 初期化の再試行で待つだけ | 稀にしか発行しない型の購読側が、1 通目の発行まで再起動を繰り返す。保険としてだけ使う |
| 起動順: 起動を止めずに後から束ねる | トポロジの宣言が Wolverine の外へ出て単一の出所を崩す。束ねまでの取りこぼしが静かに起きる。採らない |
| 起動順: 発行側が起動時に自分の exchange を宣言する | 発行する型の一覧をサービスごとに持つ必要がある。漏れると待つだけの案と同じ。採らない |
| **起動順: 運用が先に宣言する（基盤のブローカの起動の直後の初期化が定義ファイルから読み込む。採用）** | 基盤の生成器は購読の exchange の一覧を導いている。初期化は利用者の権限を当てる前に走る |

## 決定

### 決定 1: 購読する exchange の宣言の仕方を 2 つにし、既定は従来の宣言とする

- 列挙 `SubscribedExchangeDeclaration`（`Declare`＝既定・従来／`Passive`）。共通配線 `UseAiStockTradingRabbitMq` の既定の入口は環境変数
  `WOLVERINE_SUBSCRIBED_EXCHANGES` を読む（未設定・空は `Declare`、名前だけを大小無視で受け、数や綴りの誤りは起動で止める）。
  明示の引数を取る入口を足す（試験の固定用。環境変数を読まない）。サービスの `Program.cs` は変えない。

### 決定 2: `Passive` では、購読のキューを束ねる exchange だけを受動にする

- 規約の `ConfigureListeners` で、受ける型の exchange（規約と同じ `ToMessageTypeName()` ＋ `MaybeCorrectName`）に `DeclarePassive = true` を付ける。
- キューの宣言と束ね、デッドレター（`<キュー>_error`・共有）、発行だけの型の exchange は従来どおり宣言する。
- 自分も発行する型（例: 発注の承認）の exchange も受動になる。発行は宣言を要らない（exchange は運用が先に宣言する）。

### 決定 3: 起動順は運用の先の宣言を主、ブローカの初期化の再試行を保険とする

- 主: 基盤のクラウドのブローカは、起動の直後の初期化で、取引ユニットの購読される exchange（基盤の生成器が導く一覧。fanout・durable・自動削除しない＝
  Wolverine の宣言と同じ形）を利用者の権限を当てる前に宣言する。購読側が繋がる時点で exchange は在る。
- 保険: exchange が無いと受動の確かめが失敗し、Wolverine のブローカの初期化が 5 秒おきに 20 回までやり直す。尽きれば起動が失敗し、Pod が再起動する。
  exchange はキューより先に確かめるので、束ねの無いまま動かない（実ブローカで確かめた）。

### 決定 4: 段は chart の 1 つの設定で切り替える

- `global.rabbitmq.subscribedExchanges`（既定 `Declare`）。`Passive` のときだけ env を描く（既定・ローカルの描画は変えない）。values-cloud だけ `Passive`。
- 順序: 本変更のマージと取引ユニットのイメージの更新の後に、基盤の生成器から購読の exchange の configure を外す。

## 結果

- クラウドの購読側は、購読の exchange を宣言も削除もしない。基盤は購読側の利用者から購読の exchange の configure を外せる。
- 実ブローカ（RabbitMQ 3.13）で、購読の exchange の configure を持たない利用者が、起動・束ね・受信できること、非受動の宣言と削除が 403 で
  拒まれること、先に起動したときに exchange が現れるまで待つことを、Integration E2E（`BrokerSubscriberLeastPrivilegeE2ETests`）で固定した。
- 既定・ローカルの挙動は変わらない。

## 残る制約

1. 運用の先の宣言が無い新しいブローカでは、稀にしか発行しない型の購読側は 1 通目の発行まで起動の失敗と再起動を繰り返す（保険の範囲）。
2. 既定の exchange への write は、受信キューを持つ利用者に残る（戻しに要る）。その経路のメッセージは、受け手が発行元の印を確かめる仕組み（IADR-0627）で扱う。
3. exchange の名前は規約の送り先の名前に依る。型に名前の別名（`MessageIdentity`）を付けたときも同じ関数で導くが、基盤の生成器は型の完全名を前提にする（生成器の注記）。
