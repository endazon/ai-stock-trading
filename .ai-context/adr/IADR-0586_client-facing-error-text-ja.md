---
title: IADR-0586 利用者へ見せる 400 の文言は MessageFor が .NET の接尾辞を落とし、空欄の検査は日本語の文言で投げ、enum は Shared.Contracts の表示名で書く
type: impl-adr
status: Accepted
related_ids: [SC-01, SC-02, FR-03, FR-10, FR-13, FR-17, FR-19, NFR-06, IADR-0509, IADR-0503, IADR-0585, IADR-0539]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
related_specs:
  - ../specs/20261024_1472_client-facing-error-text-ja.md
---

# IADR-0586: 利用者へ見せる 400 の文言を日本語に揃える

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-24
- 決定者: Claude Code（#1472 が「`ClientFacingErrors.MessageFor` で接尾辞を落とし、文言を日本語に揃える」を求めた）

## コンテキスト

1. 印つき（`ClientVisibleArgument`。IADR-0509）の `ArgumentException` の文言は、`ClientFacingErrors.MessageFor` を通って 400 の `error`
   （REST）と `INVALID_ARGUMENT` の detail（gRPC・Discord Bot）へそのまま載る。#1469（IADR-0585）以後、画面もそれを出す。
2. `ArgumentException.Message` は `ParamName` があると ` (Parameter 'x')` を付け、`ArgumentOutOfRangeException` は `ActualValue` があると
   改行と `Actual value was N.` を付ける。例外の内側の「素の文言」を読む公開の口は無い。
3. `ClientVisibleArgument.ThrowIfNullOrWhiteSpace` は CoreLib の `ArgumentException.ThrowIfNullOrWhiteSpace` の例外をそのまま投げるため、
   英語の既定文言（`The value cannot be an empty string or composed entirely of whitespace.` / `Value cannot be null.`）になる。
4. 監視銘柄・口座種別の組み合わせ・報告書の PeriodKey の文言は、enum を補間して列挙名（`UnitedStates`・`Cash`・`Daily`）を出す。
5. 本番の文言は AST の `PlatformShim` で作られる（基盤に同名の型は無い）。基盤の変更は要らない。

## 決定

1. **接尾辞は `MessageFor` の 1 か所で落とす。** 印つきの例外のときだけ、同じ `ParamName`（`ArgumentOutOfRangeException` は加えて同じ `ActualValue`）で
   空の文言の例外を作り、その `Message`（＝ .NET が付ける末尾そのもの）が末尾に一致すれば取り除く。英語の文字列は直書きしない
   （実行時の文化の資源で付く末尾と同じものを、同じ実行時から得る）。例外の型・`ParamName`・`Message` は変えない（ログ・既存の試験・`IsClientVisible` の判定は不変）。
   - 却下: 送出側の 20 か所で `ParamName` を外す（`nameof` を消すと、ログで引数が辿れなくなる。新しい送出が付け忘れると再発する）。
   - 却下: `Exception.Data` に素の文言を別に積む（`ClientVisible()` の時点でも素の文言を読む口が無い。送出側の書き換えが要る）。
   - 却下: 内部フィールド（`_message`）をリフレクションで読む（実装の詳細への依存）。
2. **空欄の検査は日本語の文言で投げる。** `ClientVisibleArgument.ThrowIfNullOrWhiteSpace(argument, label)` は `「{label}は必須です。」` を文言にし、
   null は `ArgumentNullException`・空白は `ArgumentException`（型と `ParamName` は CoreLib と同じ）で印を付けて投げる。項目の日本語名は呼び出し側が渡す
   （理由・銘柄コード）。必須の引数にして、付け忘れをコンパイルで止める。
3. **enum の表示名は `Shared.Contracts` の `TradingTermDisplay` に置く**（`OrderStatusDisplay`〔IADR-0539〕と同じ置き場・同じ作法。
   Domain からも使え、判定には使わない表示専用）。市場 `日本` / `米国`（`ControlFormatting.MarketName`・Discord の選択肢と同じ語）、
   口座種別 `信用口座` / `現金口座`、商品種別 `現物` / `信用買い` / `空売り`（画面の呼び名と同じ）。未定義の値は列挙値をそのまま出す。
   報告書の種別は ReportService の既存の呼び名（`ReportSummary.KindLabel`。`日報` / `週報` / `月報`）を内部へ開いて使う（報告書の種別は共有契約ではない）。
4. **画面とテストの期待値は契約フィクスチャ経由で追随させる。** フィクスチャは `UPDATE_CONTRACT_FIXTURES=1 dotnet test` で採り直し、画面の単体試験・E2E は
   フィクスチャを読むので書き換えない。

## 結果

- REST と Bot（gRPC の写しは同じ `MapException` / `MessageFor` を通る）の両方で、接尾辞と `Actual value was` の行が消える。
- 以後に印を付けた送出も、`nameof` を付けたまま接尾辞が出ない。

## 残余リスク

- 端点が直接組む `market は有効な市場（Japan/UnitedStates）を指定してください。`（決済・建玉差の採用）は入力値の説明として残した（画面は数値で送るため通常は出ない）。
- `RiskSettingsService` の文言に計画 ADR の番号（`ADR-0021 決定4-4`）が残る（本決定の対象外）。
- `ControlFormatting.MarketName`（RiskManagementService 内）と `TradingTermDisplay` が同じ語を 2 か所で持つ。寄せるのは別の変更とする。
