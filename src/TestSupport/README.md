# TestSupport — テスト実行・単体実行のための足場（本番非使用）

このディレクトリには、**本番実装ではない**「足場（scaffold / shim）」を置く。本番の取引ドメイン実装
（`src/Services/**`・`src/Shared/AiStockTrading.Shared.{Contracts,Infrastructure}`）とは物理的に分離し、
「実際に本番で使っている」と誤解されないようにするためのものである（[IADR-0013](../../docs/adr/IADR-0013_platform-foundation-testsupport-shim.md)）。

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
