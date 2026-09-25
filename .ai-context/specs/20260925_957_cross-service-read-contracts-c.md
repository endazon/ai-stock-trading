---
title: 送り手の型による契約テストが無かったサービス間の読み取り（監視銘柄・通知の kill switch／一時停止／稼働状態／GFV 解除・報告書の OpenD 稼働率／運用段階）に契約テストと送り手側の項目名の固定を足す（#957 の C）
type: spec
status: accepted
related_ids: [FR-10, FR-02, FR-03, FR-06, FR-13, FR-14, FR-15, FR-19, FR-20, UC-06, UC-07, ADR-0009, ADR-0028, IADR-0408, IADR-0390, IADR-0095, IADR-0062, IADR-0075, IADR-0182, IADR-0271]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-02 監視銘柄 / FR-10 リスク統制 / FR-14 通知・操作 / FR-19 GFV / FR-20 段階ゲート)
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md (一時停止・稼働状態の照会)
  - planning:projects/ai-stock-trading/07_adr/ADR-0028_gfv-violation-clearing-and-reconciliation.md (GFV 解除の残件数)
---

# 仕様書: サービス間の読み取り契約の残り（#957 の C 群の送り手型による契約テスト）

## 起点

- #957「C. 同（fail-closed・表示の誤り・低）」の全行。#943 の作業仕様書 `20260925_943_cross-service-read-contracts` の走査表の
  4・13・14・16・17・18 行に当たる:
  判断 ← 市場監視の監視銘柄（`GET /monitor/watchlist`）／通知 ← リスク管理の kill switch の起動・解除（`POST /risk-controls/kill-switch/*`）・
  一時停止と再開（`POST /risk-controls/pause`・`/resume`）・稼働状態（`GET /risk-controls/status`）・GFV 解除（`POST /risk-controls/good-faith-violations/clear`）／
  報告書 ← リスク管理の OpenD 稼働率（`GET /risk-controls/session-uptime`）・運用段階（`GET /risk-controls/stage-gate`）。
- 前提: A（実行時の堅牢化）は IADR-0399（市場監視）と IADR-0408（報告書の建玉・サイジング文脈）で、B は PR #970（リスク管理の期間照会）と
  PR #980（日報方針・費用統制・報告書のレビュー・段階ゲート・監査台帳）で扱った。本件は develop（`a0d600b7`）基点で作り、
  作業中に PR #980 が develop へ入ったので develop（`ddf2d835`）へ rebase した（衝突した文書・`.csproj` は両側の和集合で解いた）。
- 🔴 **受け手・送り手の実装は変えない**（テストと文書だけ）。実装の欠陥を見つけたら直さず記録する（下の「実測」の最後の行）。

## 🔴 実測（コードで確認・`origin/develop` = `a0d600b7`）

| 事実 | 出典 |
| --- | --- |
| 市場監視・リスク管理の Program.cs は `ConfigureHttpJsonOptions` を持たない＝web 既定（camelCase・列挙は数値） | 両 `Program.cs` を `Json` で grep して 0 件 |
| リスク管理の JSON 設定は T-10-805 が本物の Program.cs で固定している（設定は全エンドポイント共通） | `RiskManagementService/Tests/Features/RiskManagement/ReadContractWireFormatTests.cs` |
| 市場監視の送り手側で JSON 設定を固定するテストは無い | `MarketMonitorService/Tests` を `ReadContractWireFormat` / `DeepEquals` で grep して 0 件 |
| 監視銘柄の応答は `MonitoredSymbol(Symbol, Market)`（public）の一覧。受け手 `HttpWatchlistProvider` は自前の `WatchedSymbol` で読み、銘柄が空の行を落とす。既存テストは**受け手自身の型**を直列化していた | `MarketMonitorService/Domain/MonitoredSymbol.cs`・`HttpWatchlistProviderTests.cs` |
| kill switch・一時停止の応答は `KillSwitchState`・`PauseState`（public）。受け手は `Engaged`・`Paused` だけを読む射影 | `RiskManagementService/Features/RiskManagement/{KillSwitchState,PauseState}.cs`・`HttpKillSwitchController.cs`・`HttpPauseController.cs` |
| 稼働状態の応答は `RiskStatusView`（public・20 項目）。受け手は 15 項目の射影で、段階は int、`ActiveControl`・`BrokerProvider` は読まない | `GetRiskStatus/RiskStatusView.cs`・`HttpPauseController.RiskStatusView` |
| GFV 解除の応答の外側は**匿名型**（`clearedOrderIds`・`clearedAt`・`remainingCount`）。受け手は `ClearResultView` で読む | `ClearGoodFaithViolations/Endpoint.cs` |
| OpenD 稼働率の応答 `SessionUptimeView` は **internal**（受け手のテストから型を参照できない）。行の型 `OpenDSessionUptimeDay` は public | `GetSessionUptime/Endpoint.cs`・`Domain/OpenDUptimeReporting.cs` |
| 運用段階の応答は `StageGateStatus`（public）。受け手 `HttpStageProgressSource` は `CurrentStage` の 1 項目だけを読む | `StageGateStatus.cs`・`HttpStageProgressSource.cs` |
| 🔴 **欠陥（本件では直さない）**: 送り手 `RiskStatusView.MaxDailyOrderAmount` は `decimal?`（#869 以降、口座を照会できていない間は null）だが、受け手 `HttpPauseController.RiskStatusView.MaxDailyOrderAmount` は `decimal`。null が来ると逆直列化が `JsonException` になり、Discord の `/status` は「稼働状態の照会に失敗しました（JsonException）」を返す＝**口座を照会できていない間（新規建てが止まっている間）に稼働状態がまったく見えない**。送り手の本物の型で `Capital = null` の値を直列化した本文を読ませて実測した（下の「受け入れ基準」11） | `RiskStatusService.cs` の `MaxDailyOrderAmount: snapshot.Capital is { } … : null` |

## 決定（記録は IADR-0408 と IADR-0390 への日付つき追記。新 IADR は作らない）

1. 受け手のテストプロジェクトに送り手サービスへのテスト専用参照を extern alias で足す。判断 → 市場監視は新しい別名 `MarketMonitorWorker`
   （同名の別名は既存に無い。`RiskManagementWorker` と同じ命名規則）、通知 → リスク管理は `RiskManagementWorker`。報告書は既に
   `RiskManagementWorker` を参照している。本体の `.csproj` は参照しない。
2. 契約テストは送り手の本物の型を**送り手の実際の JSON 設定**（両送り手とも web 既定）で直列化した応答を受け手のアダプタに読ませる（PR #980 と同じ形）。
   送り手が匿名型・internal 型の外側（GFV 解除・OpenD 稼働率）は、行の値を送り手の本物の型で作り、外側を送り手と同じ項目名で組む
   （T-10-884 と同じ形）。その項目名は送り手側のテストが本物の Program.cs で固定する（T-10-885 と同じ形）。
3. 送り手側の固定: 市場監視は `GET /monitor/watchlist` の本文を `MonitoredSymbol` の一覧の web 既定の直列化と JSON の木として突き合わせる
   （T-10-805 と同じ形・市場監視では初めて）。リスク管理は既存の T-10-805 が JSON 設定を固定しているので、外側が匿名型・internal 型の 2 口だけを足す。
4. 変えない: 受け手・送り手の実装と JSON 設定。`/status` の欠陥（上表の最後の行）は直さず、PR と残余リスクへ書く。

## 受け入れ基準

1. （T-10-930）判断: `MonitoredSymbol`（日本株・米国株）を web 既定で直列化した応答から、銘柄と市場をそのまま読める（フォールバックへ倒れない）。
2. （T-10-931）市場監視の本物の Program.cs の `/monitor/watchlist` の本文が `MonitoredSymbol` の一覧の web 既定の直列化と一致し、`symbol`（文字列）・`market`（数値）を持つ。
3. （T-10-932）通知: `KillSwitchState`（起動・解除）を web 既定で直列化した応答から、起動後は `Engaged=true`、解除後は `false` と読める。
4. （T-10-933）通知: `PauseState`（停止・再開）を web 既定で直列化した応答から、停止後は `Paused=true`、再開後は `false` と読める。
5. （T-10-934）通知: `RiskStatusView`（kill switch 起動・一時停止・Stage 1・資金あり）を web 既定で直列化した応答から、稼働状態の表示に統制の ON/OFF・新規建ての停止・段階・損益・上限・ポジションが出る。
6. （T-10-935）通知: GFV 解除の応答（行の値は送り手の解除の記録から、外側は T-10-938 が固定する項目名）から、対象件数と🔴残件数（停止の継続）を読める。
7. （T-10-936）報告書: OpenD 稼働率の応答（行は `OpenDSessionUptimeDay`、外側は T-10-939 が固定する項目名）から、日ごとの稼働率と累計算入日数を読める。
8. （T-10-937）報告書: `StageGateStatus` を web 既定で直列化した応答から、現在の運用段階を読める。
9. （T-10-938）リスク管理の本物の Program.cs の GFV 解除の本文の外側の項目名が `clearedOrderIds`・`clearedAt`・`remainingCount` の 3 つである。
10. （T-10-939）リスク管理の本物の Program.cs の OpenD 稼働率の本文の外側の項目名が `days`・`stage1CumulativeCountedDays` の 2 つで、`days` は行の型を web 既定で直列化したものと一致する。
11. （T-10-940）変異注入: 送り手の通信路の名前の変更（各 1 項目）と送り手の外側の項目名・JSON 設定の変更で対応するテストが赤になる（実測をテスト仕様書へ）。
    あわせて上表の `/status` の欠陥を送り手の本物の型で実測し、結果をテスト仕様書の残余リスクに書く（テストとしては足さない＝欠陥を期待値に固定しない）。
12. 触ったテストプロジェクトの既存テストは緑。

## 🔴 母集合（規則 9〜11）

**規則 9（誤りの側の文字列で走査）**: `通知の操作結果` / `稼働／段階の照会` / `C の全行` / `#957 に残` と、受け手のアダプタ名 6 つ
（`HttpWatchlistProvider`・`HttpKillSwitchController`・`HttpPauseController`・`HttpGoodFaithViolationController`・`HttpOpenDUptimeSource`・
`HttpStageProgressSource`）を `*.md`・`*.cs`（確定済みの `.ai-context/specs`・`superpowers` を除く）で走査した（`git grep`・`origin/develop` = `a0d600b7`）。

| 箇所 | 扱い |
| --- | --- |
| `docs/tests/FR-10_risk-controls-tests.md` の #943 節の残余リスク（「日報方針・費用統制・監視銘柄・…・通知の操作結果の読み取りには…まだ無い」） | **規則 10**: 監視銘柄・通知の操作結果の分が誤りになる。直し、T-10-930〜940 の節を足す（B の分は PR #980 が直す。衝突は統合時に解く） |
| `IADR-0408` の残余リスク「…C の全行は本件に含まない（#957 に残す）」と索引行の「C の全行は #957 に残る」 | 凍結記録。日付つき追記で解消を書く（README の索引行にも追記） |
| `IADR-0390` の本文 182 行「残り（…監視銘柄・報告書の各照会・通知の操作結果）…は追随 issue #957」と末尾の追記「#943 の走査表の残り（…監視銘柄・通知の操作結果・報告書の稼働／段階の照会）は #957 に残る」 | **規則 10**: C の分は誤りになる。日付つき追記（README の索引行にも追記） |
| `IADR-0399` の 127 行（報告書・サイジングの堅牢化は #957 に残す） | **変えない**（A の記述で、IADR-0408 の追記が既に解消を書いている。本件と無関係） |
| 受け手アダプタの「同形」「射影」のコメント（6 本） | **変えない**（今も正しい。守りが契約テストへ移ったことはテスト側に書く） |
| `HttpWatchlistProviderTests.cs` の「MonitoredSymbol と WatchedSymbol は同形」 | **変えない**（正しい。受け手自身の型を直列化している既存テストはそのまま残し、送り手の型の契約は新しいテストが持つ） |
| `IADR-0062` / `0075` / `0095` / `0182` / `0271` ほかのアダプタへの言及 | **変えない**（挙動は変えていない） |

**規則 10（導出値）**: 残りの行は #980 の PR 本文の「#957 に残る分」を転記せず、#943 の走査表の「追随」の行を数え直した: 4・5・7・10〜20 の 14 行のうち、
#970 が 10〜12、#980 が 5・7・15・19・20、本件が 4・13・14・16・17・18（6 行）。21・22 は「対象外」。これで走査表の「追随」の行は全部埋まる
（#980 は rebase の時点で develop に入っている）。A の 3 アダプタは IADR-0399・IADR-0408 で済んでいる。したがって本件と #980 のマージで #957 の射程 1〜3 は満たされる。

**規則 11（窓）**: 対象の窓は「送り手だけを先に配備した」間。増える側（送り手が項目名・設定を変えて出す）のプローブは T-10-940 の変異注入、
減る側（受け手だけが新しい名前を期待する）は本件で受け手を変えないので該当しない。形（契約テストのみ／受け手の堅牢化／両方）は、C の各行が
fail-closed か表示の誤りに留まる（#957 本文の C の見出し）ため契約テストのみとした。🔴 したがって窓の間、受け手は改名された項目を従来どおり
既定値で読む（例: `MonitoredSymbol.Symbol` の改名は空の watchlist＝判断が 1 本も走らない／`RemainingCount` の改名は「停止は継続します」が出ない）。
契約テストは改名のマージを止めるだけである（残余リスクとして IADR-0408 の追記とテスト仕様書に書く）。

**テスト ID**: 割り当て範囲 **T-10-930〜T-10-949** のうち 930〜940 を使う（`git grep` で origin/* 全ブランチに未使用を確認）。941〜949 は未使用。
