---
title: 閉場前の新規建ての締め切り（審査で拒否）と閉場前の手仕舞い（既定は無効・成行で全量）をコードの統制で入れ、判断のプロンプトへ市場の現地時刻と閉場までの残り時間を渡す（#1396）
type: spec
status: done
related_ids: [FR-04, FR-10, FR-19, UC-01, UC-06, ADR-0003, ADR-0009, ADR-0040, IADR-0163, IADR-0357, IADR-0380, IADR-0470, IADR-0515, IADR-0529, IADR-0535, IADR-0558, IADR-0561]
author: claude (Claude Code)
created: 2026-10-20
updated: 2026-10-20
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md（FR-10・FR-19・FR-04）
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md（§5。本件の 2 行は反映待ち）
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md（手仕舞いは止めない）
  - planning#808（裁定 2026-10-20）
---

# 仕様書: 閉場前の新規建ての締め切りと閉場前の手仕舞い（#1396）

> 本仕様書は実装着手前に作成した（着手 2026-10-20）。基点は AST `origin/develop` `533deeb7`。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-10**（リスク統制）、**FR-19**（取引ガード）、FR-04（方針の範囲で判断する）
- 関連 ADR: ADR-0003（判断の枠）、ADR-0009（手仕舞いは統制で止めない）、ADR-0040（S1）
- 裁定: planning#808（2026-10-20、利用者）。起票: #1396（`Closes #1396`）
- 新規 IADR: **IADR-0561**

## 目的・背景

PoC（2026-10-19〜20）で、方針（daily-2026-10-17 版 5）の「保有は原則日中のみとし、取引終盤は新規建てを控えて手仕舞いを優先する」が効かなかった。
MARA を閉場の約 1 時間前（03:58 JST）に新規建てし、NVDA・MARA を夜間に持ち越した。取引判断のプロンプトには現在時刻も閉場までの残り時間も無い。
夜間はソフトウェア逆指値（S1）が保護しない（IADR-0380）。

## 裁定（planning#808 の 2026-10-20 のコメント。原文を読んだ）

1. 閉場前の新規建ての締め切り: 新設。既定 N = 60 分。市場ごとに設定でき、既定は JP・US とも 60 分。買い増し・売り増しを含める。
2. 閉場前の手仕舞い: 新設。既定 M = 15 分。既定は無効で、方針か設定で有効にする。市場ごとに設定でき、既定は JP・US とも 15 分。
   持ち越しを許す建玉は方針で銘柄を指定する。有効時の既定は全建玉を手仕舞い、方針に「持ち越し可」と明記した銘柄だけを残す。
3. 補助: 取引判断のプロンプトに市場の現地時刻と閉場までの残り時間を渡す。強制は 1・2 の統制で行う。

## 方針

1. **閉場の時刻**: 共有カーネル `MarketHours.SessionCloseOn`（新設。市場ローカルの日付の最後の閉場・半日取引日は前倒し・休場日は null）と
   `MarketSessions.FinalClose`。「N 分前以降か」は `PreCloseWindow.IsAtOrAfter`（区間は [閉場 − N, その市場ローカルの日の終わり)）。
   取引判断側は `IMarketCalendar.SessionClose`（構成の臨時休場・臨時の半日を足す）を引く。
2. **締め切り（リスク管理）**: 拒否理由 `PreCloseEntryCutoff`（序数 33・クラス A）。`RiskEvaluator` が新規建て（Open）で判定し、手仕舞いは止めない。
   `OrderScreeningService` は締め切りの構成を**必須依存**で受ける（IADR-0163 決定2）。予約の組み直し（IADR-0529 決定6）でも判定する。
   構成 `PreCloseEntryCutoff:Minutes:<Market>`（既定 60・範囲 1〜場中の長さ・範囲外は起動を止める）。
3. **手仕舞い（取引判断）**: 常駐 `PreCloseFlattenService`（60 秒ごと）→ `PreCloseFlattener`。閉場の M 分前以降の場中に、保有の全量を成行で
   決済する `TradeDecisionMade`（Close・MarketOrder）を発行する（既存の決済の経路: 審査 → OrderApproved〔出どころ TradeDecision〕→ 発注執行の成行の口）。
   有効化は構成 `PreCloseFlatten:Enabled` か方針の「閉場前の手仕舞い: 有効」の行。持ち越しは方針の「持ち越し可: …」の行（共有カーネル `PolicyPreCloseLines`）。
   処理中の決済が覆う・全注文が拒否される・保有不明・換算レートなし・参照価格なしは出さない。
4. **プロンプト**: 本判断・一次の両方に「- 市場の現地時刻: yyyy-MM-dd HH:mm（米国東部時間|日本時間） / 本日の閉場: HH:mm（残り N 分|閉場後）|なし（休場日）」。
   一次の縮退の保護分 `MarketClockReserveChars`（100）。
5. **方針の改訂の AI**: 日報の改訂に締め切り（コードが強制）と 2 つの書式の行を案内する（`PolicyRevisionPromptBuilder.PreCloseRule`）。
6. 既定値は `TradingDefaults`（`PreCloseEntryCutoffMinutes`=60・`PreCloseFlattenMinutes`=15・`PreCloseFlattenEnabled`=false）に置き試験で固定する。
   計画 §5 は反映待ち（裁定の値で実装）。

## 母集合（規則 9・10）

走査語: `RejectionReason.`（列挙の網羅の箇所）・`new OrderScreeningService(`・`IMarketCalendar`・`TradeDecisionPromptBuilder.Build(`・`BuildScreening(`・
`ReserveChars`（縮退の予算の試験）・`DateTimeOffset.UtcNow` / `SystemClock`（実時刻で審査する試験）。

| # | 箇所 | 扱い |
| --- | --- | --- |
| 1 | `Shared.Contracts/Trading/RejectionReason.cs`・`RejectionReasonClassification.cs` と序数・分類の試験 | 末尾に追加・クラス A |
| 2 | `RiskEvaluator`・`OrderScreeningService`（新規建て・予約の組み直し）・`Program.cs` | 締め切りを足す・必須依存 |
| 3 | `new OrderScreeningService(` の試験 17 か所（統合試験を含む） | 既定の締め切りを渡す |
| 4 | 実時刻で審査する試験（`TradeDecisionMadeConsumerTests`・`StopOutReentryWiringTests` の 1 本・統合試験 `EntryScreeningSerializationE2ETests`） | 締め切りの外の時刻を返す時計へ（実行時刻に結果を依らせない）。母集合は「常に締め切りの中」の変異で全試験を走らせて引いた（64 件。うち固定時刻の試験は `MoomooFillControlRegressionTests` の 15:00 JST だけが窓の中で、時刻を 13:00 JST へ移した） |
| 5 | `IMarketCalendar` の実装（本番 1・試験の stub 5） | 既定の実装を持つメソッドにし、stub は変えない |
| 6 | プロンプトの組み立て（判断のサービス・Stage 0 の記録器） | 判断のサービスだけ渡す。Stage 0 の as-of は日付（`DateOnly`）だけで時刻が無いため渡さない（行は出ない） |
| 7 | 縮退の予算を式で書く試験 4 本 | 予約を同幅足す |
| 8 | `EntryStateBlockers`（LLM の前の口）・gRPC の `EntryBlocker` | **足さない**（口の値域と proto を変えない。プロンプトの時刻と審査で足りる。下の残余） |
| 9 | `PolicyRevisionPromptBuilder` | 日報の改訂に案内を足す |
| 10 | helm `values.yaml` | 構成キーをコメントで示す（既定描画を変えない） |
| 11 | 機能仕様書・テスト仕様書（FR-10） | 節を足す |

## 受け入れ基準

- [x] AC-1: 閉場の 60 分前以降の新規建て（買い・売り・買い増し）は `PreCloseEntryCutoff` で拒否され、手仕舞いは通る（T-10-2921・T-10-2922）
- [x] AC-2: 閉場は市場の暦で求め、半日取引日は前倒し、休場日は締め切りに入らない（T-10-2919・T-10-2923）
- [x] AC-3: 分数は市場ごとの構成で変えられ、範囲外は起動を止める（T-10-2923・T-10-2925）
- [x] AC-4: 締め切りの前に承認した予約を締め切りの後に組み直して発行しない（T-10-2924）
- [x] AC-5: 閉場前の手仕舞いは既定で無効。構成か方針の行で有効になり、閉場の 15 分前以降に全建玉を成行で全量手仕舞う（T-10-2927〜T-10-2929）
- [x] AC-6: 「持ち越し可」に明記した銘柄だけを残す。自由文では有効化・持ち越しにならない（T-10-2926・T-10-2930）
- [x] AC-7: 窓の外・閉場の後・休場日・半日取引日・東証・処理中の決済・保有不明で正しく扱う（T-10-2931）
- [x] AC-8: 判断のプロンプト（本判断・一次）に市場の現地時刻と閉場までの残り時間が載る（T-10-2932）
- [x] AC-9: 既定値を `TradingDefaults` の試験で固定する（T-10-2934）
- [x] AC-10: 各統制を外す変異で赤になる（下の変異）

## 検証

- 変更したサービスの全テスト（--filter なし）: Shared.Kernel.Tests・Shared.Contracts.Tests・RiskManagementService.Tests・TradeDecisionService.Tests・ReportService.Tests
- `dotnet format --verify-no-changes`、`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`、`node scripts/check-adr-index-sync.js`
