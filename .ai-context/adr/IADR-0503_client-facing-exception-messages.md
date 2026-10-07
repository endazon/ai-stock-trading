---
title: IADR-0503 例外の文言を応答へ載せるのは業務の例外だけにする（報告書の 409 は専用の型・400 / INVALID_ARGUMENT は自前のコードの送出だけ・それ以外は固定文言か未処理例外）
type: impl-adr
status: Accepted
related_ids: [NFR-06, NFR-05, FR-06, FR-07, FR-10, FR-13, FR-17, ADR-0003, IADR-0496, IADR-0450, IADR-0449, IADR-0405, IADR-0024, IADR-0509]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-06 発注機能へのアクセスは利用者本人のみ・NFR-05 認証情報の秘匿)
---

# IADR-0503: 例外の文言を応答へ載せるのは業務の例外だけにする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-08
- 決定者: claude（#1206 の対応案と受け入れ基準に沿って起案）

## 起点・関連

- 関連する計画書 ID: NFR-06（セキュリティ）・NFR-05（認証情報の秘匿）
- 対象 Issue: [#1206](https://github.com/endazon/ai-stock-trading/issues/1206)（#1192 の独立監査 #1205 で範囲外として確認された既存の挙動）
- 関連する実装仕様書: [20261008_1206_exception-message-hardening](../specs/20261008_1206_exception-message-hardening.md)（母集合の全走査と除外の理由）
- 関連 IADR: [IADR-0496](IADR-0496_aspnetcore-environment-production-and-problem-details.md)（未処理例外の ProblemDetails）、
  [IADR-0450](IADR-0450_bot-write-grpc-stage5.md)（REST と gRPC の書き込みが同じ例外の写しを使う）、IADR-0405（監査台帳の自由記述の線引き）

## コンテキストと課題

報告書・リスク管理・市場監視・前提条件・費用統制の群のフィルタと gRPC の写しは、例外の `Message` をそのまま応答（HTTP の `error`・gRPC の status detail）へ載せていた。
とくに報告書の写しは**任意の** `InvalidOperationException` を 409 にしていたので、EF Core やフレームワークが投げる `InvalidOperationException`
（DbContext の並行使用・要素なし・未構成のプロバイダなど）の文言が「業務の競合」として返った。例外の文言はテーブル名・制約名・接続先などの内部構造を含み得る。

一方、業務のエラーの文言（「確定済み報告書 … は変更できません。」「market は必須です。」など）は Discord Bot が利用者へそのまま返し（gRPC の detail・REST の 400 の `error`）、
画面は 400 の詳細を併記するので、消してはならない。

## 決定

### 決定 1: 報告書の 409＋文言は専用の型 `ReportAlreadyConfirmedException` だけにする

- `ReportService/Common/Exceptions/ReportAlreadyConfirmedException`（`InvalidOperationException` の派生）を足し、確定済みの報告書を変更しようとした
  2 箇所（`EfReportStore`・`InMemoryReportStore`）だけがこれを投げる（`ReportService` の `throw new InvalidOperationException` の全走査で、業務として投げているのはこの 2 箇所だけだった）。
- `ReportEndpoints.MapException` はこの型と `ReportConcurrencyException`（既存の自前の型）だけを 409＋文言に写す。**素の `InvalidOperationException` は写さない**。
  REST では #1192 の共通の例外処理が 500 の ProblemDetails（type・title・status・traceId だけ）にして Error ログへ例外ごと出し、gRPC（REST と同じ写しを通る書き込み）では
  Grpc.AspNetCore の既定で詳細なしの `UNKNOWN` になりログへ出る。
- 派生にしたのは、自動生成（`ReportAutoGenerator`）の `catch (InvalidOperationException)`（「直前に確定された」を失敗に数えない）の挙動を変えないため。

### 決定 2: 400 / INVALID_ARGUMENT へ載せる `ArgumentException` の文言は「自前のコードの送出」に限る

- 共有の判定 `ClientFacingErrors`（`TestSupport.PlatformShim`。全サービスの配備で動く共通の配線）を足す。例外のスタックの先頭から CoreLib のフレーム
  （`ArgumentException.ThrowIfNullOrWhiteSpace` などの送出用の補助関数）を飛ばした最初のフレームが、呼び出し側のサービスのアセンブリか `AiStockTrading.Shared.*` なら自前の送出とみなし、文言を載せる。
- それ以外（フレームワーク・EF・ドライバ・宣言型の分からない動的メソッド・投げられていない例外）は固定文言「要求の内容が正しくありません。」にし、**状態コードは 400 のまま**、元の例外を Warning でログへ出す。
- 適用先（応答へ例外の文言が載る経路の全件。仕様書の母集合 A2〜A8）: 報告書の写し（REST・gRPC 書き込み）と gRPC 読み取りの `Reply`、リスク管理の写し（REST・gRPC 書き込み）と gRPC 読み取りの `Reply`、
  市場監視の写し（REST・gRPC の入れ替え案の適用）、前提条件と費用統制の群のフィルタ。

### 決定 3: 状態コード・gRPC の状態の分類・`error` の欄は変えない

受け手が機械的に読む形（400 / 409 と INVALID_ARGUMENT / ABORTED の対応、`{ "error": "…" }`）は保つ。変わるのは「業務でない `InvalidOperationException`」の状態（報告書の 409 → 500 / gRPC の ABORTED → UNKNOWN）と、
「自前でない `ArgumentException`」の文言だけである。

## 却下した案

- **`InvalidOperationException` を 409 のまま固定文言にする**: 状態は保てるが、サーバーの不具合を「利用者の操作と状態の競合」に見せ続け、Bot は再試行を促す文言を返してしまう。
  issue の対応案 2（#1205 の共通の例外処理と同じ形）にも合わない。
- **`ArgumentException` も専用の型で識別する**: 5 サービスの送出点（`throw new ArgumentException`・`ThrowIfNullOrWhiteSpace` の全数）の付け替えになり、取りこぼした送出点は文言が消える（利用者に何が足りないかが伝わらない）。
  送出元の判定は送出点に手を入れずに「フレームワーク由来だけを伏せる」を満たす。
- **CoreLib のフレームワークも「自前でない」とみなす**: `ThrowIfNullOrWhiteSpace(reason)` のような自前の入力検証の文言（`(Parameter 'reason')`）まで固定文言になり、既存の 400 の文言が変わる。

## 結果・影響

- 受け入れ基準 1: 報告書の REST・gRPC で、EF が実際に投げる `InvalidOperationException`（プロバイダ未構成）と接続文字列に似た目印を含む `InvalidOperationException` の文言が応答に出ないことを固定した（T-10-2388・T-10-2389）。
- 受け入れ基準 2: 確定済みの変更の 409 と文言（REST・gRPC の ABORTED）、自前の入力検証の 400 の文言（報告書・市場監視・前提条件）を固定した。既存の試験は 1 件を除き無変更で緑（下）。
- 既存の試験の変更 1 件: リスク管理の `T_10_1051`（gRPC 読み取りの ArgumentException が INVALID_ARGUMENT になる）は、試験の代役（試験のアセンブリ＝サービスのコードでない）が投げた文言を detail に期待していた。
  状態の期待は保ち、detail は固定文言を期待するように直した（本決定の意図どおりの変化）。
- 配備: 報告書・リスク管理・市場監視・前提条件・費用統制の 5 サービスのイメージの作り直しが要る（共通の配線 `TestSupport.PlatformShim` の変更は他のサービスにも入るが、挙動は呼ぶ 5 サービスだけが変わる）。

### 変異で確かめたこと（各変異で 6 つの試験プロジェクトの該当クラスを走らせた）

| 変異 | 内容 | 赤になる試験 |
| --- | --- | --- |
| M1 | 🔴 報告書の写しに素の `InvalidOperationException` → 409＋文言を戻す | T-10-2388（2）・T-10-2389（2） |
| M2 | 🔴 判定を外し常に例外の文言を返す | T-10-2392・T-10-2396・T-10-2397・T-10-2398・T-10-2400・T-10-2402・既存 `T_10_1051` |
| M3 | 判定を常に「自前でない」にする | T-10-2391・T-10-2394・T-10-2396・T-10-2399・T-10-2401 |
| M4 | CoreLib のフレームを飛ばさない | T-10-2394・T-10-2401 |
| M5 | 🔴 業務の型の 409 の写しを外す | T-10-2390・T-10-2393 |
| M6 | 固定文言に置き換えたときのログを出さない | T-10-2396 |
| M7 | 報告書の gRPC 読み取りの `Reply` だけ例外の文言に戻す | T-10-2392 |

7 本すべて赤（生存 0）。

## 残余リスク

- 自前のコードが CoreLib の API を呼んで投げられた `ArgumentException`（例: `Dictionary.Add` の重複キー・`Enum.Parse`）は自前の送出とみなされ、文言（キーや値を引用する）が載る。いずれも内部構造ではなく要求の値の引用である。
- 自前のコードが内側の例外の文言を自分の例外の文言へ写して投げた場合は、そのまま載る（全走査で応答の経路に該当は 0 件。起動時の構成エラーとログ・記録への転記は応答ではない）。
- 自前の型の `ReportConcurrencyException`・`AssumptionsConcurrencyException` の文言は従来どおり載る（期間キー・版番号だけを含む業務の文言）。
- 応答以外の記録（監査台帳の自由記述・バックテストの欠測・判断の記録）への例外の文言の転記は本決定の対象外（IADR-0405 の線引きが受け持つ）。
- 🔴 **JIT の最適化（段階コンパイルの tier-1 での インライン化）で、判定が実行中に変わり得る。** 第三者のライブラリの小さなメソッドが CoreLib の検証補助（`ThrowIfNullOrEmpty` 等）やコレクション（`Dictionary.Add` の重複キー）経由で投げる場合、そのメソッドが呼び出し元のサービスのフレームへインライン化されると、先頭のフレームがサービスの自前に見え、文言（キーの値を引用する）が載る（独立監査が Release の実験で再現した。冷えたコードの試験では再現しない）。第三者のメソッドが自分で `throw new` する場合はインライン化されず正しく判定された。現行のコードに機密を含む経路は見つかっていない。判定をスタックに依らない形（応答へ載せてよい文言を型や印で明示する）へ狭める作業は #1230 で扱う。

［2026-10-08 追記 / #1230］**上の 🔴 の残余リスク（JIT のインライン化で判定が揺れる）と、1 つ目の残余リスク（自前のコードが CoreLib の API を呼んで投げられた
`ArgumentException` の文言が載る）は [IADR-0509](IADR-0509_explicit-client-visible-argument-marker.md) で解消した。** 決定 2 のスタックの先頭のフレームによる判定
（`IsRaisedByOwnCode`）を廃し、文言を載せるのは送出点で明示の印（`Exception.Data` の `ClientVisibleArgument`）を付けた `ArgumentException` だけにした。
印の無いものは、自前のコードの送出（`ThrowIfNullOrWhiteSpace` 等）や `AiStockTrading.Shared.*` の送出でも固定文言になる。印を付けた箇所の一覧は
作業仕様書 `20261008_1230_explicit-client-visible-marker`。決定 1（報告書の 409 は専用の型）・決定 3（状態の分類は不変）は変わらない。
