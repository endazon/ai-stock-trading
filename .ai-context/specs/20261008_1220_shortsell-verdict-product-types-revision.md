---
title: 空売り解禁 verdict の無効化契機に「取引ガードの商品種別設定の変更」を入れる（商品種別設定の改訂番号。#1220）
type: spec
status: accepted
related_ids: [FR-20, FR-19, UC-06, ADR-0034, ADR-0016, ADR-0011, IADR-0281, IADR-0511, IADR-0132, IADR-0012]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0034_short-sell-inclusion-observed-and-strategy-change.md (決定 5 の表の契機 2)
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md (決定 14 の 2026-08-07 確定・verdict の形式)
---

# 空売り解禁 verdict の無効化契機に商品種別設定の変更を入れる（#1220）

## 背景（issue の観測）

- 計画 ADR-0034 決定 5 は、ADR-0016 決定 14 の verdict 無効化契機「② 戦略の変更」を 2 契機で定めた。
  契機 1（取引判断のピン留めモデルの変更）は Stage 0 の戦略 ID がモデルを含むため成立している。
  **契機 2（FR-19 の取引ガードの商品種別〔現物 / 信用買い / 空売り〕の有効・無効の変更）は判定に入っていない。**
- `ShortSellReleasePolicy.Evaluate`（`Domain/ShortSellRelease.cs`）の判定は ① 未承認 ② 期限切れ ③ 情報源の変更 ④ 戦略 ID の変更 の 4 つだけ。
  `TradingGuardSettings.EnabledProductTypes` はどこにも写し取られていない。空売りを無効化して再度有効化しても verdict は有効のまま。
- 起点: **FR-20 / FR-19 / UC-06 / ADR-0034 決定 5 契機 2**。関連 ADR-0016 決定 14・IADR-0281・#388。

## 受け入れ基準（issue のまま）

1. Given 有効な空売り解禁 verdict がある When 取引ガードの商品種別設定（`EnabledProductTypes`）が verdict の発行後に変わる
   （空売りの無効化 → 再有効化を含む） Then verdict は無効と判定され、空売りの実弾解禁は閉じる（理由が読める状態値で返る。例: `ProductTypesChanged`）。
2. Given 商品種別設定が verdict の発行後に変わっていない When 判定する Then 従来どおり（他の契機が無ければ有効）。
3. 否定形: 判定に必要な情報（発行時点の商品種別設定、または設定の変更時刻）が無い verdict は「変わっていない」と読まず無効へ倒す
   （fail-closed。既存の戦略 ID が空のときの扱いと同じ）。
4. 否定形: プロンプト・方針の変更は契機に入れない（ADR-0034 決定 5 が明示的に除外）。

## 設計判断（IADR-0511）

### 何を写し取るか —— 「商品種別設定の改訂番号」（単調増加カウンタ）

| 案 | 再有効化（無効化→再有効化）を捕まえるか | 評価 |
| --- | --- | --- |
| A. 発行時の `EnabledProductTypes` のスナップショットを verdict に写し、評価時の値と等価比較 | **捕まえない**（往復後の集合は発行時と等しい） | 受け入れ基準 1 を満たさない |
| B. 設定の最終変更時刻を持ち、verdict の発行時刻と比較 | 捕まえる | 発行時刻（`IClock`）と設定の更新時刻（ストアが `DateTimeOffset.UtcNow`）の**時計が別**。同時刻・時計の巻き戻りで順序が決まらない |
| **C（採用）. 商品種別の集合が変わった保存のたびに 1 増える改訂番号を設定ストアが持ち、verdict は発行時の番号を写す。評価時に一致を見る** | 捕まえる（往復で 2 進む） | 時計に依らず、等価比較だけで決まる。往復でも単調に進むため A の穴が無い |

- **改訂番号を進めるのはストア（`IRiskSettingsStore.Save`）である。** 呼び出し側（サービス・エンドポイント）は番号を書けない。
  保存の直前の集合と新しい集合を集合として比べ（`SetEquals`。順序・インスタンスに依らない）、違えば +1。
  経路が将来増えても、保存がストアを通る限り数え漏れない（「関門は全経路の下流に置く」。`UpdateStage` の allow-list と同じ論法）。
- 番号は**設定行の JSON**（`SettingsDto.ProductTypesRevision`）に持つ。`RiskManagementSettings`（ドメインの設定・HTTP 応答）には載せない
  ——載せると `with` で運ばれ、呼び出し側が値を作れてしまう。設定テーブルのマイグレーションは要らない（単一行 JSON のキー追加。IADR-0161 決定2 と同じ規律）。
- **キーを持たない旧行は 0 と読む。** 本変更より前に発行された verdict は番号を持たない（下記）ため、旧行を 0 と読んでも
  「旧い verdict が有効に見える」経路は生じない。デプロイ後に発行した verdict と、デプロイ後の変更の数え方は一貫する。
  - ［2026-10-08 追記 / 監査 F1］**上の「0 と読む」は改めた**（IADR-0511 決定 2 の改訂）。切り戻した旧版がキーを落として書いた行も 0 と読まれ、
    0 で発行した verdict と決定的に一致するため。判定用の読み取りはキーの無い行を `null`（`ProductTypesUnknown`）と読み、
    発行用の `EnsureProductTypesRevision` と `Save` は同じ行へ**行の版**を新しい番号として刻む（初期値 1。不変条件「番号 ≦ 行の版」）。
    旧版の書き込みも版を進めるため、刻み直した番号は発行済みのどの番号よりも大きい。デプロイ直後も手で設定を保存せずに verdict を発行できる。
- verdict の台帳（`stage_transitions`）に列 `ShortSellReleaseProductTypesRevision`（`bigint` nullable）を足す（EF マイグレーション）。
  verdict の行だけが値を持ち、段階遷移の行は null。

### 判定

- 状態値を 2 つ末尾へ足す（序数は HTTP で往来する。`StageGateCriterion` と同じ規律）:
  `ProductTypesChanged = 5`（番号が違う）・`ProductTypesUnknown = 6`（verdict 側か現在側の番号が無い＝fail-closed）。
- 判定順は ① 未承認 ② 期限切れ ③ 情報源 ④ 戦略 ⑤ 商品種別。⑤ は ④ と同じ「② 戦略の変更」の契機 2 である。
- `EfStageGateStore` は**番号の列だけが null の旧 verdict の行も添付つきで復元する**（添付ごと落とすと `Missing` になり、
  「番号が無いから無効」という理由が読めなくなる）。
- **プロンプト・方針は判定の入力に無い**（`Evaluate` の引数に現れない）。リスク上限・段階・発注先・禁止銘柄・市場など
  商品種別以外の設定変更は番号を進めない。

### デプロイ時の影響（ライブ PoC）

- 既存の verdict（本変更より前に発行された行）は番号の列が null であり、**デプロイ直後に `ProductTypesUnknown`（無効）になる**。
  解禁を続けるには利用者が verdict を再発行する（`POST /risk-controls/stage-gate/transition` の `approval=1`）。
- **ただし発注審査は今日 `StageReleaseContext` を受け取っておらず（IADR-0281 決定6）、Stage 3 の空売りは既に常に拒否である。**
  したがって発注の挙動は変わらない。変わるのは `GET /risk-controls/stage-gate`（SC-03・Discord の現況）の verdict の状態表示だけ。
- ライブのデータは書き換えない（マイグレーションは列の追加のみ。既存行は null のまま）。

## 母集合（規則 9・10。誤りの側の文字列で全走査）

走査: `git grep -n "ShortSellReleasePolicy.Evaluate\|StageReleaseContext(\|ShortSellReleaseAttestation(\|ShortSellReleaseVerdict(\|ShortSellReleaseState(\|IRiskSettingsStore" -- backend frontend`（origin/develop facba5c1）。

| # | 場所 | 対応 |
| --- | --- | --- |
| P1 | `Domain/ShortSellRelease.cs` | 添付・verdict に番号、状態値 2 つ、`Evaluate` に現在の番号 |
| P2 | `Domain/StageProductPolicy.cs` `StageReleaseContext` | 現在の番号を必須メンバに足す（既定値なし） |
| P3 | `Domain/StageGateLedger.cs` | verdict の復元に番号を通す |
| P4 | `Domain/ProductTypeSettingsRevision.cs`（新規） | 番号を進める純関数 |
| P5 | `Features/RiskManagement/IRiskSettingsStore.cs` | `GetProductTypesRevision()` |
| P6 | `Infrastructure/Persistence/InMemoryRiskSettingsStore.cs` / `EfRiskSettingsStore.cs` / `SimulatorProfileRiskSettingsStore.cs` | 保存時に番号を進める・読む（デコレータは素通し） |
| P7 | `Infrastructure/Persistence/RiskSettingsSerialization.cs` | JSON のキー `productTypesRevision` |
| P8 | `Infrastructure/Persistence/PersistenceRows.cs` / `EfStageGateStore.cs` / マイグレーション | 台帳の列 |
| P9 | `Features/RiskManagement/StageGateService.cs` / `StageGateStatus.cs` | 発行時に番号を写す・評価に渡す・現況に現在の番号を返す |
| P10 | テストの偽物（`BrokerProviderAlignmentTests.ThrowingSettings`・`SimulatorProfileRiskSettingsStoreTests.RecordingStore`） | インターフェースの追加メンバ |
| P11 | テストの構築点（`ShortSellReleaseFixtures`・`ShortSellReleaseVerdictTests`・`ShortSellReleaseVerdictRideAlongTests`・`RiskBotReadGrpcTests`・Notification の `GrpcBotReadsTests` / `OperationReadContractTests`・Report の `RiskManagementStageReadContractTests`〔初回の走査で漏れ、ソリューションのビルドで検出〕） | 新しい引数 |
| P12 | `frontend/src/lib/risk/contracts.ts`・契約フィクスチャ `risk-controls.stage-gate.json` | 型・状態の表示ラベル 5 / 6 |

除外: proto（`risk_read.proto` 等）は verdict の状態を運ばない（`StageTransitionKind` だけ）——変更なし。

## テスト（T-20 の帯。`check-test-traceability` の実測で最大 T-20-3 → T-20-4 から採る）

| ID | 内容 | 受け入れ基準 |
| --- | --- | --- |
| T-20-4 | 番号が違えば期限内でも `ProductTypesChanged`、空売りは開かない | 1 |
| T-20-5 | 無効化→再有効化（集合は発行時と同じ）でも無効（サービス結線・インメモリストア） | 1 |
| T-20-6 | 番号が同じなら有効（他の契機なし） | 2 |
| T-20-7 | verdict 側／現在側の番号が無ければ `ProductTypesUnknown`、空売りは開かない | 3 |
| T-20-8 | 旧 verdict の行（番号の列が null）を EF ストアから読むと添付つきで復元され `ProductTypesUnknown` | 3 |
| T-20-9 | 商品種別以外の設定変更（上限・禁止銘柄・市場・段階）は番号を進めず verdict は有効のまま。`Evaluate` の入力にプロンプト・方針が無い | 4 |
| T-20-10 | 番号はストアが進める（集合の比較は順序に依らない・往復で 2 進む・EF で永続化・キーの無い旧行は 0） | 1・3 |
| T-20-11 | ［2026-10-08 追記 / 監査 F1］キーの無い旧行で発行 → 旧版がキーを落として無効化・再有効化 → 評価は無効（`ProductTypesUnknown`、刻み直した後は `ProductTypesChanged`）。番号 ≦ 行の版 | 1・3 |

## 検証

- `dotnet build backend/backend.slnx`・RiskManagementService / NotificationService のテスト・`dotnet format --verify-no-changes`
- `ci.yml` の node 検査（`scripts.test.js`・trace-blocks・knowledge-graph・test-traceability・adr-index-sync・addendum-loss・doc-links・reading-budget・commit-messages）
- frontend の `typecheck` / 契約テスト（契約フィクスチャを再生成するため）
