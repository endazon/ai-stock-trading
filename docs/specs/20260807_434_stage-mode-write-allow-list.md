---
title: PUT /risk-controls/settings/stage の Stage.Mode を allow-list で検証する（書き込み経路の非対称の回復）
type: spec
status: review
related_ids: [FR-20, UC-06, IADR-0161, IADR-0163]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
---

# 仕様書: `Stage.Mode` の書き込み経路 allow-list 検証

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-20 (3)**（運用段階と発注先）
- ユースケース（UC）: **UC-06**（設定変更）
- 画面（SC）: なし（画面は既に既知の 3 値しか送らない。本件は API 直叩きの経路）
- 実装 ADR: **[IADR-0161](../adr/IADR-0161_broker-provider-allow-list-resolution.md) 決定3**（読み書きの非対称）／
  **[IADR-0163](../adr/IADR-0163_allow-list-and-required-dependency-scope.md)**（allow-list の適用範囲）
- 起点 issue: [#434](https://github.com/endazon/ai-stock-trading/issues/434)（#433 の AI レビューが見つけた既存ギャップ）

## 目的・背景

`IADR-0161` は発注先（`BrokerProvider`）の読み書きを**意図的に非対称**に設計した。

| 経路 | 未知の値の扱い | 理由（IADR-0161 決定3） |
| --- | --- | --- |
| **読み取り**（永続行） | 黙って内蔵 `paper` へ倒す | 例外にすると設定行全体が失われ `GetCurrent` が 500 になる。リスク判定そのものが止まる |
| **書き込み**（変更要求） | **`UnknownProvider` で拒否**（400） | 黙って倒すと「実弾を選んだのに paper になった」という**説明のつかない状態遷移**が生まれる |

`PUT /risk-controls/settings/broker-provider` は `BrokerProviderChange.Evaluate` がこの受理条件を判定する。
**しかし `PUT /risk-controls/settings/stage` は `StageUpdateRequest.Stage`（`StageSettings`・`Mode` を含む）を
そのまま保存しており、`Mode` の allow-list 検証を行っていない。**

### 何が起きるか

未知の `Mode`（例: 序数 `7`）を `PUT` すると:

1. **書き込みは 200 で受理される**
2. 次の読み出しで #433（`IADR-0163`）の allow-list が効き、**`InternalPaper` へ正規化される**
   （`EfRiskSettingsStore.GetCurrent` が毎回 `Deserialize` し直すため）
3. 結果として、**利用者は「保存できた」と思っているのに、読み戻すと別の値になっている**

**これは #430 が「採らない」と明記した状態遷移そのものである。**

### 実害の程度（過大評価しない）

**実弾へ倒れる経路は無い。** 正規化先は `InternalPaper` であり、`RiskEvaluator` の
`Stage.Mode != MoomooReal` で実弾は止まる。`LiveTradingGate` も掛かっている。
**塞ぐのは「説明のつかない状態遷移」という一貫性の問題**であり、資金の安全性の問題ではない。

## 対象範囲

### 対象

**`RiskSettingsService.UpdateStage` で `stage.Mode` を allow-list 検証し、未知の値は `ArgumentException` で拒否する。**

- 判定は **`BrokerProviderResolution.IsKnown`** を使う（`IADR-0161` が「どの値が既知かの単一情報源」として
  置いたもの。**新しい判定を書かない**）。
- 拒否は **`ArgumentException`**。`RiskControlEndpoints` のグループフィルタが
  `ArgumentException → 400 { error }` へ写像する（`/settings/guard` と同じ経路。**新しい配線を足さない**）。
- **拒否時は無変更**であること（`Save` を呼ばない＝設定も履歴も一切変わらない）。

### 対象外（意図的にやらない）

- **読み取り側の挙動の変更** —— 黙って `Default` へ倒すのは `IADR-0161` 決定2・#433 で確定済み。
  例外へ倒すと設定行全体が失われる
- **`BrokerProviderResolution` の allow-list の中身の変更**
- **`Enum.IsDefined` への置き換え** —— `IADR-0161` が明示的に禁止している
- **段階ゲートの判定式（`RiskEvaluator`）の変更** —— 現状で正しく安全側に倒れている
- **他の `StageSettings` 項目（資金上限・段階番号）の検証追加** —— 本作業は `Mode` の allow-list だけを扱う。
  他項目に検証が要るなら別 issue
- **新しい拒否コードの追加** —— `BrokerProviderChangeRejection` に列挙子を足さない

## 設計

### なぜサービス層に置くか（エンドポイントではなく）

エンドポイントで検証すると、**サービスを直接呼ぶ経路（別のエンドポイント・将来の消費者）が素通りする**。
本件はまさに「一方の経路にだけ関門があった」ことが問題なので、**関門は全経路の下流に置く**。

`/settings/guard` が同じ形（サービスが `ArgumentException` を投げ、エンドポイントは薄いまま）を採っており、
先例に揃う。

### 応答形式

グループフィルタが返す `400 { error: <例外メッセージ> }` になる。
メッセージは `DescribeBrokerProviderRejection(UnknownProvider)` と**同じ意味の文言**にする
（利用者から見て「どの値を指定すべきか」が読み取れること）。

> **規則の単一情報源は `BrokerProviderResolution.IsKnown` であり、文言ではない。** 文言が 2 か所にあることは
> 許容する（拒否の**条件**が 2 か所にあることとは別問題である）。

## 受け入れ基準

- [ ] 未知の序数（`7` / `-1` / `int` 上限）を `PUT` すると **400** で拒否され、**設定が変わらない**（最重要）
- [ ] 既知の 3 値（`InternalPaper` / `MoomooReal` / `MoomooSimulate`）は**従来どおり受理される**（回帰）
- [ ] 拒否時に**変更履歴が残らない**（拒否された要求を履歴に積むと監査上の事実になる）
- [ ] **読み取り側は変わっていない** —— 既に永続化されている未知の値は依然として `InternalPaper` として
      読まれ、設定行全体が失われない（T-81 / T-83 の回帰）
- [ ] **新しい拒否コードを作っていない**（`BrokerProviderChangeRejection` の列挙子が増えていない）
- [ ] `dotnet build` / `dotnet test` / `dotnet format` が緑

## テスト方針

テストケース ID は `docs/tests/FR-20_staged-gates-tests.md` の続き（**T-105 以降**）を採る
（develop 時点の最大は T-104。着手前に再確認する）。

| 区分 | 内容 |
| --- | --- |
| Application | 未知の序数 3 種で `UpdateStage` が `ArgumentException`・ストアが無変更・履歴 0 件 |
| Application | 既知の 3 値は受理され、段階と既定発注先が保存される（回帰） |
| Domain/Contracts | `BrokerProviderChangeRejection` の列挙子が増えていない（否定形） |
| Infrastructure | 読み取り側の回帰（未知の永続値は `InternalPaper` へ倒れ、設定行は失われない） |

**ミューテーション必須**: `UpdateStage` の検証を外し、「未知の序数を `PUT` すると 400」のテストが
赤くなることを確認する。

## 残余リスク

- **本作業は `Mode` だけを塞ぐ。** `StageSettings` の他項目（`Stage` の序数・資金上限比率）に同型の穴が
  無いかは確認していない。あれば別 issue とする（本作業で範囲を広げると #434 の主張が検証しにくくなる）。
- **画面（SC-02）は既知の 3 値しか送らない**ため、本修正で画面の挙動は変わらない。
  効くのは API 直叩き・外部ツール経由の書き込みだけである。
