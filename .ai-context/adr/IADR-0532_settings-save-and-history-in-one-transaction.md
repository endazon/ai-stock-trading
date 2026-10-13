---
title: IADR-0532 リスク管理の設定の保存と変更履歴の記録を 1 つの DB トランザクションにする（履歴が書けなければ設定も変えない）。kill switch と pause は対象外
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-11, UC-06, ADR-0007, ADR-0063, ADR-0003, ADR-0009, IADR-0527, IADR-0529, IADR-0012, IADR-0134]
author: claude (Claude Code)
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md (「変更は利用者のみ・変更履歴を記録する」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0063_high-volatility-symbol-order-cap.md (決定 2「変更は履歴に残る」)
related_specs:
  - ../specs/20261013_1291_hv-history-atomic-and-pinned.md
---

# IADR-0532: 設定の保存と変更履歴の記録を 1 つのトランザクションにする（#1291）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-13
- 決定者: 実装（Claude Code）。#1291 の PoC 報告（高ボラの変更が履歴に見当たらない）の切り分けで見つけた潜在的な欠陥の是正。

## 起点・関連

- 起票: [#1291](https://github.com/endazon/ai-stock-trading/issues/1291)（2026-10-13 12:00Z のコメント）
- 前提（覆さない）: IADR-0012（設定は単一行 JSON＋版の並行トークン）・IADR-0134 決定 2（履歴の種別は末尾へ追加）・IADR-0527 決定 2（高ボラの統制値は設定に置き、変更は履歴に残る）
- 先例: IADR-0529（審査の直列化を DB のトランザクションで行い、関係 DB でないプロバイダは縮退する）
- 仕様書: `.ai-context/specs/20261013_1291_hv-history-atomic-and-pinned.md`

## コンテキスト

- `RiskSettingsService.Save` は `store.Save`（`EfRiskSettingsStore`。`SaveChanges`）と `changeLog.Record`（`EfSettingsChangeLog`。`SaveChanges`）を順に呼ぶ。
  明示のトランザクションが無く、**2 つの暗黙のトランザクション**に割れていた。
- 履歴の書き込みだけが失敗すると、設定の変更は確定し、履歴は残らない（応答は 500）。ADR-0007 / FR-11 の「変更は履歴に残す」が破れる。
  利用者は 500 を見て再送し得るが、そのときの履歴の「変更前」は既に変わった後の値になり、前後値も食い違う。
- PoC の報告（PUT は 200・履歴に 0 件）はこの欠陥では説明できない（200 なら履歴は確定している。原因は集計の誤り＝種別を 11 で数えた。正しくは 10）。
  ただし、切り分けで「同じトランザクションか」を確かめた結果として見つかった。

## 決定

1. **設定の保存と履歴の記録を 1 つの DB トランザクションで確定する。** ポート `ISettingsWriteTransaction`（Features）を置き、EF 実装
   `EfSettingsWriteTransaction` が `BeginTransaction` → 本体 → `Commit` を行う。失敗なら破棄（ロールバック）し、変更追跡も捨てる。
   - 関係 DB でないプロバイダ（テストの EF InMemory）は本体をそのまま走らせる（IADR-0529 と同じ縮退。本番の配線は常に Npgsql）。
   - 外側に既にトランザクションがあれば、それに乗る。
   - `RiskSettingsService` の引数は省略可能（省略時は本体をそのまま走らせる＝ストアを直接組み立てる単体試験の互換）。
     本番の配線で EF 実装に解決されることは試験（T-10-2613）で固定する。
2. 🔴 **kill switch（`KillSwitchService`）と pause（`PauseService`）には適用しない。** 発動を履歴の失敗で巻き戻すと、止めたいときに止まらない。
   停止は履歴より優先する（安全側の向きが設定と逆）。解除側だけ原子にすると同型の 2 経路で規律が割れるため、両方据え置く。
3. 履歴の種別の応答上の整数値（0〜10）を全メンバで試験に固定する（T-10-2612）。PoC と画面はこの整数で読む。

## 検討した選択肢

| 案 | 原子性 | 影響範囲 | 採否 |
| --- | --- | --- | --- |
| A. サービスにトランザクションのポートを渡す（本決定） | 設定の 7 経路がそろって原子になる | 本番の配線 1 行・サービス 1 か所 | **採る** |
| B. 設定のストアの `Save` が `SaveChanges` を呼ばず、履歴の `Record` だけが確定する | 原子になる | 履歴を書かない保存の経路（`BrokerProviderAlignmentHandler`）が確定しなくなる | 採らない |
| C. 設定の JSON 行に履歴を同居させる | 原子になる | 履歴の照会・移行の作り直し | 採らない（費用が見合わない） |
| D. kill switch・pause も含めて原子にする | 全部そろう | 停止が履歴の失敗で巻き戻る | 採らない（決定 2） |

## 結果

- 履歴が書けなければ設定は変わらない（緩める変更も締める変更も起きない＝統制は今の値のまま働き続ける）。
- 実 DB の統合試験（T-10-2614・T-10-2615）が、1 行書かれることとロールバックを固定する。

## 残る制約

- kill switch と pause は、停止の後で履歴の書き込みが失敗すると「停止したが履歴が無い」状態が残り得る（決定 2 の受容。応答は 500 なので操作者は気付ける）。
- 履歴の GET は絞り込み・ページングを持たない（全件を新しい順）。件数が増えたときの扱いは本決定の範囲外。
