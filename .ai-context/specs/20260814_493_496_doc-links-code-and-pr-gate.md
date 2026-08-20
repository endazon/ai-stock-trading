---
title: 仕様書からコードへのリンクを検査対象に入れ、計画書実在検査を PR 段階へ戻す
type: spec
status: approved
related_ids: [NFR, IADR-0191]
author: endazon (with Claude Code)
created: 2026-08-14
updated: 2026-08-14
---

# 仕様書: doc-links の 2 つの穴を塞ぐ

> 本仕様書は実装着手前に作成する。

## 起点

- 起点 issue: [#493](https://github.com/endazon/ai-stock-trading/issues/493)（コード拡張子）／[#496](https://github.com/endazon/ai-stock-trading/issues/496)（PR 段階へ戻す）
- 起点 ID: **NFR**（工程の統制。計画側の非機能要件表に当たる番号が無いため無採番。planning#311 の 2）
- 発見元: [#492](https://github.com/endazon/ai-stock-trading/issues/492) / [IADR-0191](../adr/IADR-0191_kit-sync-classification.md) のキット突合

## 🔴 なぜ 1 つの PR にまとめるか

**2 つとも同じ `doc-links` の穴であり、別々に直すと 2 回とも同じファイルを触ることになる。**
本リポジトリの運用標準は**「同型・低リスクの変更は 1 PR に束ねる（PR を刻まない）」**
（`planning/docs/ai-implementation-workflow-guide.md`）。

## 事象1（#493）: コードファイルへのリンクが検査対象外

| | 本リポ | キット |
| --- | --- | --- |
| `LINK_EXT` | `md ya?ml json puml mmd png jpe?g svg drawio` | 左記 **＋ `js mjs cjs ts tsx cs csproj props targets slnx sh`** |
| 自己試験（`--self-test`） | **無し** | 有り |
| 裸ファイル名の検査（[#399](https://github.com/endazon/ai-stock-trading/issues/399)） | **有り（本リポが進んでいる）** | 無し |

**仕様書・IADR からコードファイルへの相対リンクが一度も検査されていない。**
キット側のコメントは、この穴が**実際に事故になった**と記録している ——
「コード拡張子が抜けていた間、仕様書からコードへの live link は一切検査されず、
破損したまま『OK: 384 件』と報告された（microservices-platform#470 / planning#167。
**検査器を作る PR が、検査器の穴で自分の参照切れを見逃した型**）」。

### 実測: 対象に入れると **20 件**の破損が出る

**先に測った**（`LINK_EXT` を暫定拡張して実走）。**すべて実在するファイルへの、経路だけが古いリンク**である。

| 原因 | 件数 | 内容 |
| --- | ---: | --- |
| **`src/` → `backend/` のレイアウト移行に未追随** | **17** | IADR-0046 / platform ADR-0019 のユニットリポジトリレイアウト移行で `src/` から `backend/` へ移ったが、**IADR-0002〜0008 のリンクが移行前のまま** |
| **`.Worker` → `.Api` のプロジェクト改名に未追随** | **2** | `RiskControlEndpoints.cs` / `MonitorSettingsEndpoints.cs` |
| **隣接クローンへのパス** | **1** | `../microservices-platform/...`。**CI には隣接クローンが存在しないため、原理的に解決できない** |

> 🔴 **17 件は「レイアウト移行から今日まで、誰も気付けなかった」ことを意味する。**
> リンク先はすべて実在するのに、**指している場所が存在しない**まま `OK` が出続けていた。

### 母集合の引き方と、除外したもの（規則 6）

**誤りの側から引いた** —— `../../src/`（移行前の経路）・`.Worker/Foundation/Endpoints`（改名前）・
`../microservices-platform`（隣接クローン）の 3 軸で全走査した。

| 引いたもの | 件数 | 扱い |
| --- | ---: | --- |
| `../../src/` を含む `docs/` 配下 | 8 ファイル | **7 ファイルを是正**。1 つは**本仕様書自身**であり、誤った経路を**引用**しているだけなので**書き換えない** |
| `.Worker/Foundation/Endpoints` | 2 ファイル | 是正 |
| `../microservices-platform` / `../project-planning` | **29 箇所** | **1 箇所のみ是正**。**残りは拡張子を持たない**（リポジトリ名・ディレクトリの言及であり、`LINK_EXT` に当たらない）ため**検査対象外である**——**除外リストで黙らせたのではなく、定義上そもそも対象でない** |

> **本仕様書自身が 1 軸目に引っ掛かった。** 検査器について書いた文書が、その語を含むために
> 自己発火する形であり、**planning#319 知見3 と同型**である。**引っ掛かったものを機械的に
> 全部書き換えると、記録が壊れる。**

## 事象2（#496）: 計画書実在検査が PR 段階で働いていない

`ci.yml` の `test-traceability` は、**「PR CI には `PLANNING_REPO_TOKEN` が無い」**を根拠に
計画書実在検査を skip し、夜間の `doc-links-planning.yml` へ委ねている。`doc-links` も同じ前提に立つ。

**この前提は [#495](https://github.com/endazon/ai-stock-trading/pull/495) の実測で反証された** ——
`kit-sync` ジョブが**同じトークンで PR の CI から submodule を取得し success した**。

夜間方式の弱点は**当の `doc-links-planning.yml` のヘッダに明記**されている ——
「planning リンクは PR では検査されないため、**マージを止められず、夜間に初めて赤くなる**。
しかも失敗が PR に紐づかないため気付かれにくい」（**実運用で 14 夜連続の失敗が放置された実績**にも言及）。
同ヘッダは**「トークンが PR CI でも使える場合は PR 段階で落とす方が望ましい」**と、あるべき姿を先に書いている。

## 決定

### 決定1: キット版の `LINK_EXT` と自己試験を取り込む。**#399 の裸ファイル名検査は失わない**

**単純な上書きは退行になる。** 本リポは #399 で「同一ディレクトリ内の裸ファイル名」を
検査対象に加えており、**キットにはこの機能が無い**。**両者をマージした形にする。**

- キットから: `LINK_EXT`（コード拡張子込み）・`selfTest()`・`--self-test`・`module.exports`
- 本リポから: `bareFileName` の判定と、その根拠コメント（#399）
- **自己試験に #399 の正例・負例を足す** —— 本リポ固有の機能も**回帰で固定する**
  （キットの自己試験は当然この機能を知らない）。

### 決定2: 20 件を是正する。**経路の付け替えであり、記述は変えない**

- 17 件: `../../src/...` → `../../backend/...`
- 2 件: `.Worker` → `.Api`
- 1 件（隣接クローン）: **live link にしない。** `../microservices-platform/...` の形は
  **CI で原理的に解決できない**ため、**先頭の `../` を外した表記へ改める**
  （`microservices-platform: src/knowledge/...` の形）。
  **除外リストで黙らせない** —— 除外は「検査したことにする」であり、次に同じ形が来ても気付けない。

### 🔴 決定3: `doc-links` / `test-traceability` を PR 段階で実効させる

`ci.yml` の両ジョブへ `submodules: recursive` ＋ `token: ${{ secrets.PLANNING_REPO_TOKEN }}` を付け、
**`--require-planning` を渡す。**

**`--require-planning` を必ず付ける。** 付けないと、submodule の取得に失敗したとき
**skip して緑になり、「配線したのに一度も検査しない」状態が固定される**
（[IADR-0191](../adr/IADR-0191_kit-sync-classification.md) 決定3 と同じ判断）。

### 決定4: 夜間の `doc-links-planning.yml` は**残す。ただし役割を書き直す**

PR で毎回検査するようになるため冗長に見えるが、**畳まない。**

- **PR は「その PR の差分」しか見ない。** 計画側（planning）が更新されて実装側の参照が
  切れる事故は、**実装側に PR が無い間に起きる**。**夜間はそれを拾う唯一の経路である。**
- **役割が変わったことをヘッダへ明記する** —— 「PR では検査されないので夜間で拾う」から
  **「PR でも検査するが、planning 側の変更起因の破損は夜間でしか拾えない」**へ。

## やらないこと

- **除外リストで 20 件を黙らせること。** 経路を直す。
- **`LINK_EXT` へ `txt` / `log` / `lock` 等の汎用拡張子を足すこと**（キットが誤検知リスクとして
  意図的に外しており、自己試験で固定されている）。
- **フォーク PR への対応。** 本リポの PR はブランチ由来で secrets が使えるが、
  **フォーク PR では使えない**。将来フォーク運用を採るなら別途の判断が要る。

## 受け入れ基準

- [ ] `LINK_EXT` にコード拡張子が入り、`--self-test` が通る
- [ ] **#399 の裸ファイル名検査が残っており、自己試験で固定されている**（正例・負例を対で）
- [ ] 破損 20 件をすべて是正した（**除外ではなく経路の是正**）
- [ ] `doc-links` / `test-traceability` が `submodules` ＋ トークン ＋ `--require-planning` で走る
- [ ] `doc-links-planning.yml` のヘッダが新しい役割分担を書いている
- [ ] `kit-sync` が緑（`check-doc-links.js` の分類理由を実態へ更新する）

## テスト方針

| 何を守るか | どう守るか |
| --- | --- |
| コード拡張子の実在検査 | 自己試験（キット由来。正例・負例を対で） |
| **#399 の裸ファイル名検査**（本リポ固有） | 自己試験（**本作業で追加**） |
| **CI が skip して緑にならない** | 回帰テスト（`ci.yml` の両ジョブが `--require-planning` を持つこと） |
| 20 件の再発 | 実ツリーの `check-doc-links.js` 実走（CI ジョブ） |

**対照実験は「壊して赤を実測する」形で行う。** 緑のまま「効いているはず」とは書かない。
