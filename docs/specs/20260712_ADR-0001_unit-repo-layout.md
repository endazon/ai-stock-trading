---
title: ユニットリポジトリレイアウトへの再編（src/ → backend/、platform ADR-0019 準拠）
type: spec
status: review
related_ids:
  - ADR-0001
  - IADR-0046
author: claude
created: 2026-07-12
updated: 2026-07-12
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md（2026-07-12 更新）"
  - "../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md（§フォルダ構成）"
  - "../../planning/projects/microservices-platform/07_adr/ADR-0019_unit-first-repo-structure.md"
---

# 仕様書: ユニットリポジトリレイアウトへの再編（src/ → backend/）

> issue #102 の作業仕様。platform 側のユニット第一構成（platform ADR-0019 / 実装 IADR-0056〜0063）を受け、
> 本リポジトリを**可変機能ユニットのユニットリポジトリ**のレイアウトへ再編する。

## 起点となる計画書（トレーサビリティ）

- 関連 ADR: ADR-0001（platform-reuse、2026-07-12 更新）・platform ADR-0019・IADR-0046（本作業で新規）
- 計画書リンク: `projects/ai-stock-trading/06_technical/01_architecture-overview.md` §フォルダ構成（ユニットリポジトリ）
- 関連 issue: #102

## 目的・背景

本リポジトリの成果物は platform 実装リポジトリの `src/<unit>/` へ git submodule でリンクされる
**可変機能ユニット**である（ADR-0001 2026-07-12 更新）。ユニットリポジトリの規約
（platform `templates/unit-template/`・`src/README.md`）ではリポジトリ直下に `backend/`
（`backend.slnx`）を置くため、現行の `src/AiStockTrading.slnx` + `src/Services/` から再編する。

## 対象範囲

- 対象:
  - `git mv src backend`（履歴保全）と `AiStockTrading.slnx` → `backend.slnx` の改名
  - 共通 MSBuild 設定のリポジトリルートへの移設（`Directory.Build.props` は unit-template の
    import-chain フォールバック形式へ。submodule 配置時に platform の単一情報源を上書きしない）
  - CI（ci.yml / security.yml / pr-title.yml / openapi.yml）・scripts（setup.sh）・CLAUDE.md 等のパス更新
  - docs のリンク切れ修正（check-doc-links ゼロ）
- 対象外（後続。issue #102 の「対象外」参照）:
  - platform 実装リポジトリへの実際の submodule リンク・合成点登録・PlatformShim の実参照置換
  - 名前空間改名（`AiStockTrading.*` は維持）・`frontend/` の新設

## 設計

```text
ai-stock-trading/                 ← リポジトリルート（= submodule 配置時の src/<unit>/）
  backend/
    backend.slnx                  ← 旧 src/AiStockTrading.slnx（slnx 内の相対パスは不変）
    Services/<Name>/              ← 旧 src/Services/<Name>（10 サービス。内部相対参照は不変）
    Shared/                       ← 旧 src/Shared
    TestSupport/                  ← 旧 src/TestSupport
  Directory.Build.props           ← import-chain フォールバック（親があれば継承・単独時のみ既定適用）
  Directory.Packages.props        ← CPM（単独ビルド用。submodule リンク時の platform 側との統合は後続整理）
  global.json / docs/ scripts/    ← 変更なし（ルートのまま）
```

- ディレクトリ名変更のみのため、`backend/` 内の csproj 相対参照・slnx 内パスは変更不要。
- `Directory.Build.props` の import-chain は platform `templates/unit-template/README.md` の規約に従う。
  `Directory.Packages.props` は単独ビルドに必須のため全定義をルートで維持し、リンク時の重複解消は
  platform 側組み込み PR で扱う（IADR-0046 に記録）。

## 受け入れ基準

- [x] レイアウトが上記の形（ルート直下 `backend/`、`backend/backend.slnx`）になっている
- [x] `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` / `dotnet format backend/backend.slnx --verify-no-changes` が通る
- [x] CI・スクリプトが新パスで動作する定義になっている
- [x] `node scripts/check-doc-links.js` がリンク切れゼロで通る
- [x] 移行判断が IADR-0046 に記録されている

## テスト方針

- 既存テストを新構成で無改変・全通過させる（挙動変更なしの機械的再編であることの確認）。

## 計画書との差異

- 差異: なし（ADR-0001 2026-07-12 更新・platform ADR-0019 に忠実）。

## 未決事項

- submodule リンク時の `Directory.Packages.props` 統合方式（platform 側組み込み PR で確定）。
