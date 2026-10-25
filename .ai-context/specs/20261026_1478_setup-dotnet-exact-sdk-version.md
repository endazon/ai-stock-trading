---
title: 作業仕様書 — setup-dotnet の dotnet-version を厳密な版 10.0.403 へ替え、全ワークフローで揃っていることを試験で見る（#1478）
type: spec
status: done
related_ids: [NFR, IADR-0590]
author: claude (Claude Code)
created: 2026-10-26
updated: 2026-10-26
---

# 仕様書: setup-dotnet の厳密な版（#1478）

> 本仕様書は AST `origin/develop` `e294bb4c` を起点に、実装の前に作成した。判断は [IADR-0590](../adr/IADR-0590_setup-dotnet-exact-sdk-version.md) に残す。
> MSP#2266 / MSP#2267 と同じ原因・同じ方針の hotfix である（コーディネーターの依頼 2026-10-26）。

## 事象と原因

- `dotnet-version: "10.0.x"` を指定すると、setup-dotnet は install script を `--channel 10.0` で呼ぶ。版は aka.ms 経由で `dotnetorigin.blob.core.windows.net` から解決される。
- そこが 2026-10-26 19:09Z ごろから HTTP 409「Public access is not permitted on this storage account」を返している。
- 厳密な版（`--version 10.0.403`）では、`builds.dotnet.microsoft.com` と `ci.dot.net` を引くので通る。MSP#2267 の CI で、導入の成功を確かめた（template-backend-build が success）。

## 受け入れ基準

- [x] `.github/workflows/` の setup-dotnet の全 7 か所が `dotnet-version: "10.0.403"` である
- [x] 全箇所が同じ厳密な版であることを `scripts/scripts.repo.test.js` が見る。x 指定・値の不一致・版の欠落・`global-json-file` を変異試験で検出する
- [x] 「aka.ms の復旧を確認したら x 指定へ戻すかを判断する」を issue と IADR-0590 に残す

## 母集合（規則 9: 誤りの側の文字列で全文を走査した）

`git grep -n -E "10\.0\.x|dotnet-install|--channel|aka\.ms/dotnet|dotnetorigin|dotnet-version|global-json-file"` で引いた（Markdown を除く）。

| 当たり | 扱い | 理由 |
| --- | --- | --- |
| `.github/workflows/` の `dotnet-version: "10.0.x"` 7 か所（ci.yml 2・claude-code-review・claude-coding・copilot-setup-steps・integration・security） | **直す** | 本件の原因 |
| `scripts/setup.sh` の `dotnet-install.sh --channel` | 外す | SessionStart hook で fail-open（失敗しても継続する）。CI を止めない |

## 値の単一情報源（検討）

- `global-json-file` は使えない。global.json は `10.0.100` + `rollForward: latestMajor` で、SDK の版を固定しない。
- workflow の `env` は workflow ごとに閉じる。そのため各所に同じ値を書き、揃っていることを試験で見る（MSP#2267 と同じ形）。
- 既存の検査器で `dotnet-version` を見るものは無い（`check-ai-workflow-config.js` は setup-dotnet と dotnet の許可の対だけを見る）。

## 戻す条件

- aka.ms の `--channel 10.0` が 409 を返さなくなったことを確認したら、x 指定へ戻すかを判断する（IADR-0590 決定 3）。
