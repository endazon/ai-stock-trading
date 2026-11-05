---
title: IADR-0615 基盤への接続は基盤の CA で検証する TLS（Postgres VerifyFull・RabbitMQ amqps＋名前の照合・LLM ゲートウェイは専用の検証で CA を信頼）へ値で切り替え、CA が無ければ描画で止める
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-18, ADR-0006, IADR-0608, IADR-0568, IADR-0129, IADR-0323, IADR-0332]
author: claude (Claude Code)
created: 2026-11-06
updated: 2026-11-06
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
---

# IADR-0615: 基盤への接続の TLS（AST 側の段）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-11-06
- 決定者: Claude Code（実装）／ オーケストレーター（CA の信頼は基盤と同じく SSL_CERT_DIR に足す・ゲートウェイの検証と失敗の試験・平文へ戻さない、の指示）

## 起点・関連

- issue: #1535。基盤の段の表（MSP の IADR-0591 の 4b-2）の AST 側。LLM ゲートウェイの https は MSP#2409。
- 実装仕様書: [`.ai-context/specs/20261106_1535_platform-infra-tls.md`](../specs/20261106_1535_platform-infra-tls.md)
- 手順書: `docs/operations/platform-infra-tls-cutover-runbook.md`

## コンテキストと課題

- 基盤は Postgres・RabbitMQ に TLS を足し、段 1 では平文と併存させている。
- AST の chart は平文で繋ぐ。AST が切り替わるまで、基盤は平文の口を閉じられない。
- RabbitMQ.Client は amqps の URI を受けると、サーバ証明書の名前の不一致を既定で受け入れる（基盤の実測）。
- クラウドでは LLM ゲートウェイが https になる。証明書は基盤の CA が発行し、公開の CA ではない。

## 検討した選択肢（ゲートウェイの CA の信頼）

1. **`SSL_CERT_FILE` を基盤の CA へ置き換える** — 外部 API（Finnhub ほか）の公開 CA を信頼しなくなる。採らない。
2. **`SSL_CERT_DIR` に足すだけ** — 基盤と同じ形で、ブローカにはこれを使う。
   - ゲートウェイについては、検証の成否がプロセスの環境変数に依り、試験で固定しにくい（子プロセスが要る）。
3. **ゲートウェイの HttpClient に専用の検証を置く（採用。2 と併用）** — 次のとおり判定する。
   - システムの検証が通ればそれで受ける。
   - 連鎖の誤りだけなら、基盤の CA を根に組み直して受ける。
   - 名前の不一致は常に拒む。
   - 試験はループバックの TLS の受け手で、通る・名前違い・CA なし・別の CA を固定できる。

## 決定

1. **値で切り替える。** 既定（`values.yaml`）は平文で、`values-local.yaml` が有効にする（段を踏む・戻し方は値を false にする）。
   - `global.db.tls.enabled`
   - `global.rabbitmq.tls.enabled` / `port`
   - `global.infraCa.configMap` / `caKey`
2. **形は基盤の chart と同じ。**
   - DB: `SSL Mode=VerifyFull;Root Certificate=/etc/platform-infra-ca/<caKey>`
   - ブローカ: `amqps://…@<host>:<tls.port>` と `SSL_CERT_DIR=/etc/ssl/certs:/etc/platform-infra-ca`
   - CA の ConfigMap は読み取り専用・非 optional でマウントする。
   - 検証を緩める値（Require・Prefer・Trust Server Certificate）を与える口は持たない。
3. **TLS を有効にして CA の設定が無ければ、描画で止める。** ConfigMap が無いクラスタでは Pod が起動しない。どちらも平文へ戻らない。
4. **`UseAiStockTradingRabbitMq` は amqps のとき、受け入れてよい誤りを空にし、`ServerName` を URI の host にする**（基盤の `UsePlatformRabbitMq` と同じ）。平文のときは何も変えない。
5. **LLM ゲートウェイの HttpClient（`llm`・`report-llm`）の主ハンドラは `PlatformInfraCaTrust.CreateHandler`。**
   - CA の場所は `PlatformInfraCa:CertificatePath`。chart が、CA をマウントしたサービスへ描く。
   - 名前の不一致・証明書なし・CA なしは拒む。
   - 検証の失敗は呼び出しの失敗であり、従来の失敗の経路（取引判断は Hold・報告書はプレースホルダ）を通る。http へは戻らない。
6. ゲートウェイの URL を https にするのは、クラウドの構成（values-cloud）の側である。経路 B は http のまま。
7. **クラウドの構成（values-cloud。IADR-0608）も同じスイッチを入れる**（DB の TLS・ブローカの TLS・ゲートウェイの https）。
   - 接続の形は chart が持ち、values-cloud は値を写すだけにする（両方の構成で同じ形になる）。
   - DB のパスワードを Secret から読む接続文字列（IADR-0608 決定 3）にも、同じ VerifyFull＋基盤の CA の接尾辞を付ける。
   - CI（helm.yml の values-cloud の段）が、DB・ブローカ・ゲートウェイの全件が TLS であることを確かめる。

## 結果

- 良い影響:
  - 基盤との通信が、CA と名前で検証された TLS になる。
  - AST が切り替わると、基盤は平文の口を閉じる段（4b-3）へ進める。
  - クラウドのゲートウェイの https を、外部 API の検証を変えずに信頼できる。
- 悪い影響・トレードオフ:
  - values-local で有効にするので、経路 B の次の配備で切り替わる。CA の ConfigMap が AST の名前空間に無いと、Pod が起動しない（手順書で配備の前に確かめる）。

## 残る制約

1. gRPC（`LlmGateway:Grpc`。h2c）の経路には専用の検証を入れていない。クラウドでは基盤が h2c の受け口を外す。gRPC を TLS で使うなら、同じ検証を channel の handler へ足す。
2. 基盤の側の段 4b-3（平文を閉じる）は基盤の作業である。基盤の IADR-0591 の段の表への本 PR の記録も、基盤の側で行う。
3. クラウドの名前空間 ai-stock-trading の CA の写しは、手で作る（クラウド移行の手順書の §5）。基盤が証明書の組を作り直したら、写しも差し替える。
