---
title: IADR-0608 単一ノードのクラウドの構成は values.yaml に直接重ねる values-cloud とし、自前のイメージを digest で固定して欠けたら描画で止め、DB のパスワードは Secret から読み、検索を使わない環境では判断のプロンプトにその旨を書く
type: impl-adr
status: Accepted
related_ids: [ADR-0006, ADR-0024, ADR-0073, FR-04, FR-08, NFR, IADR-0053, IADR-0341, IADR-0455, IADR-0496, IADR-0598, IADR-0607]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-08)
---

# IADR-0608: 単一ノードのクラウドの構成（values-cloud）と、検索を使わない判断の明示

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-11-05
- 決定者: オーナー（欧州 8 GB・RAG なし・画面なし・上限 5,000 円のまま・モデルと effort は変えない。planning#868）／ Claude Code（構成の形・digest の扱い・プロンプトの 1 行）

## 起点・関連

- issue: #1525（AST#24 の子）。計画: ADR-0006（PoC の常駐先）・ADR-0024（OpenD の無人再起動・固定の egress IP）・ADR-0073（週末の口座照会）・FR-08（RAG）。
- 環流: planning#868（クラウド移行の前提の確認。RAG を外すことと FR の関係は同 issue の 2）。
- 実装仕様書: [`.ai-context/specs/20261105_1525_cloud-single-node-values-and-runbook.md`](../specs/20261105_1525_cloud-single-node-values-and-runbook.md)
- 手順書: `docs/operations/cloud-single-node-migration-runbook.md`

## コンテキストと課題

- 経路 B（`values-local.yaml`）は、MSP の knowledge ユニット（文書・検索）・Stage 0 の記録・Web の画面を前提にしている。
  クラウドの単一ノード（8 GB）には、そのどれも置かない。
- イメージはタグ（`latest`）で参照している。クラウドではノードでビルドして取り込むので、同じタグが別の中身を指し得る。
- DB のパスワードは values の平文（既定は開発用の値）を接続文字列へそのまま描く。クラウドではリポジトリの既定値を 1 つも使わない（MSP#2401 と同じ要件）。
- 検索を外すと、判断のプロンプトの参考情報の節が出なくなる。ニュースの状態の行（取得済み）は「個々の記事は下の参考情報にあるものだけを渡します」と書くので、
  参考情報が恒久的に無いことは読めない（仕様書 §RAG を外したときの実測）。

## 検討した選択肢

### 構成の重ね方

1. **`values-local.yaml` に `values-cloud.yaml` を重ねる** — 差分だけで済むが、経路 B の調整（Stage 0・KB・Discord の ID）がクラウドへ黙って流れ込む。helm の list は丸ごと置き換わるので、env の 1 行だけを外すことができない。
2. **`values.yaml` に直接重ねる単独の上書き（採用）** — 写す量は増えるが、クラウドに何が載るかがファイル 1 つで読める。

### digest の置き場

1. **values-cloud に digest を書く** — ノードでビルドしてからしか決まらない。書けば実在しない値か、古い値になる。
2. **values-cloud は digest を要求するだけにし、値はノードで書き出した別ファイルから重ねる（採用）** — 欠けたら描画で止まる（fail-closed）。

### 検索を使わないことの伝え方

1. **何もしない** — 否定の文（無いことは、無いことを意味しない）はあるが、恒久的に無いことと検索の取りこぼしを区別できない。
2. **ニュースの行の文言を環境で変える** — 4 状態の文言（IADR-0455）の意味が環境で割れる。
3. **検索の取得口が未構成のときだけ、別の 1 行を足す（採用）** — 経路 B のプロンプトは変わらない。

## 決定

1. **`values-cloud.yaml` は `values.yaml` に直接重ねる**（`-f values-cloud.yaml` のみ。`values-local.yaml` と併用しない）。
   - KB の宛先（`KnowledgeBase__Documents__BaseUrl` / `__Search__BaseUrl`）は書かない（既定の空＝no-op）。
   - Stage 0 の記録は無効。報告書の KB の写し（`ReportDraftKnowledge`）は無効。
2. **`global.image.requireDigest: true` のとき、自前のイメージに digest が無ければ描画で止める。**
   - 対象: 11 サービス・OpenD・認証サイドカー・取引サイクルの CronJob（有効なとき）。
   - digest は `services.<名>.digest`・`opend.image.digest`・`opend.authGateway.image.digest`・`tradingCycle.cronjob.image`（`@sha256:` を含む参照）で与え、`repository:tag@sha256:…` の形で描く。
   - `pullPolicy` は `Never`。digest はノードで書き出した別ファイルに置き、コミットしない（手順書）。
   - 既定（`requireDigest` なし・digest なし）の描画は 1 バイトも変えない。
3. **`global.db.passwordSecret`（`name` / `key`）を与えたときは、`DB_PASSWORD` を Secret から読み、接続文字列のパスワードを `$(DB_PASSWORD)` で組む。**
   - `global.db.password` は描かない。values-cloud はこれを使う。
   - 与えないとき（既定・経路 B）の描画は変えない。
4. **検索の取得口が未構成（no-op）のときだけ、判断のプロンプト（本判断・一次）のニュースの状態の行の直後に 1 行を足す**: `RetrievalNotConfiguredLine`。
   - 判定は取得口の型で行う（`IRetrievalContextProvider.IsConfigured`。no-op は false）。
   - model・effort・max_tokens・用途は変えない。
   - Stage 0 の記録のプロンプトには足さない（当時の参考情報を as-of 入力から渡すので、本番の検索の構成と無関係）。
5. **秘密の供給方式は `externalSecrets.enabled` で切り替える。** values-cloud の既定は `false`（手で作る Secret）。MSP#2401 の決定（ESO か否か）が出たら、それに合わせて切り替える。
6. **OpenD**:
   - 配布物の tar の sha256 を記録してからノードでイメージを作る。
   - 初回の SMS 認証は利用者が `kubectl attach` で行う。
   - 取引パスワードはクラスタに置かない（SIMULATE では解錠は要らない。実弾の解錠は別の判断）。
7. **ArgoCD は最初の稼働では使わない。** values-cloud を ArgoCD の valueFiles に入れないことを試験で固定する（`targetRevision: main` が空のブランチを指すため、誤って同期すると空の構成で上書きされる）。

## 結果

- 良い影響:
  - クラウドに何が載るかが 1 ファイルで読める。
  - 取り込んだイメージと描画のイメージが digest で一致する。
  - リポジトリの既定の DB パスワードが描画に出ない。
  - 検索を使わない環境の判断が、参考情報の不在を恒久的なものと読める。
- 悪い影響・トレードオフ:
  - values-local と values-cloud の env が二重に保守される（片方だけ直す事故が起き得る）。
  - 取引判断のプロンプトの既定（`values.yaml`。検索は未構成）は 1 行増える（本番の既定は稼働していない）。
- 計画との関係: クラウドの PoC の期間は FR-08（RAG の利用）を満たさない。オーナーの決定（planning#868 の 2）として環流済みで、本 IADR の決定 4 を同 issue へ追記する。

## 残る制約

1. 秘密の方式（ESO か手で作る Secret か）は MSP#2401 の決定待ちである。決まったら values-cloud の `externalSecrets.enabled` と手順書を合わせる。
2. digest の参照（`repository:tag@sha256:…`）で、ノードへ取り込んだイメージを kubelet が解決できることは、ノードの上で確かめる（手順書の初日の確認）。
3. 基盤との接続の暗号化（Postgres の検証つき TLS・RabbitMQ の amqps・LLM ゲートウェイの https と基盤の CA の信頼）は本 IADR の範囲外で、#1535 が入れる。values-cloud はその値を参照するだけにし、#1535 の後に写す。
4. MSP の最小構成の名前空間・ホスト名（`platform-infra` の postgres・rabbitmq・keycloak・otel-collector、`microservices-platform` の LlmGateway）は経路 B と同じと仮定した。MSP#2401 が変えるなら合わせる。
