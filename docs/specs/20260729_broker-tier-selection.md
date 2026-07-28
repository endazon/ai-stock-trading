---
title: ブローカー選択の階層化（provider × environment）と任意切替
type: spec
status: review
related_ids: [FR-05, FR-12, FR-20, ADR-0002]
author: endazon (with Claude Code)
created: 2026-07-29
updated: 2026-07-29
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# 仕様書: ブローカー選択の階層化（provider × environment）と任意切替

> 利用者指示（2026-07-29）。運用の本番近接順に **paper ＜ シム（moomoo / 他証券） ＜ 実弾（moomoo / 他証券）**
> の 3 階層とし、**それぞれ設定で任意に切り替えられる**ようにする。paper 検証は今後も残す。
>
> **本作業で実弾（live）は解禁しない。** live 階層は「型として表現できるが到達不能」であり、
> 既存の SIMULATE 固定 4 層は**一行も触らない**。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-05（発注執行）、FR-12（ペーパートレード）、FR-20（段階ゲート＝実弾到達の統制）
- ADR: [ADR-0002](../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md)（証券会社連携＝moomoo 第一候補・**Proposed**。
  「立花証券 e支店 API は日本株の冗長系として将来追加する価値がある（`IBrokerAdapter` で抽象化済みのため追加は容易）」と明記）
- 関連 IADR: [IADR-0016](../adr/IADR-0016_safe-broker-execution.md)（安全既定 paper・実弾防止の二重ゲート）／
  [IADR-0056](../adr/IADR-0056_moomoo-simulate-poc-complete-real-gated.md)（SIMULATE 限定・**§3 実弾解禁前提**）／
  [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)（OpenD 本番化・決定 5＝第三の閂）／
  [IADR-0092](../adr/IADR-0092_reservation-broker-probe-moomoo.md)（moomoo リコンサイルプローブ）／
  本作業で新規 [IADR-0111](../adr/IADR-0111_broker-tier-selection.md)
- 運用手順書: [live-trading-cutover-runbook](../operations/live-trading-cutover-runbook.md)
- 対象 Issue: [#267](https://github.com/endazon/ai-stock-trading/issues/267)（`Refs #267`）

## 現状（この変更の直前・実コードで確定）

| 面 | 実態 |
| --- | --- |
| `BrokerFactory.Create` | `Broker:Provider` を `paper` / `moomoo` の 2 分岐で switch。未知は起動時停止 |
| `Program.cs` | `BrokerFactory.IsMoomoo(config["Broker:Provider"])` を **3 箇所**で個別に文字列読みして分岐 |
| introspection | `AddPort("broker", config["Broker:Provider"] ?? "paper")` ＝生の provider 文字列を自己申告 |
| Helm | `moomoo.enabled`（bool）→ `Broker__Provider: {{ ternary "moomoo" "paper" }}` |
| `TrdEnv` | 4 層で SIMULATE 固定（下表「閂」参照） |

**問題**: 「証券会社（provider）」と「取引環境（sim / 実弾）」という本来直交する 2 軸が、単一の bool・
単一の文字列に潰れている。そのため (1) 運用の本番近接順という階層が設定の語彙に存在せず、
(2) 証券会社の追加が provider 文字列の増殖になり、(3) 実弾解禁時に「どこを開けるのか」が
`Broker:Provider` と `Broker:Moomoo:TrdEnv` に分散して解禁の単一責務が定まらない。

## 目的

1. provider × environment の行列でブローカーを選択でき、3 階層（paper ＜ sim ＜ live）を設定で任意に切り替えられる。
2. 将来の証券会社追加が**最小差分**（enum 1 値 ＋ アダプタ ＋ switch 1 腕 ＋ Helm の tier 値 1 つ）で済む。
3. **live は構造だけ用意し到達不能のまま**。既存の実弾防止をひとつも temper しない。
4. 既存の moomoo SIMULATE 経路を新体系へマッピングして温存し、**本番 values の描画をバイト等価**に保つ。
5. fail-safe 既定＝未設定は paper、不正は起動時停止（黙って発注可能な状態にしない）。

## 設計

### 1. 型（`OrderExecutionService.Worker` に閉じる。`Shared.Contracts` 不変・新規イベント無し）

```
enum BrokerProvider    { Paper, Moomoo }        // 将来 Tachibana 等を追加
enum BrokerEnvironment { Simulated, Live }      // Live = 実弾

sealed record BrokerSelection(BrokerProvider Provider, BrokerEnvironment Environment)
    static BrokerSelection FromConfiguration(IConfiguration)   // 正規化・検証の単一入口
    bool   IsLive  => Environment == BrokerEnvironment.Live
    bool   IsMoomoo => Provider == BrokerProvider.Moomoo
    string Tier    => "paper" | "moomoo-sim" | "moomoo-live"   // 正準名（introspection / ログ）
```

`Tier` の命名がそのまま本番近接順（`paper` ＜ `<broker>-sim` ＜ `<broker>-live`）を表す。
paper は environment 非該当（内蔵擬似）であり `Tier` は常に `paper`。

### 2. 設定キー

アプリ側は **2 キー**（直交軸を潰さない）、Helm 側は **単一 value**（運用者が触るスイッチは 1 つ）。

| 層 | キー | 既定 | 受理値 |
| --- | --- | --- | --- |
| App | `Broker:Provider`（**既存キー据置**） | `paper` | `paper` \| `moomoo` |
| App | `Broker:Environment`（新規） | `sim` | `sim` \| `live` |
| Helm | `broker.tier`（新規・単一スイッチ） | `""`＝非推奨エイリアスから導出（既定は `paper`） | `paper` \| `moomoo-sim` \| `moomoo-live` |

`Broker:Tier` の単一キーにしなかった理由: provider 軸は将来増え、environment 軸は 2 で固定である。
単一文字列にすると直積の列挙になり、証券会社を足すたびに parse 側が増える。逆に Helm 面は運用者の
誤設定を減らすため単一 tier に畳み、template が 2 つの環境変数へ展開する。

`moomoo.enabled` は**非推奨エイリアス**として温存する。`broker.tier` 未指定（`""`）で `moomoo.enabled=true`
なら `moomoo-sim` として描画し（既存構成が壊れない）、両者を矛盾指定したら描画時 `fail` で止める。

### 3. fail-safe（すべて「発注抑止」側へ倒れる）

| 入力 | 挙動 |
| --- | --- |
| `Broker:Provider` 未設定/空 | `paper`（既存踏襲） |
| `Broker:Environment` 未設定/空 | `Simulated` |
| 未知の provider | **起動時停止**（既存踏襲） |
| 未知の environment 値 | **起動時停止**（黙って sim へ倒さない＝誤設定を隠さない） |
| `paper` ＋ `live` | **起動時停止**（「実弾のつもりで擬似発注」を作らない） |
| `moomoo` ＋ `live` | **起動時停止**（閂 0＝`LiveTradingGate`。本 PR では常に停止する） |

大小文字・前後空白は正規化する（`Trim().ToLowerInvariant()`。既存 `BrokerFactory.Normalize` と同じ流儀）。
未知値・矛盾を例外にする方針は既存 `MoomooBrokerOptions.EnsureSimulate` / `ParseReplyTimeout` と同一で、
「黙って安全側へ倒す」より「誤認を起動時に表面化させる」を選ぶ house style に従う。

### 4. live ガードの位置（多重・既存を一切 temper しない）

新設は**閂 0** と **Helm 外周**のみ。既存の閂 1〜4 は一行も触らない。

| # | 閂 | 実体 | 実装箇所 | 本 PR |
| --- | --- | --- | --- | --- |
| **0（新）** | 解禁ゲート | `const bool LiveTradingReleased = false;`。live 選択時は IADR-0056 §3 の前提を列挙して起動時停止 | `LiveTradingGate` | **新設** |
| 1 | provider ゲート | 既定 paper・未知は停止 | `BrokerFactory` | 据置（型経由に移行） |
| 2 | ヘッダ固定 | `SetTrdEnv(TrdEnv_Simulate)` ハードコード | `MMApiMoomooTradeClient.BuildHeader` | **不変** |
| 3 | config 拒否 | `Broker:Moomoo:TrdEnv` は `simulate` のみ受理 | `MoomooBrokerOptions.EnsureSimulate` | **不変** |
| 4 | 口座選択 | SIMULATE 口座のみ採用 | `MMApiMoomooTradeClient.FetchSimulateAccIdAsync` | **不変** |
| **外周（新）** | Helm 描画 | `broker.tier=moomoo-live` は描画時 `fail`＝クラスタに届かない | `deployment.yaml` | **新設** |

閂 0 は `Program.cs` の合成起点（`BrokerSelection` を組み立てた直後）と `BrokerFactory.Create` の入口の
双方で作動する。前者により `moomoo-live` を設定しても OpenD への接続も `IBrokerAdapter` の生成も起きず、
後者により合成起点を経由しない呼び出し（テスト・将来の呼び出し点）でも live はアダプタを得られない。

**将来の解禁は `LiveTradingReleased` を `true` にする 1 ファイルの変更に集約される**（別 IADR が必要）。
これはコード comment と PR 本文の双方に明記する。

### 5. 影響範囲

- `backend/Services/OrderExecutionService/src/OrderExecutionService.Worker/Composable/Adapters/BrokerSelection.cs`（新規）
- 同 `LiveTradingGate.cs`（新規）
- 同 `BrokerFactory.cs`（`BrokerSelection` ベースへ）
- 同 `Program.cs`（3 箇所の文字列読み → 単一パース。introspection を `Tier` へ）
- `deploy/helm/ai-stock-trading/templates/deployment.yaml` / `values.yaml`（`broker.tier` 追加・エイリアス）
- `.github/workflows/helm.yml`（3 系統の検査を追加）
- `docker-compose.yml`（`Broker__Environment` の設定点）
- `docs/operations/live-trading-cutover-runbook.md`・`deploy/helm/ai-stock-trading/README.md`（閂表の追随）

## テスト（TDD・受け入れ基準の写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | provider × env が独立に選択できる | `BrokerSelectionTests`: 行列の各組を parse して `Provider`/`Environment`/`Tier` を検証 |
| 2 | 未設定は paper / sim | `BrokerSelectionTests`: 空・null・空白の既定 |
| 3 | 不正値は起動時停止 | `BrokerSelectionTests`: 未知 provider・未知 environment が `InvalidOperationException` |
| 4 | `paper`＋`live` は拒否 | `BrokerSelectionTests`: 例外メッセージに両キー名を含む |
| 5 | live は到達不能 | `LiveTradingGateTests`: `LiveTradingReleased` が false／live で例外／sim・paper は no-op。`BrokerFactoryTests`: `moomoo-live` は `IBrokerAdapter` を生成しない |
| 6 | 閂 2〜4 不変 | 既存 `MoomooBrokerOptionsTests` を**無改変**で緑（差分ゼロを PR 本文で提示） |
| 7 | Helm 描画 | `helm.yml`: 既定 `paper` 不変／各 tier の描画／`moomoo-live` は描画失敗 |

## 受け入れ基準チェック

- [x] provider（`paper`/`moomoo`/将来）と environment（`sim`/`live`）が独立した型として表現されている
- [x] 単一のスイッチ体系（アプリ 2 キー ＋ Helm 単一 `broker.tier`）で 3 階層を切り替えられる
- [x] 未設定は `paper`、不正な provider / environment は起動時に明示エラーで停止する
- [x] `paper` と `live` の同時指定は起動時に拒否する
- [x] live 階層を選んでも発注に到達しない（閂 0 ＋ 既存 4 層 ＋ Helm 外周）
- [x] 既存の `moomoo.enabled=false`（既定）構成が描画バイト等価（Helm CI で検証）
- [x] `live-trading-cutover-runbook.md` の閂表が新体系に追随している
- [x] `dotnet build` / `dotnet test` / `dotnet format` green・CI green

## スコープ外

- 実弾（live）の解禁そのもの。IADR-0056 §3 の前提充足判断も本作業では行わない。
- 既存 SIMULATE 固定 4 層の緩和（**一行も触らない**）。
- `Shared.Contracts` の変更・新規イベント・`TradeMode`（FR-12/FR-20 の段階概念）の変更。
- 実際の他証券アダプタ（立花証券等）の実装。本作業は追加が最小差分になる形を用意するに留める。
