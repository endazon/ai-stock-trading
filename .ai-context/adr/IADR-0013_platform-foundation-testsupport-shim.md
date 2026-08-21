---
title: IADR-0013 platform 由来 Foundation は本番非使用の最小 shim として TestSupport に物理分離する
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

# IADR-0013: platform 由来 Foundation は本番非使用の最小 shim として TestSupport に物理分離する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・方針指示）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: ADR-0001（platform 再利用・可変部分への組み込み拡張・基盤無改修）、NFR
- 対象 Issue: [#22](https://github.com/endazon/ai-stock-trading/issues/22)、[#12](https://github.com/endazon/ai-stock-trading/issues/12)
- 関連する実装仕様書: [20260710_foundation-min-port](../specs/20260710_foundation-min-port.md)、[20260710_risk-management-worker](../specs/20260710_risk-management-worker.md)
- 関連 IADR: [IADR-0011](IADR-0011_foundation-min-port.md)（移植の範囲・方法。本 IADR が**配置・命名・位置づけを一部 supersede**）、[IADR-0010](IADR-0010_risk-service-layering-and-slicing.md)

## コンテキストと課題

IADR-0011 で platform（`../microservices-platform`）の Foundation を最小移植し、`AiStockTrading.Shared.Infrastructure/Foundation`
に配置した。しかし本番（実運用）では ai-stock-trading の各サービスは platform の可変部分へ組み込まれ、バス設定・
可観測性・認証などの共通基盤は **platform 本体の Foundation** が提供する。移植した Foundation は本リポ単体での
ビルド・テスト・ローカル単体実行を成立させるための **最小の足場（shim）** であり、本番で使うものではない。

ところが現状の配置は、本番実装（`PaperBrokerAdapter` 等）と同じ `Shared.Infrastructure`（本番共有基盤に見える名前）に
同居しており、「移植 Foundation も本番で使っている」と誤解される。本番実装と、テスト実行のための足場が混在しない
よう、物理配置・命名で「本番非使用の足場」であることを一目で分かるようにする必要がある。

## 検討した選択肢

1. **現状維持（`Shared.Infrastructure/Foundation`）** — 本番実装と足場が混在し、誤読を招く。
2. **`TestSupport/` へ物理分離し、専用プロジェクト `AiStockTrading.TestSupport.PlatformShim`＋命名で本番非使用を明示** —
   フォルダ・プロジェクト名・名前空間・README の多重で「テスト実行・単体実行専用の shim・本番非使用」を示す。
3. **移植 Foundation を丸ごと削除し、常に platform 本体を ProjectReference** — 本リポ単体でビルド・テストできなくなり、
   別ソリューション・`KnowledgePlatform` 名前空間への恒常依存を生む（IADR-0011 の選択肢1と同じ難点）。

## 決定

選択肢 2 を採用する。IADR-0011 の「何を・どう移植するか」（範囲・コピー移植・基盤無改修・バージョン整合）は維持し、
**配置・命名・位置づけを本 IADR で更新**する。

- 移植 Foundation を `src/Shared/AiStockTrading.Shared.Infrastructure/Foundation` から
  `src/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation` へ**物理移動**する。
- 名前空間を `AiStockTrading.Shared.Infrastructure.Foundation.*` → `AiStockTrading.TestSupport.PlatformShim.Foundation.*`
  へ変更する。利用側の `using` から「本番非使用の足場」であることが一目で分かる。
- `src/TestSupport/README.md` に位置づけ（テスト実行・単体実行専用の最小 shim・本番非使用・基盤リポ無改修）を明記する。
- `Shared.Infrastructure` は Foundation 撤去により**本番純ライブラリに復帰**する（不要になった ASP.NET 共有フレームワーク・
  OTel・MassTransit・JwtBearer 参照を撤去。`PaperBrokerAdapter` 等の本番実装のみを残す）。
- Foundation のテストは `AiStockTrading.TestSupport.PlatformShim.Tests` へ移設する。`Shared.Infrastructure.Tests` は
  本番実装（PaperBroker）のテストのみを残す。
- リスク管理 Worker は Foundation 参照先を shim に付け替える。Worker の standalone 起動配線（MassTransit/RabbitMQ・
  PostgreSQL・Keycloak を shim 経由で組む部分）は **dev/test/CI のローカル単体実行用**であり、本番は platform 統合（#22）で
  共通基盤に置き換わる旨を `Program.cs` に注記する。

## 理由

- 本番実装と「テスト実行のための足場」を物理的に分離し、フォルダ・プロジェクト名・名前空間・README の多重で示すことで、
  「実際に本番で使っている」という誤読を構造的に防げる（利用者の方針指示）。
- ADR-0001 の「platform の可変部分への組み込み・基盤無改修」に沿う。本番は platform 本体の Foundation を用いるという
  位置づけが構成から明確になる。
- 本リポ単体でのビルド・テスト・単体実行は維持できる（選択肢3の難点を避ける）。

## 結果

- 良い影響: 本番非使用の足場が一目で分かる。`Shared.Infrastructure` が本番純ライブラリに戻り、依存も減る。
- 悪い影響・トレードオフ: 移植コードが `TestSupport` 配下に移り、本番ホストが `TestSupport.*` を参照するという一見不自然な
  依存が残る（＝standalone 実行が足場依存であることの反映）。本番統合（#22）で platform 本体へ差し替える際に解消する。
- **重要な留意（#22 完了まで）**: 「TestSupport」「本番非使用」という命名は **#22 完了後の姿** を指す。#22 未完了の現時点では、
  この shim が Worker の実行時動作を規定する **de facto な配線**であり、いまデプロイすれば実際に動くのは shim のコードである。
  命名・文言だけを見て「このフォルダは削除・変更してもデプロイ後の挙動に影響しない」と誤解しないよう、README にも同旨を明記した
  （今回の「誤読防止」目的が逆方向に取り違えられるのを防ぐため）。
- フォローアップ: #22 で platform 本体との本番統合（shim の置き換え）を扱う。#12/#22 に本位置づけを注記する。

## 関連

- Supersedes: [IADR-0011](IADR-0011_foundation-min-port.md) の**配置・命名・位置づけ**部分（移植の範囲・方法は 0011 を維持）
- Superseded by: なし
- 関連: [IADR-0001](IADR-0001_repo-structure-and-stack.md)、[IADR-0010](IADR-0010_risk-service-layering-and-slicing.md)、[IADR-0012](IADR-0012_risk-settings-persistence.md)
