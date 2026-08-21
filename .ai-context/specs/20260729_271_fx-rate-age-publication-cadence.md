---
title: 為替レートの鮮度上限を公表周期（H.10 週次）へ整合させる（既定 14 日・上限クランプ・取得窓 ≥ 受容窓）
type: spec
status: review
related_ids: [FR-10, FR-17, ADR-0004]
author: endazon (with Claude Code)
created: 2026-07-29
updated: 2026-07-29
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
---

# 仕様書: 為替レートの鮮度上限を公表周期（H.10 週次）へ整合させる

> 経路B（ローカル SIMULATE）で `Fx:Provider=fred` を有効化し FRED API キーも投入済みの状態で、
> **米国株（AAPL）の新規建てが全件見送り**になった（実測 2026-07-27）。fail-safe の動作自体は
> 設計どおりで、**既定の鮮度上限 7 日がデータ源の公表周期と噛み合っていない**ことが故障である。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制）、FR-17（全体前提条件）
- 計画書: 05_trading-assumptions（計画リポ） §3
  （基準通貨 = JPY／レート取得元 = 日銀API または FRED）、
  ADR-0004（計画リポ）（情報源＝案A+・無料ソース）
- 関連 IADR: [IADR-0107](../adr/IADR-0107_base-currency-conversion.md)（基準通貨換算・決定3＝レート無しは見送り／
  決定5＝鮮度上限）／[IADR-0064](../adr/IADR-0064_official-source-connectors.md)（FRED アダプタの型）／
  [IADR-0059](../adr/IADR-0059_dedupe-retention-purge.md)（設定値ではなく構造で安全性を担保するクランプの型）／
  本作業で新規 [IADR-0112](../adr/IADR-0112_fx-rate-freshness-publication-cadence.md)
- 関連する実装仕様書: [20260727_257_currency-base-unification](20260727_257_currency-base-unification.md)（起点）／
  [20260728_262_263_fx-key-required-and-secret-preservation](20260728_262_263_fx-key-required-and-secret-preservation.md)
- 対象 Issue: [#271](https://github.com/endazon/ai-stock-trading/issues/271)（`Refs #271`）

## 現状（この変更の直前・実コードで確定）

| 面 | 実態 |
| --- | --- |
| `FxOptions.MaxRateAgeDays` | 既定 **7**。0 以下は既定へ丸める（上限側のクランプは無い） |
| `CachingFxRateSource` | `now - rate.AsOf > maxRateAge` で棄却し `null`（＝レート無し＝IADR-0107 決定3 で見送り） |
| `FredFxRateSource.ObservationLimit` | **10**（降順で取得し最初の有効値を採る） |
| 実測（2026-07-27） | 直近観測 `2026-07-17`＝**10 日前** → 上限 7 日を超過 → USD 建て銘柄が全件見送り |

### 故障の構造

FRED `DEXJPUS` は系列としては営業日次だが、**公表は H.10 週次リリース**（月曜 16:15 ET ≒ 20:15 UTC・
前週金曜までを一括収載。月曜が米国の祝日なら火曜へずれる）である。観測は日付のみを持つ（＝00:00 UTC 扱い）ため、
「最新観測の齢」は **公表間隔（7 日）＋ 公表ラグ（金→月の 3 日）＋ 当日の公表時刻** として積み上がる。

| 状況 | 直近観測 | 判定時刻 | 最大齢 |
| --- | --- | --- | --- |
| 通常（次の月曜公表の直前） | 前々週 金 | 月 20:15 UTC | **10.84 日** ← 実測（07-17 / 07-27） |
| 月曜が祝日 → 火曜公表 | 前々週 金 | 火 20:15 UTC | 11.84 日 |
| 対象週の金曜が休場（直近観測が木）＋翌月曜が祝日 | 前々週 木 | 火 20:15 UTC | **12.84 日** |
| 週次リリースが 1 回丸ごと欠落（系列側の異常） | 3 週前 金 | 月 20:15 UTC | 17.84 日 |

すなわち **7 日は「予定どおりの公表」でも毎週必ず超える**。実測は例外事象ではなく構造の帰結であり、
週明け・連休明けに米国株の取引が数日間まったく成立しない期間が定常発生する。

## 目的

1. 予定どおりの公表遅延（週末・連休・祝日ずれ）を吸収し、直近公表値で米国株の新規建てができる。
2. **真に古い**観測（リリースが丸ごと欠落した等・系列側の異常）は従来どおり見送る（fail-safe は緩めない）。
3. 構成値で鮮度 guard を実質無効化できないよう、**上限を構造で担保**する。
4. 本番（`values.yaml`）／SIMULATE（`values-local.yaml`）双方で妥当な既定にする。
5. 実弾（live）・SIMULATE 固定の閂には一切触れない。

## 設計

### 1. 既定 `MaxRateAgeDays` を 7 → **14** へ引き上げる

導出は上表の最大齢に基づく: `週次間隔 7 ＋ 公表ラグ 3 ＋ 祝日ずれ 2 ≒ 12.84 日` ＋ 余裕約 1.2 日。

- **予定どおりの公表遅延（最大 12.84 日）は全て吸収する。**
- **リリースが 1 回丸ごと落ちた（17.84 日）は見送る**（＝系列側の異常であり、公表周期では説明がつかない）。

安全側とのバランス（IADR-0112 に明記）: 14 日（約 10 営業日）の USD/JPY 変動は 1σ ≒ 2%・テールでも数%オーダー。
統制金額（`NotionalInBase`）の誤差も同オーダーであり、IADR-0107 が是正した約 150 倍（15,000%）とは 3 桁違う。
一方 14 日を超える空白は公表周期で説明できないため、見送りが正しい。

### 2. 設定値に**上限クランプ 31 日**を設ける（現行の下限側と対称）

`ResolveMaxRateAge` を単一入口とし、`0 以下 → 既定 14 日` に加えて `31 日超 → 31 日` へ丸め、丸めた場合は警告する。

週次公表が 4 回以上連続で落ちる事態は公表周期では説明できない。「動かないから 365 にする」といった運用の
逃げ道で guard を実質無効化させない。IADR-0059（保持期間の**下限** 7 日クランプ）と同じ
「設定値ではなく構造で安全性を担保する」型を、上限側に適用する。

### 3. 取得窓 ≥ 受容窓（`ObservationLimit` 10 → **23**）

FRED は欠測（休場・未公表）を `"."` の観測レコードとして返すことがあり、そのぶん要求件数を消費する。
降順 10 件では受容窓の端まで届かず、**広げた窓が部分的に無効**になる（実測時のように欠測日のレコード自体が
存在しない場合は届くが、どちらの返り方でも成立させる必要がある）。

件数は**設定できる最大の受容窓**（決定 2 の 31 日）を営業日へ換算した数として導出する
（`ceil(31 × 5/7) = 23`）。マジックナンバーを置かず、`FxOptions.MaxAllowedRateAgeDays` を単一情報源にする。
リクエスト回数は 1 回のまま変わらず、レート予算・費用への影響は無い。

### 4. fail-safe（すべて「発注抑止」側の性質を保つ）

| 入力 | 挙動 |
| --- | --- |
| `Fx:MaxRateAgeDays` 未設定 | 14 日 |
| `Fx:MaxRateAgeDays` ≦ 0 | 14 日（既存踏襲） |
| `Fx:MaxRateAgeDays` > 31 | **31 日へ丸め＋警告**（新規） |
| 観測が受容窓超過 | 従来どおり `null`＝レート無し＝非基準通貨の新規建て見送り（IADR-0107 決定3・**不変**） |
| provider 未設定・キー無し・未知の値 | 従来どおり no-op（**不変**） |

### 5. 影響範囲

- `backend/Services/TradeDecisionService/src/TradeDecisionService.Worker/Composable/Adapters/FxOptions.cs`（既定値・doc）
- 同 `FxRateSourceFactory.cs`（`ResolveMaxRateAge` を internal 化＋上限クランプ）
- 同 `FredFxRateSource.cs`（`ObservationLimit`）
- 同 `appsettings.Development.json`（設定点のコメントと値）
- `docs/adr/IADR-0107_base-currency-conversion.md`（決定5 へ本 IADR への逆リンクを追記）／`docs/adr/README.md`
- `docs/operations/operations.md`（トラブルシュートの「鮮度上限 7 日」）
- `deploy/helm/ai-stock-trading/README.md`（為替換算の表）

Helm の `values.yaml` / `values-local.yaml` は `Fx__MaxRateAgeDays` を**描画していない**（アプリ既定に委ねる）。
本作業でも新しい env を足さない＝**本番描画はバイト等価**を保ち、既定の引き上げが本番・SIMULATE 双方に同じく効く。

## 検討した代替案

- **(b) 営業日ベース / 公表カレンダー考慮の鮮度判定**: `MarketCalendar` の休場日集合は**構成注入で既定が空**
  （週末のみ判定）であり、issue が問題にしている**連休をそのままでは吸収できない**。機構を足しながら
  約束の半分しか果たさない。さらに「月曜祝日で公表が火曜へずれる」ケースは営業日換算では救えない
  （祝日を休みとして数えても、公表そのものがずれた分は別軸）。祝日データ源の取り込みは #271 の範囲を超える。
- **(c) 許容窓を系列頻度に連動させる**: 実在する provider は FRED ひとつであり、系列メタから公表周期を
  推定する機構は分岐の無い抽象化（CLAUDE.md 禁止事項）。将来 provider が増えた時点で
  「窓は系列の性質である」という本 IADR の記述に従って provider 別既定へ分ければよい。
- **H.10 の公表カレンダーを内蔵して「次の公表までは古くてよい」と判定する**: 最も窓は狭くなるが、
  公表スケジュールの変更・FRED 側の遅延が即・偽陰性（無音の停止）に化ける。**いま直している故障モードを
  そのまま再生産する**ため採らない。
- **鮮度上限を撤廃する**: 系列が停止しても古いレートで建て続ける。IADR-0107 決定5 の否定であり採らない。
- **代替系列・代替プロバイダ（日次更新）へ移す**: issue の案3。ADR-0004（無料ソース）の範囲で日次更新かつ
  安定した系列の調査が要り、本件（既定値と公表周期の不整合）の是正とは別軸。#271 に残置する。

## テスト（TDD・受け入れ基準の写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 週次公表の直前（10.84 日）でも直近公表値で建てられる | `CachingFxRateSourceTests`: 公表周期シナリオ表（実測 07-17/07-27 を含む） |
| 2 | 月曜祝日で火曜公表（11.84 日）でも建てられる | 同上 |
| 3 | 金曜休場＋翌月曜祝日（12.84 日）でも建てられる | 同上 |
| 4 | リリース 1 回欠落（17.84 日）は従来どおり見送る | 同上 |
| 5 | 既定値そのものが公表周期を賄う（回帰の本体） | 上記シナリオは `new FxOptions().MaxRateAgeDays` を用いる（定数の直書きをしない） |
| 6 | 既定は 14 日であり、導出根拠が記録されている | `FxRateSourceFactoryTests`: `ResolveMaxRateAge(new FxOptions())` = 14 日 |
| 7 | 0 以下は既定へ倒す | `FxRateSourceFactoryTests` |
| 8 | 上限超過の設定は 31 日へ丸める | `FxRateSourceFactoryTests` |
| 9 | 範囲内の設定はそのまま尊重する | `FxRateSourceFactoryTests` |
| 9b | 丸めた事実は警告として出力される（範囲内では出さない） | `FxRateSourceFactoryTests`: 捕捉ロガーで出力を検証 |
| 10 | 取得窓 ≥ 受容窓 | `FredFxRateSourceTests`: 要求 URL の `limit` が「設定できる最大の受容窓」の営業日換算件数以上 |
| 11 | 鮮度切れの棄却・非キャッシュは不変 | 既存 `CachingFxRateSourceTests` を無改変で緑 |

## 受け入れ基準チェック

- [x] 週末・連休・公表遅延（最大 12.84 日）でも直近公表値で新規建てができる
- [x] 真に古い観測（リリース欠落＝17.84 日）は従来どおり見送る
- [x] 構成値で鮮度 guard を無効化できない（上限 31 日でクランプ・警告）。**丸めた事実は警告として出力される**
      （テストで出力そのものを検証。黙って丸めると「緩めたつもりが効いていない」に気づけない）
- [x] 取得窓が受容窓以上である
- [x] 本番（`values.yaml`）描画がバイト等価（`Fx__MaxRateAgeDays` を描画しない設計を維持）
- [x] IADR-0107 決定5 から本 IADR への逆リンクがある
- [x] `dotnet build` / `dotnet test` / `dotnet format` green・CI green

## スコープ外

- **無音停止の可視化**（issue 案2・Discord 通知等）。鮮度判定の是正とは別軸であり、通知の重複排除設計を伴う。
- **代替系列・代替プロバイダの採用**（issue 案3）。
- IADR-0107 決定3（レート無し＝非基準通貨の新規建て見送り）の見直し。**本作業では一切緩めない。**
- 実弾（live）解禁・SIMULATE 固定の閂（IADR-0060 / IADR-0111 閂 0〜4）。**一行も触らない。**
- 含み損益の日次終値レート化（IADR-0107 決定4 の残置事項・#257）。
