---
title: 作業仕様書 — `plan_refs` の実在しない計画書パス 20 件を是正し、ADR-0007 の誤帰属 2 件を ADR-0003 へ直す
type: work
status: review
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-08-04
updated: 2026-08-04
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md # UC-01（定時取引サイクル）・UC-06（設定変更・一時停止・緊急停止）
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md # FR-10 トレーサビリティ行
related_specs:
  - ./20260801_impl-handoff-kit-sync.md
---

# 作業仕様書: `plan_refs` の実在しない計画書パスを是正する

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（計画書参照の健全性。**NFR** 相当）
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: なし（**新規 IADR も作らない**。文書参照の誤りの訂正であり、設計判断を伴わない）
- 対象 Issue: [#327](https://github.com/endazon/ai-stock-trading/issues/327)（`doc-links (planning)` が失敗している）。`Closes #327`
- 部分的に関わる Issue: [#299](https://github.com/endazon/ai-stock-trading/issues/299)（ADR-0007 の誤引用が全体化）。`Refs #299`
- 先行する診断: [20260801_impl-handoff-kit-sync.md](./20260801_impl-handoff-kit-sync.md) §本作業で扱わない既存不具合

## 目的・背景

`planning` submodule を populate した状態で `node scripts/check-doc-links.js` を実行すると、**破損リンク
20 件**が検出される。夜間の `doc-links-planning` ワークフローはこれで**少なくとも 3 日連続で失敗**している
（issue [#327](https://github.com/endazon/ai-stock-trading/issues/327)）。

### 原因は「改名への未追随」ではなく「実在しないファイル名」である

破損している参照先 8 種を planning の**全履歴**で調べたところ、**8 種とも一度も存在したことがない**。

```bash
git log --all --oneline --diff-filter=A -- "projects/ai-stock-trading/07_adr/ADR-0008_staged-rollout.md"
# → 出力なし（追加コミットが存在しない）
```

> 以下の表とこの直後の対応表では、左列に**実在しない旧参照名をそのまま**書いている。
> この作業の一括置換をこの仕様書自身へ掛けると左列まで書き換わり、表が自己参照になって意味を失う
> （実際に一度そうなったので、置換対象から本ファイルを外すこと）。

| 参照先 | 履歴上の存在 |
| --- | --- |
| `07_adr/ADR-0008_staged-rollout.md` | 一度も存在しない |
| `07_adr/ADR-0008_backtest-and-staged-rollout.md` | 一度も存在しない |
| `07_adr/ADR-0003_human-in-the-loop.md` | 一度も存在しない |
| `07_adr/ADR-0009_pause-resume.md` | 一度も存在しない |
| `07_adr/ADR-0007_kill-switch-authz.md` | 一度も存在しない |
| `03_usecases/UC-01_information-collection-to-decision.md` | 一度も存在しない |
| `03_usecases/UC-06_settings.md` | 一度も存在しない |
| `03_usecases/06_settings-and-controls.md` | 一度も存在しない |

すなわち、**参照を書いた時点で実在確認をしていない**（もっともらしいファイル名を組み立てた）ものである。
PR CI の `doc-links` ジョブは submodule を取得しないため planning 配下を検査対象外にしており、
この隙間で蓄積した。前回の同期作業（[20260801_impl-handoff-kit-sync.md](./20260801_impl-handoff-kit-sync.md)）が
同じ 20 件を検出して「別 issue へ切り出す」と記録しており、本作業がその切り出し先にあたる。

## 対象範囲

- **対象**: 破損している 20 件の参照すべて（17 ファイル）。分類 2 に該当する 2 ファイルについては、
  同じ誤帰属が現れる `related_ids` と本文の説明文も併せて直す。
- **対象外**:
  - **[#299](https://github.com/endazon/ai-stock-trading/issues/299) の残り**。同 issue は
    IADR-0012 / 0021 / 0024 / 0038 / 0051 / 0063 / 0067 / 0070 / 0075 / 0080 / 0084 / 0088 / 0090 ほか多数と
    コードコメントに及ぶ一括是正である。本作業は #327（**リンクが壊れている 20 件**）を単位とし、
    そのうち #299 と重なる 2 ファイルだけを巻き取る。両者を混ぜるとレビュー単位が濁り、
    「リンクが直ったか」と「引用が正しいか」を別々に追跡できなくなる。**残りは別 PR で扱う。**
  - **`docs/specs/20260718_20_stage-gate-transitions.md` の ADR-0007 注記**（後述「持ち越す既知の誤帰属」）。
  - **[20260801_impl-handoff-kit-sync.md](./20260801_impl-handoff-kit-sync.md) の当該記述の書き換え**。
    作業仕様書は PR 単位の point-in-time 記録であり、当時の状態を正しく記録している。後から書き換えない。
  - `scripts/check-doc-links.js` の改修（PR CI で `plan_refs` のファイル名を検査する仕組み）。
    #299 の「やること」4 番目にあたり、検査器の設計判断を伴うため本作業と分ける。

## 設計

### 分類 1: ファイル名の綴りのみが誤り（18 件）

参照している ADR / UC の**番号は正しく**、計画側に実体がある。パスを実体へ差し替える。

| 現参照（実在しない） | 差し替え先 | 件数 |
| --- | --- | --- |
| `07_adr/ADR-0008_staged-rollout.md` | `07_adr/ADR-0008_staged-gates-and-backtest.md` | 8 |
| `07_adr/ADR-0008_backtest-and-staged-rollout.md` | 同上 | 2 |
| `07_adr/ADR-0003_human-in-the-loop.md` | `07_adr/ADR-0003_ai-decision-guardrails.md` | 2 |
| `07_adr/ADR-0009_pause-resume.md` | `07_adr/ADR-0009_pause-resume-and-lockout-states.md` | 2 |
| `03_usecases/UC-01_information-collection-to-decision.md` | `03_usecases/01_usecases.md` | 2 |
| `03_usecases/UC-06_settings.md` | 同上 | 1 |
| `03_usecases/06_settings-and-controls.md` | 同上 | 1 |

**ユースケースは計画側で単一ファイルへ統合されている**（`03_usecases/` の実体は `01_usecases.md` のみ）。
実体確認: `### UC-01: 定時取引サイクル`（L61）・`### UC-06: 設定変更・取引の一時停止・緊急停止`（L134）。

パスを畳むと**どの UC を指していたかが失われる**ため、本リポに既にある書式に揃えて UC 番号を注記する。

```yaml
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md # UC-01（定時取引サイクル）
```

注記付きの引用形（`"path (説明)"`）を採る既存ファイルは、その形のまま説明部分に UC 番号を保つ。

### 分類 2: 参照先 ADR そのものの誤り（2 件・誤帰属）

[IADR-0098](../adr/IADR-0098_owner-realm-client.md) と [20260720_226](./20260720_226_owner-realm-client.md) は
ADR-0007 を「**kill switch 認可＝利用者のみ**」として引用している。しかし計画側の ADR-0007 の実体は
「**取引商品は現物基本＋信用を設定で有効化し、取引ガードをソフト設定で強制する**」であり、認可の ADR ではない。

**パスだけを差し替えてはならない。**「壊れたリンク」が「実在するが内容が無関係なリンク」へ変わり、
機械的検査では二度と検出できなくなるためである。

正しい参照先を計画書で裏取りした。

| 根拠 | 実測 |
| --- | --- |
| `02_requirements/01_requirements.md` FR-10 のトレーサビリティ行（L133）の関連 ADR | `ADR-0003` / `ADR-0009` / `ADR-0016` / `ADR-0018`。**ADR-0007 は含まれない** |
| `07_adr/ADR-0003_ai-decision-guardrails.md` L34 | 「リスク管理サービスを**直列に配置**し、1注文上限・日次上限・保有数上限・損切り・**kill switch** を決定的なコードで強制する。**AI はこれを上書きできない（FR-10）**」 |

→ **ADR-0007 の引用を ADR-0003 へ是正する**。これは #299 が既に定めた是正方針
（「誤引用を `ADR-0003` ＋ FR-10 本文へ、pause/lockout 文脈は `ADR-0009` へ」）と一致する。
同時に引用されている ADR-0009（pause/resume）は文脈として正当であり、パスのみ直す。

是正は 1 ファイルにつき 3 箇所（`related_ids` / `plan_refs` / 本文の説明文）に及ぶ。
`plan_refs` だけ直すと、`related_ids` と本文に誤帰属が残る。

### 注記（annotation）の扱い

パスを差し替える行に注記が付いている場合、**注記が実体の内容を正しく説明していれば維持する**
（例: ADR-0008 への「段階的実弾投入と撤退基準」は同 ADR の主題であり誤りではない）。
誤帰属そのものである注記（ADR-0007 の「kill switch 認可＝利用者のみ」）のみ書き換える。

## 受け入れ基準

- [ ] `node scripts/check-doc-links.js` の破損リンクが **20 件 → 0 件**になる。
- [ ] 差し替えたパスがすべて `planning` 配下に実在する。
- [ ] `ADR-0007` を「kill switch 認可 / 統制の権限」の意味で引用している箇所が、本作業の対象 2 ファイルに
      残っていない（`related_ids` / `plan_refs` / 本文のいずれにも）。
- [ ] UC を指していた参照が、どの UC かを失っていない（UC 番号の注記が残っている）。
- [ ] [20260801_impl-handoff-kit-sync.md](./20260801_impl-handoff-kit-sync.md) を書き換えていない。
- [ ] 本作業で新たに `ADR-0007` を「取引ガード／信用」以外の意味で使っていない。

## テスト方針

文書のみの変更であり、テストコードの対象ではない。検証は検査器の実走と実体確認で行う。

| 検証 | 期待 |
| --- | --- |
| `node scripts/check-doc-links.js`（submodule populate 済み） | 変更前 20 件 → 変更後 **0 件** |
| 差し替え先パスの `ls` 実在確認 | 5 パスすべて実在 |
| `grep -rn "ADR-0007" docs/` | 残るのは「取引ガード／信用」文脈のみ（#299 の残りは別 PR。件数を記録する） |
| `grep -rn "staged-rollout\|human-in-the-loop\|kill-switch-authz\|pause-resume\.md\|UC-0._\|06_settings-and-controls" docs/` | 残るのは [20260801_impl-handoff-kit-sync.md](./20260801_impl-handoff-kit-sync.md) の記録のみ |
| `node scripts/check-commit-messages.js` | 適合 |

## 計画書との差異

- 差異: なし。本作業は実装側の参照誤りの是正であり、計画書の内容には触れない。

## 未決事項

なし。

## 持ち越す既知の誤帰属（本 PR の対象外・別 PR で扱う）

[20260718_20_stage-gate-transitions.md](./20260718_20_stage-gate-transitions.md) の `plan_refs` に次の行がある。

```yaml
  - ".../07_adr/ADR-0007_trading-guard-and-margin.md (変更は利用者のみ・変更履歴を記録)"
```

**パスは実在するのでリンクは壊れていない**（＝ #327 の対象外）が、注記「変更は利用者のみ・変更履歴を記録」は
**統制の権限の文脈**であり、ADR-0007（取引ガードと信用）の主題ではない。#299 が扱う誤帰属そのものである。

本 PR では触らない。リンク健全性（#327）と引用の妥当性（#299）を別々に追跡するためであり、
**この行を含む #299 の残りは別 PR で是正する**。見落としではないことを明示するために本節へ記録する。
