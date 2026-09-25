---
title: IADR-0420 サービス間 HTTP の読み取り契約を規約にし、送り手の型の契約テストを持たない受け手の操作を機械的に止める
type: impl-adr
status: Accepted
related_ids: [NFR, FR-10, FR-14, FR-09, IADR-0397, IADR-0390, IADR-0408, IADR-0399, IADR-0335, IADR-0128, IADR-0050, IADR-0146]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs: []
---

# IADR-0420: サービス間 HTTP の読み取り契約の規約と、その機械検査

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 [#952](https://github.com/endazon/ai-stock-trading/issues/952)。利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: [#952](https://github.com/endazon/ai-stock-trading/issues/952)（[IADR-0397](IADR-0397_composition-wiring-guard.md) の射程外として切り出された形）
- 関連する実装仕様書: [20260925_952_cross-service-read-contract-guard](../specs/20260925_952_cross-service-read-contract-guard.md)
- 関連 IADR: [IADR-0390](IADR-0390_working-entries-in-decision-input.md)（#940・#943。契約テストの最初の形 T-10-744・T-10-800・送り手側の固定 T-10-805）、
  [IADR-0408](IADR-0408_report-positions-and-sizing-context-read-tolerance.md)（#957・#990。残りの経路の契約テスト）、
  [IADR-0399](IADR-0399_monitor-position-row-tolerance.md)（市場監視の実行時の堅牢化）、
  [IADR-0397](IADR-0397_composition-wiring-guard.md)（組み立てガード。1 サービスの組み立ての内側）、
  [IADR-0335](IADR-0335_unwired-di-registration-detection.md)（同じ Architecture.Tests の静的走査とラチェットの作法）、
  IADR-0050 決定1（`Program` の曖昧を extern alias で避ける機構）、IADR-0146（フロント向けの契約フィクスチャ）。
- 計画 ID: NFR（メタ作業＝規約と検査器）。所見の是正（T-10-944）は FR-14 / FR-09 の経路。

## コンテキストと課題

「**サービス間 DTO の項目名が変わっても両サービスの試験が緑のまま**」の形が 2 回実測された:
PR #940（送り手 `WorkingEntryOrderView.Symbol` の改名で判断・リスク管理の両スイートが緑）と #943（`OpenPositionView.Symbol` の改名で
判断 751 件・市場監視 157 件・報告書 1101 件がすべて緑）。受け手のアダプタが自前の DTO で読み、手書きの JSON でしか試していないため、
実行時は既定値（null / 0 / false）で読んで「保有なし」「損切りなし」へ黙って倒れる。

個々の経路は #943・#957・#990 で「送り手の本物の型を送り手の実際の JSON 設定で直列化し、受け手のアダプタに読ませる」契約テストを
足して塞いだ（#943 の走査表の全経路）。しかし**それを守る仕組みが無い** —— 新しいアダプタ・新しい操作は契約テストなしで増え得る。
組み立てガード（IADR-0397）は 1 つの `Program.cs` しか組まず、相手サービスの応答形を知らないので、この形を見ない。
規約「検査器の追加は同型の事故が 2 回から」を満たしている。

## 決定

### 決定1: 受け手の規約 —— 本文を読む操作ごとに、送り手の本物の型による契約テストを持つ

サービス間 HTTP の受け手アダプタ（`backend/Services/<受け手>/…/ExternalServices/Http*.cs`）の**公開操作のうち応答の本文を読むもの**は、
受け手のテストに次の形の契約テストを 1 本以上持つ。

1. 受け手のテストプロジェクトが送り手のプロジェクトを `ProjectReference … Aliases="<名>Worker"` で参照し、テストは `extern alias` で
   送り手の型を使う（本体の `.csproj` は送り手を参照しない。`Program` の曖昧を避ける機構は IADR-0050 決定1）。
2. **送り手の本物の型の値**を、**送り手の実際の JSON 設定**（web 既定、報告書・費用統制は文字列列挙を足す）で直列化した本文を、
   受け手のアダプタの当該操作に読ませ、読めた値を表明する。本番の配線（Program.cs が選ぶ実装）で読ませるとなおよい（T-10-800）。
3. 送り手が匿名型・internal 型で返す外側は、行・値を送り手の本物の型で作り、外側を送り手と同じ項目名で組む（T-10-884・T-10-935）。

状態コードだけを見る操作（例: 報告書の確定）は本文の契約を持たないので対象外である。読み始めた時点で対象に入る（決定3 の判定が自動で拾う）。

### 決定2: 送り手の規約 —— JSON 設定と外側の項目名を本物の Program.cs で固定する

受け手の契約テストは「送り手がその設定で出している」ことを前提にするが、受け手の側からはそれが見えない。
したがって送り手は `ReadContractWireFormatTests` を持ち、**本物の Program.cs**（`WebApplicationFactory` の `CreateClient`）が返す本文と、
応答型を同じ設定で直列化したものを `JsonNode.DeepEquals` で突き合わせる（T-10-805・T-10-912・T-10-921・T-10-922・T-10-931）。
匿名型・internal 型の外側の項目名もここで固定する（T-10-885・T-10-938・T-10-939）。JSON 設定は Program.cs 全体で共通なので、送り手ごとに 1 本でよい。

### 決定3: 規約を守っていない経路を Architecture.Tests で列挙する（`CrossServiceReadContractTests`）

**母集合はコードから導く**（一覧を手で書かない）。

| 集合 | 導き方 |
| --- | --- |
| 送り手のルート | 各サービスの本番コードの `x.Map{Group,Get,Post,Put,Delete,Patch}("…")`。`app.Map*` はそのまま、群（`g`・`read`・`owner` 等）の葉は同じサービスの `MapGroup` の接頭辞と結ぶ。`{…}` は任意の 1 区間 |
| 受け手の単位 | `ExternalServices/Http*.cs` のアダプタに現れる文字列リテラル（コメントは除く。`CSharpSource.StringLiterals`）のうち、**他サービスのルートに一致するもの**。そのリテラルを含むメンバーから、非公開のメソッド・定数を参照している**公開操作**へ辿る。操作（とそこから参照するメンバー）が本文を読まない（`ReadFromJsonAsync`・`ReadAsStringAsync`・`Deserialize` 等が無い）なら単位にしない。単位＝`受け手/アダプタ.操作 -> 送り手 ルート` |

判定（単位ごと）: 受け手のテストのうち 1 ファイルが、① 送り手の参照に付いた別名で `extern alias` し `別名::` を使い ② アダプタの型名を書き
③ `JsonSerializer.Serialize*` を呼び、そのファイルの **1 つのテストメソッド**（`[Fact]`/`[Theory]`）が ④ 当該の操作を呼び
⑤ **送り手が宣言し、受け手の本番コードが宣言も参照もしない型**を使う。

判定の形は 3 通りを試作して選んだ（作業仕様書「試作での実測」）。**ファイル単位**は #943 の形（同じファイルに別の経路 `/working-entry-orders` の
契約 T-10-744 だけがあり、`/open-positions` は手書きの JSON）を見逃した。**メソッド単位**でも、#943 以前のサイジング文脈の試験（受け手自身の型
を直列化していたが、受け手の本番が別名で使う送り手の型 `RiskLimitSettings` が現れる）を契約と誤認した。⑤ の絞り込みで両方を捕まえ、
develop の所見は変わらない。

加えて次を表明する。

- **送り手側**: 1 本でも契約が満たされた送り手は、`JsonNode.DeepEquals` と `CreateClient` を使うテストを持つ（決定2）。
- **母集合の閉包**: 本番コードで他サービスのルートに一致するリテラルが `ExternalServices/Http*.cs` の**外**にあれば赤（そこから呼べば本検査の母集合から漏れる）。
  送り手自身の `Map*` の引数は数えない。
- **本リポジトリの外**: どのルートにも一致しない `Http*` アダプタは、送り手が本リポジトリの外であることを理由つきで一覧に載せる（載っていなければ赤）。
- **空振り検知**: アダプタ・送り手のルート・単位・送り手の数に下限（実測の約 8 割）。

### 決定4: allowlist はラチェット

`KnownWithoutContractTest`（単位の鍵→理由）と `ProvidersOutsideRepository`（アダプタ→理由）は、**所見が消えた行（実体を失った行）も赤**、
**理由に `#NNN` が無い行も赤**（IADR-0335・IADR-0397 決定4 と同じ規律）。

### 決定5: 固定データのファイル（ContractFixtures）は使わない

issue は `AiStockTrading.TestSupport.ContractFixtures` の仕組みを案に挙げたが、採らない。

| 選択肢 | 送り手の改名が同じ PR で赤になるか | 採否 |
| --- | --- | --- |
| **送り手の型を extern alias で直接参照して直列化する（採用）** | なる（受け手の契約テストが同じビルドで送り手の型を使う） | 既に全経路がこの形 |
| 固定データのファイルを送り手が生成し受け手が読む | 再生成すれば緑に戻る（再生成はオプトイン `UPDATE_CONTRACT_FIXTURES=1`）。受け手が古いファイルを読み続ける窓が残る | 不採用 |

ContractFixtures は**フロント**（別のツールチェーン。バックエンドの型を参照できない）のための仕組みであり（IADR-0146）、同じビルドの中の
バックエンド同士には型の直接参照のほうが強い。

### 決定6: 検査器は Architecture.Tests に置く（`scripts/` に置かない）

- 姉妹の検査（IADR-0397 の `CompositionWiringGuardPresenceTests`・IADR-0335 の `UnwiredDiRegistrationTests`）が同じ場所で C# のソースを静的に走査しており、
  コメント・文字列を長さを保って潰す字句器 `CSharpSource` とその試験がある（本件で文字列リテラルの抽出 `StringLiterals` を足した）。
- `scripts/` の検査器は文書・設定・コミットを読むもので、C# の字句器を持たない。置けば字句器が 2 つになり、片方だけが直る。
- `dotnet test backend/backend.slnx` に乗るので、CI の `build-and-test` がそのまま走らせる（ワークフロー・必須チェック名は無変更）。実行時間は約 2 秒。

## 実測

### develop（`a592b34f`）での所見

アダプタ 25（うち単位を持つもの 23・本リポジトリの外 2）、送り手のルート 62、単位 32（送り手 6 サービス）。

| 所見 | 分類 | 対処（本 PR） |
| --- | --- | --- |
| 通知 `HttpReportReviewController.RequestChangesAsync` → 報告書 `/reports/*/request-changes` | **本物**。受け手は応答の版を読むが、送り手の型 `ReportReview` の契約テストが無かった（改名で「版 0 を差し戻しました」と表示する。統制は送り手で成立し、表示の誤り） | 契約テスト T-10-944 を足した |
| 通知 `HttpReportReviewController.ConfirmAsync` → 報告書 `/reports/*/confirm` | **偽陽性**（状態コードだけを見る） | 判定を直した（決定3 の「本文を読まない操作は単位にしない」）。直す前の所見は 4 件（同じ判定の試作での実測） |
| 判断・費用統制 `HttpAssumptionsClient.FetchAsync` → 構成 `/assumptions`（2 件） | **偽陽性**（受け手・送り手とも共有型 `VersionedAssumptions`。#943 の走査表の 6 行） | allowlist（2 行・#943） |

最終の所見は 3 件（本物 1・偽陽性 2）で、本物は本 PR で是正した。allowlist は 2 行（偽陽性だけ）、本リポジトリの外の一覧は 2 行
（LLM ゲートウェイを呼ぶ判断 `HttpLlmCompletionClient`・報告書 `HttpReportNarrativeDrafter`）。

### 変異注入（T-10-948）

1 つずつ入れて実行し、実行ごとに変異前へ書き戻した（書き戻した後、作業ツリーに差分が無いことを確認した）。送り手の改名は、送り手の型の項目に
通信路の名前を付けて入れた（C# の名前は変えない＝IDE の改名で送り手側だけが一緒に直った状態と同じ通信路になる）。

| 変異 | 赤になったもの |
| --- | --- |
| #940 の変異: 送り手 `WorkingEntryOrderView.Symbol` を `ticker` へ | 判断 779 件中 1 件（T-10-744） |
| #943 の変異: 送り手 `OpenPositionView.Symbol` を `ticker` へ | 判断 779 件中 1 件（T-10-800）／市場監視 189 件中 21 件（T-10-803 と、送り手の型から本文を組む行の許容のテスト）／報告書 1138 件中 5 件（T-10-804 と T-10-880 の銘柄の 4 行） |
| #943 の是正前の形: 判断の `/open-positions` の契約テスト（T-10-800）を外す | 検査（T-10-945）: `HttpHeldPositionProvider.GetPositionAsync -> RiskManagementService /risk-controls/open-positions`（同じファイルの手書きの JSON のテストは数えない） |
| 同上＋サイジング文脈の契約テスト（T-10-802）も外す | 検査: 上に加えて `HttpSizingContextProvider.GetContextAsync`（既存の `HttpSizingContextProviderTests` は数えない） |
| #940 の是正前の形: T-10-744 を外す | 検査: `HttpHeldPositionProvider.GetWorkingEntryOrdersAsync` |
| T-10-944 を外す | 検査: `HttpReportReviewController.RequestChangesAsync` |
| 契約テストの無い新しいアダプタ（判断が `/risk-controls/status` を自前の型で読む） | 検査: `HttpFooProvider.GetAsync -> RiskManagementService /risk-controls/status` |
| アダプタの外（判断の `Features/`）に `/risk-controls/status` のリテラル | 検査（閉包）: 該当ファイル |
| 市場監視の `ReadContractWireFormatTests` を消す | 検査（送り手側）: `MarketMonitorService` |
| allowlist に満たされている単位（監視銘柄）を理由だけで足す | 検査（ラチェット）: 実体を失った行・issue 番号の無い行の 2 本 |

## 結果

- 良い点: 新しいアダプタ・操作が契約テストなしで入ると、`build-and-test` がその単位を名指しして赤になる。契約テストを消しても赤になる。
- 残る制約（検査が見ないもの）:
  - **値の表明の中身は見ない**。操作を呼び送り手の型を使うテストメソッドがあれば満たす。直列化した本文をその操作に実際に読ませているか、
    読めた値を表明しているかはレビューに依る（テストの中身の妥当性は 3 点セットと人手レビューが担う、という `check-test-traceability` と同じ線引き）。
  - **直列化の設定が送り手と同じかは見ない**。送り手側の固定テスト（決定2）の存在だけを見る。受け手の契約テストが送り手と違う設定で直列化していれば
    検査は緑のまま、契約テストは送り手の変更を見逃し得る。
  - ルートをリテラル以外（定数の連結・設定値）で組むアダプタは単位を持たず、「本リポジトリの外」の一覧に載せるよう赤で求められる（黙って落ちない）。
    `Http*` 以外の名前のアダプタでルートのリテラルを持つものは閉包の検査が赤にする。
  - BFF（`backend/Bff`）の中継と gRPC（`Grpc*AssumptionsClient`。proto が契約で `scripts/check-proto-contracts.js` が見る）は対象外（#943 の走査表の 21・22 行）。
  - 送り手だけを先に配備した窓の実行時の挙動は変えない（IADR-0399・IADR-0408 の経路ごとの堅牢化に依る）。
