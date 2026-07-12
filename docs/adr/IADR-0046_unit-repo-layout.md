---
title: IADR-0046 ユニットリポジトリレイアウト（ルート直下 backend/・import-chain フォールバック props）を採る
type: impl-adr
status: Accepted
related_ids:
  - ADR-0001
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md（2026-07-12 更新。submodule pin 未更新のため更新内容は planning リポ main を参照）"
  - "https://github.com/endazon/project-planning/blob/main/projects/microservices-platform/07_adr/ADR-0019_unit-first-repo-structure.md"
---

# IADR-0046: ユニットリポジトリレイアウト（ルート直下 backend/・import-chain フォールバック props）を採る

- 状態: Accepted
- 日付: 2026-07-12
- 決定者: endazon（計画確定: ADR-0001 2026-07-12 更新）・claude（実装詳細）

## 起点・関連

- 関連する計画書 ID: ADR-0001（platform-reuse）・platform ADR-0019（ユニット第一構成）
- 関連する実装仕様書: [作業仕様書](../specs/20260712_ADR-0001_unit-repo-layout.md)・issue #102
- platform 側の規約: microservices-platform `src/README.md`・`templates/unit-template/`・
  `docs/how-to/adding-a-unit-submodule.md`（実装 IADR-0056 / IADR-0060）

## コンテキストと課題

本リポジトリは platform 実装リポジトリの `src/<unit>/` へ submodule リンクされる可変機能ユニットとなる
ことが計画で確定した（ADR-0001 2026-07-12 更新）。ユニットリポジトリはルート直下に `backend/`
（`backend.slnx`）を置く規約であり、現行レイアウト（`src/AiStockTrading.slnx`＋`src/Services/`）からの
移行方法と、共通 MSBuild 設定の扱い（submodule 配置時に platform の単一情報源を上書きしない）を決める。

## 検討した選択肢

1. **`git mv src backend` の一括改名＋props をルートへ移設（採用）** — 内部相対参照が全て不変で差分が最小。
   props は unit-template の import-chain フォールバック形式にする
2. `src/` を残し `backend/` を新設して段階移動 — 移行期間中に二重構造となり、CI・参照の整合が複雑化
3. platform リポへ直接マージ（submodule にしない） — ユニットの独立開発・独立リリースを失い、
   ADR-0001（拡張プロジェクト）の位置づけに反する

## 決定

1. **レイアウト**: `git mv src backend`（履歴保全）＋ `AiStockTrading.slnx` → `backend.slnx`。
   `backend/` 直下の構成（`Services/`・`Shared/`・`TestSupport/`）は不変。`frontend/` は画面 features を
   持つ段階で追加する（現時点では置かない）。
2. **共通 MSBuild 設定**: `Directory.Build.props` / `Directory.Packages.props` をリポジトリルートへ移設する。
   `Directory.Build.props` は unit-template の **import-chain フォールバック**形式
   （上位に platform の props があれば継承し、単独時のみ既定を適用）とする。
   `Directory.Packages.props` は単独ビルドに必須のため全 PackageVersion をルートで維持し、
   submodule リンク時の platform 側 CPM との統合（重複解消・バージョン整合）は組み込み PR で扱う。
3. **命名**: 名前空間・アセンブリ名（`AiStockTrading.*`）は本再編では変更しない
   （platform の段階改名方針 IADR-0062 と同型。ユニット名との整合改名は後続判断）。
4. **CI**: ci.yml / security.yml / pr-title.yml / openapi.yml / setup.sh のパスを `backend/backend.slnx` 系へ
   更新する。CI の構造（単一ユニット）は維持する（platform 側のような自動発見は不要）。

## 理由

- 一括改名（選択肢 1）は csproj の相対参照・slnx 内パスが全て不変で、機械的かつ検証可能。
  platform 側の再編（実装 PR #233）でも同方式で全テスト無改変・全通過を確認済み。
- import-chain フォールバックは platform `templates/unit-template/README.md` の確定規約であり、
  submodule 配置時に platform の単一情報源（`src/Directory.Build.props`）を上書きしない唯一の形。

## 結果

- 良い影響: platform への submodule 組み込み（後続）がレイアウト変更なしで可能になる。
  リポジトリ構造が計画（ADR-0001・platform ADR-0019）と一致する。
- 悪い影響 / トレードオフ: 大きな（ただし機械的な）リネーム差分が一度発生する。
  `Directory.Packages.props` の platform 側との統合は組み込み時まで残課題。
- フォローアップ:
  1. platform 実装リポジトリへの submodule リンク・合成点登録・PlatformShim の実参照置換（組み込み PR）
  2. `Directory.Packages.props` の統合方式（リンク時に platform 側へ集約 or 条件 import）
  3. 名前空間のユニット整合改名の要否判断

## 関連

- Supersedes: なし（[IADR-0001](IADR-0001_repo-structure-and-stack.md) のリポ構成節を更新する。
  スタック・規約の他の決定は存続）
- Superseded by: なし
