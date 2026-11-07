---
title: IADR-0631 クラウドの名前空間のストア（SecretStore openbao）を、基盤の OpenBao の TLS の口（https・8443）へ向け、同じ名前空間の ConfigMap openbao-ca の CA で検証する。宛先が https でない・CA が無ければ描画で止める。基盤の併存の段の後に当てる
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-05, ADR-0080, IADR-0623, MSP:IADR-0696, MSP:IADR-0689]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-05)
---

# IADR-0631: 名前空間のストアを基盤の OpenBao の TLS の口へ向ける

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「OpenBao も TLS にする」）・claude（方式）

## 起点・関連

- 要求 **NFR-05**（秘匿は OpenBao）。通信の暗号化の番号は計画に無いので裸の `NFR` を併記する。
- 基盤の MSP:IADR-0696（OpenBao の受け口を TLS の 8443 だけにする。受け口の証明書は基盤の `tls-cert.sh` が作り、CA を ConfigMap `openbao-ca` に置く）。
  これにより MSP:IADR-0689 決定 3（受け口は平文）が改められた。
- 前提: IADR-0623 決定 2（名前空間の SecretStore・kubernetes 認証・ServiceAccount `ast-eso-reader`）。
- 作業仕様書: `.ai-context/specs/20261107_cloud-openbao-tls-store.md`。

## コンテキストと課題

- 取引ユニットの ExternalSecret は、写す値の全部（所有者の TOTP の種・ボットの資格・DB とブローカの資格・moomoo のログイン）を、名前空間のストアを通して
  基盤の OpenBao から受ける。基盤の受け口が TLS だけになると、平文の宛先のストアは届かなくなる。

## 決定

### 決定 1 — ストアは https と、同じ名前空間の ConfigMap の CA で検証する

- values-cloud: `server: https://openbao.openbao.svc:8443`・`caProvider: { type: ConfigMap, name: openbao-ca, key: ca.crt }`。
- ConfigMap は基盤の `tls-cert.sh` が置く（受け口の証明書を作り直すと、基盤が CA を併記してから外す。取引ユニットは何もしない）。名前空間のストアは別の名前空間の
  ConfigMap を読めないので、同じ名前空間に置く。取引ユニットを新しく入れるとき（名前空間が後から作られる）は、入れた後に基盤の
  `tls-cert.sh --live publish-ca --namespace ai-stock-trading` で置く（それまでストアは Ready にならない）。
- 採らなかった: CA を values に埋める（`caBundle`）— 基盤が受け口の証明書を作り直すたびに取引ユニットの chart を当て直すことになる。

### 決定 2 — 宛先が https でない・CA が欠けると描画で止める

- `openbaoStore.create=true` のとき、`server` が `https://` で始まらない、または `caProvider` の `name`・`key`・`type`（ConfigMap か Secret）が欠けると `fail` する。
  検証を外す口は chart に持たない。既定（`create=false`）と values-local は変わらない。
- CI の `helm.yml` の段が、宛先・CA の描画と、外したときに描画が止まることを固定する。

### 決定 3 — 基盤の併存の段の後に当てる

- 順序: 基盤の `tls-cert.sh issue` → 基盤の併存の段（平文の 8200 と TLS の 8443 の両方）→ 基盤のストア → **本 chart** → 基盤が平文を閉じる。
  先に当てると、ストアが平文の受け口へ https で話して届かない（写した Secret は残るが、更新が止まる）。手順は移行の手順書 §5.4 と基盤の手順書。

## 結果

- 良い影響: ESO と基盤の OpenBao の間の通信が暗号化され、受け口の証明書の検証で宛先の取り違えも止まる。
- 悪い影響: 名前空間を新しく作るときに、基盤の手順を 1 つ（`publish-ca`）流す。

## 残余

1. ConfigMap `openbao-ca` が無い間（新しく入れた直後）は、ストアが Ready にならない（同期が始まらない）。手順で置く。
