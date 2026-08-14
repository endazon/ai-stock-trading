---
title: SC-03 発生回数の対象期間の裁定（当月）を反映し、計画 pin を 130a109 へ進める
type: spec
status: approved
related_ids: [FR-21, FR-10, SC-03, UC-06, ADR-0016, IADR-0186, IADR-0188]
author: endazon (with Claude Code)
created: 2026-08-14
updated: 2026-08-14
---

# 仕様書: SC-03 の期間の裁定の反映と計画 pin の前進

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-21** / **SC-03** / FR-10 / UC-06（計画 ADR-0016 決定15）
- 裁定: **2026-08-14**（[project-planning#328](https://github.com/endazon/project-planning/issues/328)。
  反映は planning [#330](https://github.com/endazon/project-planning/pull/330)・`130a109`）
- 起点となった実装: [#470](https://github.com/endazon/ai-stock-trading/issues/470) / [IADR-0186](../adr/IADR-0186_sc03-buy-in-count-supply.md) 決定1
- 環流記録: `feedback/20260813_sc03-buy-in-count-period-undefined.md` ／ `feedback/20260814_planning-feedback-record-status-stale.md`

## 裁定の内容

計画 05_screens SC-03 §供給元の表へ次が追加された。

> **対象期間は当月（月初〜当日）とする**（［2026-08-14 追加］裁定依頼 #328）。
> 当月を採るのは、**ADR-0016 決定 15 が「発生回数」を月報（当月）へ、「発生有無」を日報（当日）へ**
> 割り当てており、**本項目の名称が「発生回数」である**ためである。
> **当日を採らない** —— 決定 15 が当日へ割り当てているのは「発生有無」であって回数ではない。
> **全期間も採らない** —— 観測は OpenD 常駐の開始後にしか届かないため**被覆が永久に成立せず、
> 恒久的に供給が始まらない**。

**これは [IADR-0186](../adr/IADR-0186_sc03-buy-in-count-supply.md) 決定1 が採った案 A そのものである。**
環流した根拠（決定15 の語彙・当日案と全期間案を採らない理由・安全側の性質は案によらないこと・
祝日の残余リスク）がそのまま計画へ載っている。

## 実装の現状（実測 2026-08-14・`develop` = `b5286fc`・計画 pin `cff0e7b`）

| 対象 | 現状 |
| --- | --- |
| `ShortSellingStatusService.BuildBuyInCount()` | **当月（月初〜当日）で被覆判定**。**裁定と一致している** |
| 計画 submodule の pin | `cff0e7b`（裁定の 3 コミット前） |
| IADR-0186 決定1・残余リスク | **「裁定待ち」「裁定が案 B・C を指すなら差し替え」と書いたまま** |
| 作業仕様書 `20260813_470_*` | 同上 |
| 環流記録 2 件 | 実装側 `status: open`。**計画側は既に `accepted`** |

### 🔴 したがって本作業はコードを変更しない

**振る舞いは既に裁定どおりである。** 変えるのは**記述だけ**であり、これは本セッションで
[IADR-0187](../adr/IADR-0187_stage1-holiday-non-detection-arbitration.md)・IADR-0188 と
繰り返し扱ってきた「陳腐化した文書」と同じ形である —— **今回は自分が 1 日前に書いた IADR-0186 が該当する。**

**「裁定待ち」と書いてあれば、次に読む者は「まだ決まっていない」と読む。** 実際には決まっている。

## 決定

### 決定1: 計画 submodule の pin を `cff0e7b` → `130a109` へ進める

pin 以降の 3 コミットのうち、本リポに関係するのは次のとおり。

| コミット | 内容 | 本リポへの影響 |
| --- | --- | --- |
| `130a109` | **SC-03 の期間を当月と裁定**（#330） | **本作業で反映する** |
| `915981a` | kit: CodeQL の PR トリガーへ paths フィルタ（planning#327） | **追随済み**（[#481](https://github.com/endazon/ai-stock-trading/issues/481)。実測で確認） |
| `4083b9e` | mondriq のドキュメントサイト | **無関係**（別プロジェクト） |

### 決定2: IADR-0186 の「裁定待ち」を「裁定済み・当月で確定」へ改める

**決定1 の本文・残余リスク・決定者欄**の 3 箇所を、日付付きの追記で是正する
（本リポの文書ポリシー＝解消済みでも消さずに残す）。

**「案 B・C を指すなら差し替えられる」という記述も改める** —— 差し替えは起きなかった。
その柔軟性を持たせた判断自体は正しかったので、**その事実は残す**。

### 決定3: 環流記録 2 件を `accepted` へ（計画側の裁定段階の転記）

IADR-0188 決定2 の規律に従い、**計画側の記録の `status` を転記する**（実装側が独自に判断しない）。

| 記録 | 計画側 | 実装側（変更後） |
| --- | --- | --- |
| `20260813_sc03-buy-in-count-period-undefined.md` | `accepted` | **`accepted`** |
| `20260814_planning-feedback-record-status-stale.md` | `accepted` | **`accepted`** |

**`20260708_trading-defaults-derived-values.md` は既に `accepted`** であり変更不要
（IADR-0188 決定2 の例外③で ② を採っていた。**裁定で計画側が追いついたため、両者が一致した**）。

### 決定4: **コードは 1 行も変えない**

`BuildBuyInCount()` は既に当月で判定している。**テストの主張も変えない。**

## やらないこと

- **`ShortSellingStatusService` の変更**（裁定と一致済み）。
- **祝日の扱いの変更**（#407 / IADR-0187 の裁定により**暦を足すことは裁定違反**。
  裁定文にも同じ残余リスクが明記された）。
- **point-in-time 記録の書き換え** —— 作業仕様書 `20260813_470_*` は日付付きの追記で是正し、
  当時の記述は消さない。

## 受け入れ基準

- [x] 計画 submodule の pin が `130a109` である
- [x] IADR-0186 に「裁定待ち」の記述が残っていない（打ち消し線＋是正の 1 箇所のみ。文書ポリシーどおり）
- [x] 環流記録 2 件が `status: accepted` である（分布: `accepted` 31 / `open` 1〔TEMPLATE〕）
- [x] `node scripts/check-feedback-dispatched.js` が OK（31 件）＋ `--self-test` OK
- [x] `REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js` が通る（**213 件**）
- [x] `PLAN_ID_PREFIXES=MSP,AST node scripts/check-plan-id-qualification.js` が OK（1720 件）
- [x] `node scripts/check-doc-links.js` が通る（459 件）
- [x] `dotnet build`（**0 Warning / 0 Error**）／`dotnet test`（**1798 件 Passed**）
- [x] `git diff -- backend/ frontend/` が**空**である（コード無変更の確認）

> 🔴 **`AiStockTrading.IntegrationTests` の 8 件はローカルで実行できない。**
> `DockerUnavailableException`（`unix:///var/run/docker.sock` へ接続不可）であり、
> **本サンドボックスに Docker が無いことが原因**である。**本作業の変更とは無関係**
> （`backend/` の差分は空である）。**CI では緑**である（本セッションの全 PR で `build-and-test` は success）。
> **「実行できなかった」ことを記録として残す** —— 通ったふりをしない。

## テスト方針

**新規のテストは足さない。** 振る舞いを変えないためである。
既存の T-10-278〜283（IADR-0186 の 6 件）が当月被覆の主張を既に固定している。

> **pin の前進で計画 ADR の実在性検査の対象が変わる。** `check-commit-messages.js` は
> planning submodule の `07_adr/` を見るため、**pin を動かすと検査の母集合が変わる**。
> 本作業では ADR の追加・削除は無いが、**`dotnet test` と全検査器を通して確認する**。
