---
title: 書き込みの 400 で、サーバの理由（本文 `{ error, details }`）を画面に出す
type: spec
status: done
related_ids: [SC-01, SC-02, FR-10, FR-13, FR-19, FR-20, NFR-19, IADR-0585, IADR-0503, IADR-0146]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
---

# 仕様書: 400 のサーバの理由を画面に出す（#1465）

> 本仕様書は実装着手前に作成した（AST `origin/develop` `f908d05f`＝#1468 のマージ後）。

## 起点（トレーサビリティ）

- 起点: #1465（#1464 の作成中に見つかった）。計画: SC-02「設定変更の一般則」（拒否の実効はサーバが持ち、画面は理由を示す）。
- 判断: [IADR-0585](../adr/IADR-0585_bad-request-body-reasons-read-on-client.md)（新規）。前提: IADR-0503 決定 3（`error` の欄は変えない）・IADR-0146（契約フィクスチャ）。

## 事実（走査の結果）

- **画面が叩く書き込みの 400 は、すべて `{ error, details? }` である**（RFC 7807 は 0 件）:
  - RiskManagementService: `settings/limits`（error＋details）・`guard`（ArgumentException の共通の写し → error）・`broker-provider`（error／error＋details）・
    `stop-loss-method`（error＋details）・`stage1-minimum-trade-count`（error／error＋details）・`high-volatility`（error／error＋details）・
    `pre-close`・`theme-concentration`（error／error＋details）
  - MarketMonitorService: `watchlist` POST / DELETE（ArgumentException の写し → error）・`movement-threshold`・`cooldown`（error）
  - ConfigurationService: `PUT /assumptions`（error）
  - 走査: `grep -rn "Results.BadRequest(" backend --include=*.cs`（試験を除く）。上記以外（段階遷移・ドリフトの採用・建玉の手仕舞い・報告書・方針・費用・監査の照会など）は
    Discord Bot・サービス間の端点で、画面は叩かない（除外理由）。それらの受け手は `error` を読む（`RiskControlsOwnerWriteGrpcService`・
    `WatchlistOwnerWriteGrpcService`・`HttpStageGateController`）。
- **画面の写しは 9 か所**（`e.details` を括弧書きで出す同じ形）: SC-01 `SettingsPage`、SC-02 `RiskSettingsPage`（上限・ガード・発注先）・`StopLossMethodForm`・
  `Stage1TradeCountForm`・`HighVolatilityForm`・`PreCloseControlsForm`・`ThemeConcentrationForm`・`WatchlistForm`・`MonitorParametersForm`。
  **9 か所とも、本番ではサーバの理由を出していなかった**（`ApiError.details` が空のため）。
- 単体試験のモックは 8 か所が理由を `ApiError.details` へ直接詰めていた（本番と違う形）。E2E も 4 本が RFC 7807 の形（`errors` / `title`・`detail`）で組んでいた。

## 決めたこと（IADR-0585）

- サーバの本文は変えない。画面が `ApiError.body` から読む（`validationReasonsOf`）。比較と却下の理由は IADR-0585。
- 400 の本文を契約フィクスチャで縛る: `risk-controls.settings-stop-loss-method.400.json`（error＋details）・`risk-controls.settings-stage1-minimum-trade-count.400.json`（error のみ）。
  型 `BadRequestBody` へ代入する（`riskContractFixtures.ts`）。

## 受け入れ基準

- A1: 損切りの実行機構の 400 で、サーバの理由が画面に出る（Vitest・E2E。本文は契約フィクスチャ）。
- A2: 9 つのフォームのどれでも、本文の `details`（無ければ `error`）が出る。単体試験のモックは本番の形（`serverBadRequest(body)`）。
- A3: E2E の 400 のモックを本番の形に改め、理由が出ることを確かめる（SC-01 前提条件・SC-02 上限・ガード・損切り）。
- A4: 変異: `validationReasonsOf` を `e.details` だけ返す形に戻すと、Vitest 6 本と E2E 4 本が赤。

## PoC での確認

- **サービスの配備は要らない**（バックエンドの差分は契約テストとフィクスチャだけで、サーバの振る舞いは変わらない）。変わるのは画面だけで、
  基盤の SPA（本ユニットを合成したもの）の次の配備で入る。
- 画面の各フォームは値域を先に止めるため、正常な操作では 400 がほぼ起きない。PoC では**本文の形だけ**を確かめる（閉場後に行う。400 は設定を変えない）:
  `trading-owner` でログインした SC-02 で、開発者ツールのコンソールから
  `fetch('/bff/risk-controls/settings/stage1-minimum-trade-count',{method:'PUT',headers:{'Content-Type':'application/json','X-MSP-CSRF':'1'},body:'{"reason":"e2e 1465"}'})`
  を送り、400 と本文 `{"error":"minimumTradeCount（件数）は必須です。"}` を確かめる（契約フィクスチャと同じ）。CSRF のヘッダ名は基盤の現行に合わせる。
- 画面に理由が出ることは L1 の E2E（`sc01-settings.spec.ts`・`sc02-risk-settings.spec.ts`）が固定している。

## ［2026-10-24 追記 / #1469 の AI レビュー］

- 🟡: 画面へ出す 1 文の組み立て（一般文言 ＋ 括弧書きの理由）が 9 か所に写されていたので、`validationMessageOf(e, general)` へまとめた。
  一般文言は i18n のカタログの都合で呼び出し側が渡す（文言とその抽出元は変わらない。`npm run i18n` の差分 0）。挙動は変えていない（既存の Vitest・E2E は無変更で緑）。
- 🟡: MarketMonitorService・ConfigurationService の 400 の契約での縛りは #1470 で追跡する。
- 🟢: 無採番の `NFR` を NFR-19（WCAG 3.3.1・3.3.3）へ替えた。IADR-0585 決定 2 に「空の `details` なら `error` へ倒れる」を足した。

