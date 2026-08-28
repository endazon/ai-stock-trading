# TestSupport — テスト実行・単体実行のための足場（本番非使用）

このディレクトリには、**本番実装ではない**「足場（scaffold / shim）」を置く。本番の取引ドメイン実装
（`backend/Services/**`・`backend/Shared/AiStockTrading.Shared.*`）とは物理的に分離し、
「実際に本番で使っている」と誤解されないようにするためのものである（[IADR-0013](../../.ai-context/adr/IADR-0013_platform-foundation-testsupport-shim.md)）。

## AiStockTrading.TestSupport.PlatformShim

`microservices-platform`（基盤リポ `../microservices-platform`）の `KnowledgePlatform.Shared.Infrastructure/Foundation`
から**最小移植**したランタイム Foundation（MassTransit 共通再試行・可観測性 OTel/Serilog・ヘルスチェック・
Keycloak 認証・相関ID）。

- **位置づけ**: 本リポ単体でのビルド・テスト・ローカル単体実行を成立させるための **shim（最小構成）**。
- **本番非使用**: 本番（実運用）では ai-stock-trading の各サービスは platform の可変部分へ組み込まれ、**platform 本体の
  Foundation** が提供する共通基盤（バス設定・可観測性・認証など）を用いる。本プロジェクトはそれを本番で置き換えるもの
  **ではない**。
- **基盤リポは無改修**（ADR-0001）。ここは基盤コードのコピーであり、由来は各ファイル冒頭コメントに明記する。
- 名前空間 `AiStockTrading.TestSupport.PlatformShim.*` とすることで、利用側の `using` から「本番非使用の足場」で
  あることが一目で分かる。

> 本番統合（platform 側のホスト・基盤との結線）は #22（platform 拡張規約への準拠）で扱う。本 shim はそれまでの間、
> および CI・ローカルでの単体実行のための最小の代替である。

## ⚠️ 現時点の注意（#22 完了まで）

**#22（本番統合）が未完了の現時点では、この shim が Worker の実行時動作を規定する de facto な配線である。**
`RiskManagementService.Worker` は本 shim を `ProjectReference` しており、`Program.cs` の起動配線（MassTransit/RabbitMQ・
OTel・Keycloak 認証）は現状この shim の実装だけで動く。したがって **いまこのサービスをデプロイすれば、実際に動くのは
この shim のコードそのもの** である。「TestSupport」「本番非使用」という名前だけを見て「このフォルダは削除・変更しても
デプロイ後の挙動に影響しない」と誤解しないこと。本 shim が「本番非使用」になるのは **#22 で platform 本体の Foundation へ
差し替えた後** である。それまでは実行時の振る舞いを担う本番相当の配線として扱う。
