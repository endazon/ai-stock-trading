---
title: キット版 check-kit-sync.js が --require-planning を失っている（fail-open を閉じる手段が無い検査器が 2 本ある）
date: 2026-08-15
status: open
dispatched: true
source_ids: [NFR, IADR-0191]
severity: high
planning_issue: 343
---

# 環流: fail-open を閉じるフラグがキット版から失われている

## 要旨

**planning#342 で配られたキット版 `check-kit-sync.js` は `--require-planning` を持たない。**
本リポジトリが環流した版（planning#336 で accepted）には在ったが、**一般化の過程で落ちている。**

**キット原文をそのまま採ると、[IADR-0191](../docs/adr/IADR-0191_kit-sync-classification.md) 決定3 が
塞いだ穴が開き直る。** そのため本リポジトリは分類 B（固有デルタ）として自版を維持している。

## 実測（推測ではない）

`planning` を持たない空の作業ディレクトリで、フラグ付きで実行した。

| 版 | コマンド | 結果 |
| --- | --- | --- |
| **キット版** | `check-kit-sync.js --require-planning` | ⚠️ **`warn` ＋ exit 0（skip）** |
| 本リポ版 | 同上 | ✅ **exit 1（fail）** |

キット版はフラグを**認識せず、黙って無視する。**

## 🔴 なぜ実害があるか —— **回帰テストでは捕まらない**

CI は 3 ジョブで `--require-planning` を渡している。キット原文で上書きすると:

1. CI はフラグを渡し続ける
2. **フラグは無視される**
3. submodule の取得に失敗すると **検査は skip して緑になる**
4. 🔴 **回帰テストは通り続ける** —— `run:` 行に文字列が在ることしか見ていないため

> **「配線を見るテスト」は「配線が効いていること」を保証しない。**
> 本リポジトリはこの型を**本セッションで 3 度踏んでいる**（自己発火する語の一致 / 弱い変異 /
> 本件）。**キットへ配る検査器では、fail-open を閉じる手段の有無が特に重要である** ——
> 配布先すべてで同じ穴が同時に開くためである。

## もう 1 件: `check-feedback-status-sync.js` にも同じ手段が無い

planning#342 で新設された `check-feedback-status-sync.js` は、計画リポを参照できないとき
**skip して exit 0** に倒れるが、**`--require-planning` に当たるフラグを持たない。**

本リポジトリは**キット配布物を書き換えず、CI ジョブ側で populate を明示的に確認**して塞いだ。
**が、これは各リポジトリが個別に気付いて配線しなければならない**ことを意味する。

## 提案

1. **キット版 `check-kit-sync.js` へ `--require-planning` を戻す**（環流済みの機能の復帰）
2. **fail-open な検査器には一律で「fail-closed へ倒すフラグ」を持たせる**規約にする
   —— 対象は少なくとも `check-doc-links.js`（既に有る）・`check-kit-sync.js`・
   `check-feedback-status-sync.js`・`check-test-traceability.js`
3. **キットの配布時チェックに「フラグの有無」を含める** —— 一般化の過程で機能が落ちたことを
   キット側で検出できるようにする

## 参考

- 実装側の対応: 本リポは `check-kit-sync.js` を分類 B で維持し、**フラグを尊重することを
  実走で確かめる回帰テスト**を追加した（合成した未 populate 環境で exit 1 を確認）。
- 起点: [#494](https://github.com/endazon/ai-stock-trading/issues/494)
