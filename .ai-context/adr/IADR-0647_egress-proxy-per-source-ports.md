---
title: IADR-0647 外への HTTPS の中継の受け口を送り元（サービス）ごとに描き（networkPolicy.services.<名前>.egressProxyPort）、受け口の欠け・重なり・旧の単一の口は描画で止める
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0644]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
plan_refs: [ADR-0006]
---

# IADR-0647: 中継の受け口を送り元ごとに

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-08
- 決定者: claude（依頼はオーケストレーター 2026-11-08。受け口の割り当ては基盤の側の設計）

## 起点・関連

- 改める記録: [IADR-0644](./IADR-0644_egress-via-platform-proxy-and-node-except-required.md) の決定 1（中継の口を 1 つ〔3128〕で描く）。段（Off / Report / Enforce）・`NO_PROXY`・Discord.Net・ノードの公開アドレスの除外（決定 2・3）は変えない。
- 基盤の側: MSP#2517（基盤の IADR-0709）。中継の受け口を送り元ごとに分け、受けた口で送り元を見分けて送り元ごとの許可表を使う。
- 作業仕様書: [20261108_egress-proxy-per-source-ports](../specs/20261108_egress-proxy-per-source-ports.md)（割り当ての表・母集合・陰性対照）。

## 決定

1. **受け口は宣言の側に置く**（`networkPolicy.services.<名前>.egressProxyPort`）。`values-cloud.yaml` が基盤の割り当て（3141〜3146）を持つ。
   `HTTPS_PROXY` は `http://<networkPolicy.egressProxy.host>:<受け口>`、出の方針の中継の規則はその受け口の TCP だけ。旧の `url` / `port` は持たない。
   - 理由: 受け口が送り元の身元になるので、サービスごとに違う値を同じ宣言（externalHttps と並べて）で持つのが写し違えにくい。
2. **描画で止める**: `Report`・`Enforce` で externalHttps のサービスに受け口が無い・範囲の外。段に依らず、受け口の重なり・externalHttps でないサービスの受け口・旧の `url` / `port`。
   - 理由: 受け口が無いまま上げると、そのサービスの外への HTTPS が届かない。重なると基盤の中継が別の送り元と見分け、別の許可表を使う。旧のキーを黙って無視すると、書いた側の意図と描画が食い違う。

## 結果

- 既定（`Off`）の描画は変わらない。`Report` へ上げるのは基盤の側の適用の後（手順書）。
- 割り当てを変えるときは基盤と対で変える（試験が values-cloud の値を基盤の割り当ての写しと突き合わせる）。

## 残るリスク

- 試験が持つ割り当ての写しは基盤の値と機械で突き合わせていない（リポジトリを跨ぐ検査は持たない）。基盤が割り当てを変えたら、手で追随する。
