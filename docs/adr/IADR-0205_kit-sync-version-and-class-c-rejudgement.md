---
title: IADR-0205 キット版が上回った検査器はキット版で差し替えて A へ戻し、分類 C は kit の新条件で全件再判定する
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0191, IADR-0195, IADR-0203, IADR-0204]
author: endazon (with Claude Code)
created: 2026-08-16
updated: 2026-08-16
---

# IADR-0205: キット版が上回った検査器は差し替えて A へ戻し、分類 C は kit の新条件で全件再判定する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-16
- 決定者: Claude Code（[#524](https://github.com/endazon/ai-stock-trading/issues/524) の解消。MSP#755 と対）

## 起点・関連

- 計画書 ID: **NFR**（無採番。工程の統制であり計画側の非機能要件表に当たる番号が無い）
- 対象 Issue: [#524](https://github.com/endazon/ai-stock-trading/issues/524)
- 作業仕様書: [20260816_524](../specs/20260816_524_pin-4d6a7d6-catchup.md)
- 前提: [IADR-0195](IADR-0195_kit-sync-after-arbitration.md)（キットが失った機能は本リポ側で守る）／[IADR-0203](IADR-0203_class-c-requires-local-delta.md)（C は「埋めている」で判定）／[IADR-0204](IADR-0204_reading-budget-mother-set.md)（予算はエージェントごと）

## コンテキストと課題

計画 pin が 1 コミット（`4d6a7d6`）遅れており、その 1 コミットの裁定 2 件（planning#363 分類 C の新条件／planning#364 必読規約の母集合）が本リポの現状運用と食い違っていた。あわせて 3 つの事実が実測された。

1. 🔴 **`check-kit-sync.js` は Windows ローカルで機能していない。** `path.relative` の `\` 区切りと分類表の `/` 区切りが一致せず、**115 件中 108 件が偽 unclassified・exit 1**。Linux の CI では露見しない ——「CI は緑・ローカルは赤」で、**ローカルの検査だけが黙って死んでいた**。
2. 🔴 **分類 B「本リポが進んでいる」の記録（2026-08-15）が古い。** 記録は「キット版は `--require-planning` を持たない」と書くが、キット版は planning#343 でフラグ・未知引数の拒否・自己試験を得て 15,578 B へ倍増している。**B の理由が消えたのに B のまま**であった。
3. 🔴 **分類 C に、当のファイルが「バイト一致に保て」と書くものが在った。** `scripts/scripts.test.js` は本文で「本ファイルはキットとバイト一致に保て、同期は上書きコピー 1 回で済む」と宣言しているのに C（同期しない）に置かれ、**43 KB 分のキット試験追加が戻ってきていなかった**。[#517](https://github.com/endazon/ai-stock-trading/issues/517) の `traceability.md`（IADR-0202）と**同型**である。

> **1 と 2 は同じ結論へ収束する。** キット版は Windows パスを正規化しており（`.split(path.sep).join('/')`）、本リポ版の欠陥を既に持っていない。

## 決定

### 決定1: **分類 C は kit の新条件（planning#363）で全件再判定し、根拠の無い C は A / B へ移す**

`$comment` の定義部を kit の `kit-sync-classification.example.json` と**同文**にし（履歴は後段に分離）、C 全 14 件を「(a) キットに対応物が無い／(b) 雛形から書き起こし本リポが置換点を実際に埋めている」で判定した。判定表は作業仕様書に置く。要点:

| 判定 | 件数 | 内訳 |
| --- | --- | --- |
| C 据え置き | 7 | 雛形を埋めた文書 6（`CLAUDE.md`・`CHANGELOG.md`・`docs/adr/README.md`・`operations.md`・`security.md`・`tech-requirements.md`）＋【置換点】`PLAN_PROJECT` を埋めた `check-commit-messages.js` |
| C → A | 2 | **`scripts.test.js`**（A の意味論を自ら宣言）／`AGENTS.md`（差分は同義の要約節の位置だけ） |
| C → B | 5 | `.gitignore`（2）／`docs/README.md`（3）／`docs/ai-workflow.md`（2＋3）／`scripts/README.md`（3＋2）／`changelog-overrides.json`（**5**＝空で配り各リポが埋める欄） |
| notApplicable → A | 1 | `kit-sync-classification.example.json`（キット版 `scripts.test.js` が実在を検査する） |

**B へ移した 4 文書には、いずれもキット土台の追随漏れが見つかった**（`docs/README.md` 1 行／`docs/ai-workflow.md` 2 節 72 行／`scripts/README.md` 表 6 行＋実行例 6 行＋規約 1 節）。**移す際に追随した。** C に置いた瞬間に検査が止まる、という IADR-0203 の指摘が**文書側でも実証**された。

### 決定2: 🔴 **キット版が上回った検査器はキット版で差し替え、B → A へ戻す。優劣は HOWTO の手順で実走して決める**

`check-kit-sync.js` の両版を**同フラグで実走し exit code を比較**した（kit `HOWTO.md` の手順。キット版は `scripts/` へ一時複製して実走 —— kit ディレクトリから直接叩くと `REPO` がキット自身になり比較にならない）。

| 実走 | 本リポ版 | キット版 |
| --- | --- | --- |
| `--require-planning`（populate 済み・Windows） | exit 1（偽 unclassified 108） | **exit 0**（115 件突合） |
| 未知の引数 | 黙って本検査へ進む | **exit 1**（拒否） |
| `--self-test` | 無い | 13 件が通る |
| キット参照不能＋`--require-planning` | exit 1 | **exit 1** |

**キット版が全項目で同等以上。** [IADR-0195](IADR-0195_kit-sync-after-arbitration.md) が守れと定めた機能（`--require-planning`）はキット版が備えた。差し替えて **B → A**。**IADR-0195 の原則は変わらない** —— 「キットが失った機能は守る」であって「キットが追いついても差し替えない」ではない。**B の理由が消えたら B を維持する根拠も消える。**

Windows パスの回帰は `scripts.repo.test.js` に**肯定形（`/` 区切りで返る）と否定形（`\` 区切りは表と一致しない）**で置く。A に置く以上、検査器本体へは足さない（IADR-0203 決定3 と同じ配置）。

### 決定3: **必読規約の予算値は正本（運用ガイド §8）の複製として 51,200 を持ち、出典を値の隣に置き、CI へ配線する**

[IADR-0204](IADR-0204_reading-budget-mother-set.md) の残余リスク 2 件（「予算値は複製で自動追随しない」「検査器はあるが CI に居ない」）に対し、planning#364 が「複製は認めるが値の隣に出典を書く」と裁定した。**50,000 → 51,200**（50KB の正確値）とし、`ci.yml` に `reading-budget` ジョブを新設。`CLAUDE.md` には測定コマンド 2 本と「エージェントごとに分けて測り合算しない」を明記。**既定値 51,200 と出典コメントの共存**を回帰テストで固定する（値だけ直して出典を落とす退行を止める）。

### 決定4: **§11（複数実装リポのパリティ維持）の要点を `CLAUDE.md` に持つ**

配布点は kit に一本化／同時起票 issue は 7 日後に相互のクローズ状態を突合／突合観点 6 種／定期監査は「最後に結果が産出された日時」を見る／pin の鮮度は機械監視。正本の日付を 2026-08-15 へ。

## 実測

| 項目 | 実測 |
| --- | --- |
| `check-kit-sync.js --require-planning`（Windows） | **A 87 / B 13 / C 7 / 対象外 8・違反 0** |
| 必読規約（Claude Code） | **42,897 / 51,200 バイト（83.8%）**。AGENTS.md 系 4,460（8.7%）／Copilot 2,850（5.6%）。合算していない |
| リポテスト | **305 tests passed**（Windows 限定で落ちるキット配布テスト 1 件を一時コピーで skip して計測。下記） |

## 影響

- **良い影響**: `check-kit-sync.js` が Windows でも動く。分類表の定義が kit と同文になり、C の誤置を kit の言葉で判定できる。C が 14 → 7 に減り、**「同期しない」領域が半減**した。予算が CI で測られる。
- **悪い影響**: A が 83 → 87 に増え、キットの退行がそのまま入る面が広がった。`scripts.test.js` を A に置いたため、**キット配布テストの Windows 非互換（下記）が本リポのローカルで顕在化する**。

## 残余リスク

- 🔴 **キット版 `scripts.test.js` の「置換点が主経路（inspect）へ配線されている」は Windows（cmd.exe）で落ちる。** `execSync('node -e "…\\n…"')` の `\n` が実改行にならず `RESULT=0` になる。Linux の CI では通る。A のため本リポでは直せず、**キットへ環流する**。ローカル（Windows）で `scripts.test.js` を回すときは同テストが**環境要因で落ちる**ことを知っておくこと（他の 305 件は通る）。
- **分類 B に移した 4 文書のキット土台は、今回追随したが今後は人手で追随する。** B はバイト一致を見ないため、機械は追随漏れを検出しない（IADR-0203 残余リスクと同じ。同型事故はこれが 1 回目）。
- **MSP 側も `scripts.test.js` を C に置いている**（`origin/develop` 実測 56,314 B。同じ追随漏れ）。MSP#755 の 7 日後突合で確認する。
