---
title: 高ボラティリティ銘柄の設定変更の履歴を固定し、設定の保存と履歴の記録を同じトランザクションにする（#1291 の PoC 報告の切り分け）
type: spec
status: accepted
related_ids: [FR-10, FR-11, UC-06, ADR-0063, ADR-0066, ADR-0007, IADR-0531, IADR-0527, IADR-0134, IADR-0012]
author: claude (Claude Code)
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0063_high-volatility-symbol-order-cap.md (決定 2「変更は履歴に残る」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md (「変更は利用者のみ・変更履歴を記録する」)
---

# 高ボラティリティ銘柄の設定変更の履歴（#1291 の PoC 報告の切り分け）

## 背景

#1291 のコメント（2026-10-13 12:00Z）で PoC が報告した。所有者が `PUT /risk-controls/settings/high-volatility` で TSLA を明示指定した。
PUT は 200 を返し、GET にも反映され、再起動後も残っている。ところが `GET /risk-controls/settings/history` に
`HighVolatilityChanged`（**changeType=11** または名前の一致）が 0 件だった。PoC の集計の誤りか、履歴が書かれていないかを切り分ける。

## 実測（origin/develop `baafcfef`）

| 確かめた点 | 結果 | 根拠 |
| --- | --- | --- |
| 種別の値 | **10**（11 ではない）。0 始まりの序数で、`HighVolatilityChanged` は 11 番目のメンバ | `Features/RiskManagement/SettingsChangeEntry.cs:14-45` |
| 応答での種別の表現 | **整数**（`JsonStringEnumConverter` は無い）。名前 `HighVolatilityChanged` は応答に出ない。DB の列は名前の文字列 | `Program.cs`（JSON の構成なし）・`EfSettingsChangeLog.cs:16` |
| PUT が履歴を書くか | 書く。`UpdateHighVolatility` → `Save` → `changeLog.Record`。`Record` が失敗すれば例外で 500 になるので、**200 が返ったなら行は確定している** | `RiskSettingsService.cs:94-113, 248-261`・`EfSettingsChangeLog.cs:8-23` |
| 同じトランザクションか | **違う**。設定の `store.Save` と履歴の `Record` がそれぞれ `SaveChanges` を呼ぶ（暗黙の 2 トランザクション） | `EfRiskSettingsStore.cs:99`・`EfSettingsChangeLog.cs:22` |
| 履歴の GET の絞り込み | **無い**。期間・種類・ページング・件数の上限を持たず、全件を新しい順で返す | `GetSettingsHistory/Endpoint.cs:7`・`EfSettingsChangeLog.cs:25-36` |
| 既存の試験 | `T-10-2562` は「履歴に含まれる」を見るが、**1 行増えること**と**応答上の整数値 10** は固定していない。InMemory でしか走らない | `HighVolatilitySettingsEndpointTests.cs:85-109` |

**結論**: PoC の観測は集計の誤りで説明できる（値 11 は存在しない種別、名前は応答に現れない）。履歴が書かれないという欠陥は無い。
ただし、**設定の保存と履歴の記録が別トランザクション**なので、履歴の書き込みだけが失敗すると「設定は変わったが履歴が無い」状態が残り得る（応答は 500）。
FR-11 / ADR-0007 の「変更は履歴に残す」に対する潜在的な欠陥として、本 PR で塞ぐ。

## 設計（IADR-0531）

- 設定の保存と履歴の記録を `ISettingsWriteTransaction`（Features のポート）の 1 単位で実行する。EF 実装 `EfSettingsWriteTransaction` は
  関係 DB なら `BeginTransaction` → 本体 → `Commit`、失敗なら破棄（ロールバック）と変更追跡の破棄。関係 DB でないプロバイダ（テストの InMemory）は本体をそのまま走らせる（`EfEntryReservationStore` と同じ縮退）。
- `RiskSettingsService.Save`（ガード・上限・高ボラ・段階・発注先・Stage 1 件数・損切りの機構の 7 経路）がこれを使う。
- **kill switch と pause は対象外**（下の母集合）。

## 母集合（規則 9・10）

`changeLog.Record` を `git grep` で走査した（5 か所）:

| 場所 | 扱い | 理由 |
| --- | --- | --- |
| `RiskSettingsService.Save`（1 か所。7 経路の合流点） | 改める | 設定の変更は、履歴が書けなければ設定も変えない方が安全側（緩める変更も締める変更も、変わらないだけで統制は働き続ける） |
| `KillSwitchService.Engage` / `Disengage` | **除外** | 🔴 発動を履歴の失敗で巻き戻すと、**止めたいときに止まらない**。全停止は履歴より優先する（fail-safe の向きが設定と逆） |
| `PauseService.Pause` / `Resume` | **除外** | kill switch と同じ理由（停止を巻き戻さない）。解除側だけ原子にすると同型の 2 経路で規律が割れるため、両方据え置く |

`store.Save` を履歴なしで呼ぶ経路（`BrokerProviderAlignmentHandler`）は履歴を書かない設計であり、本件の対象外。

新たに誤りになる自分の記述: 無し（`SettingsChangeType` の値・GET の挙動は変えない）。

## 受け入れ基準 → 試験

| 受け入れ基準 | 試験 |
| --- | --- |
| 高ボラの PUT 1 回で履歴がちょうど 1 行増え、応答の `changeType` は整数 10・理由と前後値が載る | T-10-2611 |
| 履歴の種別の応答上の整数値を全メンバ固定する（0〜10。末尾追加の規律・PoC の集計の拠り所） | T-10-2612 |
| 本番の配線で `ISettingsWriteTransaction` が EF 実装に解決される | T-10-2613 |
| 🔴 実 PostgreSQL で、高ボラの変更が履歴に 1 行（列は名前 `HighVolatilityChanged`・序数 10）書かれる | T-10-2614（統合） |
| 🔴 実 PostgreSQL で、履歴の書き込みが失敗したら設定も変わらない（ロールバック） | T-10-2615（統合） |

## 検証

- `dotnet test`（RiskManagementService.Tests 全件）・`dotnet format --verify-no-changes`・警告 0 のビルド。
- 統合試験は Docker の無い手元では skip になるため、`integration.yml` を本ブランチで `workflow_dispatch` して実走を確かめる。
- `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・文書系の検査器。

## PoC への手順（集計し直し）

- `GET /risk-controls/settings/history` の応答は**配列**で、各要素の `changeType` は**整数**である。高ボラは **10**。
- 例: `jq '[.[] | select(.changeType == 10)]'`。理由・前後値（`after` に `TSLA(UnitedStates)`）で該当行を確かめる。
