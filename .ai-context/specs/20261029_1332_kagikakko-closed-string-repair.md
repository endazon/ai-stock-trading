---
title: 文字列を日本語の閉じかぎ「」」で閉じ、JSON の閉じの `"` が欠けた構造化出力（UnterminatedString）を、閉じかぎの直後が既知のキーか閉じ括弧のときだけ補って読む（#1332）
type: spec
status: done
related_ids: [FR-04, FR-11, ADR-0003, IADR-0248, IADR-0531, IADR-0597]
author: claude (Claude Code)
created: 2026-10-29
updated: 2026-10-29
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04 判断根拠の記録・FR-11 監査可能なログ)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (出力の構造化・不正は安全側)
---

# 仕様書: 閉じかぎで閉じて `"` が欠けた構造化出力の狭い補修（#1332）

> 本仕様書は実装着手前に作成した（着手 2026-10-29）。基点は AST `origin/develop` `ac465a63`。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-04**（判断の根拠を記録し、判断を下す）、FR-11（後から監査できるログ）。
- 計画 ADR: ADR-0003（出力の構造化・不正は安全側）。
- 起票: #1332。PoC 2026-10-29 の追記（10/26〜28 の稼働。一次スクリーニングの解析不能 2 件）。
  - 10/26 14:55:47 UTC NVDA Buy。
  - 10/27 19:08:20 UTC HOOD Sell。解析不能で打ち切られ、決済が 6 分遅れた。
  - どちらも rationale の文字列を日本語の閉じかぎ `」` で閉じ、JSON の `"` が無いまま終わる（`UnterminatedString`）。
- 前提（覆さない）: IADR-0248（解析不能は Hold）、IADR-0531 決定 1・2（原本の抜粋と形の指紋）・決定 4（再試行しない）。
- 改める: **IADR-0531 決定 3 のうち「閉じない文字列の補修（D 向け）」だけ**を、本件の 2 形に限って改める（新規 IADR-0597）。

## 🔴 実物の原本で形を確かめていない

- オーケストレーターは PoC の抜粋を取得できなかった。PoC 実施側へ、2 件の「解析不能の応答の原本」の行（指紋と excerpt の末尾）を依頼済み（#1332 のコメント 2026-10-29）。
- 実装は、オーケストレーターが示した次の 2 形で試験を固定して進める。
  - 形 1: `"rationale":"…」,"<既知のキー>":…`
  - 形 2: `"rationale":"…」}`
- **原本が届いたら照合する。** 形が違えば、その形を試験へ足し、補修の条件を見直す（別 PR）。

## 対象範囲

- 対象:
  - `TradeDecisionParser.ReadEnvelope`（一次・二次で共通の前段）。抽出が失敗し、走査が末尾で文字列の内側に終わったときだけ補修を試みる。補修の後で改めて抽出・JSON 構文・action の検査を通す（以降の検査は変えない）。
  - `ParsedScreening` / `ParsedTradeDecision` に補修の印（`Repair`）を持たせる。
  - `DecisionOrchestrator` は、一次・二次それぞれで補修したときに WRN 1 行を出す。中身は**補修した事実と形（`layer` と `shape`）だけ**で、応答の本文は書かない。
  - 試験: パーサの正例 4・否定形 4（`」` の後の `,` に `"` が続かない形をレビューで追加）・正しい JSON は補修しない 1、オーケストレータの 1（一次の補修で二次へ進む・WRN の中身）。
  - IADR-0597、IADR の索引。
- 対象外:
  - 他の候補（`NoBrace` / `FullWidthBraces` / `UnclosedBrace`）の頑健化。IADR-0531 決定 3 のまま（原本で原因が確定してから）。
  - 素の `"` の混入（`監視銘"柄` 型）。閉じる位置が一意に決まらないため補修しない（否定形の試験で固定）。
  - 再試行（IADR-0531 決定 4 のまま）。
  - プロンプトの出力形式の指示の強化。原本を見てから決める。
  - Stage0 の記録（`Stage0DecisionRecorder`）へ補修の印を足すこと。記録は同じパーサで読み直すため補修後の値が入る。補修の事実はオーケストレータの WRN に残る。
  - `docs/`。FR-04 は網羅裁定 #211 の必須範囲外で、作業仕様書と xUnit を正の記録とする。運用文書に解析不能の grep 手順は無い（下の母集合）。

## 母集合（規則 9・10。着手前に引いた）

- パーサの呼び出し元: `grep -rn "ParseScreening(\|ParseDetailed(\|TradeDecisionParser.Parse(" backend --include=*.cs`（`/Tests/`・`/obj/` を除く）→ 実呼び出しは 3 か所。
  - `DecisionOrchestrator`（一次 49 行・二次 109 行）。WRN はここで出す。
  - `Stage0DecisionRecorder`（475 行・492 行）。オーケストレータを通した後に同じパーサで読み直す（決定的）。補修も同じに掛かり、本番と記録が食い違わない。印は持たない（上の対象外）。
  - `TradeDecisionParser.Parse`（17 行）は本番の呼び出し元が無い（テストのみ）。
  - 残りのヒット（`PositionEffectResolver`・`TradeDecisionPromptBuilder`・オーケストレータの 27 行）はコメントであり除外。
- 関連の記述: `grep -rln -e UnterminatedString -e 閉じない文字列 -e 解析不能の応答の原本 -e IADR-0531`（`.git`・`bin`・`obj`・`node_modules` を除く）→ 16 ファイル（本仕様書と IADR-0597 を書く前に数えた値。書いた後は両者が加わって 18 になる）。
  - `.ai-context/specs/` の 4 件と IADR-0531・IADR-0555 は凍結記録のため除外。IADR-0531 の決定 3 は本文を書き換えず、IADR-0597 が改める（索引の行で示す）。
  - `scripts/scripts.test.js`・`scripts/fixtures/review-floor/pr1333-review.txt` はファイル名の固定・過去のレビューの記録であり、意味は変わらないため除外。
  - `UnparseableOutputShapeTests` の `UnterminatedString` の例（48・49 行）は素の `"` の混入で、補修の形に当たらない。指紋の分類は補修の前の原文に対して掛かるため変わらない。
  - 残りはコード（本 PR の対象）と索引（本 PR で行を足す）。
- `docs/` の `解析不能` の 14 行（FR-15 のバー取得、FR-10 の損切り幅の任意化と根拠文の化け）は、解析不能の扱いが変わらないため追随しない。補修は解析不能だった出力を読めるようにするだけで、解析不能になった後の挙動は変えない。
- 新たに誤りになる自分の記述（規則 10）: IADR-0531 決定 3 の「閉じない文字列の補修: どこで閉じるべきかを推測するしかない」は、本件の 2 形では閉じる位置が一意に決まるため当てはまらなくなる。凍結記録のため本文は直さず、IADR-0597 が射程を限って改める。

## 補修の条件（すべて満たすときだけ）

1. `ExtractJsonObject` が null を返し、最初の `{` からの走査が**末尾で文字列の内側**に終わった。
2. 文字列の内側にある `」` の直後（空白を除く）が次のどちらかである。
   - 形 2: `}`。
   - 形 1: `,` → `"` → 既知のキー → `"` → `:`。既知のキーは出力形式の項目名 `action` / `rationale` / `referencePrice` / `stopLossDistancePerShare` / `expectedProfitPerShare` / `entryCondition` / `entryReferencePrice`。
3. その `」` の直後に `"` を補い、文字列の外へ出たとみなして走査を続ける。1 つの出力で補うのは最大 3 か所。
4. 補った結果から JSON オブジェクトを抽出できる。抽出できなければ補わなかったことにし、従来どおり `NoJsonObject` になる。抽出の後の JSON 構文・action・数値の検査は従来どおり掛かる。

## 受け入れ基準

1. 形 1・形 2 の出力は一次・二次とも解析でき、向きと数値が補修のないときと同じに読める。結果に補修の印が立つ。
2. 補修したときだけ、WRN 1 行に目印 `構造化出力の閉じの引用符を補って読みました` と `layer` と `shape` が出る。応答の本文は出ない。
3. 一次で補修して関心あり（Buy/Sell）になれば二次へ進む（打ち切らない）。解析不能の原本の行は出ない。
4. 補修の形に当たらない閉じない文字列（未知のキー、素の `"`、閉じかぎの後に構造が続かない）は、従来どおり解析不能になる。
5. 正しい JSON の中の閉じかぎは補修しない（印も立たない）。
6. 既存の試験（特性試験 `UnparseableOutputShapeTests` を含む）が全緑。

## 検証

- red → green: 補修を無効にする変異（`TryRepair` が常に false を返す）で、正例の試験 5 件（パーサの正例の Theory 4 ケースと、オーケストレータの試験 1 件）が赤になる（`Failed: 5, Passed: 5`。否定形 4 ケースと正しい JSON の 1 件は緑のまま）。戻すと緑になる。
- `dotnet build backend/backend.slnx`・`dotnet test`（TradeDecisionService.Tests・Architecture.Tests）・`dotnet format --verify-no-changes`。
- 文書系の検査: `node scripts/scripts.test.js`・`node scripts/check-trace-blocks.js`・`node scripts/gen-knowledge-graph.js --check`・`node scripts/check-cross-repo-refs.js`。

## PoC 配備への影響

- 構成・スキーマ・メッセージの変更は無い。TradeDecisionService のイメージの再ビルドだけで効く。
- 監査の見方: `grep '構造化出力の閉じの引用符を補って読みました' session-YYYYMMDD/trade-decision-service.log` で補修の件数と `layer`・`shape` を数える。補修した出力は解析不能の行（`構造化出力が解析不能`・`解析不能の応答の原本`）に出なくなる。
- 補修が増えたら、出力の形の退行の信号として扱う（プロンプトの指示を見直す材料にする）。
