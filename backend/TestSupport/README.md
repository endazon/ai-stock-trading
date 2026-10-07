# TestSupport — 実行時の配線（PlatformShim）とテスト専用の部品

このディレクトリは、本番の取引ドメイン実装（`backend/Services/**`・`backend/Shared/AiStockTrading.Shared.*`）と物理的に分けた
「足場（scaffold / shim）」の置き場として作った（[IADR-0013](../../.ai-context/adr/IADR-0013_platform-foundation-testsupport-shim.md)）。
🔴 **ただし `AiStockTrading.TestSupport.PlatformShim` は、名に反して配備でも動く**（次節）。それ以外
（`Composition`・`ContractFixtures`・`Messaging`・`Metrics` と各 `*.Tests`）は本番プロジェクトから参照されず、テスト専用である。

## AiStockTrading.TestSupport.PlatformShim

`microservices-platform`（基盤リポ `../microservices-platform`）の `KnowledgePlatform.Shared.Infrastructure/Foundation`
から**最小移植**したランタイム Foundation（Wolverine の共通配線・可観測性 OTel/Serilog・ヘルスチェック・
Keycloak 認証・相関ID・例外応答・east-west gRPC の共通配線と所有者の門）。

- 🔴 **位置づけ（2026-10-07 に実物へ合わせた。#1204 / IADR-0013 の追記）**: 本リポから組む各サービスは本 shim を
  `ProjectReference` しており、`Program.cs` の起動配線は**配備でもこの shim の実装で動く**。変更・削除は配備の挙動を変える。
  - 当初は「本番では platform 本体の Foundation に差し替えるので本番非使用」としていたが、差し替えを扱うはずだった #22 は
    差し替えをせずにクローズし（拡張規約の 3 要求の充足でクローズ）、差し替えの予定は無い。「本番非使用」は成り立たない。
  - 認可の判定を含む: gRPC の所有者の門（`Foundation/Auth/GrpcOwnerClientGate.cs`。Discord ボットのトークンの `azp` を確かめる）、
    例外応答の終端（`UseAiStockTradingExceptionHandler`）。
- **基盤リポは無改修**（ADR-0001）。ここは基盤コードのコピーであり、由来は各ファイル冒頭コメントに明記する。
- 名前空間 `AiStockTrading.TestSupport.PlatformShim.*` は**改めない**（参照する本番プロジェクトと試験の全部へ波及し、得るのは名前の
  正しさだけ。IADR-0013 の 2026-10-07 追記）。名前ではなく本節で位置づけを読むこと。

## AiStockTrading.TestSupport.Composition

**本番の組み立て（各サービスの `Program.cs`）を組み、配線の抜けを機械的に検査する**テスト専用のエンジン
（[IADR-0397](../../.ai-context/adr/IADR-0397_composition-wiring-guard.md) / #947）。

- 各サービスのテストの `CompositionWiringGuardTests` が、既存のファクトリ（外界は伝送の境界だけ差し替え）から
  `InspectComposition(...)` で組み立てを組み、`AssertNoUnexpectedFindings(allowlist)` で判定する。
- 規則は W0 組めない／W1 省略可能依存の未解決／W2 渡し忘れ／W3 偽物の陰の本物。所見への対処は IADR-0397 決定6。
- **新しいサービスを足したらガードも足す**（`AiStockTrading.Architecture.Tests` の `CompositionWiringGuardPresenceTests` が止める）。
- 伝送の境界（外界へ出る最下層のクライアント）だけを差し替えるときは `TransportStub.Create<T>()` を使う。
