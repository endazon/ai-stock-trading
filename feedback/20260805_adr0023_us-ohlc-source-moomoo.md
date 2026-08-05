---
title: ADR-0023 が定めていない米国株日足 OHLC の代替源として moomoo が使えることの環流
type: plan-feedback
status: open
category: 要求の不足
related_ids: [FR-15, FR-20, ADR-0005, ADR-0019, ADR-0023]
source_repo: ai-stock-trading
source_ref: chore/ADR-0019-moomoo-poc-plan（作業仕様書 docs/specs/20260805_342_moomoo-poc-plan.md・IADR-0144 決定6）
author: Claude Code（実機確認セッション）／endazon（実機実行）
created: 2026-08-05
---

# フィードバック: 米国株日足 OHLC の履歴源として moomoo を使える

## 種別

要求の不足。[ADR-0023](https://github.com/endazon/project-planning/blob/main/projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md) は Stooq が取得不能であることと回避実装の禁止を決めたが、**代替源を定めていない**。実測の結果、moomoo がその答えになる。

## 起点となる計画書

- 機能要求（FR）: FR-15（バックテスト）・FR-20（段階ゲート・Stage 0 の合格判定）
- 関連 ADR: **ADR-0023**（米国株日足 OHLC 履歴源）・ADR-0005（有料情報源の判断プロセス）・ADR-0019（moomoo PoC）
- 実装側の記録: `docs/specs/20260805_342_moomoo-poc-plan.md`／`docs/adr/IADR-0144_moomoo-short-selling-poc-outcomes.md` 決定 6

## 現状（As-Is）

- ADR-0023 決定 1 は Stooq が JavaScript proof-of-work のボット検知により実質的に取得不能であることを認め、**回避実装を明示的に禁止**した。
- **代替源は定めていない。** 実装側では `StooqHistoricalBarSource` が唯一の履歴プロバイダであり、既定は `none`（no-op）で安全側に倒れている。
- 結果として **FR-15 のバックテストに流せるデータが 1 件も無く、Stage 0 の合格判定が構造的に成立しない**。実装側の `IADR-0138`（Stage 0 の最大 DD 許容値を 0.15 → 0.10 へ厳格化）も「実効は #382 の解決に依存する」と記録しており、**一度も発火しない統制**になっている。

## 問題点 / あるべき姿（To-Be）

**moomoo OpenAPI の `QotRequestHistoryKL` で米国株の日足 OHLCV が取得できる。** 2026-08-05 に実測した。

```
KLQUOTA   retType=0  usedQuota: 0  remainQuota: 300
HISTKL    begin=2015-01-01  件数=1000  hasNextKey=True
   BAR 2015-01-02  o=24.648455279 h=24.659519313 l=23.75448132 c=24.192617072 v=212818504
HISTKL    begin=2005-01-01  →  2006-07-24 から返る
   BAR 2006-07-24  o=1.833753777 h=1.858898295 l=1.808908598 c=1.838543209 v=722988112
```

| 確認事項 | 結果 |
| --- | --- |
| 日足 OHLCV が取れるか | ✅ `KLType_Day` ／ `RehabType_Forward`（前復権） |
| 遡れる期間 | AAPL で **2006-07-24 まで**（約 20 年） |
| 1 リクエストの上限 | **1,000 件**。`NextReqKey` によるページングで継続できる |
| 取得枠 | `remainQuota: 300`（OpenD 起動ログの `Historical Candlestick Quota: 300` と一致） |
| 費用 | **追加費用なし**（既に接続している OpenD で取得できる。ADR-0005 の有料情報源プロセスに乗せる必要が無い） |

**あるべき姿**: ADR-0023 に「代替源は moomoo OpenAPI の履歴 K 線とする」旨の決定を追加する。**既にブローカとして採用済みであり、新たな契約も費用も生じない。**

### ただし未確定事項が 2 つある

1. **取得枠の単位と回復周期。** `remainQuota: 300` が「銘柄数」なのか「リクエスト数」なのかが分からない。本 probe は 2 リクエストを投げたが `usedQuota` は 0 のままだった（照会時点のスナップショットである可能性がある）。**バックテストで多数銘柄を遡ると枠を使い切る可能性があり、本実装前に確認が要る。**
2. **前復権（`RehabType_Forward`）の調整方式がバックテストの前提と一致するか。** 分割・配当の扱いが違えば成績が変わる。ADR-0016 決定 14 はバックテストに借株料・配当相当額を織り込むと定めており、**復権方式と配当の扱いが二重計上や欠落を起こさないかの確認が要る。**

### 単一障害点についての注記

06_technical/03_moomoo-integration の「リスク・未決事項」5 は「無料構成では市況データと発注が moomoo に集中する」ことを単一障害点として挙げている。**履歴データも moomoo に寄せると、この集中がさらに進む。** ただし履歴 OHLC は取引時のリアルタイム性を要求されず、取得済みデータをローカルに保持できるため、**発注・時価とは障害の性質が異なる**（moomoo が落ちてもバックテストは既存データで走る）。この差を踏まえた判断を求める。

## 実装で判明した経緯

実機確認セッション（2026-08-05）。#342（ADR-0019 の PoC）の項目 7 として、米国株日足 OHLC の取得可否を確認した。ADR-0019 決定 1 の項目 7（2026-08-04 追補）である。

quote 側の probe は `MMSPI_Qot`（133 メソッド）の実装を要するため、リフレクションから空実装を自動生成し、必要な 3 つ（`GetSecuritySnapshot` / `RequestHistoryKL` / 同 `Quota`）だけを実装した。

## 提案（計画への反映案）

- 反映先候補: **ADR-0023 の改定（新 ADR）** ／ 06_technical/02_datasource-candidates の更新
- 提案内容:
  1. **米国株日足 OHLC の履歴源を moomoo OpenAPI の履歴 K 線（`QotRequestHistoryKL`）とする決定を追加する。** 追加費用は生じない。
  2. **取得枠の単位・回復周期を確認する作業を実装側のタスクとして残す**（#382）。枠が銘柄数単位であれば、バックテスト対象銘柄数の上限が制約になる。
  3. **前復権の調整方式とバックテストの費用モデル（借株料・配当相当額）の整合を確認する。**
  4. 単一障害点の集中について、履歴データは発注・時価と障害の性質が異なることを踏まえて許容するかを判断する。

## 影響範囲

- **#382**（Stooq 取得不能への追随）: 本フィードバックが解決策になる。`StooqHistoricalBarSource` に代わる moomoo アダプタの実装が対象。
- **#16 / FR-15**（バックテスト基盤）: データ源が入ることで初めて実走できる。
- **IADR-0138**（Stage 0 の DD 厳格化）: 「実効は #382 の解決に依存する」という残余リスクが解消し得る。
- **ADR-0023**: 代替源の決定を追加。
- **02_datasource-candidates**: 情報源の区分に履歴 OHLC を追加。
