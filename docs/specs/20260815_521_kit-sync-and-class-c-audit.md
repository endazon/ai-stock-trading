---
title: キット追随（planning#358 / planning#354 / planning#355）と、分類 C の全数監査 — 取り違え 2 件の是正
type: spec
status: approved
related_ids: [NFR, IADR-0191, IADR-0200, IADR-0202, IADR-0203]
author: endazon (with Claude Code)
created: 2026-08-15
updated: 2026-08-15
---

# 仕様書: キット追随と分類 C の全数監査（#521）

> 本仕様書は実装着手前に作成する。

## 起点

- 起点 issue: [#521](https://github.com/endazon/ai-stock-trading/issues/521)
- 起点 ID: **NFR**（無採番。工程の統制であり計画側の非機能要件表に当たる番号が無い／環流しない）
- 実測時点: `develop` = `30191c1` / 計画 `b640159`（本リポ pin は `ce9abd2`）

## 課題1: 分類 A のドリフト 2 件 — **移したばかりの分類が、初回から効いた**

```
[check-kit-sync] 追随の違反 2 件を検出しました:
    [drift] .claude/rules/traceability.md が分類 A なのにキットとバイト一致でない。…
    [drift] scripts/check-review-verdict.js が分類 A なのにキットとバイト一致でない。…
```

> 🔴 **`.claude/rules/traceability.md` は [#517](https://github.com/endazon/ai-stock-trading/issues/517) で分類 C → A へ移したばかりである。
> C のままなら、この drift は今回も黙って見逃されていた。** [IADR-0202](../adr/IADR-0202_traceability-md-classification.md) の効果が
> **翌 PR で実測された。**

| ファイル | キット側の変更 | 由来 |
| --- | --- | --- |
| `.claude/rules/traceability.md` | ① 規則 8 の前の空行を削除（表の外に落ちていた行）／② **新規約: キット配布物の中では他リポの `ADR` / `IADR` を番号で引かない** | **どちらも本リポの環流**（planning#358 / planning#354） |
| `scripts/check-review-verdict.js` | docstring に**配線先の限定**を明記（`prompt:` で判定書式を強制するレビュー用ワークフローに限る） | planning#355 |

## 課題2: 🔴 分類 C の取り違えが 2 件あった（#517 と同型）

[IADR-0202](../adr/IADR-0202_traceability-md-classification.md) は残余リスクにこう書いた ——
「**残る C 16 件について同種の取り違えが無いとは言えない**」。**実際にあった。**

### 走査（分類 C 全 16 件を、キット版とバイト比較した生出力）

| ファイル | 差分 |
| --- | --- |
| `.gitignore` | 452 行 |
| `AGENTS.md` | 19 行 |
| `CHANGELOG.md` | 274 行 |
| `CLAUDE.md` | 69 行 |
| `docs/README.md` | 24 行 |
| `docs/adr/README.md` | 222 行 |
| `docs/ai-workflow.md` | 127 行 |
| `docs/operations/operations.md` | 287 行 |
| `docs/security/security.md` | 206 行 |
| `docs/tech/tech-requirements.md` | 57 行 |
| `scripts/README.md` | 94 行 |
| `scripts/changelog-overrides.json` | 26 行 |
| `scripts/check-commit-messages.js` | 127 行 |
| **`scripts/check-cross-repo-refs.js`** | **13 行** 🔴 |
| **`scripts/check-plan-id-qualification.js`** | **バイト一致（0 行）** 🔴 |
| `scripts/scripts.test.js` | 782 行 |

### 🔴 2 件はいずれも**固有デルタが 0** である

| ファイル | 実測 | なぜ 0 なのか |
| --- | --- | --- |
| `scripts/check-plan-id-qualification.js` | **バイト一致** | 置換点 `PROJECT_PREFIXES` は**空**。本リポは他プロジェクトの計画書を参照しないため、**空が正常な状態**（検査は skip する）。埋める理由が無い |
| `scripts/check-cross-repo-refs.js` | 差分 13 行だが、**その 13 行はすべてキット側の是正**（planning#354） | 置換点は `<sibling-repo-name>` / `<SHORT>` / `<SELF_SHORT>` / `<self-repo-name>` の**プレースホルダのまま**。本リポは[IADR-0200](../adr/IADR-0200_cross-repo-ref-notation.md) 決定5 で **env 注入を選んだ**ため埋めていない |

**結果として、後者は古い写しを保持し続けていた** —— `IADR-0140` の誤引用（planning#354 で環流した当のもの）と、
「**ワークフローを編集できない**」という**実測で否定済みの前提**を、キット側が是正した後も抱えていた。

### 分類 C の定義の読み違い

| 定義 | 読み方 |
| --- | --- |
| C = 「本リポの中身そのもの（雛形から書き起こした実体、または**置換点を持つ配布物**）。同期しない」 | ❌ 従前: **置換点を「持つ」**なら C |
| 同上 | ✅ 是正: **本リポが置換点を「埋めている」**なら C |

**埋めていないなら固有デルタは 0 であり、「各リポが自分の値を埋める前提」という C の根拠がそもそも成り立たない。**

> `scripts/check-commit-messages.js` は**埋めている**（[IADR-0201](../adr/IADR-0201_cross-repo-refs-commit-face.md) 決定2 の直書き）。**C のままで正しい。**

## 課題3: ✅ planning#355 の誤配線は本リポでは起きていない（実測）

- `check-review-verdict.js` の配線は **`claude-code-review.yml` の 1 箇所のみ**（同ファイルは `prompt:` を持つ）
- `claude-coding.yml` へは配線されていない（同ファイルは `prompt:` を持たないと明記済み）

**直す対象は無い。「該当しないことを確かめた」ことを記録として残す**（黙って通過させない）。

## 課題4: ✅ planning#356（無主 ID 検査）は本リポの担当ではない

計画側の裁定は「**実装側（microservices-platform#748）で先に作り、動くものを環流する**」。**本リポの作業は無い。**

## 決定

| # | 決定 |
| --- | --- |
| 1 | 計画 pin を `ce9abd2` → `b640159` へ進める |
| 2 | 分類 A のドリフト 2 件をキット原文で上書きする |
| 3 | 🔴 **`check-cross-repo-refs.js` と `check-plan-id-qualification.js` を C → A へ移す**（前者はキット原文へ同期） |
| 4 | 🔴 **再発を機械で止める。** 分類 C のうち「キットとバイト一致」または「置換点が未記入」のものを検出する回帰テストを置く |

### 決定4 を今 作る理由

運用標準は「**検査器・規約の追加は同型事故 2 回から**」と定める。**2 回に達した。**

| # | 事故 | issue |
| --- | --- | --- |
| 1 | `.claude/rules/traceability.md`（固有デルタ 0 なのに C。2 件取りこぼし） | [#517](https://github.com/endazon/ai-stock-trading/issues/517) |
| 2 | `check-cross-repo-refs.js`（固有デルタ 0 なのに C。古い写しを保持） | 本 issue |

**1 回目は「気づいた人がいた」だけである。** 2 回目も人が気づいた。**3 回目は機械が止める。**

## 受け入れ基準

- [ ] `check-kit-sync.js` が 0 件（**A 83 / B 9 / C 14 / 対象外 9**）
- [ ] `traceability.md` と `check-review-verdict.js` と `check-cross-repo-refs.js` がキット版と**バイト一致**
- [ ] 分類 C に**固有デルタ 0 のファイルが 1 件も無い**（新テストが緑）
- [ ] `scripts.test.js` が緑
- [ ] クロスリポ検査・リンク検査・ADR 索引検査が緑
- [ ] 必読規約の総量を**実測して記録する**（予算超過なら別 issue へ）

### 実測（すべて実走）

| 受け入れ基準 | 実測 |
| --- | --- |
| キット追随 | `A 83 件はバイト一致 / B 9 件 / C 14 件 / 対象外 9 件`（A 81 → 83・C 16 → 14） |
| リポテスト | **232 tests passed**（+1） |
| 必読規約の総量 | 🔴 **45,760 バイト＝予算の 91.5%**（`traceability.md` が 20,912 → 21,590 へ増えた） |

> 🔴 **予算が [#519](https://github.com/endazon/ai-stock-trading/issues/519) に書いた着手条件（90%）を超えた。**
> **本 PR では扱わない**（分類の是正とは別型の作業であり、母集合の定義は計画側へ環流が要る）。
> **#519 へ実測を記録し、次の PR で着手する。** 条件を書いて超えたのに黙って通すのは、
> **blocked を放置するのと同じ**である。

## 対照実験（変異試験。実走した実測）

| # | 変異 | 期待する枝 | 結果 |
| --- | --- | --- | --- |
| A | `check-cross-repo-refs.js`（**同期後**）を C へ戻す | バイト一致 | ✅ **赤**（`キットとバイト一致（固有デルタ 0）`） |
| B | `check-plan-id-qualification.js` を C へ戻す | バイト一致 | ✅ **赤**（同上） |
| C | `check-cross-repo-refs.js` を**同期前の版に戻して** C へ置く | **置換点の未記入** | ✅ **赤**（`置換点が未記入のまま（<sibling-repo-name> <SHORT> <SELF_SHORT> <self-repo-name>）`） |

> 🔴 **変異 C を足したのは、A と B が両方とも「バイト一致」の枝でしか赤くならなかったからである。**
> **枝が 2 つあるのに 1 つしか試していない状態**は「検査したつもり」である
> —— **今回の課題そのもの（固有デルタ 0 を C に置くと検査が止まる）と同じ形**であり、
> **自分の追加したテストで繰り返すところだった。**

## 母集合の取り方（規則 5 / 6 / 8）

### 軸1: 分類 C の全件 × キット版とのバイト比較 — **16 件を全数**

上表のとおり。**加工していない**（`head` で切らず、全 16 行を出した。規則 7）。

### 軸2: 置換点のプレースホルダが残っている分類 C — **1 件**

軸1 の「バイト一致」だけでは `check-cross-repo-refs.js` が**落ちる**（キット側の是正があるため差分 13 行に見える）。
**軸を変えたから見つかった**（規則 5）。

### 除外したものと理由（**全数**。規則 6）

| 除外 | 件数 | 理由 |
| --- | --- | --- |
| キットに存在しないファイル | 0 | 分類 C の 16 件はすべてキットに対応物がある |
| `scripts/check-commit-messages.js` | 1 | **置換点を埋めている**（IADR-0201 決定2）。C で正しい |
| 固有デルタが大きい 13 件 | 13 | 本リポの実体を持つ。C で正しい |

> **規則 8: 時点を明示する。** 上記は**本仕様書を書く前**の走査である（`develop` = `30191c1`）。
> 本仕様書は分類 C のファイル名を多数含むが、**走査対象は `kit-sync-classification.json` の
> エントリであり `.md` 本文ではない**ため、**本仕様書の追加は母集合を動かさない。**

## 影響範囲

- `planning` submodule の pin
- `.claude/rules/traceability.md`・`scripts/check-review-verdict.js`・`scripts/check-cross-repo-refs.js`（キット原文で上書き）
- `scripts/kit-sync-classification.json`・`scripts/scripts.repo.test.js`
- 新設 IADR-0203・`docs/adr/README.md`

**C# のコードには一切触れない。**

## 環流（計画側へ返すもの）

**分類 C の定義を「置換点を持つ」から「本リポが置換点を埋めている」へ改める**提案。
定義が曖昧なままだと**各リポで同じ取り違えが起きる**（本リポで 2 回起きた）。
検出方法（バイト一致／置換点の未記入）も併せて渡す。

## 参照

- [IADR-0191](../adr/IADR-0191_kit-sync-classification.md)（分類表の起点）
- [IADR-0202](../adr/IADR-0202_traceability-md-classification.md)（1 回目の取り違え・残余リスクが本件を予告していた）
- [IADR-0200](../adr/IADR-0200_cross-repo-ref-notation.md) 決定5（env 注入を選んだ決定）
- [IADR-0201](../adr/IADR-0201_cross-repo-refs-commit-face.md) 決定2（直書きを選んだ決定＝C で正しい側）
- [作業仕様書 20260815_517](20260815_517_kit-sync-and-exclusion-removal.md)
