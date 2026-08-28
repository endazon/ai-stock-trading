---
title: IADR-0262 計画 ID 修飾検査の既定値を埋め、資料再編後に取り残された矛盾を是正する
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0189, IADR-0203, IADR-0200]
author: endazon (with Claude Code)
created: 2026-08-28
updated: 2026-08-28
---

# IADR-0262: 計画 ID 修飾検査の既定値を埋め、資料再編後に取り残された矛盾を是正する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: Claude Code（利用者指示「msp 側の実装と作りが合っているかどうか確認し是正する」による事前監査への対応。W11 段0）

## 起点・関連

- 計画書 ID: **NFR**（無採番。規約整備・検査器の整備＝メタ作業。`.claude/rules/traceability.md`「起点 ID の種別」許容ケース2）
- 作業仕様書: [20260828_w11s0_msp-alignment-noop-and-contradictions](../specs/20260828_w11s0_msp-alignment-noop-and-contradictions.md)
- 関連: [IADR-0189](IADR-0189_plan-id-qualification-and-traceability-kit-sync.md)（本 ADR が決定2・決定6 を部分的に supersede する）、
  [IADR-0203](IADR-0203_class-c-requires-local-delta.md)（`check-plan-id-qualification.js` を「キットとバイト一致（Class A）」と分類した時点のスナップショット。本 ADR 以降は該当しない）、
  [IADR-0200](IADR-0200_cross-repo-ref-notation.md)（姉妹検査器 `check-cross-repo-refs.js` の置換点表と同じ様式に揃えた）

## コンテキストと課題

利用者指示による事前監査（親セッション）で、MSP（`microservices-platform`）と実装の作りを突き合わせたところ、
本リポ側にだけ残っている **no-op**（検査器が黙って何も検査していない）と、**リポジトリ内の記述の矛盾**が
4件挙がった。裁定は「no-op と矛盾の是正のみ」であり、MSP の検査器一式の移植は範囲外とした。

着手前に対象4件を自分で再検証したところ、(1) は単純な値の穴埋め以上の背景があることが分かった。

### (1) `check-plan-id-qualification.js` の `PROJECT_PREFIXES` 既定が空 → 素の実行が no-op

`PROJECT_PREFIXES` の既定は `splitList(process.env.PLAN_ID_PREFIXES, [])` で空であった。
`node scripts/check-plan-id-qualification.js` を env なしで実行すると「PROJECT_PREFIXES が空のため
skip した」と出て**何も検査しない**。MSP は自身の値（`['AST']`）をファイルへ直接埋めている。

これは**設定漏れではなく、[IADR-0189](IADR-0189_plan-id-qualification-and-traceability-kit-sync.md)
決定2・決定6 が意図して選んだ設計**である。決定2は「検査器2本をバイト一致で取り込み、置換点は
環境変数で与える」——**キットとのバイト一致を保つため**、ファイル自体は書き換えず、CI（`ci.yml`）が
`PLAN_ID_PREFIXES=MSP,AST` を渡す形にした。決定6は、この結果生じる「env を落とすと skip して緑になる」
という fail-open を**残余リスクとして自認**したうえで、「回帰テストが唯一の番人である」として受容していた。

**しかし、この理由づけは資料再編（計画 ADR-0029・2026-08-21）より前（IADR-0189 は 2026-08-14）に
書かれている。** ADR-0029 決定6 は「キットとの乖離は受容する。リポ個別に直した運用装備を kit へ
環流する義務も追随 issue も無い」と明示しており、**バイト一致を保つ動機そのものが方針転換で失われた**。
kit-sync のバイト一致検査（`check-kit-sync.js` 相当）も既に退役している。

**実測（着手前に自分で確認した）**:

| 実行 | 挙動 |
| --- | --- |
| `node scripts/check-plan-id-qualification.js`（env なし） | skip・exit 0（**no-op**） |
| `PLAN_ID_PREFIXES=MSP node scripts/check-plan-id-qualification.js` | OK・1969 件走査・違反 0 件 |
| CI（`ci.yml` の `Check plan ID qualification` ジョブ） | `PLAN_ID_PREFIXES=MSP,AST` を明示しており **no-op ではない** |

つまり **no-op なのは CI ではなく素の実行**（手元・pre-commit 相当・本作業の検証コマンド）である。

### (2) `traceability.md` の companion 段落が自リポ `CLAUDE.md` と矛盾

`traceability.md` 冒頭は「直接編集するとバイト一致が崩れ、キットを同期するたびに手動マージが要る」と
書くが、`CLAUDE.md` は「キットは bootstrap 専用であり、既存リポジトリに追随義務は無い（同期のバイト
一致検査は退役済み）」と書いており、**同じ必読集合の中で前提が矛盾**していた。MSP は既にこの段落を
是正済み。

### (3) `AGENTS.md` / `CLAUDE.md` の「新 ADR」表記

実装リポジトリで実装判断の根拠として残すべきは IADR であって計画 ADR ではない。`AGENTS.md:42` と
`CLAUDE.md:105`（禁止事項の節、計画書に反する実装の差異について）が「新 ADR」のままだった。
`AGENTS.md:9` は既存 issue 検索の指示と `feedback` ラベルも欠けていた。

### (4) `CLAUDE.md` の共有プロジェクト列挙漏れ

親からの指摘時点の情報では「共有物は `backend/Shared/AiStockTrading.Shared.{Contracts,Infrastructure}`」
（`KnowledgeBase` が漏れている）とされていたが、**着手前に確認したところ `CLAUDE.md` は既に
`AiStockTrading.Shared.*`（ワイルドカード）へ是正済みだった**。列挙形の同じ矛盾パターンを
`backend/TestSupport/README.md` に発見した（母集合の再確認）——`src/Shared`（現存しないパス。実際は
`backend/Shared`）と `{Contracts,Infrastructure}`（KnowledgeBase 漏れ）の両方が古いままであった。

## 決定

### 決定1: `PROJECT_PREFIXES` の既定を `['MSP', 'AST']` へ埋める。IADR-0189 決定2・決定6 を部分的に supersede する

**`AST` を含める。** 本リポは `AST/FR-17` のような自己修飾を実際に使っており（IADR-0189 決定3・24件超）、
CI が渡す実運用値も `MSP,AST` である。`MSP` のみに絞ると、素実行時に自己修飾の空白区切り誤りを
検出できないという別の縮退を残す。

**IADR-0189 のうち supersede するのは決定2・決定6 のみ**（「バイト一致のため env のみで供給する」
という手段の選択と、その結果生じる fail-open を受容するという判断）。決定1・決定3・決定4・決定5・決定7は
影響を受けず、引き続き有効である。決定3（`AST` を含める判断そのもの）はこの既定値へ**引き継いだ**。

IADR-0189 の本文は書き換えず（凍結記録）、`traceability.repo.md`「Superseded / Deprecated な ADR を
引用するときの書式」に倣い、日付つき追記ブロックを本文へ足す。

`.claude/rules/traceability.repo.md` に、姉妹検査器 `check-cross-repo-refs.js` と同じ様式で
`check-plan-id-qualification.js` の置換点表を新設した。

### 決定2: 走査 0 件の下限検査は既存の設計を維持し、`--self-test` で固定する

`main()` には「非空の checker があるのに `trackedFiles()` が `[]` を返したら fail-closed」という
分岐が既に実装されていた（「対象なし（skip）」と「拾えなかった（fail）」を区別する設計）。新規に
作る必要はなかったが、この分岐は `--self-test` 経由でカバーされていなかった（`main()` のインライン
ロジックであり CLI 経由でしか通らないため）。

判定ロジックを純関数 `isEmptyScanFailure(checker, files)` へ切り出し、`main()` から呼ぶ形へ
リファクタリングし、4ケース（checker あり×`[]` → true／checker あり×非空 → false／checker なし →
false／`files=null`（git 失敗の fail-open）→ false）を `--self-test` へ追加した。

「非空なのに 0 件なら fail」という単純な下限にしなかった理由: `PROJECT_PREFIXES` を意図的に空へ戻す
（他プロジェクトを参照しないフォークにする等）ケースが設計上の正常系として存在するため、判定は
「checker が非空であること」を必須条件とした。

### 決定3: `traceability.md` の companion 段落を MSP の是正済み文面へ揃える

ADR 番号は AST の対応する計画 ADR（`ADR-0029`。資料再編）へ読み替え、日付は AST の `CLAUDE.md` /
`traceability.repo.md` に既出の資料再編日付（2026-08-21）に合わせた。差分は 22 バイトの減
（15,977 → 15,955）。

**「配布物・直接編集しない」との整合**: 以下の理由で編集は正当である。

1. バイト一致検査は ADR-0029 決定6 で既に退役している。「直接編集するとバイト一致が崩れる」という
   当の懸念が、依拠する検査ごと存在しない。
2. MSP 自身が既に同じファイルへ自リポの ADR 番号（`ADR-0048`）を書き込んで編集済みである。
3. ADR-0029 決定6 は「kit との乖離を受容する」と明示し、追随義務も同期義務も無いとしている。直接
   編集を避ける動機（将来の同期を楽にする）自体が、この方針の下では成立しない。

### 決定4: `AGENTS.md` / `CLAUDE.md` の表記を是正する

`AGENTS.md:9` に既存 issue 検索の一文と `feedback` ラベルを追加、`AGENTS.md:42` と `CLAUDE.md:105`
の「新 ADR」を「新 IADR」へ是正した（いずれも MSP の対応箇所に揃えた）。

**母集合の再確認（規則1: 誤りの文字列「新 ADR」で全文走査）**: 12件ヒット。上記3箇所以外は、全件
個別に確認したところ**すべて正しい用法**であった（計画 ADR を指すべき文脈で計画 ADR を指していた。
詳細は作業仕様書の表を参照）。新規の是正は無い。

### 決定5: `CLAUDE.md` は既に是正済みと確認し、同じ矛盾パターンを別ファイルで是正する

`CLAUDE.md:121` は着手前確認の時点で既に `AiStockTrading.Shared.*`（ワイルドカード）へ是正済みで
あり、変更していない。母集合の再確認で見つかった `backend/TestSupport/README.md` の同型の矛盾
（列挙漏れ＋現存しないパス `src/Shared`）を、`CLAUDE.md` が採用済みのワイルドカード表記に揃えて
是正した。`AiStockTrading.Shared.Kernel`（未マージの別 PR で新設中）は名指しで足していない。

### 決定6: 既定値変更で古くなった記述を追随させる（母集合の規則10）

`PROJECT_PREFIXES` の既定を変えたことで、「env を落とすと skip して緑になる」という前提で書かれていた
2箇所の記述が事実と異なるものになった。両方を是正し、`scripts/scripts.repo.test.js` には既定へ
戻す退行を検出する回帰テストを1件追加した。

- `.github/workflows/ci.yml`（`Check plan ID qualification` ステップ手前のコメント）
- `scripts/scripts.repo.test.js`（同名テストの説明コメント）

## 対照実験（実走した実測）

| # | 内容 | 結果 |
| ---: | --- | --- |
| 1 | 変更前: `node scripts/check-plan-id-qualification.js`（env なし） | skip・exit 0 |
| 2 | 変更後: 同上 | `OK: 1969 件に他プロジェクト ID の修飾違反はありません。`・exit 0 |
| 3 | 変更後の `--self-test` | 42 件 all passed（新規4ケース含む） |
| 4 | `REQUIRE_REPO_TESTS=1 node --test scripts/scripts.test.js scripts/scripts.repo.test.js` | 全件 green（新規回帰テスト含む） |

## 影響

- 肯定的:
  - **素の実行が no-op でなくなった。** CI とローカル・pre-commit 相当の実行が同じ挙動になる。
  - `traceability.md` の companion 段落が自リポ `CLAUDE.md` と整合した（必読規約の総量は 22 バイト減）。
  - 「新 ADR」/「新 IADR」の混同・共有プロジェクト列挙の陳腐化を、母集合の再確認を通じて2件追加是正した。
- 否定的 / トレードオフ:
  - `check-plan-id-qualification.js` は**もはやキットとバイト一致ではない**（`IADR-0203` が記録した
    「Class A（バイト一致）」という分類は、本 ADR 以降のこのファイルには当てはまらない。`IADR-0203`
    自体は point-in-time の記録として書き換えない）。ADR-0029 決定6 の「kit との乖離は受容する」の
    範囲内であり、追随義務も同期義務も無い。

## 残余リスク

- **`check-cross-repo-refs.js` 側の 269 件（IADR-0189 決定4 が配線しないと決めた分）は未着手のまま**
  である。本 ADR の射程外（別 issue [#487](https://github.com/endazon/ai-stock-trading/issues/487)）。
- **`.ai-context/adr/IADR-0203_class-c-requires-local-delta.md` の分類表は本 ADR で古くなるが、
  書き換えない**（point-in-time の凍結記録。#521 時点のスナップショットとして残す）。今後 kit-sync の
  分類監査を再実施する際は、本 ADR を踏まえて `check-plan-id-qualification.js` を Class A から
  除外すること。
