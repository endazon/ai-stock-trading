---
title: IADR-0644 取引ユニットの外への HTTPS を、段（Off 既定 / Report / Enforce）つきで基盤の外向きの中継（名前の許可表）へ寄せ、Discord.Net も環境の中継に従わせる。外への ipBlock はノード自身の公開アドレスの除外が無ければ描かない
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0631]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
plan_refs: [ADR-0006, ADR-0024]
---

# IADR-0644: 外への HTTPS の中継と、ノードの公開アドレスの除外の必須化

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-08
- 決定者: 所有者の裁定（2026-11-07「稼働前に medium・low もすべて直す」）・claude（段の形・NO_PROXY の導き方・止める条件）

## 起点・関連

- 起点: クラウドの稼働前の監査（非公開）の 2 件。外への HTTPS の出が宛先の名前で絞られていないこと、ノード自身の公開アドレスの除外が空でも描けること。
- 改める記録: [IADR-0631](./IADR-0631_cloud-opend-least-privilege-and-service-network-policy.md) の結果「外の 443 を許したサービスは、外の公開アドレスの 443 ならどこへでも届く」。
  受容の前提（宛先の名前で絞る手段が無い）は、基盤の外向きの中継（基盤の IADR-0705）で変わった。
- 作業仕様書: [20261108_egress-via-platform-proxy](../specs/20261108_egress-via-platform-proxy.md)（実測・宛先の表・陰性対照）。

## 決定

1. **段 `networkPolicy.egressProxy.mode`（`Off` / `Report` / `Enforce`）。既定は `Off`（中継を使わない従来の形）。**
   `Report` は `externalHttps: true` のサービスへ `HTTPS_PROXY`・`NO_PROXY` を渡し、出の方針に中継の口を足し、外の 443 も残す。`Enforce` は中継の口だけにする。
   `Off` 以外は基盤の側で中継が取引ユニットの Pod を受けることが前提なので、既定へは入れない。上げる手順は通信の制限の Runbook。
   - 理由: 中継を使う env を入れると、.NET は中継が無いときに直へ戻らない。基盤の側より先に入れると外への HTTPS が止まる。
2. **env は `HTTPS_PROXY` と `NO_PROXY` だけを渡す**（`HTTP_PROXY`・`ALL_PROXY` は渡さない。内側の平文の HTTP と gRPC は中継へ回らない）。
   `NO_PROXY` は描画がクラスタの中の名前（サービス名・基盤の別名・関係する名前空間の接尾辞・`.svc`・`.cluster.local`）から導き、外の名前は入れない。
   extraEnv で同じ名前を与えるのは描画で止める。**Discord.Net は既定で環境の中継を読まない（実測）ので、REST と Gateway の提供者を明示で環境の中継に従わせる**
   （`HTTPS_PROXY` が無ければ直に繋ぐので、`Off` では従来と同じ）。
3. **外への ipBlock を描くとき（サービスの方針・OpenD の出）、`networkPolicy.publicEgress.extraExcept` が空なら描画で止める。** 要素は IPv4 の CIDR だけを受ける。
   値はリポジトリに書かず、配備の値のファイルで与える。

## 検討した代替案

- 既定を `Report` にする: 基盤の側が受けるまで外への HTTPS が止まる（決定 1 の理由）。採らない。
- `NO_PROXY` を values に手で書く: サービスや別名を足したときに写し忘れ、内側の https（Keycloak・LLM ゲートウェイ）が中継へ回って止まる。描画が導く形にした。
- `extraExcept` の代わりにノードのアドレスを描画で引く: 描画はクラスタの外の値を知らない（lookup は `helm template` で空）。止める形にした。

## 結果

- `Enforce` に上げると、6 つのサービスの外への HTTPS は中継の許可表の名前だけになる。宛先の表は作業仕様書にあり、基盤の許可表と対で直す。
- 段を変えると 6 つのサービスの Pod が作り直される。OpenD は作り直されない。

## 残るリスク

- OpenD の出は中継を通らない（独自の手順で証券会社のサーバの任意の口へ繋ぐ）。IADR-0631 の残る範囲のまま。
- Discord の Gateway は再接続のときに接続ごとに返される名前へ繋ぐ。完全一致の許可表では列挙しきれないので、基盤の側の表の形（接尾辞など）で受ける必要がある。基盤の側へ渡した。
- ノードのファイアウォールの Pod の網からの受け入れを口で絞る案は、このリポジトリの外（基盤のノードの設定）で、本記録の範囲外。
