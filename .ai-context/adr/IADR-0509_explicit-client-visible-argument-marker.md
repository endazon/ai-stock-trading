---
title: IADR-0509 400 / INVALID_ARGUMENT へ ArgumentException の文言を載せるのは、明示の印（Exception.Data の ClientVisibleArgument）のある例外だけにする（スタックの先頭のフレームによる判定を廃す）
type: impl-adr
status: Accepted
related_ids: [NFR-06, NFR-05, FR-03, FR-06, FR-07, FR-10, FR-11, FR-13, FR-17, FR-20, IADR-0503, IADR-0450, IADR-0256, IADR-0013]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-06 発注機能へのアクセスは利用者本人のみ・NFR-05 認証情報の秘匿)
related_specs:
  - ../specs/20261008_1230_explicit-client-visible-marker.md
---

# IADR-0509: ArgumentException の文言を載せるのは明示の印のある例外だけにする（#1230）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-08
- 決定者: claude（[#1230](https://github.com/endazon/ai-stock-trading/issues/1230) の受け入れ基準に沿って起案）

## 起点・関連

- 関連する計画書 ID: NFR-06（セキュリティ）・NFR-05（認証情報の秘匿）
- 対象 Issue: [#1230](https://github.com/endazon/ai-stock-trading/issues/1230)（PR #1229 の独立監査の指摘。IADR-0503 §残余リスク の起票先）
- 関連する実装仕様書: [20261008_1230_explicit-client-visible-marker](../specs/20261008_1230_explicit-client-visible-marker.md)（母集合・印を付けた箇所の一覧）
- 先行 IADR: [IADR-0503](IADR-0503_client-facing-exception-messages.md) 決定 2（本決定が置き換える）。IADR-0256（Domain の依存の検査）

## コンテキスト

IADR-0503 決定 2 は、400 / INVALID_ARGUMENT の応答へ `ArgumentException` の文言を載せるかを、例外のスタックの先頭から CoreLib のフレームを飛ばした
最初のフレームがサービスのアセンブリか `AiStockTrading.Shared.*` か（`ClientFacingErrors.IsRaisedByOwnCode`）で決めた。

PR #1229 の独立監査は Release の実験で次を再現した。段階コンパイルの tier-1（呼び出し回数が増えた後、または `DOTNET_TieredCompilation=0`）で、
第三者のライブラリの小さなメソッドが呼び出し元のサービスのフレームへインライン化されると、CoreLib の検証補助（`ThrowIfNullOrEmpty`）や
コレクション（`Dictionary.Add` の重複キー。文言にキーの値を引用する）経由の例外が「自前の送出」と判定され、文言が応答に載る。
同じ要求でも起動直後（tier-0）と温まった後（tier-1）で応答の文言が変わり、冷えたコードの試験では捕まらない。

判定がスタックに依る限り、JIT・インライン化・末尾呼び出しの変化で結果が揺れる。載せてよい文言は送出点で明示するしかない。

## 検討した選択肢

1. **専用の例外型 `ClientVisibleArgumentException : ArgumentException`**。型で捕まえられ、検索もしやすい。
   ただし自前の検証には `ArgumentOutOfRangeException`（`Stage1TradeCountBounds`。試験が型と `ActualValue` を固定）と、CoreLib の空欄検査
   （null で `ArgumentNullException`・空白で `ArgumentException`。文言 `(Parameter 'reason')` を利用者が読んでいる）がある。派生 1 つでは
   これらの型を保てず、文言も作り直しになる（型ごとに派生を増やすと「印」が 3 つの型に分かれる）。
2. **`Exception.Data` の印**（採用）。どの `ArgumentException` 系の例外にも、型・`ParamName`・`ActualValue`・文言を変えずに付けられる。
3. **スタックの判定を残して tier-1 を検出する／インライン化を禁止する**。第三者のメソッドの JIT の扱いは本リポジトリから制御できず、
   判定の揺れそのものは消えない。却下。

## 決定

### 決定 1: 印は `Exception.Data` に置き、`Shared.Contracts` の `ClientVisibleArgument` が付け外しを受け持つ

- `AiStockTrading.Shared.Contracts.Errors.ClientVisibleArgument`:
  - `throw new ArgumentException(…).ClientVisible();` — 例外へ印（`Data["AiStockTrading.ClientVisibleMessage"] = true`）を付けて同じ例外を返す。
    制約は `where TException : ArgumentException`（`ArgumentOutOfRangeException` もそのまま）。
  - `exception.IsClientVisible()` — 印のある `ArgumentException` 系か。**別の型に同じキーを置いても印とみなさない**。内側の例外は見ない。
  - `ClientVisibleArgument.ThrowIfNullOrWhiteSpace(reason)` — CoreLib の `ArgumentException.ThrowIfNullOrWhiteSpace` と同じ検査・同じ例外・同じ文言を、印を付けて投げる。
- 置き場所を `Shared.Contracts` にするのは、Domain（`RiskLimitBounds`・`Stage1TradeCountBounds`）から使うため。Domain が `using` してよい共有物は
  `Shared.Contracts` / `Shared.Kernel` だけ（IADR-0256 の検査）で、`.NET` 標準だけで書く。判定の置き場所（`TestSupport.PlatformShim`）も `Shared.Contracts` を参照済み。

### 決定 2: 判定は印だけで行う（スタックを読まない）

- `ClientFacingErrors.MessageFor(exception, logger, fixedMessage)` は、印があれば例外の文言、無ければ固定文言「要求の内容が正しくありません。」を返し、
  元の例外を Warning でログへ出す（IADR-0503 と同じ。状態コード・gRPC の状態の分類は不変）。
- `IsRaisedByOwnCode` とサービスのアセンブリの引数は撤去した。呼び出し 7 箇所（報告書・リスク管理の REST と gRPC 読み取り、市場監視・前提条件・費用統制の REST。
  gRPC 書き込みは REST の写しを共有）を直した。
- **印の無いものは、自前のコードの送出（`ThrowIfNullOrWhiteSpace` 等の CoreLib の補助を含む）でも固定文言**になる。`AiStockTrading.Shared.*` の送出も同じ
  （IADR-0503 は「自前」としていた）。

### 決定 3: 印を付けるのは、利用者（Discord・画面）が読む入力検証の文言だけ

- 仕様書の母集合 B の V1〜V20（送出点 26）。明示の検証の文言（期間キーの不一致・リスク上限と最小取引件数の値域・口座種別と商品種別・段階の既定発注先・
  監視銘柄の追加と削除と置換・入れ替え案の形・変動閾値とクールダウン）と、利用者が入力する欄の空欄検査（理由・銘柄コード）。
- 印を付けないもの: 認証から入る `actor`・経路の値・内部の不変条件・起動時の構成・`Shared.*` の内部の計算（一覧と理由は仕様書）。
- 新しい入力検証を足すときは、利用者へ見せる文言なら送出点で印を付ける。付け忘れは「利用者に何が足りないかが伝わらない」側へ倒れる（安全側）。

## 結果

- 受け入れ基準 1: T-10-2418（`AggressiveInlining` の第三者相当の補助を 1,000 回）・T-10-2420（報告書の REST・gRPC 読み取り・gRPC 書き込みを 200 回）・
  T-10-2424（リスク管理の REST・gRPC 読み取り）・T-10-2426（市場監視の REST）で、印の無い送出が常に固定文言であることを固定した。
  `DOTNET_TieredCompilation=0` の構成の試験は置かない（判定がスタックを読まないので、段階の有無が結果に入る経路が無い）。
- 受け入れ基準 2: T-10-2419（印は型・`ParamName`・`ActualValue`・文言を変えない）・T-10-2421・T-10-2423・T-10-2424・T-10-2425 と既存の T-10-2391・T-10-2399・T-10-2401。
- 受け入れ基準 3: T-10-2418・T-10-2420・T-10-2422（実際の経路の印の無い `ThrowIfNullOrWhiteSpace`）。
- 撤去した試験: T-10-2394・T-10-2395（スタックの判定の単体）。T-10-2396 は印の判定へ直した。既存の他の試験は無変更で緑。
- 配備: 報告書・リスク管理・市場監視・前提条件・費用統制のイメージの作り直しが要る。

### 変異で確かめたこと

| 変異 | 内容 | 赤になる試験 |
| --- | --- | --- |
| Y1 | 🔴 判定を常に「見せる」にする | T-10-2418・T-10-2396・T-10-2392・T-10-2420（2）・T-10-2422・T-10-2424・T-10-1051・T-10-2398・T-10-2426・T-10-2400 |
| Y2 | 判定を常に「見せない」にする | T-10-2419・T-10-2396・T-10-2421・T-10-2391・T-10-2423・T-10-2424・T-10-2425・T-10-2399・T-10-2401 |
| Y3 | 印つきの空欄検査が印を付けない | T-10-2419・T-10-2423・T-10-2401 |

3 本すべて赤（生存 0）。

## 残余リスク

- 印の付け忘れは機械で検査しない（利用者へ見せる文言かは意味の判断）。付け忘れた検証は固定文言になり、利用者に何が足りないかが伝わらない（漏れの側ではない）。
- 印を付けた送出点の文言に、内側の例外の文言や内部の値を後から混ぜると、そのまま載る。印を付けるのは自前で組み立てた文言（と CoreLib の空欄検査の文言）に限る。
- `Exception.Data` は可変で、どのコードからも印を付けられる。第三者の例外を捕まえて印を付け直すことは本リポジトリの規約で禁じる（仕様書の母集合に該当 0 件）。
