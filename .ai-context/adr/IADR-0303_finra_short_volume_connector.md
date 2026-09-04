---
title: IADR-0303 FINRA 空売りデータは日次ファイルの日付遡り取得で欠測判定へ配線し、SourceAllowlist も同時に埋める
type: impl-adr
status: Accepted
related_ids: [FR-01, ADR-0016, ADR-0020]
author: endazon (with Claude Code)
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md
---

# IADR-0303: FINRA 空売りデータは日次ファイルの日付遡り取得で欠測判定へ配線し、SourceAllowlist も同時に埋める

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-04
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-01（情報収集）、ADR-0016 決定12（FINRA 空売りデータを必須情報源へ格上げ）、
  ADR-0020 決定1・決定3（情報源の 4 区分・必須源の欠測時振る舞い 3 種）
- 対象 Issue: [#687](https://github.com/endazon/ai-stock-trading/issues/687)（分割元 #643）
- 関連する実装仕様書: [20260904_687_finra-short-volume-adapter](../specs/20260904_687_finra-short-volume-adapter.md)
- 関連 IADR: [IADR-0064](IADR-0064_official-source-connectors.md)（公式ソースコネクタの合成・レート制限の型。
  本 IADR はこれと同型で FINRA を追加する）、[IADR-0022](IADR-0022_information-collection-safe-sourcing.md)
  （安全既定 no-op）

## コンテキストと課題

`InformationSourceCatalog.Default` は `finra-short` を `Required` / `LimitedDegradation`
（`LimitsShortEntriesOnly=true`）として既に登録済みで、欠測判定（`DegradationEvaluator`）も #336 で
実装・テスト済みである。しかし `InformationSourceFactory` に FINRA の provider 実装が無く、
`finra-short` は毎巡回「未構成の必須源」（`UnconfiguredRequired`）に分類され続けていた
（実測は issue #687 本文）。空売り解禁時に ADR-0020 決定3 の限定縮退（空売りの新規建てのみ停止）が
発火する経路が存在しない状態だった。

追加で判明した論点が 2 つある。

1. **データの性質**: FINRA Daily Short Sale Volume Files は「当日 18:00 ET 更新」の**日次 1 本の
   静的ファイル**（全銘柄を含む）であり、他の公式コネクタ（SEC EDGAR＝CIK 単位の API・FRED/BOJ＝
   系列単位の API）のような「識別子ごとに要求する」形ではない。日中の巡回（30 分毎）は当日ファイル
   公表前に走り得るため、**単純に「今日の日付」を要求するだけでは常に失敗する**。
2. **SourceAllowlist の欠落**: ADR-0016 決定12 の実装状況は「`SourceAllowlist` へ FINRA 空売り
   データを追加する」ことを明記しているが、issue #687 の「やること」チェックリストには無い。
   `SourceAllowlist` を埋めないと、取得自体は成功して `SourceOutcome.Ok` を返し欠測判定は正しく
   動く一方、収集したアイテムは `InformationCollectionAppService` の許可リスト選別で毎回破棄され、
   空売り判断材料としては実質 no-op のまま——受け入れ基準の否定形テストは通るが ADR の意図を
   満たさない状態になる。

## 検討した選択肢

### 1. 対象日の決定方法

1. **常に「今日」の日付だけを要求する** — 単純だが、18:00 ET 以前の巡回・週末・休場日で常に
   失敗し、事実上 FINRA が稼働しない時間帯が生まれる（当日 18:00 ET 更新という仕様と整合しない）。
2. **「今日」から遡って最初に成功した日を採用する（採用）** — 週末・休場日・未公表を同じ「非2xx」
   として扱い、直近の営業日データを確実に拾える。他コネクタ（BOJ・EDINET）が単一日を前提にするのに
   対し、FINRA は「日次ファイルが必ず存在する営業日」を探す必要があるための固有の対応。
3. **休場日カレンダーを持ち込み、営業日だけを計算する** — 精度は上がるが、`MarketCalendar`
   （TradeDecisionService）との重複を持ち込み、本コネクタの目的（情報収集）に対して過剰。
   FINRA 自身が「無ければ 403」を返すため、暦計算より実応答に従うほうが単純かつ確実。

**選択肢 2 を採る。** `LookbackDays`（既定 7）で試行回数の上限を切り、全滅なら欠測として扱う
（他コネクタと同じ「取得失敗はログして空を返す」作法）。

### 2. SourceAllowlist への追加

1. **今回は加えず、別 issue に切り出す** — issue #687 の「やること」の文字どおりの範囲に収まるが、
   取得したデータが KB へ一切載らない状態のまま「対応済み」と報告することになり、ADR-0016 決定12
   の実装状況（`SourceAllowlist` への追加を明記）と矛盾する。
2. **本 PR で `SourceAllowlist.Default` へ `"finra-short"` を追加する（採用）** — ADR が既に指示して
   いる対応であり、欠測判定（`SourceOutcome`）とは独立した別の配線漏れを同じ根本原因（FINRA 未実装）
   のついでに埋める。差分は 1 行で、レビュー単位を過度に広げない。

**選択肢 2 を採る。**

### 3. 対象ファイル（市場区分）の選択

FINRA は `CNMS`（全市場 Consolidated NMS 銘柄）・`FNRA`（ADF 単独）・`FNYX`（NYSE）・`FNSQ`（Nasdaq）
の複数系統を公開している。**`CNMS` を採る** — 監視銘柄がどの取引所に上場していても横断的に拾える
唯一の系統であり、ADR-0016 決定12 が「空売り残高が積み上がっている銘柄」の判断材料として求める
網羅性に合致する。個別市場系統は特定の执行経路（ADF 経由の OTC 出来高など）に限定され、判断材料として
不完全になる。

### 4. InformationKind

既存の `Quote`/`News`/`Disclosure`/`MacroIndicator` はいずれも FINRA 空売り出来高（銘柄単位の需給
指標）に当たらない。**新しい種別 `SupplyDemand` を追加する**——`InformationKind` はサービス内部の
分類であり `InformationCollected` イベント（`ItemCount` のみ）を跨がないため、追加は他サービスへ
波及しない（実測: `grep -rl InformationKind backend/` は本サービス内のみがヒット）。

## 決定

1. `FinraShortVolumeInformationSource`（`Infrastructure/ExternalServices/`）を新設する。
   `https://cdn.finra.org/equity/regsho/daily/CNMSshvol{yyyyMMdd}.txt` を、東部時間の「今日」から
   `LookbackDays`（既定 7）日ぶん遡って最初に 2xx が返る日を採用し、構成された `Symbols`
   （大文字小文字を無視して突合）に一致する行だけを `RawInformationItem`
   （`InformationKind.SupplyDemand`・`Source="finra-short"`）へ写像する。
2. `InformationSourceFactory` に `Finra = "finra-short"`（カタログの名前と一致）の provider 定数・
   `case`・`Normalize()` を追加する。必須構成は `Finra:Symbols`（資格情報は不要）。
3. `CollectionSourceOptions` に `FinraOptions`（`Symbols` / `RateLimitPerMinute` 既定 5
   ／ `LookbackDays` 既定 7）を追加する。レート上限は公式公表が無いため、静的ファイル 1 本／巡回と
   いう負荷特性から独自に定めた自制値である（「公表上限の 1/2」という他コネクタの根拠は使えない
   ことを明示する）。
4. `SourceAllowlist.Default` へ `"finra-short"` を追加する（ADR-0016 決定12 の実装状況が明記する
   対応）。
5. `InformationKind` へ `SupplyDemand` を追加する。

## 影響・トレードオフ

- **利点**: `finra-short` が「未構成の必須源」から実際に稼働する情報源へ移行し、ADR-0020 決定3 の
  限定縮退（空売りの新規建てのみ停止）が空売り解禁時に実際に機能するようになる。
- **コスト**: 1 巡回あたり最大 `LookbackDays` 回の HTTP 要求が発生し得る（通常は 1 回で成功する—
  当日ファイルが 18:00 ET 以降に公表済みであれば offset=0 で成功する）。静的ファイル 1 本の取得で
  あり、他コネクタ（SEC EDGAR の CIK 単位ループ等）と比べても負荷は小さい。
- **残存リスク**: `LookbackDays=7` は経験的な既定であり、稀な多日連休（例: 感謝祭前後の複合休場）で
  枯渇する可能性はゼロではない。枯渇時は「欠測」として安全側（空売りの新規建て停止）に倒れるため、
  実害は無い。

## 対象外・据え置き

- `docker-compose.yml` / `deploy/helm/ai-stock-trading/values*.yaml` への env 追加は**意図的に
  含めない**。資格情報が不要で `Collection:Source:Provider` へ `finra-short` を加えるだけで有効化
  できるため必須ではなく、Helm の env リストは過去に大規模な巻き込み事故（#279・IADR-0114 決定4
  「リスト置換で env が消える」）があるため、本 PR の受け入れ基準に無い変更は追加しない。
- 銘柄の株式クラス表記（優先株・種類株の `.`/`/` 表記差）の突合強化は行わない（他コネクタも同水準）。

## 検証

- `dotnet test backend/Services/InformationCollectionService/Tests/InformationCollectionService.Tests.csproj`
  緑（既存 468 件 + 新規 9 件 = 477 件）。
- `dotnet build backend/backend.slnx` 警告 0。
- `dotnet format --verify-no-changes` 差分無し。
- 実 FINRA エンドポイントを一次確認済み（作業仕様書「一次確認」節参照。2026-09-04・資格情報不要で
  到達可能・応答形を確認）。CI では実ネットワークを使わず fake `HttpMessageHandler` で検証する
  （既存コネクタと同じ方針）。
