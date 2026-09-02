---
title: コンテナイメージの publish が NETSDK1152 で失敗する退行の是正
issue: "#616"
plan_refs:
  - NFR
adr_refs:
  - IADR-0259
  - IADR-0048
status: done
created: 2026-09-02
---

# 作業仕様書: コンテナイメージの publish が NETSDK1152 で失敗する退行の是正（#616）

## 背景

2026-09-02、ローカル k3s へ最新 develop（51514383）を配備するため `scripts/k8s-local-images.sh` を実行したところ、
`backend/Dockerfile` の `dotnet publish` が **TradeDecisionService** で `NETSDK1152`（同じ相対パスの publish 出力が複数）
により失敗し、イメージが 1 つも作れなかった。

真因は VSA 移送（PR #594 / #600・IADR-0259）で `TradeDecisionService.csproj` の参照先が旧 `RiskManagementService.Domain.csproj`
（クラスライブラリ）から **Web SDK のサービス本体** `RiskManagementService.csproj` へ張り替わったこと。Web SDK は
`appsettings*.json` を `CopyToPublishDirectory` 付き Content として持つため、参照元の publish 出力へ推移的に流れ込み、
参照元自身の同名ファイルと衝突する。CI は build/test のみで publish を実行しないため検知されなかった。

## 受け入れ基準

- [x] `dotnet publish backend/Services/TradeDecisionService/TradeDecisionService.csproj -c Release` が成功し、出力の
      `appsettings.json` / `appsettings.Development.json` が **TradeDecisionService 自身のもの**である（`diff -q` で一致）。
- [x] `scripts/k8s-local-images.sh` が 11 サービスすべてのイメージを作れる。
- [x] `ErrorOnDuplicatePublishOutputFiles=false` は採らない（どちらの設定が出荷されるか不定になる）。
- [x] 再発防止: CI が全サービスの publish（`--no-build`）を乾式実行し、同型の衝突をマージ前に検知する。

## 実装

- `backend/Services/TradeDecisionService/TradeDecisionService.csproj`: `ComputeFilesToPublish` の後・
  `_HandleFileConflictsForPublish` の前に走るターゲット `AstRemoveForeignAppSettingsFromPublish` を追加。
  `ResolvedFileToPublish` のうち `RelativePath` が `appsettings*.json` で **`FullPath` が自プロジェクトのディレクトリ外**の
  項目だけを除く。除いた項目は `Message`（Importance=high）で publish ログへ出す。
- `.github/workflows/ci.yml`: `backend-test` の shard 1 だけで、Build の後に 11 サービスを `dotnet publish --no-build` で
  乾式 publish する（ファイルコピーのみ・数十秒）。衝突があれば NETSDK1152 で赤くなる。

## 検証

- ローカル: publish 出力の `appsettings.json` が `backend/Services/TradeDecisionService/appsettings.json` と一致（実測）。
  publish ログに `AST: publish から除いた参照先の設定ファイル: …RiskManagementService\appsettings.Development.json, …appsettings.json`。
- ローカル: `scripts/k8s-local-images.sh` で 11 イメージがビルドできること（本 PR のブランチから実施）。

## 選ばなかった案

- `backend/Directory.Build.targets` に汎用ターゲットを置く: MSP へ submodule 配置されたときに上位の
  `Directory.Build.targets` を遮る恐れがあるため採らない（import-chain は IADR-0046 の射程）。
- クロスサービス参照そのものの解消（サイジング型を Shared へ移す）: #613 / #601 の構成整理の射程であり、
  本 PR は配備不能の解消に限る。
