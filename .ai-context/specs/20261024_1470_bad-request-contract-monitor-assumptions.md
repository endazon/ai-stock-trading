---
title: MarketMonitorService・ConfigurationService の書き込みの 400 の本文を契約フィクスチャで縛り、画面のモックを実応答に揃える
type: spec
status: done
related_ids: [SC-01, SC-02, FR-03, FR-13, FR-17, NFR-19, IADR-0585, IADR-0146, IADR-0503, IADR-0496]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
---

# 仕様書: 残りの 400 を契約で縛る（#1470）

> 本仕様書は実装着手前に作成した（AST `origin/develop` `9ab605bb`＝#1469 のマージ後）。

## 起点（トレーサビリティ）

- 起点: #1470（#1469 の残余。IADR-0585「残余リスク」）。作法は #1469 の RiskManagementService の契約テストと同じ（IADR-0146）。
- 新しい IADR は起こさない（IADR-0585 の決定 3 を残りの 2 サービスへ広げるだけ）。

## 母集合（規則 9）

画面が叩く書き込みのうち、#1469 で契約に入っていないもの（`grep -rn "Results.BadRequest(" backend/Services/{MarketMonitorService,ConfigurationService}`
と画面側の `apiFetch` の呼び出し `grep -rn "'/monitor/\|'/assumptions" frontend/src/lib` の突き合わせ）:

| 端点 | 400 の経路 | 本文 | 契約テスト（新規） | フィクスチャ |
| --- | --- | --- | --- | --- |
| `POST /monitor/watchlist` | 市場の省略（`ArgumentException`・印つき → 群の写し） | `{ error }` | `MonitorContractFixtureTests` | `monitor.watchlist-post.400.json` |
| `DELETE /monitor/watchlist` | 監視対象に無い銘柄（同） | `{ error }` | 同 | `monitor.watchlist-delete.400.json` |
| `PUT /monitor/settings/movement-threshold` | 値域外（同）／省略（端点の明示の拒否・同じ `{ error }`） | `{ error }` | 同 | `monitor.settings-movement-threshold.400.json` |
| `PUT /monitor/settings/cooldown` | 値域外（同）／省略（同） | `{ error }` | 同 | `monitor.settings-cooldown.400.json` |
| `PUT /assumptions` | 理由の空欄（`ClientVisibleArgument` → 群のフィルタ） | `{ error }` | `AssumptionsContractFixtureTests`（新規ファイル） | `assumptions.400.json` |

- `PUT` の監視銘柄は存在しない（追加は POST・削除は DELETE。issue の「PUT / POST / DELETE」の PUT は該当なし）。
- **`{ error, details? }` でない 400**: 要求の束縛に失敗したとき（本文が JSON でない・型が合わない）は、ASP.NET Core が**本文なしの 400** を返す
  （IADR-0496 の「束縛失敗は本文なしの 400」）。画面は一般文言だけを出す。**例外として残す**: 画面は型どおりの本文しか送らないので、利用者の操作では起きず、
  起きたら画面かサーバの不具合である（理由を利用者へ見せても対処できない）。IADR-0585 の形へ寄せる必要は無い。

## 受け入れ基準

- A1: 上の 5 端点の 400 に契約テストがあり、実端点から採った本文を frontend のフィクスチャと突き合わせる。
- A2: 変異: 群の写し（`MonitorSettingsEndpoints.MapException`）の本文を `{ message }` に変えると監視の 4 本が、前提条件の群のフィルタを
  `Results.Problem` に変えると前提条件の 1 本が赤になる（実測）。
- A3: 型の突合: フィクスチャを `BadRequestBody` へ代入する（`monitorContractFixtures.ts`・新規 `assumptionsContractFixtures.ts`）。
- A4: 画面のモック: 単体試験（SC-01 前提条件・監視銘柄の削除）と E2E（SC-01 前提条件）のモックをフィクスチャへ替えた。E2E に監視銘柄の追加・変動閾値・クールダウンの
  400 を足し、サーバの理由が画面に出ることを確かめる。

## 見つけたこと（本 issue では直さない。報告する）

- フィクスチャを採ってみると、**利用者へ見せる文言に .NET の引数名の接尾辞 ` (Parameter 'cooldown')` が付いている**（`ArgumentException.Message` の仕様）。
  また監視銘柄の削除の文言は市場を enum 名（`UnitedStates`）で書き、前提条件の理由の空欄は .NET の英語の既定文言（`The value cannot be an empty string …`）である。
  #1469 以後、これらはそのまま画面に出る（前提条件の理由の空欄と値域外は画面が先に止めるため、通常の操作では出にくい）。直すなら `ClientFacingErrors.MessageFor`（共通）で
  接尾辞を落とす・文言を日本語にする別の issue になる（Bot の文言にも効く）。
