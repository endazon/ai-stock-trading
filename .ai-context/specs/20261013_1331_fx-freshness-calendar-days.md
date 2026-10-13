---
title: JPY の為替レートの鮮度警告（5.9 日）を調べ、齢を暦日差で数え直し、取得の成否をログで読み分けられるようにする（#1331）
type: spec
status: done
related_ids: [FR-11, FR-10, FR-17, ADR-0022, IADR-0174, IADR-0194, IADR-0196, IADR-0308, IADR-0533]
author: claude (Claude Code)
created: 2026-10-13
updated: 2026-10-13
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0022_fx-rate-source-and-freshness.md (決定 1 追補「収録は翌々営業日 8:50 頃」・決定 3・決定 4・決定 5)
---

# 仕様書: JPY の為替レートの鮮度警告（5.9 日）の原因と是正（#1331）

> 本仕様書は実装着手前に作成した（着手 2026-10-13）。基点は AST `origin/develop` `baafcfef`。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-11**（円換算の表示）・FR-10（非基準通貨の新規建ての統制）・FR-17。
- 計画 ADR: **ADR-0022**（決定 1 追補: 収録は翌々営業日 8:50 頃／決定 3: 営業日カレンダーを持たない／決定 4: 警告 5 日／決定 5: 停止 30 日）。
- 起票: #1331（PoC 2026-10-12 の監査。risk-management 13:34:21Z・report 09:38Z に「Jpy: 観測日 10/07 / 経過 5.9 日 / 警告 5 日」）。
- 関連 IADR: IADR-0174（3 段の縮退）・IADR-0194（日銀アダプタ・順位つきフォールバック）・IADR-0196（可視化）・IADR-0308（`Fx__Provider=boj`）。
- 新規 IADR: **IADR-0533**。

## 調査（依頼の 4 点）

| 項目 | 実装 |
| --- | --- |
| 取得の周期 | `CachingFxRateSource` の TTL（既定 21,600 秒＝6 時間。`Fx:CacheTtlSeconds`）ごと。判断サイクルや換算の呼び出しで TTL が切れていれば取りに行く。失敗（null）と停止域の値はキャッシュしない。日銀へは送信前に 1 回/分のレート制御を通す |
| 取得元と形式 | `GET https://www.stat-search.boj.or.jp/api/v1/getDataCode?format=json&lang=en&db=fm08&code=FXERD04`（期間指定なし＝全期間）。`RESULTSET[].VALUES.SURVEY_DATES`（yyyyMMdd）と `VALUES`（null を含む）を末尾から走査し、値のある直近を採る。観測日は JST 0 時 |
| 失敗時の扱い | 非 2xx・通信例外・JSON 解析失敗・系列なし・有効な観測なしは**すべて WRN を出して null**。`FallbackFxRateSource` が次の源（FRED。鍵があるときだけ）を試し、全滅なら WRN「どの源からも解決できませんでした」→ JPY の新規建ては見送り |
| 鮮度の警告 | `CachingFxRateSource.Classify` が `now − AsOf`（**経過時間**）を 5 日・30 日と比べ、警告域なら WRN「為替レートの観測が古くなっています」＋ `IFxSourceStatusNotifier.ReportStaleAsync`（監査・Discord・日報） |

## 実測（2026-10-13 21:19 JST・このセッションから日銀 API に到達できた）

- 日銀 API は HTTP 200・`STATUS 200 / M181000I`・159,800 バイト・3〜4 秒。形式・URL の変更は**無い**。
- `FXERD04` の末尾: `20261005 157.66 / 20261006 158.12 / 20261007 158.11 / 20261008 158.19`、`LAST_UPDATE 20261013`。
  **10/08 の値は 10/13（火）に収録された**（10/08 木 → 翌々営業日は 10/09 金・10/12 月が祝日〔スポーツの日〕のため 10/13 火）。
  したがって **10/12 の時点で日銀 API が返せる最新は 10/07 であり、取得は成功していた**（10/09 は 10/14 に収録される）。
- FRED `DEXJPUS`（fredgraph.csv）の最新は 10/02（H.10 は 10/05 公表。10/12 はコロンブスデーで次回は 10/13 火）。
  **10/07 の観測を返せるのは日銀だけ**であり、警告の観測日 10/07 は日銀からの取得が成功していた証拠でもある。

## 原因

仮説と判定:

| 仮説 | 判定 | 根拠 |
| --- | --- | --- |
| 取得自体が失敗（形式・URL 変更） | **否** | 実測で形式・URL とも不変。警告の観測日 10/07 は日銀しか返せない |
| 解析で 0 件 | **否** | 0 件なら WRN「有効な観測がありません」→ null で、鮮度警告ではなく見送りになる |
| 黙って握りつぶし | **否（ただし穴あり）** | 失敗経路は全て WRN。ただし**成功時に何も出さない**ため「取れているが古い」を「取れていない」と区別できなかった。また**タイムアウト（TaskCanceledException）は取り消しとして素通し**し、FRED を試さず日銀側の WRN も出ない |
| 日付の扱い（JST・祝日） | **是（主因）** | 下記 |

**主因: 齢の数え方が計画と違う。** 計画 ADR-0022 決定 4 は「最新レートの**日付**が 5 日を超えて古い」で数え、曜日表（月・火＝4 日、3 連休明けの火曜＝5 日）も**日付の差**で書き、「3 連休明けの火曜（5 日）までは収まる」とした。実装は観測日 0 時（JST）からの**経過時間**で数えるため、同じ暦日差 5 日が夜には 5.9 日となり**計画が通常運用とした日に警告が鳴る**。

- 10/12（祝日の月曜）22:34 JST: 観測 10/07 → 暦日差 5（計画では通常）・経過 5.94 日（実装では警告）。← 本件
- 10/13（3 連休明けの火曜）夜: 観測 10/08 → 暦日差 5・経過 5.9 日 → 是正前は今夜も鳴る。
- **平常週でも毎週水曜 00:00〜08:50 JST**（米国セッションの後半）は観測＝前週金曜で経過 5.0〜5.4 日 → 是正前は毎週鳴っていた。

## 母集合（規則 9・10。着手前に引いた）

- 規則 9（誤りの側の語で走査）: `grep -rn "now - rate.AsOf\|now - cached.Rate.AsOf\|経過時間で\|観測日の 0 時\|齢は.*経過"` → 実装 `CachingFxRateSource.cs` の 2 箇所のみ（判定）。`TradeDecisionAppService`（`clock.UtcNow - reading.Rate.AsOf`）は決済の報告に経過を載せるだけで判定に使わない（**変えない**）。`.ai-context/specs/20260729_271_…` は凍結記録（対象外）。
- 鮮度を判定する箇所: `grep -rn "FxRateFreshness\.\|StaleRateWarning"`（テスト除く）→ `CachingFxRateSource.Classify` の 1 箇所に集約済み（IADR-0197 決定 3）。
- 規則 10（この変更で新たに誤りになる自分の記述）: 文書の「平常時は 2〜4 日」「月・火は 4 日」「警告しきい値ちょうどでは警告を出さない」（`docs/tests/FR-10_risk-controls-tests.md` T-10-198・chart README・`docs/operations/operations.md`）は**暦日差の数え方でそのまま正しい**（むしろ一致する）。chart README の鮮度警告の行に数え方を追記する。IADR-0174 の本文は「齢」の数え方を定めていないため誤りにならない。
- 停止（30 日）の境界も同じ規則で数える（判定 1 箇所）。是正前は経過 30 日超（暦日差 30 日の当日の時刻次第で停止）、是正後は暦日差 31 日から停止＝**最大 1 日遅く停止する**。計画の「30 日超」は日付の差で読むのが決定 4 と整合する。IADR-0533 に記録。
- 並行作業（#1330・#1332・#1291）と同じファイル: `.ai-context/adr/README.md`（索引の追記のみ）以外は触らない。`docs/tests/FR-10_risk-controls-tests.md` には触らない（新しいテスト ID は採番しない。テスト名と本仕様書で写像する）。

## やること

1. `CachingFxRateSource`: 齢の判定を**暦日差**（観測の時差の暦。日銀＝JST、FRED＝UTC）へ。`ElapsedCalendarDays` を公開の純関数として置く。ログに暦日差を併記。通知の `age`（経過）は表示用に従来どおり。
2. `BojFxRateSource`: 成功時に INF（観測日・値・`LAST_UPDATE`）。タイムアウト（呼び出し側が取り消していない OCE）を WRN＋null へ。系列なしの WRN に `STATUS`・`MESSAGEID`・`MESSAGE` を載せる。
3. `FredFxRateSource`: 成功時の INF とタイムアウトの扱いを日銀側と揃える。
4. chart README「為替換算」に数え方と PoC の確認手順（ログ・API の叩き方）を追記。
5. IADR-0533 と索引。

## 受け入れ基準 → テスト

| 基準 | テスト（`AiStockTrading.Shared.Infrastructure.Tests`） |
| --- | --- |
| PoC の事象（10/12 夜・暦日差 5）・3 連休明けの火曜・平常週の水曜未明では警告しない／JST 0 時を跨いだら警告（UTC 0 時ではない） | `警告は日本時間の暦日差が5日を超えたときだけ出る`（6 例・境界の両側） |
| 停止も暦日差で数え、30 日ちょうどは採り 31 日は止める | `停止は日本時間の暦日差が30日を超えたときに働く`（両側） |
| キャッシュの経路も同じ規則 | `キャッシュから返すときも暦日差で判定し直す` |
| 暦日差は観測の時差の暦で数える（JST／UTC） | `暦日差は観測の時差の暦で数える` |
| 成功が INF で残り、警告ではない | `取得に成功したら観測日と系列の最終更新をInformationで残す`（2026-10-13 の実応答の末尾） |
| タイムアウトは WRN＋null、FRED を試す／呼び出し側の取り消しは伝播 | `タイムアウトはレート無しに縮退し警告を残す` / `日銀がタイムアウトしたらフォールバックの源を試す` / `呼び出し側が取り消したときのタイムアウト形の例外は伝播する` |
| 系列なしの理由が残る | `系列の無い応答はSTATUSとMESSAGEIDを警告に残す` |
| 既存の境界（T-10-197〜199）は不変 | 既存の `CachingFxRateSourceTests` 全件 |

## PoC での確認手順

chart README「為替換算」§取得の成否を確かめる に記載（ログの grep・API の直接取得・鮮度警告との突き合わせ）。

## 検証

- `dotnet build backend/backend.slnx -warnaserror`・`dotnet test`（Shared.Infrastructure・TradeDecision・RiskManagement・Report）・`dotnet format --verify-no-changes`・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・文書系の検査器。
