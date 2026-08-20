---
title: IADR-0163 allow-list は設定行の同じ型の項目すべてに適用し、不在が統制の無効を意味する依存は必須引数にする
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-10, UC-06, SC-02, ADR-0016, IADR-0012, IADR-0140, IADR-0159, IADR-0161]
author: Claude Code (implementation session)
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
---

# IADR-0163: allow-list の適用範囲と、必須にすべき依存の見分け方

- 状態: Accepted
- 日付: 2026-08-07
- 決定者: Claude Code（実装セッション）

## 起点・関連

- 関連する計画書 ID: **FR-20 (3)**（発注先の初期値は内蔵 `paper`。読めない行を実弾に倒さない）／
  **FR-10**（リスク統制）／**UC-06**／**ADR-0016 決定4（2026-08-06 改訂）**（強制買戻しの事後推定と 30 日禁止）
- 関連する実装仕様書: [作業仕様書 20260807_431-428](../specs/20260807_431-428_stage-mode-allow-list-and-buy-in-store-wiring.md)／
  [機能仕様書 FR-20](../../docs/functional/FR-20_staged-gates.md)／[機能仕様書 FR-10](../../docs/functional/FR-10_risk-controls.md)／
  [テスト仕様書 FR-20](../../docs/tests/FR-20_staged-gates-tests.md)／[テスト仕様書 FR-10](../../docs/tests/FR-10_risk-controls-tests.md)
- 隣接する実装 ADR: **[IADR-0161](IADR-0161_broker-provider-allow-list-resolution.md)（本 ADR はその適用範囲を広げる）**／
  [IADR-0159](IADR-0159_buy-in-post-hoc-inference.md) 決定5（禁止期限の単独供給）／
  [IADR-0140](IADR-0140_broker-provider-axis.md) 決定3（`StageSettings.Mode` の型を `TradeMode` → `BrokerProvider` へ入れ替えた経緯）／
  [IADR-0012](IADR-0012_risk-settings-persistence.md)（設定は単一行 JSON。DTO を介した直列化）
- 起点 issue: [#431](https://github.com/endazon/ai-stock-trading/issues/431)・[#428](https://github.com/endazon/ai-stock-trading/issues/428)

## コンテキストと課題

**本 ADR は新しい規則を作らない。** 既に確定している 2 つの規律の**適用範囲**を、それが及んでいなかった箇所へ広げる。

| 既存の規律 | 及んでいなかった箇所 |
| --- | --- |
| [IADR-0161](IADR-0161_broker-provider-allow-list-resolution.md) 決定1・決定2: **発注先は allow-list で解決し、解決できない値はすべて内蔵 `paper` へ落とす**（例外にして設定行ごと失わない） | 同じ設定行の隣の項目 **`StageSettings.Mode`**（同じ `BrokerProvider` 型） |
| [IADR-0159](IADR-0159_buy-in-post-hoc-inference.md): **配線を忘れても静かに推定されない状態を作らない**——推定の不在は「強制買戻しが起きていない」ことを意味しない（`BrokerPositionsObservedHandler` は依存を必須にしている） | **`OrderScreeningService`**（推定台帳を省略可能引数で受けていた） |

### 欠陥1（#431）: allow-list が設定行の片方の項目にしか効いていない

`RiskSettingsSerialization` の `[property: JsonConverter(typeof(BrokerProviderJsonConverter))]` は
`SettingsDto.BrokerProvider` にだけ付いており、`StageSettings Stage` は属性を持たず標準の直列化で往復していた。

| `Stage.Mode` の永続値 | 是正前 | 評価 |
| --- | --- | --- |
| 未知の**序数**（`7` / `-1` / `int` 超過） | 素通りするが `RiskEvaluator` の `Stage.Mode != MoomooReal` で**実弾は止まる** | 安全側。**構造の担保ではない** |
| **未知の文字列**（`"MOOMOO REAL"` / `"Live"` / `""`）・`true` / `{}` / `[1]` | **`JsonException` で設定行全体が読めなくなる** | **IADR-0161 が「採らない」と明記した挙動そのもの** |

両者は性質が違う。前者は実害のない未担保、後者は**リスク判定そのものが動かなくなる可用性の欠落**である
（統制値・ガード・段階もろとも失われ `GetCurrent` が 500 を返す）。到達経路（手編集された行・外部ツールが
書いた行）は IADR-0161 が塞いだ経路と**同一**であり、**同じ経路の片方だけを塞いだ状態**になっていた。

### 欠陥2（#428）: 「不在が統制の無効を意味する依存」が省略可能引数だった

```csharp
public sealed class OrderScreeningService(
    ..., IManipulativeOrderPatternDetector? patternDetector = null, IBuyInInferenceStore? buyInInferences = null)
```

本番配線（`Program.cs`）は `GetRequiredService<IBuyInInferenceStore>()` を渡しており**現在の挙動は正しい**。
問題は**その引数を削っても、コードはコンパイルが通り、テストは全緑のまま、強制買戻し由来の 30 日禁止だけが
静かに効かなくなる**ことである（既存テストは本サービスをテスト内で直接構築するため配線の消失を検知しない）。
[#416](https://github.com/endazon/ai-stock-trading/issues/416)（`AddSingleton<T>(factory)` の遅延生成で preflight が
起動時に効かなかった件）と同じ**「CI は緑だが実は何も守っていない」**類型である。

## 決定

### 決定1: 設定行の直列化では、同じ型の項目すべてに同じ allow-list を通す。ドメイン型へ属性は直付けしない

`SettingsDto.Stage` を **`StageDto` 経由**にし、`StageDto.Mode` に
`[property: JsonConverter(typeof(BrokerProviderJsonConverter))]` を付ける。

- **`StageSettings` へ直付けしない。** `StageSettings` は**ドメイン型**であり、永続化の関心がドメインへ漏れる。
  `GuardDto` が先例であり（`IReadOnlySet` を具象化するために DTO を挟んだ）、**同じ形**を採る。
- **倒し先は `BrokerProviderResolution.Default`（内蔵 `paper`）**。`Stage.Mode` の意味は「その段階で通常選ぶ
  発注先」であり、解決できない値を内蔵 `paper` へ倒せば `!= MoomooReal` となり実弾は止まる
  （現状の序数ケースと同じ安全側）。**新しい既定を発明しない。**
- **ワイヤ形式は不変**（プロパティ名・順序は `StageSettings` と同一、書き込みは数値の序数）。
  **旧行を書き換える移行は行わない**（IADR-0161 決定2 と同じ規律。既定は読み取り時に与える）。
- **DTO → ドメインの写像で二重に `Resolve` を呼ばない。** 解決の単一情報源は converter である。
  二重に呼ぶと「属性を外す」変異がテストで検知できなくなり、**退行検知そのものが無効化される**。

**allow-list の中身は変えない**（3 値の明示一致。IADR-0161 で確定）。**`Enum.IsDefined` へも置き換えない**
（IADR-0161 が明示的に禁じている）。**段階ゲートの判定式（`RiskEvaluator` の `Stage.Mode != MoomooReal`）も
変えない**——現状で正しく安全側に倒れている。

### 決定2: 「不在が統制の無効を意味する依存」は必須引数にする。「構成しないことが正当な依存」は省略可能のままにする

`OrderScreeningService` の `buyInInferences` を**必須引数**にし、`patternDetector` より**前**へ移す
（省略可能引数の後ろに必須引数は置けないため。全 8 か所の構築点＝本番 1・テスト 7 で引数順が変わる）。

- **`buyInInferences`（推定台帳）は必須。** `null` は「30 日禁止が効かない」を意味し、
  **推定の不在は「強制買戻しが起きていない」ことを意味しない**（IADR-0159 と同じ文言）。
  必須にすれば、配線を削った瞬間に**コンパイルエラー**になる。
- **`patternDetector`（相場操縦検出器）は省略可能のまま。** 「検出器を構成していない」は**正当な状態**であり、
  `null` の意味が違う。両者をまとめて必須／省略可能へ倒すと、この区別が失われる。
- 台帳を関心に持たないテストは**空の台帳**（`InMemoryBuyInInferenceStore`）を渡す。**`null!` は渡さない**
  ——必須化の意味が消えるうえ、`GetBanUntil` で `NullReferenceException` になる。
- 供給（`BuyInBanSupply`）は**常に**組む。台帳が空なら `GetBanUntil` が `null` を返し
  `BuyInBanPolicy.IsBanned` が `false` になるため、**振る舞いは変わらない**。

**DI グラフの結線テスト（issue の案①）は採らない。** コンパイルエラーへ落とす案②のほうが確実であり、
テストは「必須のまま保たれていること」を固定する構造テスト 1 本（T-10-267）で足りる。

## 検討した代替案

| 代替案 | 却下理由 |
| --- | --- |
| `StageSettings` へ `[JsonConverter]` を直付けする | ドメイン型に永続化の関心が漏れる。`StageSettings` は HTTP 応答・イベントでも往復しており、影響範囲が設定ストアを超える |
| DTO → ドメインの写像でも `Resolve` を呼ぶ（二重の安全網） | **converter 属性を外す変異が検知できなくなる。** 解決の単一情報源を 1 か所に保つ |
| `Stage.Mode` の未知値を例外にする（厳格） | 設定行**全体**が失われリスク判定が動かない。IADR-0161 が明示的に却下した選択肢 |
| `Stage.Mode` の未知値を `MoomooSimulate` へ倒す | 新しい既定の発明であり、計画のどこにも根拠が無い。`Default` をそのまま使えばよい |
| 段階ゲートの判定式を「既知の 3 値のときだけ通す」形に変える | 現状で正しく安全側に倒れており、issue が「やらないこと」に挙げている |
| `#428` を DI 結線テスト（案①）で塞ぐ | テストは追加しないと存在しない。**型で塞げるものを型で塞ぐ**ほうが確実で、書き忘れが起こらない |
| `patternDetector` も必須にする | `null` の意味が違う。「検出器を構成していない」は正当な状態であり、必須化は嘘の制約になる |

## 結果

- **良い点**: 設定行の発注先系の項目が**すべて**同じ allow-list を通るようになり、「片方だけ塞いだ」状態が消えた。
  壊れた設定行でも統制値・ガード・段階が読める。推定台帳の配線は**削ればビルドが落ちる**。
- **悪い点 / トレードオフ**: `Stage.Mode` の未知値も**黙って内蔵 `paper` になる**（警告ログを出していない。
  設定ストアの読み取りは高頻度でありログは実質ノイズ——IADR-0161 と同じトレードオフ）。
  必須引数化により、推定台帳に関心の無いテスト 6 か所が空の台帳を明示的に渡すことになった（記述は増えるが、
  **「この経路では禁止が供給されている」ことが読める**という副次的な利点がある）。
- **残余リスク**:
  - **`StageSettings` は HTTP 応答でも往復する**が、本作業は設定ストア（永続行）の読み取りだけを対象とした。
    API 応答は書き込み側であり、本リポジトリが書く値は常に解決済みの 3 値である。
  - **実弾は `LiveTradingGate`（閂 0）が起動時に止めている。** 本 ADR はその閂に触れていないため、
    ここで整えた段階ゲートの正しさは**まだ実地で観測できない**。
  - **設定ストアの値が発注経路を動かさない状態は変わっていない**（起動時構成 `Broker:Provider` が支配。
    `blocked-tasks` の既存項目。#334 / #422 と同じ）。

## 追記

（なし）
