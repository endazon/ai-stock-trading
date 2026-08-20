---
title: IADR-0011 基盤ランタイム Foundation は最小移植しコピー＋AiStockTrading 命名で持つ
type: impl-adr
status: Accepted
related_ids: [ADR-0001, NFR]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# IADR-0011: 基盤ランタイム Foundation は最小移植しコピー＋AiStockTrading 命名で持つ

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: ADR-0001（platform 再利用・可変部分への組み込み拡張・基盤無改修）、NFR（可観測性・回復性・セキュリティ）
- 対象 Issue: [#22](https://github.com/endazon/ai-stock-trading/issues/22)、#12 Slice B（利用者）
- 関連する実装仕様書: [20260710_foundation-min-port](../specs/20260710_foundation-min-port.md)
- 関連 IADR: [IADR-0001](IADR-0001_repo-structure-and-stack.md)（リポ構成を基盤に揃える）、[IADR-0010](IADR-0010_risk-service-layering-and-slicing.md)（Slice B が本 Foundation を使う）

## コンテキストと課題

`AiStockTrading.Shared.Infrastructure` には共通ランタイム Foundation（バス再試行・OTel 計装・Serilog・ヘルスチェック・
JWT 認証・相関ID）がなく、Worker ホスト（#12 Slice B）を platform 慣習で組めない。基盤リポ
`../microservices-platform` の `KnowledgePlatform.Shared.Infrastructure/Foundation` に該当実装があるが、これをどう
取り込むか（依存参照 / コピー移植 / 自前再実装）と、どこまで取り込むか（範囲）を決める必要がある。

## 検討した選択肢

1. **基盤リポの `KnowledgePlatform.Shared.Infrastructure` を直接 ProjectReference** — 早いが、別ソリューション・
   `KnowledgePlatform` 名前空間への恒常依存を生む。IADR-0001 の「名前空間プレフィックスは AiStockTrading」に反し、
   基盤リポのビルド構成・パッケージ集合に密結合する。
2. **必要分だけコピー移植し `AiStockTrading` 命名へ** — ADR-0001 の「可変部分への組み込み拡張・基盤無改修」に沿う。
   基盤リポは変更せず、取り込んだコードは本リポで保守する。バージョンは基盤リポと揃える（IADR-0001）。
3. **全面自前再実装** — 車輪の再発明。基盤との規約差異・保守コストが大きい。

## 決定

選択肢 2 を採用する。加えて**範囲を最小限**にする。

- **移植する（ランタイム Foundation・本 PR）**: MassTransit 共通再試行（`UseAiStockTradingRetry`）、可観測性
  （`AddAiStockTradingObservability` / `ConfigureAiStockTradingSerilog`）、ヘルスチェック
  （`Add/MapAiStockTradingHealthChecks`）、Keycloak 認証（`AddAiStockTradingAuth` ＋ `AiStockTradingAuthPolicies` ＋
  `KeycloakRolesClaimsTransformation`）、相関ID（`CorrelationIdMiddleware` ＋ `UseAiStockTradingMiddleware`）。
- **移植しない（後続・#22 本体）**: イベント共通エンベロープ・宣言的バインディング（pipeline）・構成情報 API 自己申告
  （introspection/drift）。これらは platform 側スキーマ確定に依存し規模も大きいため、Foundation の上に載る後続 Slice。
- **移植しない（不要）**: オブジェクトストレージ（S3）— リスク管理では使わない。
- **命名**: 名前空間 `AiStockTrading.Shared.Infrastructure.Foundation.*`、公開 API は `AiStockTrading` プレフィックス。
  （**注: 配置・命名・位置づけは [IADR-0013](IADR-0013_platform-foundation-testsupport-shim.md) で更新。**移植 Foundation は
  本番非使用の最小 shim として `src/TestSupport/AiStockTrading.TestSupport.PlatformShim/` へ移動し、名前空間は
  `AiStockTrading.TestSupport.PlatformShim.Foundation.*` に変更した。本 IADR の「何を・どう移植するか」は維持。）
- **認可ポリシー**: 単独利用者運用のため platform の Admin/Operator 二層ではなく `OwnerOnly` 単層（レルムロール
  `trading-owner`）とする。ロール・レルム名は構成で差し替え可能。
- **バージョン**: 追加パッケージは基盤リポ `Directory.Packages.props` と同一バージョンで CPM 管理する（IADR-0001）。

## 理由

- ADR-0001 が「基盤リポ無改修・可変部分への組み込み」を定めるため、依存参照（選択肢1）より本リポで保守できるコピー移植が
  方針に合致する。名前空間衝突も避けられる。
- 範囲を最小化することで PR をレビュー可能に保ち、#22 本体（エンベロープ・バインディング・自己申告）の未確定スキーマに
  引きずられずに Worker ホストを前進させられる。

## 結果

- 良い影響: #12 Slice B が platform 慣習どおり（Serilog+OTel+MassTransit 再試行+Keycloak）に組める土台ができる。
- 悪い影響・トレードオフ: 基盤リポとコードが二重化し、基盤側の Foundation 改良は手動追随が必要になる。追随漏れ防止のため
  由来（platform の対応ファイル）をコメントで残す。
- フォローアップ: #22 本体（エンベロープ・バインディング・自己申告）を後続 Slice で実装。#12 Slice B で本 Foundation を配線。

## 関連

- Supersedes: なし
- Superseded by: [IADR-0013](IADR-0013_platform-foundation-testsupport-shim.md)（**配置・命名・位置づけのみ**。移植の範囲・方法は本 IADR を維持）
- 関連: [IADR-0001](IADR-0001_repo-structure-and-stack.md)、[IADR-0010](IADR-0010_risk-service-layering-and-slicing.md)
