---
title: 米国株日足 OHLC 履歴源として moomoo 履歴 K 線アダプタを実装する（ADR-0023 決定5・既定は none のまま）
type: spec
status: approved
related_ids: [FR-15, FR-20, ADR-0023, ADR-0019, ADR-0016, ADR-0004, ADR-0005, ADR-0008, IADR-0105, IADR-0138, IADR-0156, IADR-0157]
author: Claude Code
created: 2026-08-06
updated: 2026-08-06
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0019_moomoo-poc-margin-paper-account.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
related_specs:
  - ../adr/IADR-0157_moomoo-history-kline-adapter.md
  - ../adr/IADR-0156_us-ohlc-history-source-absence.md
  - ../../docs/functional/FR-15_backtest.md
  - ../../docs/tests/FR-15_backtest-tests.md
  - 20260806_382_us-ohlc-source-arbitration.md
  - 20260805_342_moomoo-poc-plan.md
---

# 仕様書: moomoo 履歴 K 線アダプタの実装（#382 の残り / ADR-0023 決定5）

> 本仕様書は実装着手前に作成した。以降の作業は本書に沿って進める。
> 直前の PR [#415](https://github.com/endazon/ai-stock-trading/pull/415)（作業仕様書
> [20260806_382_us-ohlc-source-arbitration](20260806_382_us-ohlc-source-arbitration.md)）は
> **計画側の裁定に依存しない範囲（可視化）だけ**を実装した。本書はその続きであり、**裁定が下りた部分**を実装する。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-15**（バックテスト＝Stage 0 の前提）・FR-20（段階ゲート）
- ユースケース（UC）: UC-06（要求トレーサビリティ表の `FR-15, FR-20 | UC-06`）
- 関連 ADR: **ADR-0023（計画リポ） 決定5**
  （2026-08-06 追加・本作業の直接の起点）／同 決定1（Stooq は取得不能・回避実装は禁止・**候補からは削除しない**）／
  ADR-0019（計画リポ） 決定1 項目7（PoC の実測）／
  ADR-0016（計画リポ） 決定14（バックテストの費用モデル＝未確認事項 2 の相手方）／
  ADR-0008（計画リポ）（Stage 0）
- 関連 IADR: [IADR-0105](../adr/IADR-0105_backtest-historical-bar-source.md)（実過去データ源の構造・安全既定）・
  **[IADR-0156](../adr/IADR-0156_us-ohlc-history-source-absence.md)**（現況 4 点。本作業で**改訂節を追記**する）・
  [IADR-0138](../adr/IADR-0138_stage0-drawdown-tolerance-tightening.md)（Stage 0 の最大 DD 厳格化）・
  **IADR-0157**（本作業で新規作成）
- 対象 Issue: [#382](https://github.com/endazon/ai-stock-trading/issues/382)（**クローズしない**。後述「本 PR で解けないもの」）
- 計画 submodule: `d980a01` → **`e36b592`**（ADR-0023 決定5 を含む 3 コミット）

## 目的・背景

計画側で **ADR-0023 が改定され、moomoo OpenAPI の履歴 K 線が米国株日足 OHLC 履歴源として正式採用された**
（決定5・利用者裁定 2026-08-06・環流 project-planning#205）。PR #415 が「裁定待ち」として保留していた当の裁定が下りた。

| 決定5 が定めたこと | 実装への効き方 |
| --- | --- |
| 履歴源は **moomoo の `QotRequestHistoryKL`**（`KLType_Day` / `RehabType_Forward`） | 本作業でアダプタを実装する |
| 1 リクエスト **1,000 件**上限・`NextReqKey` でページング | ページングを実装する（**切り詰めは Stage 0 の判定を歪める**） |
| 追加費用なし（既に接続している OpenD で取れる） | ADR-0005 の有料枠は発動しない |
| Stooq の扱い（決定1）は**変更しない** | Stooq のコード・テストは削除しない |
| **実装側で確認を要する 2 点**（本決定の前提。**確認するまで本番のバックテストへ流さない**） | 後述「本 PR で解けないもの」 |

## 対象範囲（本 PR でやること）

1. **計画 submodule を `e36b592` へ更新する。**
2. **moomoo 履歴 K 線アダプタを実装する。**
   - `IHistoricalBarSource` の実装 `MoomooHistoricalBarSource`（ページング・欠測記録・レート自制）
   - OpenD への実接続 `MMApiMoomooHistoryKLineClient`（`MMAPI_Qot` / `MMSPI_Qot`・SDK 依存はここに閉じる）
   - `HistoricalBarSourceFactory` の provider 選択へ `moomoo` を加える（**allow-list 構造は壊さない**）
3. **既定は `none` のまま変えない。** moomoo は `Backtest:BarData:Provider=moomoo` を**明示的に構成したときだけ**使う。
4. **未確認 2 点を、構成した人が必ず目にする場所に残す**（アダプタの警告ログ・XML doc・IADR-0157・`docs/blocked-tasks.md`）。
5. **PR #415 が置いた関門テストを「採用後の正しい姿」へ書き換える**（削除しない。書き換えの事実と理由を IADR-0157 に残す）。
6. 記録の追随: IADR-0156 の改訂節／新規 IADR-0157／`docs/functional/FR-15_backtest.md`・`docs/tests/FR-15_backtest-tests.md` の「現況 4 点」／
   `docs/blocked-tasks.md`（A-3・B-4・「実装済みだが発動しない機能」）／環流記録 `feedback/20260805_adr0023_us-ohlc-source-moomoo.md` の `status`。

## 対象外（本 PR でやらないこと）

- **未確認 2 点を推測で埋めること。** 取得枠の単位・回復周期も、前復権と費用モデルの整合も**実 OpenD でしか分からない**。
  分かったふりをして既定を有効化しない。
- **ADR-0016 の 2 箇所の改訂**（決定3 の一次ゲートを `IsShortPermit` へ／決定4 の事後推定）**への追随**。
  別 issue（[#329](https://github.com/endazon/ai-stock-trading/issues/329) /
  [#331](https://github.com/endazon/ai-stock-trading/issues/331) /
  [#374](https://github.com/endazon/ai-stock-trading/issues/374)）の範囲であり、submodule 更新で ADR は入るが追随は別 PR で行う。
- **Stooq のコード・テストの削除**（ADR-0023 決定1 が候補として残すと定めている）。
- **日本株の履歴取得。** ADR-0023 決定5 が定めたのは**米国株の日足 OHLC 履歴源**である。日本株は写像せず欠測として残す。
- `LiveTradingGate.LiveTradingReleased` の変更（不変）。

## 設計

### 層と依存（既存の moomoo 発注経路と同型）

```
BacktestService.Application   IHistoricalBarSource（既存ポート・変更なし）
        ▲
BacktestService.Infrastructure
        ├─ MoomooHistoricalBarSource : IHistoricalBarSource   ← ページング・写像・欠測・警告（SDK 非依存＝単体テスト可能）
        ├─ IMoomooHistoryKLineClient（ポート）＋ SDK 非依存 DTO
        └─ MMApiMoomooHistoryKLineClient : IMoomooHistoryKLineClient, MMSPI_Qot, MMSPI_Conn   ← 実 OpenD（SDK 依存）
```

**判断の要点**: 「1 リクエスト 1,000 件・`NextReqKey` でページング」「前復権を指定する」という
**壊れると黙って結果が歪む決定**を、SDK 非依存の層（`MoomooHistoricalBarSource`）へ置く。
SDK に閉じた層へ置くと実 OpenD 無しでは検証できず、変異検査で守れない。

### provider 選択（allow-list を維持する）

現行の `ResolveProvider` は「`stooq` かつベース URL が正当なときだけ Stooq、**それ以外はすべて no-op**」である。
`moomoo` を足す際も同じ形を保つ。

```
configured switch
{
    "stooq"  when ベース URL が正当   => stooq,
    "moomoo" when OpenD 接続構成が正当 => moomoo,
    _                                  => none,   // 未知の provider は実アダプタへ落ちない
}
```

`moomoo` かつ OpenD クライアント未提供（composition root が登録していない）は、
**起動を落とさず警告して no-op へ倒す**（IADR-0105 の既存方針。バーが取れなければ Stage 0 は不合格＝安全側）。

### 取得の仕様（ADR-0023 決定5・PoC 実測に一致させる）

| 項目 | 値 | 根拠 |
| --- | --- | --- |
| K 線種別 | `KLType_Day` | 決定5 |
| 復権 | **`RehabType_Forward`（前復権）** | 決定5。外すと分割を跨いで価格が不連続になる |
| 1 リクエスト上限 | **1,000 件**（`MaxAckKLNum`） | 決定5・PoC 実測 |
| 継続 | **`NextReqKey` が返る限り繰り返す** | 決定5。止めると長期履歴が黙って切り詰められる |
| 市場 | 米国株のみ（`QotMarket_US_Security`） | 決定5 は米国株の履歴源を定めた決定である |
| 期間外のバー | 呼び出しの `[from, to]` で濾す | Stooq 実装と同じ（データ源の揺れを素通しさせない） |
| 失敗・写像不能 | **銘柄ごとの欠測（`HistoricalBarGap`）**として残し、他銘柄は続行 | 既存 `IHistoricalBarSource` の契約 |
| レート自制 | `IRateLimiter`（既定 30 回/分・0 以下は 1 回/分へクランプ） | IADR-0064「送信前に自制する」 |

### 未確認 2 点をどこに残すか

**「実装したから使える」と読める記述を作らない。** 次の 4 か所すべてに、同じ 2 点を残す。

| 場所 | 形 |
| --- | --- |
| `MoomooHistoricalBarSource.LoadBarsAsync` | **取得のたびに警告ログ**（構成した人が実行時に必ず目にする） |
| `MoomooBarDataOptions` / アダプタの XML doc・ヘッダコメント | 構成を書く人・コードを読む人が目にする |
| **IADR-0157**（新規） | 決定として固定し、テストで警告の存在を固定する |
| `docs/blocked-tasks.md` A-3 | 「実機確認が要る」項目として残す（解消済みにしない） |

## 受け入れ基準 → テスト

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 構成を何も与えなければ実効 provider は `none`（**既定は安全側**） | `HistoricalBarSourceFactoryTests.構成を何も与えなければ実効providerはnone_既定で外部へ接続しない` |
| 2 | `moomoo` を明示指定したときだけ moomoo アダプタが解決される | `HistoricalBarSourceFactoryTests.provider_moomoo_で履歴K線アダプタを組み立てる_ADR0023決定5` |
| 3 | 未知の provider は依然としてすべて no-op（allow-list 維持） | 同 `実効providerの解決は既定と構成不備でnoneを返す`（Theory） |
| 4 | `moomoo` でも OpenD 接続が構成不備なら no-op へ倒れる | 同 `moomooはOpenD接続が構成不備なら_no_opへ倒れる` |
| 5 | **`NextReqKey` が返る限りページングする**（1,000 件で切り詰めない） | `MoomooHistoricalBarSourceTests.NextReqKeyが返る限りページングして全期間のバーを取得する` |
| 6 | 1 リクエストの上限は 1,000 件を要求する | 同 `1リクエストの上限は1000件を要求する` |
| 7 | **前復権（`RehabType_Forward`）を指定する** | 同 `前復権を指定して取得する_ADR0023決定5` ／ `MMApiMoomooHistoryKLineClientMappingTests.前復権はRehabType_Forwardへ写像する` |
| 8 | 日足（`KLType_Day`）で取得する | `MMApiMoomooHistoryKLineClientMappingTests.日足はKLType_Dayへ写像する` |
| 9 | 取得失敗・米国株以外は欠測として残る（無音破棄しない） | 同 `取得に失敗した銘柄は欠測として残し他の銘柄は続行する` / `米国株以外は写像せず欠測として残す` |
| 10 | **未確認 2 点が取得のたびに警告として出る** | 同 `未確認2点を取得のたびに警告する_本番のバックテストへ流さない` |
| 11 | 期間外のバーを素通しさせない | 同 `期間外のバーは捨てる` |
| 12 | ホストの配線（`provider=moomoo` で実アダプタ・自己申告も `moomoo`） | `BacktestWorkerWiringTests` の追加 2 件 |

## 本 PR で解けないもの（issue #382 を閉じない理由）

| 残件 | なぜ解けないか |
| --- | --- |
| **取得枠 `remainQuota: 300` の単位と回復周期** | 実 OpenD が要る。銘柄数単位ならバックテスト対象銘柄数の上限が制約になる |
| **前復権の調整方式と ADR-0016 決定14 の費用モデル（借株料・配当相当額）の整合** | 実データで二重計上・欠落を確認する必要がある |
| **上記 2 点が済むまで本番のバックテストへ流さないこと** | ADR-0023 決定5 の明文の前提 |

## 検証

- `dotnet build backend/backend.slnx`（警告 0）／`dotnet test backend/backend.slnx --filter 'Category!=Integration'`
- `dotnet format --verify-no-changes`
- `node scripts/check-doc-links.js` / `check-commit-messages.js origin/develop..HEAD` /
  `check-banned-libraries.js` / `check-test-traceability.js` / `check-consumer-endpoint-names.js`
- **変異検査**（本 PR で必須。IADR-0157 に記録する）
  1. 既定 provider を `none` → `moomoo` へ変える → 落ちること（既定が安全側であることの固定）
  2. ページングを止める（`NextReqKey` を無視して 1 回で打ち切る）→ 落ちること
  3. 前復権の指定を外す（`RehabType_Forward` → `None`）→ 落ちること
