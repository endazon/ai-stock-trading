---
title: FoundationRegistrationTests が組み立てた TracerProvider/ServiceProvider を破棄し、漏れないことを試験で固定する（#766）
type: spec
status: done
related_ids: [NFR]
author: endazon (with Claude Code)
created: 2026-09-11
updated: 2026-09-11
plan_refs: []
---

# 仕様書: FoundationRegistrationTests の TracerProvider 破棄漏れの是正（#766）

## 起点

issue [#766](https://github.com/endazon/ai-stock-trading/issues/766)。
[#751](https://github.com/endazon/ai-stock-trading/issues/751)（PR [#760](https://github.com/endazon/ai-stock-trading/pull/760)・
[IADR-0333](../adr/IADR-0333_credential-bearing-uri-trace-redaction.md)）の陰性対照を実送信で書こうとした際に**実測**した副作用の是正。
`FoundationRegistrationTests`（同じアセンブリ `AiStockTrading.TestSupport.PlatformShim.Tests`）が
`可観測性の登録は例外なく解決できる()` で組み立てた `TracerProvider` を `using` で破棄しておらず、
グローバルな `ActivityListener`（`AddAspNetCoreInstrumentation()` / `AddHttpClientInstrumentation()` が
購読する `Microsoft.AspNetCore` / `System.Net.Http` の実 ActivitySource）がプロセス内に残り続ける。
PR #760 は「こちら側を外乱に強くする」（専用 `ActivitySource` の A/B へ置き換える）ことで**回避**したが、
根本原因の是正は本 issue（#766）へ**射程外として明記**して送っていた
（[20260911_751_trace-uri-redaction.md](20260911_751_trace-uri-redaction.md) 未決事項 3）。

- 計画 ID: メタ作業（規約整備ではなく既存試験のバグ修正だが、稼働する製品要求に対応する番号が無い。
  `.claude/rules/traceability.md`「無採番の NFR を許す場合 2」に該当——テスト基盤の是正であり
  計画側の非機能要件表に不足があるわけではないため、環流しない）。
- 関連 issue: [#751](https://github.com/endazon/ai-stock-trading/issues/751)、
  PR [#760](https://github.com/endazon/ai-stock-trading/pull/760)
- 関連記録: [IADR-0333](../adr/IADR-0333_credential-bearing-uri-trace-redaction.md)（「陰性対照は実送信では
  書けなかった」の根拠節）、[20260911_751_trace-uri-redaction.md](20260911_751_trace-uri-redaction.md)
  （未決事項 3。本作業の直接の起点）

## 対象範囲

- 対象: `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim.Tests/FoundationRegistrationTests.cs`
  （`ServiceProvider` を構築して破棄していない 4 メソッド）。破棄漏れの是正 ＋ 破棄が
  `ActivityListener` を実際に外すことを固定する新規試験の追加。
- 対象外: 本番コード（`AiStockTrading.TestSupport.PlatformShim` 側）、
  `CredentialBearingUriRedactionProcessor` とその秘匿ロジック（#766 の射程外。既に PR #760 で
  外乱に強い形へ回避済みであり、本作業はその回避を要らなくすることが目的ではなく
  **漏れそのものを塞ぐ**ことが目的）。

## 走査した母集合（規則 1・2・3・6。「破棄されていない ServiceProvider/TracerProvider/MeterProvider の構築」から引いた）

軸を 3 本引いた（規則 5）。パスの除外のみ・拡張子で絞らない（規則 3）。

| 軸 | 検索語 | ヒット | 判断 |
| --- | --- | --- | --- |
| 1 | `BuildServiceProvider` （`backend/**/*.cs`、テストプロジェクトへ絞らず全走査） | 40+ ファイル | 生産コード（`Program.cs` 等のホスト構築）は対象外（プロセス生存期間中は解放しない設計が正しい）。テストプロジェクトの呼び出し箇所を個別に確認（下表） |
| 2 | `Sdk.CreateTracerProviderBuilder\|Sdk.CreateMeterProviderBuilder` | 2 ファイル（`BusinessMetricsWiringTests.cs`・`CredentialBearingUriTraceRedactionTests.cs`、いずれも同アセンブリ） | 両方とも `using var provider = builder....Build();` で破棄済み（確認: `grep -n "using var provider" ...`）。是正不要 |
| 3 | `AddAiStockTradingObservability` （`backend/**/*.cs`） | 8 ファイル（本番 1・テスト 7） | 下表で個別に判定 |

軸 3 のテスト側 7 ファイルの内訳:

| ファイル | `BuildServiceProvider`/`Build()` の破棄 | 判断 |
| --- | --- | --- |
| `PlatformShim.Tests/FoundationRegistrationTests.cs` | **破棄していない**（4 箇所。`using` が無い） | **是正対象（本作業）** |
| `PlatformShim.Tests/BusinessMetricsWiringTests.cs` | `using var provider = services.BuildServiceProvider();`（4 箇所すべて） | 是正不要（既に破棄済み） |
| `PlatformShim.Tests/CredentialBearingUriTraceRedactionTests.cs`（PR #760 で新設） | `using var provider = services.BuildServiceProvider();`（2 箇所） | 是正不要 |
| `Services/RiskManagementService/Tests/Infrastructure/Steps/TradeDecisionMadeConsumerTests.cs` | `using var host = await BuildHostAsync(...)`（`Host` 経由。ホストの `StopAsync`/`Dispose` が内部の `ServiceProvider` を解放） | 是正不要 |
| `Services/TradeDecisionService/Tests/Infrastructure/Steps/InformationCollectedConsumerTests.cs` | 同上（`using var host = await BuildAsync(...)`） | 是正不要 |
| `Services/TradeDecisionService/Tests/Infrastructure/Steps/PriceMovementDetectedConsumerTests.cs` | 同上 | 是正不要 |
| `Services/BacktestService/Program.cs` | 本番コード（ホスト自身） | 対象外 |

除外したもの・見つけたが直さないもの（規則 6）:

| 対象 | 種別 | 対応 | 理由 |
| --- | --- | --- | --- |
| `PlatformShim.Tests/IntrospectionTests.cs`（`services.BuildServiceProvider().GetRequiredService<...>()` 2 箇所・`using` なし） | 同アセンブリのテスト | **不変（射程外）** | `AddAiStockTradingIntrospection` のみを呼び、`AddAiStockTradingObservability` を呼ばない——`TracerProvider`/`MeterProvider`/`ActivityListener` を一切構築しないため、#766 が指す「グローバルな ActivityListener の漏れ」は起きない。破棄しない `ServiceProvider` 自体は行儀が悪いが、別の性質の指摘であり本 issue の受け入れ基準（ActivityListener の分離）の外 |
| `PlatformShim.Tests/PlatformRealmTokenProviderTests.cs`（`new ServiceCollection().AddHttpClient().AddLogging().BuildServiceProvider()` 2 箇所・`using` なし） | 同アセンブリのテスト | **不変（射程外）** | 同上。`AddHttpClient()` のみで `AddAiStockTradingObservability`（＝ `AddOpenTelemetry()`）を呼ばないため `TracerProvider`/`ActivityListener` は生成されない |

🔴 上記 2 件は「`ServiceProvider` を破棄していない」という**形は同じ**だが、`ActivityListener` を
グローバルへ登録しない構成であるため、#766 の受け入れ基準（他クラスへの ActivityListener 漏れの解消）
には当たらない。**同型の事故が 2 回起きたら検査器を足す**という運用ガイドの方針に照らし、
「`BuildServiceProvider` は必ず `using` で受ける」という一般規約への昇格は本作業の射程外とし、
気付いた事実として本節に記録するに留める（是正するなら別 issue）。

## 原因

`FoundationRegistrationTests.可観測性の登録は例外なく解決できる()` が

```csharp
var provider = services.BuildServiceProvider();
provider.GetService<TracerProvider>().Should().NotBeNull();
```

の形で `ServiceProvider`（＝ `TracerProvider` を内包）を破棄せずにテストメソッドを抜けている。
`ServiceProvider` にファイナライザは無く、`Dispose()` を呼ばない限り内部の `TracerProviderSdk` も
解放されない。`TracerProviderSdk` の Dispose は `ActivitySource.RemoveActivityListener` 相当の
後始末を行うため、**破棄しなければ `ActivityListener` はプロセスが終わるまで生き続ける**。

同クラスの他 3 メソッド（`Keycloak認証の登録は例外なく解決できる()`・`OwnerOnly_認可ポリシーが登録される()`・
`OwnerOrService_認可ポリシーが登録される()`）も同じ形で `ServiceProvider` を破棄していない。これらは
`AddAiStockTradingAuth` のみを呼び `TracerProvider` を構築しないため #766 の直接原因ではないが、
**同じ関数内で同じ形の破棄漏れを放置すると次に踏む形になる**ため、本作業でまとめて是正する
（1 ファイル内の同型の書き方の統一。新たな検査器や規約を要らない範囲）。

## 設計

### 1. 破棄の追加

4 メソッドすべてで `var provider = services.BuildServiceProvider();` を
`using var provider = services.BuildServiceProvider();` へ変更する。
最後の `共通再試行を適用したメッセージ基盤は解決できる()` は既に `using var host = await Host.CreateDefaultBuilder()...` で
破棄済みであり変更しない。

### 2. 分離を証明する試験の設計（採否の検討）

「破棄すれば漏れない」ことを固定する試験の置き方を 3 案検討した。

| 案 | 内容 | 判断 |
| --- | --- | --- |
| A. 他の `[Fact]` の**後に実行される**ことを前提に、共有の `ActivitySource`（実在の `System.Net.Http` 等）の `HasListeners()` を見る | **棄却**。xUnit v3 の既定の `TestCaseOrderer` はメソッド宣言順を保証しない（アセンブリ内でのハッシュ順であり、テストメソッド名を変えると順が変わる）。同一クラス内は直列だが**順序は無保証**——「後で走る」を前提にすると、たまたま先に実行された回だけ偽陰性になる再現性の無い試験になる | 
| B. `IClassFixture<T>` の `DisposeAsync`（クラスの全 `[Fact]` 完了後に確実に 1 回走る）でアサーションする | **棄却（今回は採らない）**。順序非依存という利点はあるが、フィクスチャの `Dispose` からの例外は「名前を持つ試験」としては読めず、失敗の帰属が `FoundationRegistrationTests` クラス全体になり分かりにくい。将来クラスへ試験が増えるたびに巻き込まれる範囲も広がる |
| **C. 1 つの `[Fact]` 内で、破棄の前後を自己完結して観測する（採用）** | 他の `[Fact]` の実行順に依存しない。**専用 `ActivitySource`**（`Guid` で一意な名前）を、`可観測性の登録は例外なく解決できる()` と同じ `AddAiStockTradingObservability` の配線へ `ConfigureOpenTelemetryTracerProvider(b => b.AddSource(...))` で相乗りさせ、`TracerProvider` を解決した直後（`HasListeners()==true`。相乗りが効いていることの健全性チェック）と、`Dispose()` した直後（`HasListeners()==false`。破棄が `ActivityListener` を実際に外すことの本題）を**同じテスト内**で比較する |

**C を採用する。** 理由は「他クラスとの相互干渉を避けるため専用 `ActivitySource` を使う」という
PR #760 の陰性対照と同じ設計判断（[IADR-0333](../adr/IADR-0333_credential-bearing-uri-trace-redaction.md)
「陰性対照は実送信では書けなかった」節）を、**xUnit のテスト順序無保証**という別の不確実性に対しても
一貫して適用したものである。専用の一意な名前を使うため、並列に走る他クラス（`BusinessMetricsWiringTests` /
`CredentialBearingUriTraceRedactionTests`）の `TracerProvider` がこの `ActivitySource` を購読することも
ない（それらは `AddSource("ast.test.foundation-registration.<guid>")` を明示的に呼ばない）。

この設計は「本ファイルの 4 メソッドが**もう漏らさない**こと」を*直接*観測してはいない
（xUnit のテスト順序が無保証である以上、直接観測は原理的に再現性を持てない）。代わりに、
**4 メソッドが使っているのと同じ配線・同じ破棄コードパス**（`AddAiStockTradingObservability` →
`BuildServiceProvider` → `TracerProvider` の解決 → `Dispose`）が、破棄によって確実に
`ActivityListener` を外すことを固定する。4 メソッドは本作業でこの配線へ `using` を追加した
（上記「1. 破棄の追加」）ため、両者を合わせて #766 の受け入れ基準を満たす。

### ミューテーション実証（実測）

新設した `可観測性のTracerProviderを破棄するとActivityListenerが残らない()` の
`finally { provider.Dispose(); }` を一時的にコメントアウトし、`Dispose` を呼ばない形へ戻すと
——破棄前の健全性チェック（`HasListeners()` が `true`）は変わらず緑のまま、破棄後の本題の
アサーション（`HasListeners()` が `false` のはず）が**赤**になる。

```text
失敗 …FoundationRegistrationTests.可観測性のTracerProviderを破棄するとActivityListenerが残らない
  Expected marker.HasListeners() to be False because ServiceProvider（＝ TracerProvider）を破棄したら、
  グローバルな ActivityListener も外れているべきである。…, but found True.
```

コメントアウトを戻すと緑に戻ることを確認した（下記「検証」）。

## 受け入れ基準

- [x] `FoundationRegistrationTests` の 4 メソッドが `ServiceProvider` を `using` で破棄する
- [x] 破棄が `ActivityListener` を実際に外すことを固定する試験を追加する
- [x] 当該試験の `Dispose()` を外すとその試験が赤くなる（ミューテーション実証）
- [x] 本番コード（`CredentialBearingUriRedactionProcessor` を含む）は変更しない
- [x] `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` が通る

## テスト方針

`FoundationRegistrationTests.cs` に 1 メソッドを追加する（新規ファイルは起こさない——
同じ登録拡張のテストが同クラスに既にあり、分割する理由が無い）。既存の `EmptyConfig()` ヘルパーを再利用する。

## 計画書との差異

- 差異: なし。テスト基盤のみの是正であり、機能要求・非機能要件の実装内容に変更はない。

## 未決事項

1. `IntrospectionTests.cs` / `PlatformRealmTokenProviderTests.cs` の未破棄 `ServiceProvider`
   （上記「走査した母集合」の除外表）は、`ActivityListener` を持たないため #766 の射程外として
   本作業では直さない。行儀の悪さとしては同型であり、気になる場合は別 issue で扱う。
2. 「`BuildServiceProvider` は必ず `using` で受ける」という一般規約・検査器の追加は、
   運用ガイドの「同型の事故が 2 回起きたら」を満たさない（今回で 1 回目）ため見送る。
