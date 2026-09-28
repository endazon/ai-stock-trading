---
title: NotificationService テストの CS0108 警告（BotAuthHandler.Scheme が継承メンバーを隠す）を解消する
type: spec
status: accepted
related_ids: [NFR]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs: []
---

# NotificationService テストの CS0108 警告を解消する

## 背景

MSP#1698 の監査で、`dotnet build backend/backend.slnx` を実行すると警告が 1 件出ると報告された（エラーは 0）。
警告は `PolicyApprovalRealReportHostTests.cs(194,29): warning CS0108 'BotAuthHandler.Scheme' hides inherited member` である。
基底の `AuthenticationHandler<T>.Scheme`（`AuthenticationScheme` 型のプロパティ）を、同名の `const string` が隠している。
CLAUDE.md は「警告ゼロを保つ」と定める。NFR の無採番のメタ作業で、計画 ID・計画 ADR の新たな制約は無い。

- 監査は、同じ警告が 3e17ddd6 でも出ることを確かめている。最後に変更したのは #1027 で、既存の問題である。

## 変更

定数 `Scheme` を `SchemeName` へ改名する。参照している 4 か所も追随する。

- 名前は既存の慣例に揃えた。`git grep "public const string SchemeName"` で、各サービスの `TestAuthHandler`（8 件）と `GrpcAuditLedgerSourcesTests` が同名を使っている。
- `new` 修飾子で隠蔽を明示する案は採らない。ハンドラの中の `Scheme` が定数と基底のプロパティのどちらを指すのかが読み手に分かりにくいまま残るためである。

## 母集合（規則 9）

`git grep -n "public const string Scheme\b" origin/develop -- backend` の該当はこのファイルの 1 件だけである。
同じ形の隠蔽は、ほかに無い。

## 検証

- `dotnet build backend/Services/NotificationService/Tests/NotificationService.Tests.csproj --no-incremental`: 0 Error(s) / 0 Warning(s)。
- `dotnet test ... --filter PolicyApprovalRealReportHostTests`: 2/2 成功。
- `dotnet format --verify-no-changes`（対象ディレクトリ）: 差分なし。
