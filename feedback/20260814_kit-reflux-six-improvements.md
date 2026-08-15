---
title: キット配布物への改善 6 件を環流する（check-permission-denials.js ほか）。あわせて check-kit-sync.js 自体がキットに無いことを報告する
type: plan-feedback
status: accepted
category: その他
related_ids: [NFR]
source_repo: endazon/ai-stock-trading
source_ref: docs/adr/IADR-0191_kit-sync-classification.md / IADR-0192 / issue #494
author: endazon (with Claude Code)
created: 2026-08-14
dispatched: true
planning_issue: 335
---

# フィードバック: キット配布物への改善 6 件の環流

## 種別

**キットの不足**（計画書の誤りではない）。**キットへ戻さないと配布先の他リポジトリが同じ穴を持ち続ける。**

## 経緯

キット追随の分類表と機械検査を配備した（[#492](https://github.com/endazon/ai-stock-trading/issues/492) / [IADR-0191](../docs/adr/IADR-0191_kit-sync-classification.md)）。
**キット 108 件をすべて分類して突合したところ、本リポジトリが進んでいる配布物が 6 件あった。**

**いずれも上書きせず分類 B（固有デルタ・環流候補）として保持している。**
**キットへ取り込まれ次第、分類 A（バイト一致）へ戻す。**

## 環流する 6 件

| # | ファイル | 差分規模 | 内容 |
| --- | --- | ---: | --- |
| 1 | `scripts/check-permission-denials.js` | **+319 / -16** | `hasQuotedPipe()` ／ `fixableCount` と `unfixableReason()` |
| 2 | `scripts/check-doc-links.js` | +35 / -2 | 同一ディレクトリ内の**裸ファイル名**の実在検査（[#399](https://github.com/endazon/ai-stock-trading/issues/399)） |
| 3 | `scripts/commit-allowlist.json` | +14 / -3 | 方針文 2 文（抜け穴化しない旨・B は最終手段） |
| 4 | `.github/workflows/pr-title.yml` | +13 / -8 | bot 除外の根拠コメント（planning#202） |
| 5 | `.claude/hooks/check-impl.js` | +12 / -1 | フロントマター免除の集合化と維持上の注意 |
| 6 | `AI_SETUP.md` | +6 / -1 | `.github/workflows/` 配下では `.example` が無効にならない（[#367](https://github.com/endazon/ai-stock-trading/issues/367)） |

### 🔴 1 は「キットの機能を保ったまま足している」ことを実測で確かめた

**差分が大きい（+319）ため、退行の有無を先に測った。**

| キット側の機能語 | キット | 本リポ |
| --- | ---: | ---: |
| `redirect` | 14 | **14** |
| `numTurns` | 15 | **15** |
| `source:` | 3 | **3** |
| `STRICT_PERMISSION_DENIALS` | 3 | **3** |

**すべて同数で存在する。純粋な追加であり、キットの機能を落としていない。**

> **差分の `-16` 行は「機能の削除」ではなく「変更された行」である**
> （例: `isCritical({count, numTurns})` → `isCritical({count, fixableCount, numTurns})`）。
> **行の増減だけを見て「キットの機能を落とした」と読まないこと** —— 一度そう読みかけた。

## 🔴 あわせて報告: `check-kit-sync.js` 自体がキットに無い

**キット追随を機械検査する仕組みが、キットに含まれていない。**
**microservices-platform と本リポジトリが別々に実装した**（MSP は microservices-platform#734、本リポは [#492](https://github.com/endazon/ai-stock-trading/issues/492)）。
**3 つ目の実装リポジトリは同じ穴を持つ。**

| リポジトリ | 分類 A のドリフト（実測 2026-08-14） | 仕組み |
| --- | ---: | --- |
| microservices-platform | **0 件** | 有り |
| ai-stock-trading（配備前） | **5 件** | **無し** |

**差は仕組みの有無であった。**

キットへ入れるなら、実装側で効いた 2 点を併せて検討されたい。

1. **分類表（A/B/C/対象外）をリポジトリに置く。** 表が無いと判定を回す対象が決まらない。
   **B は理由必須・4 種に当たらないものは追跡先 issue 必須**にすると放置されない。
2. **CI では `--require-planning` 相当を必須にする。** 検査器は planning 未 populate なら
   skip して緑になるため、**submodule を取得しないジョブに素朴に配線すると永久に skip して
   緑を返し続ける**（本リポの CI は当初**どのジョブも submodule を取得していなかった**）。

## 影響範囲

- **キットを使う実装リポジトリすべて。** 6 件の改善はいずれも配布先で同じ価値を持つ。
- **`check-kit-sync.js` の不在は、配布の仕組みそのものの穴である。**

## 実装側の状態

- 6 件は**分類 B（環流候補）として保持**（追跡 [#494](https://github.com/endazon/ai-stock-trading/issues/494)）。
- **取り込まれ次第、分類 A へ戻して `check-kit-sync.js` の緑で一致を確認する。**
- **そのまま採らない判断でも構わない。** その場合は分類 B の理由を
  **「キット側が採らないと判断した」へ書き換える**ため、**結論だけ返してほしい。**

## 取り込み時の注意

- **2（`check-doc-links.js`）は自己試験も一緒に取り込むこと。** キットの自己試験はこの機能を知らず、
  **固定しないと次の追随で黙って消える。**
- **3・6 は一部のみ環流する。** `commit-allowlist.json` の `allow` 実エントリと
  `AI_SETUP.md` の有効化プロファイルは**リポジトリ固有**であり、環流対象ではない。
