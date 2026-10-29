---
title: gRPC の deadline の試験 7 件の暖機を、構成した短い deadline の経路から、同じチャネルで長い deadline の生の呼び出しへ替える
type: spec
status: accepted
related_ids: [NFR, FR-10, FR-17, IADR-0364, IADR-0284]
author: claude (Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs: []
---

# 仕様書: gRPC の deadline の試験の暖機を時間に依存しない形へ（#1517・P1）

## 起点

- MSP の Integration（job 123745660928）で、AST の `InformationCollectionService.Tests.GrpcStage4ReadsTests.T_10_1698_提供側が黙れば構成した_deadline_で_Normal_へ倒れ再試行しない` が
  `Expected (gate.GetAsync()).Halted to be True because 暖機, but found False.` で赤になった。

## 根拠の実測（手元。計装は一時的で、コミットしない）

| 条件 | 暖機の所要 |
| --- | --- |
| 試験 1 本だけ（プロセスで最初の gRPC） | 216 / 256 / 323 ms |
| 試験プロジェクト全件（他の試験が先に gRPC を使う） | 3 / 5 / 7 ms |
| 暖まったプロセスで新しい提供側の初回 | 3〜47 ms |
| 同じ提供側へ新しいチャネルの初回（接続の確立を含む） | 2〜10 ms |

- 遅いのは接続ではなく、プロセスで一度きりの立ち上がりである。CI の負荷下では秒の桁へ伸び得る（IADR-0364 の実測では、RPC の開始からハンドラに入るまで最大 1.3 秒）。暖機を構成した 2 秒の経路で行うと、暖機自体が打ち切られる。

## 母集合（規則 9）

`git grep -n "RespondsOnceThenHangs\|暖機" -- 'backend/**/Tests/**'` で引いた 7 件。いずれも「1 回目は応答・2 回目から黙る提供側」で、1 回目（暖機）を構成した短い deadline の製品の経路で呼んでいる。

| サービス | 試験 | 構成した deadline |
| --- | --- | --- |
| 情報収集 | `GrpcStage4ReadsTests.T_10_1698_…_Normal_へ倒れ再試行しない` | 2 秒 |
| 情報収集 | `GrpcHeldSymbolsTests.T_10_2763_…_不明へ倒れ再試行しない` | 1 秒 |
| 取引判断 | `GrpcStage4ReadsTests.T_10_1698_…_取引しない側へ倒れ再試行しない` | 2 秒 |
| 取引判断 | `RiskManagementGrpcIntegrationTests.T_10_1058_…_不明へ倒れる` | 3 秒 |
| 市場監視 | `RiskManagementGrpcTests.T_10_1058_…_空列へ倒れ再試行しない` | 3 秒 |
| 報告書 | `GrpcAuditLedgerSourcesTests.T_10_1675_…_未供給へ倒れ再試行しない` | 3 秒 |
| 報告書 | `GrpcRiskLedgerSourcesTests.T_10_1058_…_未供給へ倒れ再試行しない` | 3 秒 |

除外: `GrpcAssumptionsClientIntegrationTests`（費用統制・取引判断）は IADR-0364 決定 1 の `ConnectAsync` で暖機しており、構成した deadline の経路で暖機していない。
`git grep -n 'GrpcTimeoutSeconds"] = "[0-9]"' -- 'backend/**/Tests/**'` でも引き直した。追加で当たる `GrpcTradingHaltSourceTests`（発注執行）と `GrpcLlmCostHaltGateTests`（取引判断）の時間切れの試験は暖機を持たない。最初から黙る提供側に対して、結果が「不明／停止していない」であることだけを表明し、呼ばれた回数も経過時間も表明しない。立ち上がりが予算を食っても同じ結果になるので、対象外とした。

## 是正

- 暖機は **同じチャネル**（輸送の internal の `Client`）で **deadline 30 秒**の生の呼び出しにする。2 回目（黙った提供側）は製品の経路・構成した deadline のまま。
- deadline の値・経過時間の上界・呼ばれた回数の表明は変えない。skip・無効化はしない。
- `InternalsVisibleTo` を情報収集・市場監視・報告書の製品の csproj に足す（試験プロジェクトへだけ）。報告書の `AuditGrpcTransport` は `Client` を internal で見せる。
- 判断は IADR-0364 の 2026-10-30 追記（決定 6）。

## red → green

提供側の 1 回目の応答を 3.5 秒遅らせる注入（6 つの `RespondsOnceThenHangs`）を一時的に入れた。3.5 秒は、どの試験の構成した deadline よりも長い。

| 形 | 結果 |
| --- | --- |
| 旧形（構成した deadline の経路で暖機） | 7 件とも赤（情報収集 2・取引判断 2・市場監視 1・報告書 2） |
| 新形（同じチャネル・30 秒で暖機） | 7 件とも緑 |
| 新形・注入なし | 4 つの試験プロジェクトがすべて緑（689・2197・411・2056 件）。Architecture も緑 |

## 受け入れ基準

- [x] 暖機の所要が構成した deadline を超えても赤にならない（注入で確認）
- [x] 2 回目は構成した deadline で打ち切られ、再試行しない（表明は不変）
- [x] `dotnet build -warnaserror`・format・対象の試験プロジェクトが緑

## PoC 配備への影響

無し（試験と、試験への可視性だけの変更）。マージの後、MSP のサブモジュール（`src/ai-stock-trading`）の更新が要る。
