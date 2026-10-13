---
title: IADR-0531 LLM の構造化出力が解析不能のときだけ、応答の原本の抜粋（先頭 1,000 字＋末尾 500 字）と形の指紋を WRN 1 行で残す。抽出は頑健化せず、再試行もしない（原本で原因が確定してから決める）
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-11, ADR-0003, ADR-0014, NFR-10, IADR-0248, IADR-0061, IADR-0316, IADR-0525, IADR-0104]
author: claude (Claude Code)
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04 判断根拠の記録・FR-11 監査可能なログ・NFR-10 監査ログの 7 年保持)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (出力の構造化・不正は安全側)
related_specs:
  - ../specs/20261013_1332_screening-unparseable-raw-excerpt.md
---

# IADR-0531: 解析不能の応答の原本を限られた長さで保全し、形の指紋で原因の候補を切り分ける（#1332）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-13
- 決定者: 実装（Claude Code）。#1332 の依頼（原本の保全と原因の特定）への判断。

## 起点・関連

- 起票: [#1332](https://github.com/endazon/ai-stock-trading/issues/1332)（PoC 2026-10-12 米国セッション。一次スクリーニング〔claude-haiku-5-5〕の `NoJsonObject` が 682 回中 2 回。出力 1,012 / 1,203 トークン・MaxTokens 4,096・上限到達の WRN 0 件＝切り詰めではない）
- 前提（覆さない）: IADR-0248（解析不能と見送りの区別・挙動は Hold）・IADR-0061 決定 1（`LogPrompts` の全量記録は既定オフ）・IADR-0316（ログ値は発生源で 1 行に正規化）・IADR-0104（拒否・空・上限到達の判定）
- 仕様書: `.ai-context/specs/20261013_1332_screening-unparseable-raw-excerpt.md`

## コンテキスト

- 解析不能のときに残るのは `kind=NoJsonObject detail=JSON オブジェクトを抽出できない` だけで、応答の本文は 1 文字も残らない。本文を残す既存の手段は `LlmGateway__LogPrompts=true`（IADR-0061 決定 1）だけで、**プロンプト全量（保有・資金残枠）も毎回書く**ため既定オフである。0.29% の事象のために常時有効にはできない。
- 抽出（`TradeDecisionParser.ExtractJsonObject`）は**最初の ASCII `{`** から、ASCII の `"`（`\` のエスケープを考慮）で文字列の内外を切り替えつつ深さを数え、深さ 0 までを返す。したがって `NoJsonObject` ⇔「ASCII `{` が無い」または「最初の `{` からの走査が末尾まで深さ 0 に戻らない（末尾で文字列の内側／`{` が残る）」である。
- 基盤のゲートウェイ（MSP `ClaudeProvider.CompleteAsync`）は thinking を落としたうえで **`Content.OfType<TextContent>().FirstOrDefault()`＝1 個目の text ブロックだけ**を返す（MSP develop で確認）。

## 原因の候補（原本なしで絞り込んだ結果）

コードで追い、特性試験（`UnparseableOutputShapeTests`）で種別を固定した。

| 形 | 種別 | 判定 |
| --- | --- | --- |
| 前置きの散文のあとに JSON／コードフェンス／文字列の中の括弧／JSON が 2 個 | 成功（最初の 1 個） | **除外** |
| 出力形式の雛形の書き写し（`参照価格` などの非数値） | MalformedJson | **除外**（種別が違う） |
| 思考だけで本文が空 | EmptyOutput | **除外**（種別が違う） |
| 切り詰め | — | **除外**（stop_reason が max_tokens ではない） |
| A. 散文だけ（拒否・説明・「以下が出力です：」で終わる） | NoJsonObject | 候補 `NoBrace` |
| B. JSON が 2 個目の text ブロックにある（ゲートウェイが 1 個目しか返さない） | NoJsonObject | 候補 `NoBrace`（AST から見ると A と同じ形。抜粋の末尾で見分ける） |
| C. 全角の括弧 `｛ ｝` で書いた JSON | NoJsonObject | 候補 `FullWidthBraces` |
| D. 文字列が閉じない（エスケープされない `"` が奇数個。#1290 で一次モデル（当時 claude-haiku-4-5）に出た語の化けが `"` を含むトークンを出す、根拠文に `"` を素で書く） | NoJsonObject | 候補 `UnterminatedString` |
| E. JSON より前の散文に閉じない `{`、または JSON の閉じ `}` の欠落 | NoJsonObject | 候補 `UnclosedBrace` |

- D は `JsonDocument` に渡せば `MalformedJson` になる形だが、走査が末尾まで文字列の内側に留まるので `NoJsonObject` として現れる（種別名から「JSON が無かった」と読むと見落とす）。
- 優先度の見立て（原本で確かめる）: 出力 1,012 / 1,203 トークンは thinking を含む数で、本文の長さは分からない。#1290 で一次モデル（当時 claude-haiku-4-5）の語の化け（`監視銘HeaderItem` など、本来の語と無関係なトークン）が 47% まで出ていたことから **D が最有力**、次いで A/B。C と E は日本語の出力では起きにくい。

## 決定

### 決定 1: 解析不能のときだけ、原本の抜粋と形の指紋を WRN 1 行で残す（方式・量の上限・保持期間）

- **どこで**: `DecisionOrchestrator`（一次・二次の両方。同じ関数 `LogUnparseableRaw`）。既存の解析不能の WRN（PoC の監査が grep する行）は**変えず**、その直後に別の 1 行を出す。目印は `解析不能の応答の原本`、層は `layer=screening|decision`。
- **いつ**: 解析不能の種別のうち `NoJsonObject` / `MalformedJson` / `UnknownAction` / `InvalidValues`。`EmptyOutput`（残す本文が無い）と解析できた応答（見送りを含む）では出さない。
- **何を**:
  - 形の指紋（`TradeDecisionParser.DescribeShape`。走査は抽出と同じ関数を共有し、抽出の挙動は変えない）: `suspect`（候補）・`length`・`braces={開}/{閉}`・`fullWidthBraces`・`quotes`・`fences`・`newlines`・`firstBrace`・`endedInString`・`depthAtEnd`。
  - 本文の抜粋（`UnparseableOutputExcerpt.Of`）: **先頭 1,000 字＋末尾 500 字**、中央は `…(中略 N 文字)…` に置き換える（前置きの散文も末尾の閉じ括弧も見える）。サロゲートペアは割らない。1,500 字以下なら全文。
  - 1 行化は `LogSanitizer.Sanitize` を 1 回だけ（IADR-0316）。改行・制御文字は `_` に置き換わるため、元の改行数を指紋の `newlines` に残す。
- **書かないもの**: プロンプト（保有・資金残枠）。`LogPrompts` の既定オフ（IADR-0061 決定 1）は変えない。
- **秘密の扱い**: 本文はモデルの出力で、見送りの根拠文（既に Information で全文を出している）と同じ種類の情報である。プロンプトに資格情報は入らないので、出力にも入らない。伏せ字は掛けない（掛けると原因の切り分けに要る `"` `{` の並びを壊す）。
- **量の上限**: 1 行あたり本文 1,500 字＋注記＋指紋（UTF-8 で約 5 KB 以下）。行数は解析不能の回数に比例し、それは LLM の呼び出し回数（月次の費用上限と判断の周期で上が決まる）を超えない。10-12 の頻度（2/682）なら 1 セッション約 10 KB。全件が解析不能になる退行でも一次 682 行＋二次の票数分であり、そのときは既存の WRN も同じ数だけ出る（本行だけが肥大化の原因にはならない）。
- **保持期間**: **運用ログ（サービスの Serilog 出力）と同じ**。監査台帳（FR-11・NFR-10 の 7 年保持）へは入れない —— 本行は診断用で、判断の記録（`TradeDecisionMade`・FR-11 の判断ログ）ではない。PoC では session ディレクトリのログファイルに残る（PoC の手順で取り出す）。
- **外す条件**: 置き続ける（解析不能の原因の切り分けは、モデルの世代が替わるたびに要る。費用は解析不能の回数に比例するだけ）。

### 決定 2: 原因の候補は形の指紋の `suspect` で機械的に決める

判定の順は「`{` が無い → 全角の括弧がある（`FullWidthBraces`）／無い（`NoBrace`）」「最初の `{` から閉じない → 末尾で文字列の内側（`UnterminatedString`）／深さが残る（`UnclosedBrace`）」。抽出できた種別は `NotApplicable`。表は上の「原因の候補」。

### 決定 3: 抽出は頑健化しない（今は）

次の頑健化は、いずれも**誤って別の JSON を拾う**経路を開くため採らない。原本で原因が確定してから、その原因に限って決める。

- 2 個目以降の `{` からの再走査（E 向け）: 閉じない外側の中にある内側の `{"action":...}`（例: 根拠の中に書いた別案）を拾い、**外側と逆の向き**を採り得る。パーサは二次本判断（発注へ進む）と共有である。
- 全角の括弧の読み替え（C 向け）: 根拠文の中の全角の括弧まで書き換える。
- 閉じない文字列の補修（D 向け）: どこで閉じるべきかを推測するしかない。
- 現行の「最初の 1 個だけ」は特性試験で固定した（変えたら赤くなる）。

### 決定 4: 再試行はしない（今は）。費用の見積り

- 一次の解析不能の影響は「その銘柄のそのサイクルの見送り」で、保有中なら出口判断が 1 サイクル遅れる（10-12 の META は 2 サイクル。S1 の損切りが保護していた）。
- 一次の再試行（1 回）を足すと、追加の費用は**解析不能の回数 × 一次 1 回分**である。10-12 の頻度なら一次の呼び出しが約 0.3% 増える（2/682）。退行で全件が解析不能になると一次の費用が倍になり、原因を隠す（再試行で通ると監査に残らない）。
- 温度: 基盤のゲートウェイは temperature を送らない（MSP `ClaudeProvider`。thinking が既定で有効なモデルで 400 になるため）。AST から変えられない。
- 原本で原因が確定し、発生率が 1% を超えたら再試行の要否を改めて決める（その時も再試行で通った件を数えて残す）。

## PoC で確かめる手順

1. イメージを再ビルドして PoC を回す（構成の変更は要らない。`LogPrompts` は無効のまま）。
2. 原本の行を取り出す:
   `grep '解析不能の応答の原本' session-YYYYMMDD/trade-decision-service.log`
   候補の集計: `grep -o 'layer=[a-z]* kind=[A-Za-z]* suspect=[A-Za-z]*' … | sort | uniq -c`
3. `suspect` で読む:
   - `UnterminatedString`: 抜粋の中で、閉じない `"` の位置（語の化け・素の引用）を探す。化けなら #1290 と同型の一次モデルの事象として扱う（プロンプトの語の言い換え・モデルの割当の見直し）。
   - `NoBrace`: 抜粋の末尾が「以下が出力です：」「次のとおり」等で終わっていれば **B**（JSON が 2 個目の text ブロック）→ MSP へ起票する（`ClaudeProvider` が 1 個目の text ブロックしか返さない。基盤無改修のため AST では直さない）。散文の拒否・説明で完結していれば **A** → 出力形式の指示を強める。
   - `FullWidthBraces` / `UnclosedBrace`: 抜粋で位置を確かめ、決定 3 の頑健化をその原因に限って検討する。
4. 再現（任意）: `length` が 1,500 以下なら抜粋は全文である。`_` のうち `newlines` の数だけを改行へ戻して `UnparseableOutputShapeTests` の `InlineData` に足し、同じ種別・同じ `suspect` になることを確かめる（`_` はタブ等の制御文字の置き換えでもある点に注意）。1,500 字を超え、中略の部分が要る場合だけ、1 セッション限りで `LlmGateway__LogPrompts=true` にして全文を得る（プロンプトも記録されるので、終わったら戻す）。
5. 結果を #1332 に書き、原因に応じた修正（プロンプト・抽出・基盤への起票）を別に決める。

## 結果

- 良い点: 構成を変えずに、次に起きた 1 件から原因を確定できる。プロンプトは書かない。抽出・種別・挙動は変わらない。
- 悪い点: モデルの出力の抜粋が運用ログに残る（見送りの根拠文と同じ種類の情報）。退行で全件が解析不能になると行数がその分増える。
- 残余リスク: 候補 B（ゲートウェイが 1 個目の text ブロックしか返さない）は、AST から見ると散文だけの応答（A）と同じ形であり、抜粋の末尾で見分けるしかない。確定には基盤側のログ（content ブロックの数）が要る。
