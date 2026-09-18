---
title: Discord の報告書レビューで会話キーの大小文字を保ち、週報を確定できるようにする
type: spec
status: accepted
related_ids: [FR-07, FR-14, UC-03, UC-04, UC-05, ADR-0003, IADR-0032, IADR-0042, IADR-0071, IADR-0240]
author: endazon (with Claude Code)
created: 2026-09-18
updated: 2026-09-18
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-07「報告書は利用者の確定をもって有効になる」/ FR-14「Discord からの操作」)
---

# 仕様書: 会話キーの大小文字を保つ（#835）

## 起点

- #835（bug・PoC 運用中に利用者が遭遇）。週報 `weekly-2026-W38` を Discord から確定できない。
- 日報 `daily-2026-09-18` / `daily-2026-09-15` は同じ操作で確定できている（実測）。

## 実測（稼働クラスタ・読み取りのみ・2026-09-18）

notification-service のログ:

```
[09:45:33 INF] Sending HTTP request POST http://report-service:8080/reports/daily-2026-09-18/confirm
[09:45:33 INF] 報告書を確定しました（Actor=developer・PeriodKey=daily-2026-09-18・版=1）。
[09:45:50 WRN] レビュー局面の照会に失敗しました（404）。
[09:45:50 INF] 報告書レビューを照会しました（Actor=developer・PeriodKey=weekly-2026-w38・Succeeded=False）。
```

報告書サービスの自然キー（`report_svc.reports`）は `weekly-2026-W38`。送られたキーは `weekly-2026-w38`。

## 原因

`BotCommandParser.Parse` はコマンド文字列**全体**に `ToLowerInvariant()` を掛けてからトークンへ割る
（`BotCommandParser.cs:37`）。`ParseReport` はその小文字化済みトークンを会話キーとして採る（同 `:70`）。

会話キーは `ReportPeriod.ExpectedKey` が作る `{kind}-{label}` であり、週報の label は ISO 週の `2026-W38`
＝**大文字 W を含む**（`ReportService/Domain/ReportPeriod.cs:11,18`）。`EfReportStore.GetReview` /
`Confirm` は自然キーの完全一致で引くため、小文字化されたキーはどの報告書にも一致しない。

影響範囲は週報だけである（日報 `daily-yyyy-MM-dd` と月報 `monthly-yyyy-MM` は英小文字と数字のみ）。
ドラフト通知の確認ボタン（CustomId `ast-report-approve-<periodKey>-<version>`）も同じパーサを通るため、
**ボタン経由でも確定できない**。利用者側の回避手段は無い（大文字で入力してもパーサが潰す）。

## 決定

1. **動詞・副コマンドの大小文字は従来どおり吸収し、会話キーだけ原文の大小文字を保つ。**
   `Parse` は原文トークン列と小文字化トークン列の 2 本を持ち、比較・分岐は小文字側、会話キーの採取は原文側で行う。
   「曖昧一致で誤起動させない」という既存方針は動詞側に残る。
2. **会話キーの値域を `^[A-Za-z0-9-]{1,32}$` へ広げる。** 記号（`/` `.` `?` `&` 等）は従来どおり許さない。
   値域制限の目的はパス・トラバーサルとクエリ注入の遮断（IADR-0240 決定6）であり、大文字英字を許しても目的は損なわれない。
3. **正規化（小文字キーを大文字へ直す等の推測補正）はしない。** 書式外は従来どおり Unknown へ倒す。
   利用者がキーを覚えなくて済むようにする件は #834（入力補完）の射程であり、本件では扱わない。

## 変更しないもの

- 報告書サービス側の引き方（完全一致）。自然キーの生成規則。
- 多層認証・版番号ガード・二重確定の吸収（IADR-0240 決定2・決定7）。
- 日報・月報の解析結果（1 バイトも変えない）。

## 受け入れ基準

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | `/report show weekly-2026-W38` が大文字 W のまま `ReportShow` として解析される | 単体 |
| 2 | `/report approve weekly-2026-W38 3` が大文字 W ＋版 3 で解析される | 単体 |
| 3 | `/REPORT SHOW weekly-2026-W38` のように動詞側が大文字でも解析できる | 単体 |
| 4 | 記号を含む会話キー（`../secrets` 等）は Unknown へ倒れる | 単体 |
| 5 | 日報・月報の解析結果が従来と同じ | 既存単体 |
| 6 | 確認ボタン（CustomId 経由）の確定も大文字 W のまま報告書サービスへ届く | 単体（ハンドラ経路） |
