---
title: 残高 0 の口座を「検知して報告する」ところまでを実装し、止めるかどうかは裁定に委ねる
type: spec
status: accepted
related_ids: [FR-10, FR-19, NFR-07, UC-06, ADR-0041, ADR-0016, IADR-0354, IADR-0372]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0041_out-of-system-trade-adoption-and-capital-baseline.md (決定 2)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§5 注記)
---

# 仕様書: 残高 0 の検知と報告（#889。止めるかどうかは決めない）

## 起点

- #889。PR #874 の差分監査が挙げた**受け入れた残余リスク**。**挙動は変わっていない**（記述だけを実態へ直した）。
- 症状: 口座照会が `TotalAssets <= 0` を返すと供給側の門が未供給へ倒すため、**その取引日の行が 1 行も書かれない**。
  読み出し側は「当日より前の取引日で最新の行」を返すので、**前取引日の正の値が鮮度（既定 4 日）まで使われ続け、
  新規建てが止まらない**。
- 🔴 **2 つの門の帰結が非対称である**（ブローカーの 0 は止まらない／人手で 0 を 1 行入れると読み出し側が止める）。

## 🔴 本作業が「決めない」もの（最重要）

issue が挙げた 3 方向（①0 を観測した取引日を未供給として latch する ②`Power` 等で確証してから止める
③人手の Runbook に委ねる＝現状）は、**どれも選ばない。**

理由は issue 自身が書いている ——

> 🔴 **どれを採るにしても、実口座で `TrdGetFunds` が残高 0 のときに何を返すか（0 か・フィールド未設定か・
> エラーか）が分かっていない。まずそれを観測してから決めるべきである。**

**その観測は本作業では行えない。** 接続は模擬取引口座に固定されており（IADR-0016）、稼働クラスタにも触れない。
残高 0 を人為的に作ることもできない（模擬口座の残高を 0 にする操作は取引そのものである）。

したがって本作業は **「観測が届いたときに、それが見えるようにする」ところまで**を実装する。
選択肢と害は [IADR-0372](../adr/IADR-0372_zero-equity-observation-and-open-gate-decision.md)（**状態 Proposed**）に書き、
**裁定は利用者に残す**。

## 🔴 なぜ「検知と報告」だけでも価値があるのか

現状、**この状態は外から一切見えない**。

| 事象 | 現状の見え方 |
| --- | --- |
| ブローカーが 0 を返した | 発注執行の WARN ログ 1 行だけ（`資産純値が 0 以下です`）。リスク管理側には**何も届かない** |
| その結果、その取引日の行が書かれなかった | **どこにも現れない** |
| 前取引日の値が使われ続けている | **どこにも現れない**（統制は平常どおり動いて見える） |
| 人手で 0 を 1 行入れた（Runbook 経路） | 読み出し側が黙って `null` を返すだけ（ログも無い） |

🔴 **「気づけなければ止まらない」は issue の方向 3（現状維持）の害そのものである。**
裁定がどちらへ転んでも、**観測できることは前提条件**になる（方向 1 を採るなら「0 の日」の頻度が要り、
方向 3 のままなら「気づく手段」が要る）。したがって観測の追加は**どの裁定とも衝突しない**。

## 🔴 母集合（走査したファイルと除外理由）

走査に使った語: `#889` / `残高 0` / `T-10-516` / `CapitalBaselineStore` / `account_equity_days`（29 件ヒット）。

| ファイル | 扱い |
| --- | --- |
| `backend/Services/RiskManagementService/Infrastructure/Persistence/EfCapitalBaselineStore.cs` | **直す**（読み出しの帰結を分類して観測する） |
| `backend/Services/RiskManagementService/Infrastructure/Steps/BrokerAccountObservedHandler.cs` | **直す**（評価額が未供給の観測を警告にする） |
| `backend/Shared/AiStockTrading.Shared.Contracts/Observability/BusinessMetricNames.cs` / `BusinessMetrics.cs` | **直す**（新カウンタ 1 本。**末尾へ足す**） |
| `backend/Services/RiskManagementService/Program.cs` | **直す**（ストアへログと計器を渡す） |
| `deploy/observability/dashboards/ai-stock-trading-business.json` | **直す**（新カウンタのパネル。**置かないと検査 R2 が落ちる**） |
| `deploy/observability/README.md` | **直す**（系列の表） |
| `docs/tests/FR-10_risk-controls-tests.md` | **直す**（T-10-668・T-10-669 と、T-10-516 の節への追記） |
| `docs/operations/capital-baseline-seed-runbook.md` | **直す**（気づき方＝新しい系列とログを足す。**手順そのものは変えない**） |
| 各テスト（`CapitalBaselineTests` / `BrokerAccountObservedConsumerTests`） | **直す** |
| `.ai-context/adr/IADR-0354_capital-baseline-from-broker-account.md` | **直す**（§結果 フォローアップ 5 に後継 IADR を併記。**決定 7 は変えない**） |
| `.ai-context/adr/IADR-0372_*.md` / `.ai-context/adr/README.md` | **新設 / 直す** |
| `backend/Services/RiskManagementService/Features/RiskManagement/PortfolioProjection.cs` ほか読み出し側 | **直さない**。基準資金の値の扱いは変えない（本作業は観測だけ） |
| `.ai-context/specs/20260919_869_*` / `20260919_897_*` / `20260923_893_*` | **直さない**（point-in-time の凍結記録） |
| `scripts/cutover-count-reconcile.sh` / `docs/migration/20260903_*` | **直さない**（語の一致が別文脈） |
| `CHANGELOG.md` | **直さない**（生成物） |

## 射程

| 含む | 含まない（理由） |
| --- | --- |
| 読み出しの帰結を 5 つに分類して数える（供給／供給したが観測に欠落がある／行が無い／鮮度切れ／0 以下） | **門の変更**（どの帰結でも従来と同じ値を返す） |
| 評価額が未供給の観測を受けたときの警告（その取引日の行は書かれない） | 供給側（`MMApiMoomooTradeClient`）の変更。既に WARN を出している |
| 選択肢と害の記録（IADR-0372・**Proposed**） | **裁定そのもの**（実口座の応答形が未観測。利用者に残す） |
| Runbook へ「気づき方」を足す | Runbook の手順（人手投入の作法は変えない） |

## 決めたこと

### 決定 A: 読み出しの帰結を分類し、**値は変えずに**数える

`ICapitalBaselineStore.GetCurrent()` が返す値は**1 ミリも変えない**。帰結だけを次の 5 値へ分類して計上する。

| 帰結 | 意味 | 返す値 |
| --- | --- | --- |
| `Supplied` | 直前の取引日の行を供給した（平常） | 値 |
| 🔴 `SuppliedWithGap` | 供給したが、**その行は直前の取引日のものではない** | 値（従来どおり） |
| `UnavailableNoRow` | 当日より前の行が 1 行も無い | `null` |
| `UnavailableStale` | 鮮度切れ（既定 4 日超） | `null` |
| `UnavailableNonPositive` | 最新行が 0 以下（人手投入の経路） | `null` |

🔴 **`SuppliedWithGap` が #889 の症状そのものである。** 口座照会が 0 を返した日は行が書かれないため、
翌日以降の読み出しは「直前の取引日ではない行」を返し続ける。**原因（残高 0 か照会障害かプロセス停止か）を
区別しないのは意図である** —— 読み出し側から区別はできないし、**危険なのは原因ではなく状態**である。

- 取引日は**米国東部時間の暦日**で数え、巡回（既定 5 分）は土日も回るため**期待される間隔は 1 日**である
  （IADR-0354 決定 2）。`(今日 − 行の取引日) > 1 日` を欠落とする。
- `SuppliedWithGap` と `UnavailableNonPositive` は **Warning**、残りは計器のみ（ログを出さない）。
  平常の読み出しは審査のたびに起きるため、Information でも鳴りすぎる。

### 決定 B: 評価額が未供給の観測を**警告にする**（行が書かれないことを名指しする）

`BrokerAccountObservedHandler` は `EquityInBase` が `null` のとき何も書かない（正しい）。
ただし現状はその事実が Information の 1 行に埋もれている。**Warning で「この取引日の行は書かれない＝
前取引日の値が使われ続ける」と名指しする。**

🔴 **ここは「0 を観測した」と「照会できなかった」を区別できない**（供給側が両方を `null` へ畳むため）。
区別するには供給側の契約を変える必要があり、**それは裁定の対象である**（IADR-0372 選択肢 1 の一部）。
本作業では**区別しないことを明記して**警告する。

### 決定 C: 計器は 1 本だけ足す（末尾へ）

`ast.risk.capital_baseline_reads{outcome}`。帰結の 5 値をタグで持つ。
**新しい計器を 2 本以上足さない** —— ダッシュボードのパネルも 1 枚で読める。

### 決定 D: 裁定は IADR-0372（**Proposed**）に残し、本 PR では選ばない

選択肢・害・**先に必要な観測**を書き、状態を `Proposed` のままにする。
🔴 **`Accepted` にしない** —— 決めていないものを決めた形で残すと、次に読む人が「裁定済み」と誤読する。

## 受け入れ基準（issue）との対応

| # | issue の受け入れ基準 | 本 PR |
| --- | --- | --- |
| 1 | 実口座で「残高 0 のときの応答形」が記録されている | 🔴 **未達**（本作業では観測できない）。**観測の手順と、観測が届いたときに見える形**を用意した |
| 2 | 3 方向のいずれかを選び、選ばなかった側の害とともに実装 ADR へ書いてある | 🔴 **半分**。**害は書いた**（IADR-0372）が、**選んでいない**（裁定は利用者） |
| 3 | 変更するなら 2 つの門の非対称が解消されている（または意図として明記） | 🔴 **変更していない。** 非対称は**意図として明記**し、**両方の帰結が計器で区別して数えられる**ようにした |

## 受け入れ基準 → テスト

| # | 受け入れ基準（本 PR） | テスト |
| --- | --- | --- |
| 1 | 読み出しの 5 帰結が区別して数えられ、**返す値は従来と同じ** | **T-10-668** |
| 2 | 評価額が未供給の観測は警告として残り、**行は書かれない**（従来どおり） | **T-10-669** |

## 🔴 変異注入（示すこと）

| 変異 | 期待 |
| --- | --- |
| ① 欠落の判定を「常に `Supplied`」にする | **T-10-668 が赤**（#889 の症状が見えなくなる） |
| ② 未供給の観測でも行を書く（`Record` を呼ぶ） | **T-10-669 が赤**（鮮度の検査が殺され、「今日も照会できた」という起きていない事実を記録する） |

## 検証

`dotnet build backend/backend.slnx` / `dotnet test`（RiskManagementService・Shared.Contracts）/
`dotnet format --verify-no-changes` / `node scripts/check-observability-assets.js` ほか。
