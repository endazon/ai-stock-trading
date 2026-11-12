---
title: Dependabot の react 系を 1 group にして React 19.3 へ揃え、/backend の .NET 基底の大版を出させない（#1630 / #1624 / #1625 の置き換え）
type: spec
status: accepted
related_ids: [NFR, IADR-0649, IADR-0046, IADR-0609, IADR-0625]
author: claude (Claude Code)
created: 2026-11-12
updated: 2026-11-12
---

# 作業仕様書: Dependabot の react 対と .NET 基底の大版（NFR）

> 着手 2026-11-12（develop `d4ebaeb38`）。ブランチ `chore/NFR-dependabot-react-pair-dotnet-major`。
> 決定は [IADR-0649](../adr/IADR-0649_dependabot-react-group-and-dotnet-base-no-major.md)。

## 起点

- #1630: `react` ^19.3.1・`@types/react` ^19.3.0 だけを上げ、`react-dom` ^19.2.0 が残った。CI は
  「Incompatible React versions: react and react-dom must have the exact same version」で赤。
- #1624 / #1625: `backend/Dockerfile` の `dotnet/aspnet` / `dotnet/sdk` を 10.0 → 11.0。対象の枠 net10.0（IADR-0046）を変える移行。
- 起点 ID: 工程の統制（無採番 NFR）。

## 着手前の実測

```
$ npm view react-dom@19.3 version     → 19.3.0, 19.3.1
$ npm view react@19.3 version         → 19.3.0, 19.3.1
$ npm view @types/react@19 version    → … 19.3.0（最新）
$ npm view @types/react-dom@19 version→ … 19.3.0（最新）
$ npm view react-dom dist-tags        → latest 19.3.1
$ gh api repos/endazon/ai-stock-trading/pulls/1624 --jq .title → chore(deps): bump dotnet/aspnet from 10.0 to 11.0 in /backend
$ gh api repos/endazon/ai-stock-trading/pulls/1625 --jq .title → chore(deps): bump dotnet/sdk from 10.0 to 11.0 in /backend
```

react-dom 19.3.1 が実在するので React を上げる。Dependabot の docker の `dependency-name` は host を除いた `dotnet/sdk`・`dotnet/aspnet`。

## 母集合（規則 9: 走査で引く）

- `.github/dependabot.yml` の項（5 件）: github-actions `/`・docker `/backend`・docker `/deploy/opend`・nuget `/`・npm `/frontend`。
  変更は npm `/frontend`（group）と docker `/backend`（ignore）だけ。
- `backend/Dockerfile` の `FROM`: `mcr.microsoft.com/dotnet/sdk:10.0@sha256:…`・`mcr.microsoft.com/dotnet/aspnet:10.0@sha256:…`（2 件。両方を ignore に入れる）。
  `deploy/opend/Dockerfile` の `dotnet/runtime-deps:8.0-jammy` は依頼の範囲外（広げない）。
- `frontend/package.json` の react 系: `react`・`react-dom`（dependencies）、`@types/react`・`@types/react-dom`（devDependencies）。
  `@tanstack/react-*`・`@lingui/react`・`@testing-library/react`・`lucide-react`・`@vitejs/plugin-react`・`eslint-plugin-react-*` は
  react と版を揃える関係に無いので group に入れない。
- 先例: MSP の `.github/dependabot.yml` と IADR-0711（docker の `ignore` に `dependency-name` ＋ `version-update:semver-major`）。
- dependabot.yml を見る既存の試験: `scripts/scripts.repo.test.js` の「dependabot.yml は github-actions・nuget（/）・npm（/frontend）を対象にする」1 件。

## 受け入れ基準

- [x] react・react-dom 19.3.1、@types/react・@types/react-dom 19.3.0（package.json・package-lock）。`npm ls` で重複なし
- [x] dependabot.yml の npm /frontend に 4 つちょうどの group、docker /backend に 2 つの semver-major の ignore
- [x] `scripts.repo.test.js` に言明。陰性対照（group から react-dom を抜く／aspnet の ignore を抜く）で赤
- [x] `npx vitest run`（全件）・`npm run lint`・`npm run typecheck`・`npm run e2e:typecheck`・`node scripts/scripts.test.js` が緑
- [x] 文書系検査（check-trace-blocks / gen-knowledge-graph --check / check-doc-links / check-commit-messages / check-cross-repo-refs /
      check-plan-id-qualification / check-adr-index-sync）が緑
