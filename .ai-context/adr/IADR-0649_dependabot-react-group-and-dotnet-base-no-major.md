---
title: IADR-0649 Dependabot の npm は react・react-dom・@types/react・@types/react-dom を 1 つの group で上げ、docker /backend は dotnet/sdk・dotnet/aspnet の大版を出させない
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0046, IADR-0609, IADR-0625]
author: claude (Claude Code)
created: 2026-11-12
updated: 2026-11-12
---

# IADR-0649: Dependabot の react 系を 1 本に束ね、.NET 基底の大版を出させない

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-11-12
- 決定者: claude（依頼はオーケストレーター 2026-11-12。壊れた Dependabot PR #1630 / #1624 / #1625 の置き換え）

## 起点・関連

- 計画書 ID: **NFR**（工程の統制。無採番）
- 前提: [IADR-0625](./IADR-0625_ci-workflow-hardening-deterministic-backlog-audit.md)（Dependabot に nuget / npm を足した）、
  [IADR-0609](./IADR-0609_pod-security-context-staged.md)（`backend/Dockerfile` の基底を digest で固定し、docker /backend を足した）、
  [IADR-0046](./IADR-0046_unit-repo-layout.md)（対象の枠 net10.0）。
- 倣った先例: microservices-platform の IADR-0711（同リポの採番空間。docker の `ignore` に `version-update:semver-major`、
  `dependency-name` は registry の host を除いた名前）。
- 作業仕様書: [20261112_dependabot-react-pair-dotnet-major](../specs/20261112_dependabot-react-pair-dotnet-major.md)。

## 文脈

1. **#1630** は `react` を ^19.3.1、`@types/react` を ^19.3.0 へ上げ、`react-dom` を ^19.2.0 のまま残した。CI は
   「Incompatible React versions: react and react-dom must have the exact same version」で赤。react と react-dom は
   完全に同じ版を要求するが、Dependabot の既定は 1 依存 1 PR で、対で上げる手掛かりを持たない。
2. **#1624 / #1625** は `backend/Dockerfile` の基底 `dotnet/aspnet` / `dotnet/sdk` を 10.0 → 11.0 へ上げた。対象の枠は net10.0
   （IADR-0046・`Directory.Build.props`）であり、基底の大版はフレームワークの移行（SDK・ランタイム・パッケージの揃え直し）で、
   依存の更新ではない。しかも sdk と aspnet が別の PR に割れ、どちらか片方だけでは成り立たない。

## 決定

1. **npm（/frontend）に group `react` を置き、`react`・`react-dom`・`@types/react`・`@types/react-dom` を 1 本の PR で上げる。**
   パターンは名前の完全一致（ワイルドカードを使わない。`@tanstack/react-*`・`@testing-library/react` 等を巻き込まない）。
2. **docker（/backend）の `ignore` に `dotnet/sdk`・`dotnet/aspnet` の `version-update:semver-major` を置く。**
   - `dependency-name` は host（`mcr.microsoft.com`）を除いた名前。#1624 / #1625 の題（`bump dotnet/aspnet from 10.0 to 11.0`）が同じ表記で、
     MSP の IADR-0711 も同じ形。
   - 同じ tag（`10.0`）の digest の更新（基底のセキュリティ修正）は従来どおり提案させる（IADR-0609 の意図を保つ）。
   - 名指しにし、`dependency-name: "*"` にはしない（本決定の対象は .NET の基底だけ。後から別の基底が足されたときに黙って止めない）。
3. **広げない。** `/deploy/opend`（`dotnet/runtime-deps:8.0-jammy`）・nuget・github-actions には手を入れない。
4. **今の版へ揃える**: react・react-dom 19.3.1、@types/react・@types/react-dom 19.3.0（npm の latest。react-dom 19.3.1 の実在を `npm view` で確かめた）。
5. **試験**: `scripts.repo.test.js` に group の中身（4 つちょうど）と /backend の 2 つの ignore、/deploy/opend に ignore が無いことの言明を置く。

## 代替案

- **`ignore` に react-dom の版を足す／react を固定する**: 採らない。更新を止めるだけで、対で上げる形にならない。
- **group のパターンを `react*` / `@types/react*`**: 採らない。`react-*` を名に持つ別の依存（`@types/react-*` の別パッケージ等）を巻き込みうる。
- **docker /backend も group で sdk・aspnet を束ね、大版も出させる**: 採らない。大版はフレームワークの移行で、PR 1 本の更新として扱わない。
  .NET 11 への移行は、必要になったときに IADR で決め、`Directory.Build.props`・CI の SDK・基底を一緒に上げる。
- **`dependency-name: "*"`（MSP の /deploy/cloud と同じ）**: 採らない。決定 2 のとおり。

## 結果

- 試験: `node scripts/scripts.test.js`（固有テストの言明。陰性対照: group から `react-dom` を抜く→赤、`dotnet/aspnet` の ignore を抜く→赤）。
- 残余:
  - `group` と `ignore` の効き方は、次の週次の Dependabot の実行（PR の題・分け方）で確かめる。
  - .NET の基底の大版は機械では提案されなくなる。移行の時期は人が決める（.NET 10 の LTS の期限を見て判断する）。
  - group は既定で version-updates だけに効く。react 系のセキュリティ更新が来たときは単独 PR になりうる（その場合は手で対を揃える）。
