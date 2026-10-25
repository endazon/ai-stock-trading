---
title: IADR-0590 CI の setup-dotnet は SDK を厳密な版（10.0.403）で指定し、全ワークフローで同じ値にする。x 指定は配布元の障害で CI を止めるため使わず、aka.ms の復旧を確かめたら戻すかを判断する
type: impl-adr
status: Accepted
related_ids: [NFR]
author: claude (Claude Code)
created: 2026-10-26
updated: 2026-10-26
related_specs:
  - ../specs/20261026_1478_setup-dotnet-exact-sdk-version.md
---

# IADR-0590: setup-dotnet の SDK を厳密な版で指定する（#1478）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-10-26
- 決定者: claude（#1478。版と方針はコーディネーターの依頼 2026-10-26。MSP#2267 と同じ）

## 起点・関連

- 起点 issue: #1478。同じ原因の MSP 側は MSP#2266 / MSP#2267 である。
- 作業仕様書: [20261026_1478_setup-dotnet-exact-sdk-version](../specs/20261026_1478_setup-dotnet-exact-sdk-version.md)。

## コンテキストと課題

- setup-dotnet の 7 か所は、すべて `dotnet-version: "10.0.x"` を指定していた。
- x 指定では、install script が `--channel 10.0` で呼ばれる。版は aka.ms 経由で `dotnetorigin.blob.core.windows.net` から解決される。
- 2026-10-26 19:09Z ごろから、そこが HTTP 409 を返している。
- 厳密な版では、install script は別の配布元（`builds.dotnet.microsoft.com`・`ci.dot.net`）を引く。MSP#2267 の CI で導入が通った。

## 決定

### 決定 1: setup-dotnet の SDK は厳密な版 `10.0.403` で指定する

- MSP と同じ版にする。MSP の CI が障害の前に `10.0.x` から解決していた SDK である。

### 決定 2: 値は各所に同じものを書き、揃っていることを試験で見る

- workflow をまたいで値を 1 か所に置く手段が無い。
  - `env` は workflow ごとに閉じる。
  - `global-json-file` は、global.json（`10.0.100` + `rollForward: latestMajor`）が版を固定しないので使えない。
- そこで、`scripts/scripts.repo.test.js` が全 workflow の setup-dotnet の step を読み、次を確かめる。
  - すべてに `dotnet-version` がある。
  - その値が厳密な版である。
  - 全箇所で値が同じである。
  - `global-json-file` を使っていない。
- 変異試験で、どの違反も検出することを確かめた。

### 決定 3: aka.ms の復旧を確かめたら、x 指定へ戻すかを判断する

- 戻す条件: aka.ms の `--channel 10.0` が 409 を返さなくなったことを確認する。
- 判断の比較:
  - 厳密な版のままにすると、SDK の更新を手で追う費用がかかる。
  - x 指定へ戻すと、配布元の障害で再び CI が止まる危険がある。
- 判断の結果は、本 IADR への日付つき追記か、改訂する IADR で残す。MSP 側（MSP#2266）と揃えて判断する。

## 結果

- SDK の導入が配布元の 409 に依らなくなる。
- SDK の更新は手作業になる。上げるときは 7 か所を同時に替える（片側だけの更新は試験が止める）。
- 残余: `scripts/setup.sh`（SessionStart hook）も `--channel` を使う。fail-open なので範囲外とした。
