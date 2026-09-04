---
title: 業務メトリクスの否定形テストが並行実行で偽陽性になる欠陥の是正
issue: "#695"
plan_refs:
  - NFR
adr_refs:
  - IADR-0309
status: done
created: 2026-09-05
---

# 作業仕様書: 業務メトリクスの否定形テストが並行実行で偽陽性になる欠陥の是正（#695）

## 背景

2026-09-04 の Integration E2E（develop `a5613718`）が 1 件の失敗で赤くなった。

```
Failed InformationCollectionService.Tests.InformationSourceFactoryDailyVolumeTests
       .finnhub系ソースが未有効なら銘柄設定があっても見積らない

  Expected capture.ValuesOf(BusinessMetricNames.FinnhubDailyVolumeEstimate) to be empty,
  but found at least one item {
      InstrumentName = "ast.finnhub.daily_request_estimate",
      Tags = {empty},
      Value = 24.0
  }
```

**このテスト自身は `ast.finnhub.daily_request_estimate` を 1 度も発火させない**（provider が
`sec-edgar` なので見積り自体を行わない）。**値 24.0 は別のテストが発火させたもの**である。

## 真因

`System.Diagnostics.Metrics.Meter` は**プロセス全体で観測される**。`MeterCapture` は Meter 名で
購読するため、**同時に走っている別テストの測定値まで拾う**。`MeterCapture` のクラスコメント自身が
この性質を警告しており、「表明は『含む（Contain）』の形で書け」と書いてある。

**しかし否定形の表明（「発火しなかった」）は、原理的に『含む』では書けない。** 規約が
「否定形をどう書くか」を示していなかったため、6 箇所の `BeEmpty()` がすべてこの偽陽性を抱えていた。

**赤くなるかどうかは同時に走るテストのタイミング次第**であり、ローカルでは再現しない
（実測: 該当プロジェクト単体では 477 件緑）。**Integration E2E は全プロジェクトを 1 プロセスで
走らせるため、混入の確率が上がる。**

## 決めたこと

詳細と根拠は [IADR-0309](../adr/IADR-0309_meter-isolation-for-negative-assertions.md)。要点のみ。

1. `BusinessMetrics` に**省略可能な `meterName` 引数**を足す（既定は `BusinessMetricNames.MeterName`
   ＝**本番の挙動は 1 バイトも変わらない**）。
2. `MeterCapture.NewIsolatedMeterName()` を新設し、**否定形の表明だけ**が一意名を使う。
3. **隔離そのものを証明するテスト**を追加する（既定名の Meter が同時に発火しても隔離側は拾わない／
   一意名が呼び出しごとに異なる）。
4. **検査器は足さない。** CLAUDE.md の「検査器・規約の追加は同型事故 2 回から」に照らして本件は 1 回目。
   代わりに `MeterCapture` と `BusinessMetrics` の両方に**書き方を doc コメントで示す**。

## 変更したファイル

| ファイル | 変更 |
| --- | --- |
| `Shared.Contracts/Observability/BusinessMetrics.cs` | 省略可能な `meterName` 引数（既定は従来と同じ） |
| `TestSupport/AiStockTrading.TestSupport.Metrics/MeterCapture.cs` | `NewIsolatedMeterName()` を新設・書き方の doc |
| `Shared.Contracts.Tests/BusinessMetricsTests.cs` | 否定形 3 件を隔離 ＋ **隔離の証明テスト 2 件を新設** |
| `InformationCollectionService/Tests/.../InformationSourceFactoryDailyVolumeTests.cs` | 否定形 2 件を隔離 |
| `Shared.Infrastructure.Tests/MarketData/MarketDataSourceFactoryDailyVolumeTests.cs` | 否定形 1 件を隔離 |

## 引いた母集合と、除外したものと理由

**軸 1（否定形の表明）**: `git grep -n 'ValuesOf(.*).Should().BeEmpty()\|TagValuesOf(.*).Should().BeEmpty()\|Measurements.Should().BeEmpty()' -- backend` → **6 件**。**全件を隔離した。**

**軸 2（`MeterCapture` の利用箇所）**: `git grep -ln 'new MeterCapture' -- backend` → **10 ファイル**。

| 除外 | 理由 |
| --- | --- |
| 肯定形の表明（`ContainSingle` / `HaveCount` / `Equal` / `BeEquivalentTo` など） | **他人の測定値が混ざっても壊れない**。むしろ隔離すると「本番と同じ Meter 名で発火している」ことを表明できなくなり、**検査の意味が薄まる** |
| `SumOf(...)` を使う表明 | 上と同じ理由で今回は触らない。**ただし合計は混入で狂い得る**ため、実際に赤が出たら本 issue へ追記して同じ隔離を適用する（現時点で赤の実測は無い） |
| 検査器の新設 | 同型事故 1 回目（CLAUDE.md の規約）。**doc コメントで書き方を示すに留める** |

## 検証

- `dotnet build backend/backend.slnx` → 警告 0 / エラー 0
- `Shared.Contracts.Tests` **359 → 361**（隔離の証明 2 件）・`InformationCollectionService.Tests` **477**（不変）・
  `Shared.Infrastructure.Tests` **283**（不変）—— いずれも失敗 0
- 🔴 **変異試験**: 証明テストの `MeterCapture(meterName)` を `MeterCapture(BusinessMetricNames.MeterName)`
  へ戻すと**当該テストが失敗する**ことを実測（`失敗: 1 / 合格: 0`）。復元して緑を再確認した。
  **隔離が load-bearing であることの証跡**である。
