---
title: IADR-0188 環流記録の語彙と伝達検査を kit へ揃え、`status` は計画側の裁定段階だけを表す
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0047, IADR-0170]
author: endazon (with Claude Code)
created: 2026-08-14
updated: 2026-08-14
plan_refs:
  - planning:tools/impl-handoff-kit/repo-template/feedback/README.md
  - planning:tools/impl-handoff-kit/repo-template/feedback/TEMPLATE.md
  - planning:tools/impl-handoff-kit/repo-template/scripts/check-feedback-dispatched.js
---

# IADR-0188: 環流記録の語彙と伝達検査を kit へ揃え、`status` は計画側の裁定段階だけを表す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-08-14
- 決定者: endazon（利用者裁定 2026-08-13〜08-14・planning#319 / planning#323）／ Claude Code（本リポへの適用）

## 起点・関連

- 計画書 ID: **NFR**（運用保守）
- 対象 Issue: [#477](https://github.com/endazon/ai-stock-trading/issues/477)（kit 追随の棚卸し）のうち**環流語彙の項**
- 作業仕様書: [20260814_477_feedback-vocabulary-kit-sync](../specs/20260814_477_feedback-vocabulary-kit-sync.md)
- 裁定: [planning#319](https://github.com/endazon/project-planning/issues/319)（検査器が 2 経路の片方しか読まない）／
  [planning#323](https://github.com/endazon/project-planning/issues/323)（`status` の語彙）。
  反映は planning [#320](https://github.com/endazon/project-planning/pull/320) / [#325](https://github.com/endazon/project-planning/pull/325)
- 実測の入力: [#483 のコメント](https://github.com/endazon/ai-stock-trading/issues/483#issuecomment-5294041520)

### 既存 IADR との関係

| IADR | 関係 | 内容 |
| --- | --- | --- |
| **[IADR-0170](IADR-0170_backlog-audit-automation.md) 決定4・5** | **置き換え** | 決定4（証拠は Issue 経路の 2 形だけ）は**判定の形が狭すぎた**。決定5 が前提にした `resolved` という値は語彙外であった。**両決定に日付付きの追記を入れた** |
| [IADR-0047](IADR-0047_kit-template-sync-policy.md) 決定1 | **根拠** | 「**kit テンプレート更新への追随を原則とする**」。本 IADR はその適用である |

## コンテキストと課題

### 🔴 裁定は本リポジトリを名指しで指示していた

planning#323 の裁定コメント（2026-08-14 01:36）は「実装側で対応が要る事項」を 3 件挙げ、うち 2 件が本リポを名指しする。

> 2. **ai-stock-trading**: `resolved` 6 件を `accepted` へ移行する
> 3. **両リポジトリ**: キットの `check-feedback-dispatched.js` を同期する（`/pull/` 対応が未反映）

**本リポはどちらも未対応のままであった**（起票日から 1 日。#477 に棚卸しとして起票済み）。

### 🔴 検査器が 2 経路の片方しか読まず、警告 9 件が全件偽陽性であった

`feedback/README.md` 手順 3 は伝達を**両経路**（GitHub Issue 経路 / 記録ファイル経路）で認めるが、
本リポ固有の `check-feedback-reflux.js`（IADR-0170）は `project-planning#NNN` /
`project-planning/issues/NNN` の 2 形しか証拠と認めない。

**実測（2026-08-14・`develop` = `fed85a3`・計画 pin `cff0e7b`）**:

| | 件数 |
| --- | ---: |
| 警告 | **9**（kit 検査器では 10） |
| **うち偽陽性** | **全件** |

**9 件とも計画リポ `draft/feedback/` に実在し、9 件とも計画側で `status: accepted`** であった。
**記録に嘘は無く、検査器が経路を読めていなかった。** これは planning#319 が姉妹検査器について
指摘した defect と同型であり、**kit 側では planning#320 で解決済み**である。

> **恒常的に鳴る警告は読まれなくなる。** 実際、2026-08-14 のバックログ監査（#483 §6-1）は
> この出力をそのまま「未起票 10 件」として報告しており、**偽陽性が監査の結論に伝播していた。**

### 🔴 `status` が 2 つの軸を 1 語に混ぜていた

planning#323 の裁定は、**`status` は「計画側の裁定段階」だけを表し、「伝達したか」は
`dispatched:` / `planning_issue:` の別鍵が担う**と定めた。**1 つの語に 2 つの軸を持たせない。**

本リポの `resolved` は**語彙の 4 値に無い**うえ「解決した」としか読めず、
**裁定段階なのか伝達済みなのかを読み分けられない**。実測の分布は **`open` 22 / `resolved` 8** であった。

## 決定

### 決定1: kit の 3 ファイルを取り込み、本リポ固有の `check-feedback-reflux.js` を廃止する

**2 つの検査器を併存させない。** 同じ対象を別の規則で見る検査器が 2 本あると、
**どちらが正かを読む人が決めることになり、規則が 2 か所に分かれて必ず食い違う**。

| | 対象 |
| --- | --- |
| **取り込む（バイト一致）** | `feedback/README.md`・`feedback/TEMPLATE.md`・`scripts/check-feedback-dispatched.js` |
| **廃止する** | `scripts/check-feedback-reflux.js`（`ci.yml` の配線・`backlog-audit.yml` の配線・回帰テスト 10 件ごと） |

**kit 由来ファイルへ独自改変を加えない**（IADR-0047 決定1）。改変が要るなら計画側へ環流する。

### 決定2: 30 件の `status` を**計画側の裁定段階の転記**として書き換える

語彙の定義どおり「**計画側の裁定を実装側が転記する**」。**実装側が独自に判断しない。**
転記元は次の優先順とした。

1. 計画リポ `draft/feedback/<同名>.md` の `status`（**計画側のトリアージ出力そのもの**）
2. 無ければ計画側 issue の state と裁定コメント
3. **例外: ① が計画側 issue の決着と矛盾する場合は ② を採り、① の陳腐化を計画側へ環流する**

**③ の例外を置く理由。** ① は計画側の出力ではあるが**人手更新の写し**であり、
**裁定そのものではない**。① を無条件に優先すると、**計画側の記録の陳腐化が実装側へそのまま伝播する**
—— それは本 IADR が実装側で解消しようとしている状態と同じものである。
**裁定の所在は issue であり、記録はその写しである。**

**適用は 30 件中 1 件**（`20260708_trading-defaults-derived-values.md`）。
① は `status: open` だが、**対応する planning#61 は CLOSED** であり、
実装側の記録は決着日（2026-08-02）とその内容を引いていた。
**② を採り、① の陳腐化を [planning#329](https://github.com/endazon/project-planning/issues/329) として環流した**（残余リスクに再掲）。

> **例外を無条件の裁量にしない。** ③ は「**①と②が矛盾したとき**」に限る。
> 矛盾が無ければ ① が優先であり、**②だけを見て ① を無視してはならない**
> —— ① には issue に現れないトリアージの判断（反映先・却下理由）が入ることがある。

**結果: `accepted` 29 件 / `open` 1 件。`resolved` は 0 件になった。**

### 決定3: 伝達の事実は `dispatched:` / `planning_issue:` へ移す

`status` から伝達の軸を抜く。**両経路とも `dispatched: true`** で表し、到達先の番号を `planning_issue:` に残す。

> **`dispatched: true` かつ `status: open` は正しい状態である。** 「送ったのに `open` のまま」と読めるが、
> **2 つの鍵は別の軸を表している** —— `dispatched` は**実装側が送ったか**、`status` は**計画側が裁定したか**である。
> 送った直後は必ずこの組み合わせになり、計画側が受理して初めて `accepted` へ動く。
> 本 PR 時点では [planning#328](https://github.com/endazon/project-planning/issues/328) /
> [#329](https://github.com/endazon/project-planning/issues/329) の 2 件がこの状態にある。
>
> **軸を分けた目的がまさにこれである** —— 旧来の 1 語（`resolved`）では、この 2 件を
> 「送ったが裁定待ち」と書き分けられず、**送っていない記録と同じ見た目になっていた。**

### 決定4: **未伝達の 1 件は、警告を消すのではなく実際に伝達した**

`20260813_sc03-buy-in-count-period-undefined.md`（#470 / IADR-0186 決定1 の環流）は
**30 件中ただ 1 件の真に未伝達の記録**であった。**記録に嘘を書いて警告を消さない**
（planning#319 で実装側 IADR-0184 決定2 として確立した規律）。
**[planning#328](https://github.com/endazon/project-planning/issues/328) として起票し、`dispatched: true` にした。**

> 🔴 **この 1 件は、移行前から検査器が「緑」と判定していた。** 記録が本文で
> `project-planning#292` を**文脈として**引いており、**検査器は URL の走査をファイル全体で行う**ため
> 伝達の証拠と読んだ。**緑は伝達の証明ではない。** kit README はこの走査範囲を意図的な仕様として
> 明記しており（「URL の走査は**ファイル全体**が対象」）、**本 IADR はこれを既知の限界として受け入れる**
> （残余リスクへ再掲）。

### 決定5: `status` の語彙だけをリポジトリ固有の回帰テストで守る

**伝達の判定そのものは kit の自己試験（`--self-test`）に委ねる** —— 本リポで二重に書くと
kit 同期のたびに二重の追随が要る（IADR-0047 決定1 の趣旨に反する）。

**本リポが独自に守るのは `status` の語彙だけ**である。kit README は
「**この語彙を検査する機械は無い。値の誤りは沈黙する**」と明記しており、実際に本リポは
語彙外の値を 8 件持っていた。

**同型 2 回目であるため番人を置く**（planning#296「検査器・規約の追加は同型の事故が 2 回から」）
—— 1 回目は本リポの `resolved` 8 件、2 回目は microservices-platform の `triaged` 7 件の誤用であり、
**同じ kit を配った 2 リポジトリで語彙が割れていた**（planning#323 が実測）。

**検査器ではなくリポジトリ固有の回帰テスト**（`scripts.repo.test.js`）として置く。
kit 由来ファイルを改変せずに済み、語彙が kit 側で変わったときは本テストだけを追随させればよい。

### 決定6: 振る舞いを持つコードは変更しない

本作業はドキュメント・検査器・CI 配線に閉じる。`backend/` は 1 行も触っていない。

## 対照実験（実走した実測）

| 壊した箇所 | 赤くなったもの | 予測 |
| --- | --- | --- |
| 記録 1 件の `status` を `resolved` へ戻す | **回帰テスト 1 件**（`環流記録の status が kit の語彙（4 値）の内にある`） | 一致 |
| 記録 1 件の `dispatched:` を消す | **回帰テスト 1 件**（`dispatched: を true / false のいずれかで持つ`） | 一致 |
| 記録 1 件の `dispatched:` を `no` にする | **回帰テスト 1 件**（同上。**YAML 1.1 では `no` も偽**であり、素の真偽値判定なら黙って通る） | 一致 |
| 伝達済み記録から `planning_issue:` を消す | **回帰テスト 1 件**（`伝達済みの記録は planning_issue: を伴う`） | 一致 |

> **`dispatched: no` の実験を入れたのは、これが「黙って通る」形だからである。** kit README が
> 名指しで警告している（「**YAML 1.1 では `no` / `off` も偽**であり、書くと黙って警告が消える」）。

## 影響

- 肯定的:
  - **恒常的に鳴っていた警告 9 件が、嘘を書かずに 0 件になった。** 記録側に到達先を書いただけである。
  - **偽陽性が監査の結論へ伝播する経路が切れた**（#483 §6-1 で実際に起きていた）。`backlog-audit.yml` の指示文も改めた。
  - **30 件が計画側の裁定に追随した。** 実装側 `open` ／ 計画側 `accepted` という乖離が 18 件あった。
  - **監査が一次情報へ到達できるようになった** —— 計画側のトリアージ結果は submodule だけで読める。
  - 検査器が 1 本になり、kit の更新がそのまま届く。
- 否定的 / トレードオフ:
  - **`status` は依然として人手更新である。** 計画側が裁定を進めても実装側は自動では変わらない。
  - kit 検査器は 646 行と大きい（本リポ固有版は 203 行）。ただし**自己試験を内蔵**しており、
    追随のコストは同期作業だけである。

## 残余リスク

- 🔴 **緑は伝達の証明ではない。** 検査器は URL をファイル全体から拾うため、
  **文脈として引いた計画リポの番号も証拠と判定する**（決定4 の 1 件が実例）。
  kit が意図的な仕様として明記しているため本リポでは変えない。
  **「警告 0 件」を「全件伝達済み」と読み替えないこと** —— 本 IADR の 30 件は
  `draft/feedback/` の実在と計画側 issue の state を**全数で個別に実測**して確かめた。
- **`status` の語彙は守れるが、値の正しさは守れない。** 回帰テストが見るのは
  「4 値のいずれか」だけであり、**`accepted` と書いてあるのに計画側が `open` である**ことは検出できない。
  この観点は週次 AI 監査の担当のままである（IADR-0170 決定5 の追記を参照）。
- **計画側の記録にも陳腐化がある。** `draft/feedback/20260708_trading-defaults-derived-values.md` は
  `status: open` のままだが、対応する planning#61 は CLOSED である。**計画側へ環流する。**
- **kit の同期は人手の作業である。** 本作業で 3 ファイルを揃えたが、**次の kit 更新で再び乖離する**。
  #477 が棚卸しの受け皿であり、パリティ点検（計画リポ `draft/cross-project/`）が定期的に検出する。
