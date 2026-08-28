---
title: IADR-0224 レート制限は設定値へ外出しし、Finnhub Free の日次上限は「未実測」を既定とする
type: impl-adr
status: Accepted
related_ids: [FR-01, NFR, ADR-0020, IADR-0064, IADR-0068, IADR-0222]
author: claude (Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md
  - planning:projects/ai-stock-trading/06_technical/02_datasource-candidates.md
related_specs:
  - ../specs/20260828_336_information-collection-tiers-and-degradation.md
---

# IADR-0224: レート制限は設定値へ外出しし、Finnhub Free の日次上限は「未実測」を既定とする

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: claude（起票 #336。実測は実 API が要るため後日）

## 起点・関連

- 関連する計画書 ID: FR-01 / NFR（法規・レート制限。無採番）/ ADR-0020 §結果 のフォローアップ
- 関連する実装仕様書: [`.ai-context/specs/20260828_336_information-collection-tiers-and-degradation.md`](../specs/20260828_336_information-collection-tiers-and-degradation.md)

## コンテキストと課題

ADR-0020 は結果のフォローアップとして「**Finnhub Free の実効レート制限を実測し、監視銘柄数の上限を確定する**」
ことを求めている。計画は第三者検証の観測（2026 年 4 月時点で 1 日およそ 300 回）を注記として持つが、
**これは一次ソースの実測ではない。**

実装側の現状は、レート制限が `InformationSourceFactory` に**ハードコード**されていた
（`Limiter(30, 1分)` など）。実測値が出ても**コード変更なしには反映できない。**

## 検討した選択肢

1. **レート制限を構成値へ外出しし、日次上限は `null`＝未実測を既定とする**（採用）
2. 第三者検証の観測値（300 回/日）を既定値として焼き込む — **推測値が「実測した上限」として運用へ伝わる。**
   超過（429・一時ブロック）が「実測どおりのはずなのに起きた事象」として扱われ、原因究明が歪む。却下
3. 現状維持（ハードコード） — 実測結果の反映にコード変更と再デプロイが要る。ADR のフォローアップを
   閉じられない。却下

## 決定

1. **1 分あたり（SEC EDGAR は 1 秒あたり）の自制値をソースごとの構成値にする。**
   既定値は**現行のハードコード値と同値**（各ソースの公表上限より保守側）であり、挙動は変わらない。
2. **`Collection:Source:Finnhub:DailyRequestLimit` は `null`（未設定＝未実測）を既定とする。**
3. **`FinnhubQuotaCalculator.MaxWatchlistSymbols` は上限が未実測なら `null` を返す**（推測しない）。
   設定されたときだけ、1 日の巡回数 × 1 銘柄あたりの要求数から**銘柄数上限を逆算**して起動時にログへ出す。
   端数は**切り捨てる**（足りない側へ倒す。超過はブロックを招き収集が丸ごと止まる）。
4. **未実測のときは警告ログを出す** ——「逆算していない」ことを黙らない。
5. **共有の `FinnhubQuoteClient`（実市況・IADR-0068）のレート予算は本 IADR の対象外**である。
   情報収集の枠とは別枠であり（鍵も別）、片方の変更で他方が動かないようにする。

## 理由

- **推測値を実測として書かないことは、記録の信頼性そのものである。** 実測していない数値を既定値に置くと、
  後から「いつ誰が測ったのか」を復元できなくなる。
- **未実測を `null` で表すのは、0 や大きな既定値で代用しないためである。**
  0 なら収集が止まり、大きな値なら上限超過を招く。**「知らない」を「知らない」として持つ。**
- 実測は実 API キーでの試行を要し、本 PR（CI で外部 API を叩かない方針）では行えない。
  **結果は `/plan-feedback` で計画へ環流する**（ADR-0020 のフォローアップを閉じるのは計画側である）。

## 結果

- 良い影響: 実測値が出た時点で**設定変更だけで反映できる**。銘柄数上限の根拠が計算として残る。
- 悪い影響・トレードオフ: **本 PR では監視銘柄数の上限は決まらない。**
  上限を知らないまま運用すると、銘柄数を増やしたときに 429 を踏み得る。
- フォローアップ（**実環境が要る残件**）:
  - Finnhub Free の実効レート制限（分次・日次）を実 API で測る。
  - 測った値を `Collection:Source:Finnhub:DailyRequestLimit` へ設定し、逆算された銘柄数上限を
    watchlist の運用上限として計画へ環流する。

## 関連

- Supersedes: なし（IADR-0064 の「公表上限より保守側に自制する」方針は維持し、**値の置き場所だけを変える**）
- Superseded by: なし
