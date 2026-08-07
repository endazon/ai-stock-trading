---
title: 作業仕様書 — 段階の既定発注先（StageSettings.Mode）を allow-list で読み、推定台帳の配線を必須依存で退行検知する（#431 / #428）
type: work
status: review
related_ids: [FR-20, FR-10, UC-06, SC-02, ADR-0016, IADR-0012, IADR-0140, IADR-0159, IADR-0161, IADR-0163]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
related_specs:
  - ../adr/IADR-0163_allow-list-and-required-dependency-scope.md
  - ../adr/IADR-0161_broker-provider-allow-list-resolution.md
  - ../adr/IADR-0159_buy-in-post-hoc-inference.md
  - ../adr/IADR-0140_broker-provider-axis.md
  - ../adr/IADR-0012_risk-settings-persistence.md
  - ../functional/FR-20_staged-gates.md
  - ../functional/FR-10_risk-controls.md
  - ../tests/FR-20_staged-gates-tests.md
  - ../tests/FR-10_risk-controls-tests.md
  - ./20260807_422_broker-provider-default-paper.md
  - ./20260807_419_buy-in-post-hoc-inference.md
  - ../blocked-tasks.md
  - ../DEFINITION_OF_DONE.md
---

# 作業仕様書: 適用範囲の穴と配線の穴を構造で塞ぐ（#431 / #428）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-20 (3)**（発注先の初期値は内蔵 `paper`／読めない行を実弾に倒さない）・**FR-10**（リスク統制）
- ユースケース（UC）: **UC-06**
- 画面（SC）: **SC-02**（現在の発注先の表示。本作業では画面を変更しない）
- 関連 ADR: **ADR-0016 決定4（2026-08-06 改訂）**（強制買戻しの事後推定と 30 日禁止）
- 実装 ADR: **[IADR-0163](../adr/IADR-0163_allow-list-and-required-dependency-scope.md)（本作業）**／
  [IADR-0161](../adr/IADR-0161_broker-provider-allow-list-resolution.md)（allow-list による発注先の解決。本作業はその**適用範囲**を広げる）／
  [IADR-0159](../adr/IADR-0159_buy-in-post-hoc-inference.md) 決定5（禁止期限の単独供給）／
  [IADR-0140](../adr/IADR-0140_broker-provider-axis.md) 決定3（`Mode` の型を `TradeMode` → `BrokerProvider` へ入れ替えた経緯）／
  [IADR-0012](../adr/IADR-0012_risk-settings-persistence.md)（設定は単一行 JSON。DTO を介した直列化）
- 起点 issue: [#431](https://github.com/endazon/ai-stock-trading/issues/431)・[#428](https://github.com/endazon/ai-stock-trading/issues/428)
- 計画 submodule: 本作業では更新しない

## 目的・背景

2 件はいずれも直近 PR（[#430](https://github.com/endazon/ai-stock-trading/pull/430) / [#427](https://github.com/endazon/ai-stock-trading/pull/427)）の監査で切り出した
**小粒の退行検知**であり、論点は 1 つに畳める——**「正しく書かれた実装の、配線・適用範囲の穴を構造で塞ぐ」**。
どちらも現時点で実弾が撃たれる欠陥ではなく、**将来の編集で黙って壊れる余地**を消すことが目的である。

| issue | 穴の形 | 現況の実害 |
| --- | --- | --- |
| #431 | **適用範囲の穴**。allow-list は `SettingsDto.BrokerProvider` にだけ効いており、同じ設定行の隣 `Stage.Mode`（同じ `BrokerProvider` 型）に効いていない | **未知の文字列・別の型で設定行全体が読めなくなる**（`GetCurrent` が 500）。未知の序数は安全側に倒れており実害なし |
| #428 | **配線の穴**。`OrderScreeningService` が推定台帳を省略可能引数（既定 `null`）で受けており、`Program.cs` の引数を削っても**コンパイルが通りテストは全緑**のまま 30 日禁止だけが消える | **なし**（現在の配線は正しい） |

### #431 の現況（実装前に確認した挙動）

`RiskSettingsSerialization.cs` の `[property: JsonConverter(typeof(BrokerProviderJsonConverter))]` は
**`SettingsDto.BrokerProvider` にだけ**付いており、`StageSettings Stage` は属性を持たず標準の直列化で往復している。

| `Stage.Mode` の永続値 | 現況 | 評価 |
| --- | --- | --- |
| `0` / `1` / `2` | 対応する 3 値 | 正しい（回帰として固定する） |
| 未知の**序数**（`7` / `-1` / `int` 超過） | 素通りするが `RiskEvaluator` の `settings.Stage.Mode != BrokerProvider.MoomooReal` により**実弾は止まる** | 安全側だが構造の担保ではない |
| **未知の文字列**（`"MOOMOO REAL"` / `"Live"` / `""`）・`true` / `{}` / `[1]` | **`JsonException` で設定行全体が読めなくなる**（統制値・ガード・段階もろとも失われる） | **IADR-0161 が「採らない」と明記した挙動そのもの** |

**下段が本作業の主眼である。** 前者は実害のない未担保、後者は**リスク判定そのものが動かなくなる可用性の欠落**であり性質が違う。
到達経路（手編集された行・外部ツールが書いた行）は #430 が是正した経路と同一であり、**同じ経路の片方だけを塞いだ状態**になっている。

### #428 の現況

```csharp
public sealed class OrderScreeningService(
    ...,
    IManipulativeOrderPatternDetector? patternDetector = null,
    IBuyInInferenceStore? buyInInferences = null)   // ← 既定 null
```

`buyInInferences` が `null` のとき `buyInBan` は `null` になり、`RiskEvaluator` の該当分岐が丸ごと飛ぶ
（＝**強制買戻し由来の 30 日禁止が一切効かない**）。本番配線（`RiskManagementService.Api/Program.cs`）は
`sp.GetRequiredService<IBuyInInferenceStore>()` を渡しており**正しい**が、既存テストは
`OrderScreeningService` をテスト内で直接構築するため、**`Program.cs` の配線が消えても検知しない**。

同じ PR（#427）の `BrokerPositionsObservedHandler` は、まさにこの理由で依存を**必須**にしている
（「配線を忘れても静かに推定されない状態を作らない——**推定の不在は『強制買戻しが起きていない』ことを意味しない**」）。
その規律が `OrderScreeningService` 側に及んでいない。**#416**（`AddSingleton<T>(factory)` の遅延生成で preflight が
起動時に効かなかった件）と同じ「CI は緑だが実は何も守っていない」類型である。

## 対象範囲

### やること

1. **#431**: `SettingsDto.Stage` を **`StageDto` 経由**にし、`StageDto.Mode` に
   `[property: JsonConverter(typeof(BrokerProviderJsonConverter))]` を付けて `BrokerProviderResolution` を通す。
   `GuardDto` が先例であり、**同じ形**を採る。
2. **#428**: `OrderScreeningService` の `buyInInferences` を**必須引数**にし、`patternDetector` より**前**へ移す
   （省略可能引数の後ろに必須引数は置けない）。全 8 か所の構築点（本番 1・テスト 7）で引数順が変わる。
3. 退行防止テスト（後述）。
4. 文書更新（機能仕様書 FR-20 / FR-10・テスト仕様書 FR-20 / FR-10・IADR-0163・索引・`blocked-tasks.md`）。

### やらないこと

- **`LiveTradingGate.LiveTradingReleased` に触れること**（実弾を止めている唯一の閂。本作業の対象外）。
- **`StageSettings` へ `[JsonConverter]` を直付けすること**——`StageSettings` は**ドメイン型**であり、
  永続化の関心がドメインへ漏れる（#430 が付けなかった理由もこれである）。
- **`BrokerProviderResolution` の allow-list の中身を変えること**（3 値の明示一致。IADR-0161 で確定）。
- **`Enum.IsDefined` への置き換え**（IADR-0161 が明示的に禁じている）。
- **ワイヤ形式の変更**（書き込みは数値の序数のまま。変えると旧版が読めなくなる）。
- **旧行を書き換える移行**（IADR-0161 決定2 と同じ規律。既定は読み取り時に与える）。
- **段階ゲートの判定式（`RiskEvaluator` の `Stage.Mode != MoomooReal`）の変更**——現状で正しく安全側に倒れている。
- **`patternDetector` の必須化**——「検出器を構成していない」は正当な状態であり、`null` の意味が違う
  （推定台帳の `null` は「30 日禁止が効かない」を意味する）。
- **DI グラフの結線テスト（#428 案①）** ——案②（必須引数化）を採るため不要。コンパイルエラーの方が確実である。

## 設計

### 1. `StageDto`（#431）

配置: `RiskManagementService.Infrastructure/Foundation/Persistence/RiskSettingsSerialization.cs`（既存の `GuardDto` と同じ場所）。

```csharp
private sealed record StageDto(
    TradingStage Stage,
    [property: JsonConverter(typeof(BrokerProviderJsonConverter))]
    BrokerProvider Mode,
    decimal CapitalCapRatio);
```

- **ワイヤ形式は不変**。`JsonSerializerDefaults.Web` の camelCase で `stage` / `mode` / `capitalCapRatio` と、
  プロパティ名も順序も `StageSettings` と一致する。書き込みは `BrokerProviderJsonConverter.Write` により**数値の序数**。
- **倒し先は `BrokerProviderResolution.Default`（内蔵 `paper`）**。`Stage.Mode` の意味は「その段階で通常選ぶ発注先」であり、
  解決できない値を内蔵 `paper` へ倒せば `!= MoomooReal` となり実弾は止まる（現状の序数ケースと同じ安全側）。
  **新しい既定を発明しない。**
- **DTO → ドメインの写像で二重に `Resolve` を呼ばない。** 解決の単一情報源は converter である
  （二重に呼ぶと converter を外す変異がテストで検知できなくなる）。

### 2. `OrderScreeningService` の依存（#428）

```csharp
public sealed class OrderScreeningService(
    IRiskSettingsStore settingsStore,
    PortfolioSnapshotBuilder snapshotBuilder,
    ILockoutStore lockoutStore,
    IClock clock,
    IBusinessCalendar businessCalendar,
    IBuyInInferenceStore buyInInferences,          // ← 必須。引数を削ればコンパイルエラー
    IManipulativeOrderPatternDetector? patternDetector = null)
```

- `buyInBan` は**常に**組む（`new BuyInBanSupply(clock.Today, buyInInferences.GetBanUntil(...))`）。
  台帳が空なら `GetBanUntil` が `null` を返し `BuyInBanPolicy.IsBanned` が `false` になるため、**振る舞いは変わらない**。
- 推定台帳を関心に持たないテストは `new InMemoryBuyInInferenceStore()`（空の台帳）を渡す。
  **`null!` を渡さない**——必須化の意味が消えるうえ、`GetBanUntil` で `NullReferenceException` になる。
- 構築点は 8 か所（本番 1・テスト 7）。

| ファイル | 種別 |
| --- | --- |
| `RiskManagementService.Api/Program.cs` | 本番 |
| `RiskManagementService.Application.Tests/Manipulation/OrderScreeningManipulationTests.cs` | テスト |
| `RiskManagementService.Application.Tests/TradingControlPriorityTests.cs` | テスト |
| `RiskManagementService.Application.Tests/OrderScreeningServiceTests.cs`（2 か所） | テスト |
| `RiskManagementService.Application.Tests/BuyInInferenceTests.cs` | テスト |
| `RiskManagementService.Infrastructure.Tests/TradeDecisionMadeConsumerTests.cs` | テスト |
| `RiskManagementService.Infrastructure.Tests/MoomooFillControlRegressionTests.cs` | テスト |

## 受け入れ基準

- [ ] `Stage.Mode` が `"MOOMOO REAL"` / `"Live"` / `""` / `true` / `{}` / `[1]` の行を読んでも**例外にならず**、
      設定行の他の値（統制値・ガード・段階）が**すべて読める**
- [ ] `Stage.Mode` の未知の序数（`7` / `-1` / `int` 超過）が**内蔵 `paper`** へ落ちる
- [ ] `Stage.Mode` が解決できない行では**実弾の発注が `StageProhibitsLiveTrading` で拒否される**
- [ ] `"MoomooReal"`（正準名）・`"1"` は `MoomooReal` として読まれる（回帰）
- [ ] 旧 `TradeMode` の序数（`0` / `1`）を積んだ行は同じ意味で読まれる（回帰）
- [ ] **ストア経由**（`EfRiskSettingsStore`）でも同じである（直列化単体では配線の穴を検知できない）
- [ ] 書き込みは**数値の序数**のままである
- [ ] `OrderScreeningService` の推定台帳は**必須引数**であり、`Program.cs` から引数を削ると**ビルドが失敗する**
- [ ] `LiveTradingGate.LiveTradingReleased` は `false` のままである

## テスト計画（退行防止）

| ID | 仕様書 | 内容 |
| --- | --- | --- |
| T-81 | FR-20 | `Stage.Mode` の未知の序数・未知の文字列・大小文字違い・空文字・`null`・別の型が**すべて内蔵 `paper`** へ落ち、**例外を投げない**（設定行の他の値がすべて読める） |
| T-82 | FR-20 | `Stage.Mode` の正準名（`"MoomooReal"` 等）・序数の 10 進表記（`"1"`）は明示一致で読まれる。旧 `TradeMode` の序数 `0` / `1` も同じ意味（回帰）。書き込みは数値の序数 |
| T-83 | FR-20 | **ストア経由**（`EfRiskSettingsStore.GetCurrent`）でも同じ。`Stage.Mode` が壊れた行でも統制値・ガード・発注先が読める |
| T-84 | FR-20 | `Stage.Mode` が解決できない行を読んだ設定で実弾の注文を出すと **`StageProhibitsLiveTrading` で拒否される**（安全側の挙動が保たれる） |
| T-10-257 | FR-10 | `OrderScreeningService` の推定台帳は**必須依存**である（省略可能引数へ戻すと赤くなる構造テスト）。`patternDetector` は**省略可能のまま**であることも同時に固定する（`null` の意味が違う） |

**ミューテーション（実施必須）**:

| # | 変異 | 期待 |
| --- | --- | --- |
| (a) | `StageDto.Mode` から converter 属性を外す | T-81 / T-83 が赤くなる（未知の文字列で `JsonException`） |
| (b) | `Stage.Mode` の解決先を `Default` から `MoomooReal` にする | T-81 / T-84 が赤くなる |
| (c) | `Program.cs` から推定台帳の引数を削る | **ビルドが失敗する**（必須引数化の目的そのもの） |

## 影響範囲

| 層 | ファイル |
| --- | --- |
| 永続化 | `RiskManagementService.Infrastructure/Foundation/Persistence/RiskSettingsSerialization.cs` |
| アプリケーション | `RiskManagementService.Application/Services/OrderScreeningService.cs` |
| 配線 | `RiskManagementService.Api/Program.cs` |
| テスト | `RiskManagementService.Infrastructure.Tests`（直列化・ストア）・`RiskManagementService.Application.Tests`（構築点・構造テスト） |
| 文書 | `docs/adr/IADR-0163_*`＋索引・`docs/functional/FR-20`・`FR-10`・`docs/tests/FR-20`・`FR-10`・`docs/blocked-tasks.md` |

## 未決事項・残余リスク

- **`Stage.Mode` の未知値は黙って内蔵 `paper` になる**（警告ログを出していない。設定ストアの読み取りは高頻度で
  あり毎回のログは実質ノイズになる——IADR-0161 の結果と同じトレードオフ）。
- **`StageSettings` は HTTP 応答でも往復する**が、本作業は**設定ストア（永続行）の読み取りだけ**を対象とする。
  API 応答は書き込み側であり、本リポジトリが書く値は常に解決済みの 3 値である。
- **実弾は `LiveTradingGate`（閂 0）が起動時に止めている。** 本作業はその閂に触れていないため、
  ここで整えた段階ゲートの正しさは**まだ実地で観測できない**。
- **設定ストアが発注経路を動かさない状態は変わっていない**（`blocked-tasks` の既存項目。#334 / #422 と同じ）。

## 変更履歴

| 日付 | 変更 | 理由 |
| --- | --- | --- |
| 2026-08-07 | 新規作成 | [#431](https://github.com/endazon/ai-stock-trading/issues/431)・[#428](https://github.com/endazon/ai-stock-trading/issues/428)（#430 / #427 の監査で切り出した退行検知） |
