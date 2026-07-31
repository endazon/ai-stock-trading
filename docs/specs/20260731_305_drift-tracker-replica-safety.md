---
title: 建玉乖離トラッカーの状態を DB 単一行へ durable 化し、複数レプリカでも報告が止まらないようにする
type: spec
status: accepted
related_ids: [FR-05, FR-09, FR-10, FR-11, NFR, UC-02, ADR-0002]
author: endazon (with Claude Code)
created: 2026-07-31
updated: 2026-07-31
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# 仕様書: 建玉乖離トラッカーのレプリカ安全化

> 利用者指示・設計承認（2026-07-31）。[#305](https://github.com/endazon/ai-stock-trading/issues/305)。
> [#297](https://github.com/endazon/ai-stock-trading/pull/297)（IADR-0118）の AI レビューが 🟡 として指摘した
> follow-up。**検知・記録・通知のみという IADR-0118 の設計は一切変えない**（是正は #304 の領分）。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-05（発注・注文状態の追跡。IADR-0118 と同じ拡張解釈）／FR-10（リスク統制＝乖離は
  統制の入力である台帳を狂わせる）／FR-11（監査）／FR-09（通知）／NFR（可用性・水平スケール）
- ADR: [ADR-0002](../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md)（証券会社連携）
- 関連 IADR: [IADR-0118](../adr/IADR-0118_broker-position-reconciliation.md)（建玉突合・本作業が前提節を置き換える）／
  [IADR-0085](../adr/IADR-0085_paper-withdrawal-notification-dedup.md)（durable な通知重複排除・**本作業が倣う型**）／
  [IADR-0106](../adr/IADR-0106_consumer-endpoint-name-uniqueness.md)（MassTransit のキュー名＝競合コンシューマの構造）／
  [IADR-0012](../adr/IADR-0012_risk-settings-persistence.md)（単一行＋`Version` 楽観的排他）／
  本作業で新規 [IADR-0124](../adr/IADR-0124_position-drift-state-durable.md)
- 対象 Issue: [#305](https://github.com/endazon/ai-stock-trading/issues/305)（`Refs #305`）・
  親 [#292](https://github.com/endazon/ai-stock-trading/issues/292)

## 現状（この変更の直前・実コードで確定）

| 面 | 実態 |
| --- | --- |
| `PositionDriftTracker` の登録 | `Program.cs:173` `builder.Services.AddSingleton<PositionDriftTracker>()` |
| 状態 | プロセス内の 3 フィールド（`_observedSignature` / `_consecutive` / `_reportedSignature`）＋ `lock` |
| 購読 | `BrokerPositionsObservedConsumer`。MassTransit 既定の `DefaultEndpointNameFormatter` はキュー名を **consumer クラス名**から導く（IADR-0106） |
| レプリカ | `deploy/helm/ai-stock-trading/templates/deployment.yaml:46` が `replicas: 1` 固定（value 化されていない） |
| 同種の重複排除 | `IWithdrawalNotificationStore`（IADR-0085）は **DB 単一行**。本トラッカーだけがインメモリという非対称 |

### 壊れ方（#305 の本体）

`replicas` を 2 以上にすると、同一キューを複数 Pod が consume する **競合コンシューマ**になる。観測が Pod へ
ラウンドロビンで分散し、各 Pod の連続カウンタが 1 のまま既定 `required = 2` に到達しない。結果、

- 例外は出ない
- ログにも異常が残らない
- **乖離が恒久的に未報告のまま**になる

IADR-0118 の受け入れ基準①「乖離が定期的に検知され、監査・通知へ届く」を**無言で**損なう。統制系で最も避けたい
失敗の形である。

### #305 の受け入れ基準のうち既に満たされているもの

- ①「単一レプリカ前提の明記」は **IADR-0118 の「状態をインメモリに置く理由と、その前提」節で既に満たされている**。
  本作業はその節を置き換える（前提そのものを消す）。
- 残るのは ②（レプリカを増やしても無言で止まらない）と ③（方針を選んだ理由の記録）。

## 目的

1. 乖離検知の追跡状態を**レプリカ間で一貫**にする。
2. `replicas > 1` でも乖離が**取りこぼされない**ことを回帰テストで固定する。
3. 単一レプリカ前提という**暗黙依存を、DB の並行トークンという明示的な保証へ**置き換える。
4. IADR-0118 の判定意味論（連続 2 回・順序非依存シグネチャ・解消で忘れる・是正しない）を**一切変えない**。

## 設計

### 1. 状態を DB 単一行へ移す（`IWithdrawalNotificationStore` と同型）

```
BrokerPositionsObservedConsumer (scoped / 1 メッセージ 1 スコープ)
  └ PositionDriftTracker (scoped)
       ├ IPositionDriftStateStore.Get()          ── position_drift_state（単一行・Id=1）
       ├ PositionDriftDecision.Decide(...)        ── 純関数（連続条件・dedup・解消）
       └ IPositionDriftStateStore.TrySave(next)   ── Version 不一致なら false
```

| 型 | 場所 | 役割 |
| --- | --- | --- |
| `PositionDriftState` | `Application/Ports` | `(ObservedSignature, ConsecutiveCount, ReportedSignature, Version)`。`Version` は**不透明な並行トークン**で判定には使わない |
| `IPositionDriftStateStore` | `Application/Ports` | `PositionDriftState Get()` / `bool TrySave(PositionDriftState)` |
| `PositionDriftDecision` | `Application/Services` | 純関数 `Decide(current, signature, required) → (Next, ShouldReport)`。IADR-0118 の判定をそのまま移設 |
| `PositionDriftTracker` | `Application/Services` | singleton → **scoped**。`Get → Decide → TrySave` を束ねる。シグネチャ正準化と下限 1 丸めは据え置き |
| `InMemoryPositionDriftStateStore` | `Application/Adapters` | ユニット試験・非 relational 用（**版意味論も実装する**＝競合を再現できる） |
| `PositionDriftStateRow` / `EfPositionDriftStateStore` | `Worker/Foundation/Persistence` | 単一行（`Id = SingletonKeys.Id`）。`Version` を `IsConcurrencyToken` |

### 2. 競合時の意味論（本設計の肝）

`TrySave` が `false`（＝別レプリカが先に状態を進めた）を返したら、**その観測は捨てて報告しない**。リトライしない。

これが安全である理由:

- 競合しても**必ずどれか 1 つは勝つ**。したがって状態は**単調に前進**し、止まらない。
- 捨てた側の観測は次の巡回（既定 600 秒）で**再観測される**。乖離は解消するまで毎回観測されるため、失うのは
  最大 1 巡回分の時間であって、報告そのものではない。
- 二重報告も起きない。`ReportedSignature` の dedup は DB 側にあり、勝った側だけが「報告済み」を刻む。

リトライしない理由: 同一 `DbContext` で再読込するには `ChangeTracker.Clear()` が要り、同じスコープで台帳を
読んでいる consumer の追跡エンティティまで巻き込む。得られるのは「最大 1 巡回の短縮」だけで割に合わない。
競合は `LogDebug` で観測可能にする（無言にしない）。

あわせて、乖離が解消したときは**連続回数も 0 へ戻す**（「何の」連続かが無いため）。これで乖離ゼロが続く間は
状態が完全に不変になり、トラッカーは「変化なし＝書かない」で巡回ごとの無駄な永続化を避けられる。

### 3. 判定意味論は変えない（既存テストが同値で通ることが証拠）

`PositionDriftDecision` は現行 `ShouldReport` の中身をそのまま移したもの。単一トラッカー＝単一レプリカでの
振る舞いは**同値**であり、IADR-0118 で書いた既存 9 テストは store 注入へ置換するだけで全て緑のまま通る。

**変わる 1 点**: 再起動をまたいで連続カウントと報告済みが保持される。IADR-0118 は「再起動後に 1 度だけ
再報告され得るが許容」としていたが、durable 化でその再報告も消える（改善方向・IADR-0124 に記録）。

### 4. 変えないもの

- **是正しない**（検知・記録・通知のみ）。自動で建玉を合わせにいく発注経路は作らない（#304 の領分）。
- 連続回数 `N`（既定 2）は構成キーにしない。
- `Shared.Contracts` 不変・新規イベント無し・Helm / values / compose / `.env.example` 不変。
- 実弾ゲート（閂 0〜4）差分ゼロ。ブローカ呼び出しを 1 つも増やさない。SIMULATE 限定・実弾 OFF 不変。

### 5. 非目標（正直に書く）

- `deploy/helm/.../deployment.yaml` の `replicas: 1` は**変えない**。本作業はこのコンポーネントの無言縮退を
  消すだけで、リスク管理サービス全体の水平スケールを保証するものではない。
- ただし調査の結果、リスク管理サービスに残る他の跨ぎ状態は既に durable か冪等である。

| 常駐・シングルトン | レプリカ跨ぎの扱い |
| --- | --- |
| `WithdrawalEvaluationService` | `IWithdrawalNotificationStore`（DB 単一行・IADR-0085） |
| `ObservedDrawdownRefreshService` | DB の単調 latch（IADR-0103） |
| `QuoteCache` | 純キャッシュ（Pod ごとに持っても判定は変わらない・API 呼び出しが増えるだけ） |
| `PositionDriftTracker` | **判定を担う唯一のインメモリ跨ぎ状態＝本作業の対象** |

- 発注執行側 `BrokerPositionSnapshotService` を `replicas > 1` にすると 1 巡回に複数の観測が出て連続条件が
  早く満たされる＝一過性フィルタが弱まる。**無言の停止とは逆向き**（報告が増える側）のため本作業では扱わない。

## 影響範囲

| 対象 | 変更 |
| --- | --- |
| `RiskManagementService.Application/Ports` | `PositionDriftState` / `IPositionDriftStateStore`（新規） |
| `RiskManagementService.Application/Services` | `PositionDriftDecision`（新規・純関数）、`PositionDriftTracker`（状態を捨てストア経由へ） |
| `RiskManagementService.Application/Adapters` | `InMemoryPositionDriftStateStore`（新規） |
| `RiskManagementService.Worker/Foundation/Persistence` | `PositionDriftStateRow` / `EfPositionDriftStateStore`（新規）、`DbContext` に DbSet ＋ マッピング |
| `RiskManagementService.Worker/Migrations` | `AddPositionDriftState`（**新規テーブル 1 つ**） |
| `RiskManagementService.Worker/Program.cs` | `AddSingleton<PositionDriftTracker>` → `AddScoped` 2 本 |
| `RiskManagementService.Worker.csproj` | `InternalsVisibleTo` に `AiStockTrading.IntegrationTests` を追加（実 DB での並行制御検証のため永続化層を公開） |
| `AiStockTrading.IntegrationTests` | `PositionDriftStateConcurrencyE2ETests`（`Category=Integration`・実 PostgreSQL） |
| `Shared.Contracts` / イベント | **不変**（新規イベント無し・baseline / URN 固定に差分なし） |
| Helm / values / compose / `.env.example` / 構成キー | **不変**（設定点を 1 つも足さない） |
| 実弾ゲート（閂 0〜4） | **不変** |

## テスト（受け入れ基準の写像）

| # | 観点 | テスト |
| --- | --- | --- |
| R-1 | **#305 の回帰** | 2 つのトラッカー（＝2 レプリカ）が 1 つのストアを共有すると、A の 1 回目は報告せず **B の 2 回目で報告する** |
| R-2 | レプリカ跨ぎの dedup | A が報告した後、B が同一シグネチャを観測しても再報告しない |
| R-3 | レプリカ跨ぎの解消・再発 | A で解消を観測 → B で同一乖離が再発 → 連続 2 回で再び報告する |
| R-4 | 競合で止まらない | `TrySave` が 1 度だけ失敗するストアで、負けた観測は報告せず**次の観測で報告できる** |
| R-5 | 競合の観測可能性 | 競合時に `LogDebug` が出る（無言にしない） |
| R-6 | 永続の往復 | `EfPositionDriftStateStore` が状態を保存し、別 `DbContext` から読み戻せる |
| R-7 | 版不一致 | 古い `Version` での `TrySave` が `false` を返す（ロストアップデートを起こさない） |
| R-8 | 初期状態 | 未記録の行は「未観測・カウント 0・未報告」を返す（fail-safe＝報告しない側ではなく**数え直す**側） |
| R-9 | 無駄な書き込み | 乖離ゼロが続く巡回では状態が不変＝**版が進まない**（10 分ごとに DB を叩かない） |
| R-10 | **実 DB での並行制御** | 実 PostgreSQL（Testcontainers・`Category=Integration`）で ①同版からの同時保存は片方だけ勝ち版が 1 回だけ進む ②**初回行の同時 INSERT（23505）** も例外を漏らさず false ③古い版では何も書かない |
| 既存 1-9 | 意味論不変 | `PositionDriftTrackerTests` の 9 件（連続条件・dedup・解消・再発・順序非依存・回数構成・下限丸め）が全て緑 |
| 既存 | consumer | `BrokerPositionsObservedConsumerTests` が全て緑（DI が scoped になっただけ） |

R-6〜R-9 は EF Core InMemory provider で、R-10 は**実 Npgsql** で検証する。本作業の安全性は最終的に
Npgsql が発行する `UPDATE ... WHERE Id=1 AND Version=@original` の実挙動に依存するため、InMemory だけに
閉じない（他の単一行ストアの慣習より一段厚くする）。とくに**初回行の同時 INSERT は InMemory では到達できない**
（共有ストアのため後発の `Find` が必ず行を見つける）ので、実 DB でのみ固定できる。統合テストは既定 CI から
`Category!=Integration` で除外され、`integration.yml`（nightly / dispatch・Docker 前提）で実走する。

## 受け入れ基準（`docs/DEFINITION_OF_DONE.md` と併せて）

- [x] `PositionDriftTracker` の状態がレプリカ間で一貫している（DB 単一行＋並行トークン）
- [x] レプリカを増やしても乖離報告が無言で止まらないことがテストで固定されている（R-1・R-4）
- [x] 単一レプリカ前提の暗黙依存が明示的な保証（DB の `IsConcurrencyToken`）へ置き換わっている
- [x] 方針の理由（とくに競合時にリトライしない根拠）が IADR-0124 に記録されている
- [x] IADR-0118 の該当節が本決定で置き換えられたことが追記されている
- [x] IADR-0118 の判定意味論・是正しない方針が不変（既存テストが緑）
- [x] SIMULATE 限定・実弾 OFF・Helm / values / 構成キーが不変
- [x] `dotnet build` / `dotnet test` / `dotnet format` が green・CI / gitleaks が green

## スコープ外

- 乖離の**自動是正**（#304）
- `replicas` の value 化・HPA・リスク管理サービス全体の水平スケール認定
- 発注執行側 `BrokerPositionSnapshotService` の多重化時の挙動
- `docs/functional/FR-10_risk-controls.md` / `docs/tests/FR-10_risk-guard-core-tests.md` の更新。両文書は
  建玉突合そのものを記述しておらず（#297 でも未更新）、本作業は内部状態の一貫性で**機能面の記述に変更がない**ため
  更新しない。突合機能そのものの記述は IADR-0118 と #297 の作業仕様書が正の記録である
  （CLAUDE.md「必須仕様書の必須範囲」の運用に従う）。
