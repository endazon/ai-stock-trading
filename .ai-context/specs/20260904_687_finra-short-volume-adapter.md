---
title: FINRA 空売りデータ（Daily Short Sale Volume Files）の取得アダプタ新設
type: spec
status: review
related_ids: [FR-01, ADR-0016, ADR-0020]
author: endazon (with Claude Code)
created: 2026-09-04
updated: 2026-09-04
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md
---

# 仕様書: FINRA 空売りデータの取得アダプタ新設

> Issue [#687](https://github.com/endazon/ai-stock-trading/issues/687)（#643 の分割）。
> 情報源カタログ（`InformationSourceCatalog.Default`）には `finra-short` が `Required` /
> `LimitedDegradation`（`LimitsShortEntriesOnly=true`）として既に登録済みだが、
> `InformationSourceFactory` に実装（provider・接続コード）が無く「未構成の必須源」のまま放置されている。
> 本作業はこの欠落を埋める——**ドメイン側の欠測判定ロジック（`DegradationEvaluator`）は #336 で
> 実装・テスト済みであり、本作業はソース単位の成否（`SourceOutcome`）を供給する Infrastructure 層の
> 実装に閉じる。**

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01（定時収集・正規化・KB 保存）
- 関連 ADR: **ADR-0016 決定12**（FINRA 空売りデータを必須情報源へ格上げ。取得は Daily Short Sale
  Volume Files の直接ダウンロード＝登録不要・無料・当日 18:00 ET 更新）／**ADR-0020 決定1・決定3**
  （情報源の 4 区分・必須源の欠測時振る舞い 3 種。FINRA は「空売りの新規建てのみ停止」＝
  `LimitedDegradation` かつ `LimitsShortEntriesOnly`）
- 計画書リンク:
  - `../project-planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md` 決定12
  - `../project-planning/projects/ai-stock-trading/07_adr/ADR-0020_datasource-tiering-and-fallback.md` 決定1・決定3
- 対象 Issue: [#687](https://github.com/endazon/ai-stock-trading/issues/687)（分割元 #643）

## 目的・背景

`InformationSourceCatalog.Default` は `finra-short` を `Required` / `LimitedDegradation`
（`LimitsShortEntriesOnly=true`）として登録済みだが、`InformationSourceFactory` が実装する
provider は 7 件（`finnhub` / `finnhub-news` / `google-news` / `sec-edgar` / `edinet` / `boj` /
`fred`）のみで FINRA が無い。結果として `finra-short` は毎巡回「未構成の必須源」
（`UnconfiguredRequired`）に分類され続け、空売り解禁時に ADR-0020 決定3 の限定縮退（空売りの
新規建てのみ停止）が発火する経路が存在しない。

本作業は、SEC EDGAR / EDINET / 日銀 / FRED と同型の「公式・無料・登録不要／要キー」コネクタとして
FINRA Daily Short Sale Volume Files の取得アダプタを追加し、`InformationSourceFactory` へ配線する。

## 一次確認（実 API 実測。2026-09-04）

**資格情報不要で実際にエンドポイントを叩けた。** 知識で書かず、以下を実測した。

- URL パターン: `https://cdn.finra.org/equity/regsho/daily/CNMS{yyyyMMdd}.txt`
  （`CNMS` = 全米国市場の Consolidated NMS 銘柄。ADF 単独の `FNRA`・個別市場の `FNYX`/`FNSQ` ではなく
  全銘柄を横断する本ファイルを採用する）
- 取引日（2026-09-03・木）: `GET https://cdn.finra.org/equity/regsho/daily/CNMSshvol20260903.txt`
  → `200 OK`、`Content-Type: text/plain`、認証ヘッダ不要。本文はパイプ区切りでヘッダ行
  `Date|Symbol|ShortVolume|ShortExemptVolume|TotalVolume|Market` に続き、銘柄ごとに 1 行
  （例: `20260903|AAPL|6836348.202153|96091|14143853.755472|B,Q,N`）。
  `ShortVolume`/`TotalVolume` は小数（連結市場の按分値と見られる）を含むため `decimal` で読む。
- 週末日付（2026-09-06・日）・未来日付（2099-01-01）: いずれも **`403 Forbidden`**（`404` ではない。
  CDN/オブジェクトストレージの鍵欠落が 403 として現れる）。**非 2xx はすべて「その日のファイルはまだ
  無い/存在しない」として扱い、日付を 1 日遡って再試行する**（後述）。
- レート制限は公式に明記が無い（単一 CDN 静的ファイルであり、SEC EDGAR のような per-IP 上限の公表も
  無い）。既存コネクタと同じ構成パターン（`TokenBucket`）を踏襲し、自制値として控えめな既定を置く。

## 対象範囲

### 対象

- `Infrastructure/ExternalServices/FinraShortVolumeInformationSource.cs`（新設）: 上記ファイルを
  取得し、構成された `Symbols` に一致する行だけを `RawInformationItem` へ写像する。
- `Infrastructure/ExternalServices/CollectionSourceOptions.cs`: `FinraOptions`
  （`Symbols` / `RateLimitPerMinute` / `LookbackDays`）を追加。
- `Infrastructure/ExternalServices/InformationSourceFactory.cs`: `Finra = "finra-short"`
  （カタログの名前と一致）の provider 定数・`case`・`Normalize()` への追加。
- `Domain/CollectedInformation.cs`: `InformationKind` へ `SupplyDemand`
  （需給データ。既存の `Quote`/`News`/`Disclosure`/`MacroIndicator` のいずれにも当たらない）を追加。
  他サービス・イベント契約（`InformationCollected` は `ItemCount` のみで `InformationKind` を運ばない）
  を跨がないため、追加による破壊的変更は無い。
- `Domain/SourceAllowlist.cs`: `Default` へ `"finra-short"` を追加する。**ADR-0016 決定12 の実装計画
  （同 ADR §実装状況「`SourceAllowlist` へ FINRA 空売りデータを追加する」）が明記している対応であり、
  これを欠くと取得は成功してもアイテムが許可リストで破棄され続け、実質 no-op のままになる**
  （欠測判定＝`SourceOutcome` は許可リストと独立に効くため受け入れ基準の否定形テストは通るが、
  空売り判断材料としての実効性が無いままになるのは ADR の意図に反する）。
- `appsettings.Development.json`（同サービス）: `Collection:Source:Finra` の空既定を追加。
- テスト: `Tests/Infrastructure/ExternalServices/FinraShortVolumeInformationSourceTests.cs`（新設）、
  `InformationSourceFactoryTests.cs` へ finra ケース追加、`InformationSourceCatalogTests.cs` /
  `DegradationEvaluatorTests.cs` は既存のまま（ドメイン側は #336 で実装済み・変更不要）。

### 対象外

- `docker-compose.yml` / `deploy/helm/ai-stock-trading/values*.yaml` への env 追加。
  資格情報が不要で `Collection:Source:Provider` へ `finra-short` を含めるだけで有効化できるため、
  ローカル compose・Helm chart への配線は必須ではない。**Helm の env リストは過去に大規模な巻き込み事故
  （#279・IADR-0114 の教訓）があるため、本 PR の受け入れ基準に無い変更は追加しない**（意図的な除外。
  必要になった時点で別 issue で追加する）。
- 借株可否・強制買戻し等の空売り実行系統制（ADR-0016 の他の決定）: 本作業は情報源の供給のみ。
- 銘柄シンボルの表記ゆれ（優先株・複数株式クラス等、`.`/`/` 表記差）の突合強化: 直接一致のみとし、
  必要になった時点で別途対応する（他コネクタも同水準）。

## 設計

### 日付選択（当日 18:00 ET 更新への対応）

ADR-0016 決定12 は「当日 18:00 ET 更新」と定める。巡回は市場時間中も走る（FR-01 既定 30 分毎）ため、
18:00 ET 以前に「当日」のファイルを要求すると存在しない（403）。加えて週末・休場日は当日ファイルが
存在しない。**「本日から遡って最初に取得できた日」を採用する**（`LookbackDays` 既定 7 日）。

- 週末（金曜引け後の土日）で最大 2 日、稀な 3 連休・当日未公表分を合わせても 7 日あれば実務上十分
  余裕がある（同種の趣旨で FX レート鮮度は 14 日既定・31 日上限としている前例＝#271・IADR-0112
  があるが、本データは「前営業日には必ず存在する」性質のため、より短い既定で足りると判断した）。
- 米国東部時間への変換は `TimeZoneInfo`（`"Eastern Standard Time"` / `"America/New_York"`）を用いる。
  日本銀行コネクタの固定オフセットと異なり、**ET は夏時間を持つため固定オフセットは使えない**
  （`RiskManagementService.Infrastructure.Steps.EasternTradingDate` と同じ方式。サービスを跨ぐ共有化は
  本作業の対象外とし、本コネクタ内に閉じた同型の実装を置く）。
- 1 巡回で複数日ぶん HTTP 要求し得るため、**日付ごとの試行前にも `IRateLimiter.WaitAsync` を呼ぶ**
  （他コネクタと同じ作法）。最初に 2xx を得た日で確定し、それ以降の日は試行しない。

### 銘柄の突合・写像

- `FinraOptions.Symbols`（大文字小文字を無視して突合。他コネクタの `Ciks`/`SeriesIds` と同型の
  独立配列とする——Finnhub の監視銘柄と必ず一致するとは限らないため）。
- 本文が構成銘柄に一致しない行は読み捨てる（数千行規模のファイルをそのまま KB へ入れない）。
- 写像: `InformationKind.SupplyDemand` / `Source="finra-short"` / `Symbol=<Symbol>` /
  `Content` に `date` / `shortVolume` / `shortExemptVolume` / `totalVolume` /
  `shortVolumeRatio`（`shortVolume / totalVolume`。`totalVolume=0` は算出しない）/ `market` を含める。
- `PublishedAt`: 採用した日の 18:00 America/New_York（実際の公表予定時刻。ADR-0016 決定12）。
- `Url`: 実際に取得した CDN ファイルの URL（一次資料への直接リンク）。

### レート制限

`RateLimitPerMinute` 既定 5（公式上限の公表が無いため、静的ファイル 1 本／巡回という負荷の軽さを
踏まえた保守的な自制値。他コネクタの「公表上限の 1/2」という根拠は使えないため、負荷特性から独自に
定める点を明示する）。

## 受け入れ基準

- [ ] `finra-short` が `Collection:Source:Provider=finra-short` かつ `Finra:Symbols` 設定で実際に
      構成でき、取得成功／失敗が `SourceFetchRunner`→`DegradationEvaluator` の欠測判定に載る
      （`SourceOutcome` の名前が `finra-short` でカタログと一致する）。
- [ ] **否定形**: FINRA が欠測（非 2xx が `LookbackDays` 分すべて続く）のとき、`CollectionDegradation`
      は `BlocksShortEntries=true` かつ `BlocksNewEntries=false` になる——空売りの新規建てだけが
      止まり、買戻し・手仕舞い・現物の新規建ては止まらないこと（ADR-0020 決定3。ドメイン側は
      #336 で実装済みのため、本作業では「アダプタが正しい名前で欠測を報告すること」を検証する）。
- [ ] 起点 ID コメント（FR-01, ADR-0016, ADR-0020）付き。
- [ ] 未設定（`Symbols` 空）なら安全既定（no-op・当該ソースを有効化しない）。
- [ ] 実 FINRA 応答形（一次確認済み・上記）に対する写像を fake `HttpMessageHandler` で検証する
      （実ネットワーク不使用）。
- [ ] 当日ファイル不在（403）→前日ファイル取得成功、まで含めた日付遡りを検証する。
- [ ] `LookbackDays` を使い切っても全滅なら空を返し、巡回を止めない。

## テスト方針

- 単体テスト: `FinraShortVolumeInformationSourceTests`（fake `HttpMessageHandler`・`SequenceHandler`
  で「1 日目 403→2 日目 200」の遡り、構成銘柄以外の行は無視、`ShortVolumeRatio` 算出、
  `TotalVolume=0` 時は比率を出さない、全滅時は空を返す、を検証）。
- `InformationSourceFactoryTests`: `finra-short` の構成揃い／`Symbols` 未設定時の除外、複数指定時に
  カタログと同じ名前 `finra-short` で返ることを検証。
- 実 FINRA API への実接続は CI 対象外（既存コネクタの方針を踏襲）。

## 計画書との差異

- 差異: なし。ADR-0016 決定12・ADR-0020 決定1/3 の記述どおりに実装する。
  `SourceAllowlist` への追加は ADR-0016 §実装状況が明記する対応であり、issue #687 の「やること」
  本文には無いが、同 issue の受け入れ基準（実効性を伴う空売り欠測判定）を満たすために本作業へ含めた。

## 未決事項

- 銘柄の株式クラス表記（優先株・種類株）の突合強化は、実運用で必要になった時点で見直す。
- `LookbackDays=7` は経験的な既定であり、実運用で祝日カレンダーとの整合を実測したうえで見直す余地がある
  （ADR-0020 のレート・上限の多くが「未実測」を明示している慣行に合わせる）。
