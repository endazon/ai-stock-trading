---
title: 作業仕様書 — 「供給が無い値」の表示規約を全画面（SC-01 / SC-02 / SC-03）へ適用し、供給なし・対象なし・値が 0 を区別する
type: work
status: review
related_ids: [SC-01, SC-02, SC-03, FR-10, FR-13, FR-17, FR-20, UC-06, ADR-0016, ADR-0019, IADR-0146, IADR-0154, IADR-0155, IADR-0159, IADR-0162]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
related_specs:
  - ../adr/IADR-0162_unsupplied-metric-display-convention-all-screens.md
  - ../adr/IADR-0154_supply-availability-declared-by-server.md
  - ../adr/IADR-0146_backend-response-contract-fixtures.md
  - ../adr/IADR-0155_sc01-collection-parameters-supply.md
  - ../adr/IADR-0159_buy-in-post-hoc-inference.md
  - ../../docs/screens/20260718_SC-01_settings.md
  - ../../docs/screens/20260718_SC-02_risk-settings.md
  - ../../docs/screens/20260718_SC-03_control-status.md
  - ../../docs/tests/FR-10_risk-controls-tests.md
  - ../../docs/blocked-tasks.md
  - ../../docs/DEFINITION_OF_DONE.md
---

# 作業仕様書: 「供給が無い値」の表示規約を全画面へ適用する（#424）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: **SC-01 / SC-02 / SC-03**（05_screens「供給が無い値の表示規約（共通・2026-08-07 追加）」・
  利用者裁定 質問票 第 13 回 Q9・Q10。環流 project-planning#221）
- 機能要求（FR）: **FR-10**（リスク統制・空売り統制）／**FR-20**（段階ゲート）／FR-13・FR-17（設定の閲覧・変更）
- ユースケース（UC）: **UC-06**
- 関連 ADR: **ADR-0016 決定7**（維持率。2026-08-07 追記＝**Stage 1 の全期間にわたって表示できない**）／
  **ADR-0016 決定15**（強制買戻しの発生回数の集計元）／**ADR-0019** PoC 項目 3（SIMULATE では照会 API 自体が失敗）
- 実装 ADR: **[IADR-0162](../adr/IADR-0162_unsupplied-metric-display-convention-all-screens.md)（本作業）**／
  [IADR-0154](../adr/IADR-0154_supply-availability-declared-by-server.md)（`MetricAvailability` の 3 値・サーバ宣言）／
  [IADR-0146](../adr/IADR-0146_backend-response-contract-fixtures.md)（契約フィクスチャ）／
  [IADR-0155](../adr/IADR-0155_sc01-collection-parameters-supply.md)（収集間隔に供給が無い）／
  [IADR-0159](../adr/IADR-0159_buy-in-post-hoc-inference.md)（強制買戻しの事後推定）
- 起点 issue: [#424](https://github.com/endazon/ai-stock-trading/issues/424)
- 計画 submodule: **`a4616a8`**（作業中に `06fa163` → `a4616a8` へ更新された。差分は ADR-0004/0021/0022/0023 の
  レビュー指摘是正と実装 IADR 対応表の再生成であり、**#424 の範囲に影響する新しい裁定は無い**）

## 目的・背景

計画 05_screens に「供給が無い値の表示規約（共通）」が新設された。

> **供給が無い値は「0」「—」で表示しない。**「取得できていません（供給元がありません）」と明示し、
> **「概念が成立しない（対象なし）」と区別する。** とくに統制の現況を表す値は、**供給が無いこと自体が
> 「統制が働いていない」という運用上の事実**であり、正常値と同じ見た目にしてはならない。
> **供給可否はサーバ側が宣言し、画面はそれに従う**（値の有無をクライアントが推測しない）。

| 状態 | 意味 | 表示 |
| --- | --- | --- |
| **供給が無い**（`NotSupplied`） | 供給元そのものが存在しない・取得に失敗した | **「取得できていません（供給元がありません）」** |
| **対象なし**（`NotApplicable`） | 概念は成立するが該当が無い | 「—」または「対象なし」 |
| **値が 0**（`Available` かつ 0） | 正当な測定結果としての 0 | **「0」**（正常値として表示する） |

**型は既にある。** [#412](https://github.com/endazon/ai-stock-trading/issues/412)（IADR-0154）が
`MetricAvailability`（`Available` / `NotSupplied` / `NotApplicable`）を導入済みであり、本裁定はその 3 値と
1 対 1 に対応する。**本作業の主眼は型の新設ではなく、規約を全画面・全項目へ広げること**である。

**IADR-0154 が定めた規律を維持する**: `MetricAvailability` は**その指標自身の供給元の有無だけで決め、
他の指標の供給状態を条件に混ぜない**（#412 でこの取り違えが実際に混入し是正された）。

## 現況調査（実装前の棚卸し）

`develop` `0683d80` 時点の実測。

### SC-03（統制状態参照）

| 表示項目 | 現況の宣言 | 判定 | 本作業でやること |
| --- | --- | --- | --- |
| 維持率 | `NotSupplied` | ✅ 正しい | **Stage 1 の全期間にわたって表示できない**旨と「不具合ではない」旨を画面・コードへ明記する |
| 適用される閾値 / 回復目標 | 維持率と同じ供給元（スナップショット）に従う | ✅ 正しい | 変更しない（**同一の供給元**であり他指標の混入ではない） |
| 設定上の維持率閾値・回復目標オフセット・空売り比率上限 | 常に値あり（設定値） | ✅ 供給あり | 変更しない |
| 空売り比率 | 建玉 0 件 → `NotApplicable`／時価が欠ける → `NotSupplied` | ✅ 正しい | 変更しない（**正当な 0 が `0.0%` と描かれる**ことをテストで固定） |
| 借株料の累計（口座・建玉単位） | `NotSupplied` | ✅ 正しい | 変更しない（同上） |
| 自動縮小の発動履歴 | `NotSupplied`（無条件） | ✅ 正しい | 変更しない |
| 保有ポジションの方向・数量・平均取得単価 | 供給あり | ✅ | 変更しない |
| 建玉の評価額 | 現在値がある建玉のみ `Available` | ✅ 正しい | 変更しない |
| **強制買戻しの発生回数** | **画面に存在しない**（ADR-0016 決定15 が表示を求めている） | ❌ **欠落** | **`BuyInCountAvailability` / `BuyInCount` を新設し、`NotSupplied` として宣言・表示する** |
| 3 統制・現 Stage・当日損益・上限使用率 | 供給あり（`/risk-controls/status`） | ✅ | 変更しない |

**強制買戻しの発生回数を `NotSupplied` とする理由**（IADR-0162 決定2 に詳述）:
[#419](https://github.com/endazon/ai-stock-trading/issues/419) で**推定台帳**（`buy_in_inferences`）は入ったが、
台帳は**推定が起きたときにしか行を書かない**。よって行数 0 は「観測した結果 0 件だった」と
「ブローカ建玉の観測が一度も届いていない」を**区別できない**。0 件と表示すれば後者が前者に見える
（＝ADR-0016 決定15 が名指しで禁じた向き）。**本作業は供給経路を作らない**（issue の「やらないこと」）。

### SC-01（設定画面）

| 表示項目 | 現況 | 判定 | 本作業でやること |
| --- | --- | --- | --- |
| §1 全体前提条件（税率・手数料・為替・費用上限） | サーバが `isResolved`（`Version > 0`）で**供給可否を宣言**している。画面は「設定サービスの値を解決できていません（既定値を表示しています）」と警告 | 🟡 **文言が規約に合っていない** | 規約の文言（「取得できていません（供給元がありません）」）へ揃え、**表示中の値が権威値ではなく既定値である**ことを明示する |
| §2 変動閾値 | 供給あり（MarketMonitorService） | ✅ | 変更しない |
| §2 クールダウン | 供給あり | ✅ | 変更しない |
| §2 収集間隔 | 供給元となるエンドポイント自体が存在しない（IADR-0155 決定2）。画面が注記 | 🟡 **文言が規約に合っていない** | 規約の文言へ揃える。**宣言する主体（サーバ）が存在しない例外**である旨を IADR に残す |
| §2 収集パラメータの取得失敗 | 「収集パラメータを取得できませんでした。」 | 🟡 | 規約の文言へ揃える（取得失敗も `NotSupplied` の一種） |

### SC-02（リスク設定画面）

| 表示項目 | 現況 | 判定 | 本作業でやること |
| --- | --- | --- | --- |
| リスク上限 8 項目（設定値） | 供給あり | ✅ | 変更しない |
| **equity（自己資金）に対する実額の併記** | `/risk-controls/status` の取得に失敗すると `formatAmount(null)` が **「—」** を返す | ❌ **規約違反**（未供給を「—」で描いている） | **「取得できていません（供給元がありません）」**へ改める。入力が読めないだけの場合（＝**対象なし**）とは分ける |
| 現在の equity の表示 | 「取得できません」 | 🟡 文言が揺れている | 規約の文言へ揃える |
| 実弾切替モーダル③の equity・統制値 | equity 未供給なら切替を**禁じる**（fail-closed。IADR-0141） | ✅ | 変更しない |
| 運用段階・発注先・段階の既定発注先 | 供給あり | ✅ | 変更しない |

## 対象範囲（やること）

1. **バックエンド**: `ShortSellingStatusView` へ `BuyInCountAvailability` / `BuyInCount` を**末尾に追加**し、
   `ShortSellingStatusService` が **無条件に `NotSupplied` / `null`** を返す（根拠はコードコメントと IADR）。
2. **契約フィクスチャ**（IADR-0146）を再生成し、**サーバが供給可否を宣言していること**を xUnit と
   フロントの契約テストの両方で固定する。
3. **フロント（SC-03）**: 「強制買戻しの発生回数」を 3 状態で描き分ける。`availabilityCountText` を新設し、
   **`Available` かつ 0 は「0」と描く**（正当な 0 を未供給へ倒さない）。維持率の未供給警告へ
   **Stage 1 の全期間にわたって表示できない・これは不具合ではない**旨を加える。
4. **フロント（SC-01 / SC-02）**: 上表の 🟡 / ❌ を規約の文言へ揃える。とくに SC-02 の実額併記は
   **「—」から未供給の明示へ**改める。
5. **退行防止テスト**（issue「退行防止（テスト必須）」の全項目）と**変異検査**。
6. 文書: 本書・IADR-0162・SC-01/02/03 画面仕様書・テスト仕様書（T-10-257〜266）・`blocked-tasks`・計画への環流。

## 対象外（やらないこと）

- **クライアント側で「0 かどうか」から供給有無を推測すること**（裁定が明示的に却下）。
- **`MetricAvailability` の 3 値の増減**（IADR-0154 で確定・序数も固定）。
- **維持率・借株料・強制買戻しの発生回数の供給経路そのものを作ること**（供給元が無いことは計画側で確認済み）。
- 強制買戻しの**推定経路**（#419 で実装済み）。

## 受け入れ基準

| # | 基準 | 検証 |
| --- | --- | --- |
| AC-1 | `NotSupplied` が「0」として描画されない | `ControlStatusPage.shortSelling.test.tsx` / `contracts.contract.test.ts` |
| AC-2 | `NotSupplied` が「—」として描画されない（`NotApplicable` と混ざらない） | 同上 ＋ `RiskSettingsPage.test.tsx`「declares the resolved amount as not supplied (never a dash)…」・`SettingsPage.collection.test.tsx` |
| AC-3 | **正当な 0**（`Available` かつ 0）は「0」として描画される（未供給へ倒れない） | 強制買戻し 0 件・空売り比率 0.0%・借株料 $0 の 3 経路 |
| AC-4 | 建玉が 1 件も無いときの空売り比率が `NotApplicable` であり `NotSupplied` ではない | `ShortSellingStatusServiceTests` / 画面テスト |
| AC-5 | 維持率は Stage 1 で `NotSupplied` であり、供給されたときに `Available` へ切り替わる | 画面テスト（両向き） |
| AC-6 | **サーバが供給可否を宣言している**（クライアントが推測していない） | 契約フィクスチャ（xUnit＋フロント）・`ShortSellingStatusServiceTests` |
| AC-7 | 強制買戻しの発生回数は**他の指標の供給状態に依存せず**未供給である | `ShortSellingStatusServiceTests`（維持率を供給しても未供給のまま） |
| AC-8 | SC-01 / SC-02 の未供給表示が規約の文言になっている | `SettingsPage.test.tsx`（2 件・両方向）／`SettingsPage.collection.test.tsx`（収集間隔・取得失敗）／`RiskSettingsPage.test.tsx` |

## 変異検査（本作業で実施する）

| # | 変異 | 期待 |
| --- | --- | --- |
| (a) | `NotSupplied` を「0」として描画する | 赤 |
| (b) | `NotSupplied` を「—」として描画する（`NotApplicable` と混ぜる） | 赤 |
| (c) | 正当な 0（発生回数 0 件）を `NotSupplied` 扱いにする | 赤 |
| (d) | クライアント側で「値が 0 なら未供給」と推測する | 赤 |
| (e) | **サーバ**が発生回数を `Available` かつ 0 として宣言する | 赤 |

結果は PR 本文に記す。

## 影響範囲

- `backend/Services/RiskManagementService/src/RiskManagementService.Application/State/ShortSellingStatusView.cs`
- `backend/Services/RiskManagementService/src/RiskManagementService.Application/Services/ShortSellingStatusService.cs`
- `backend/Services/RiskManagementService/tests/.../ShortSellingStatusServiceTests.cs`
- `frontend/src/features/risk/contracts.ts`・`contract-fixtures/risk-controls.short-selling.json`
- `frontend/src/features/sc03-controls/ShortSellingStatusSection.tsx`
- `frontend/src/features/sc01-settings/SettingsPage.tsx`・`CollectionSettingsForm.tsx`
- `frontend/src/features/sc02-risk-settings/RiskSettingsPage.tsx`
- テスト: `ControlStatusPage.shortSelling.test.tsx`・`contracts.contract.test.ts`・`SettingsPage.test.tsx`・
  `SettingsPage.collection.test.tsx`・`RiskSettingsPage.test.tsx`・`e2e/sc03-controls.spec.ts`
- 文書: `docs/screens/*`・`docs/tests/FR-10_risk-controls-tests.md`・`docs/adr/IADR-0162_*`・`docs/blocked-tasks.md`・`feedback/`

## 未決・環流

- 計画 05_screens SC-03 の供給元の表は、強制買戻しの発生回数の現況を「**推定経路が未実装である**」と書いている。
  **#419 で推定経路は実装済み**であり、それでもなお**発生回数は供給されない**（理由は上述）。
  記述の更新を計画へ環流する（feedback/20260807_sc03-buy-in-count-supply-status.md（環流記録））。
- 共通規約は「未供給を 0 や『—』で描かない」だけを明文化しており、**逆向き（正当な 0 を未供給として描く）**は
  明文が無い。同じ環流記録で 1 文の追加を依頼した。
</content>
</invoke>
