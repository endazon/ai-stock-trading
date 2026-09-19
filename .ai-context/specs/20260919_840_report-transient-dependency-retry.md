---
title: 依存先が一過性に落ちている間は報告書の生成を見送って再試行し、未供給だった入力を提示時に見せる
type: spec
status: accepted
related_ids: [FR-06, FR-07, FR-09, FR-14, UC-03, UC-04, UC-05, NFR-01, ADR-0003, IADR-0051, IADR-0098, IADR-0115, IADR-0116, IADR-0240, IADR-0323, IADR-0352]
author: claude (Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-06「方針を月報→週報→日報の階層で管理する」/ FR-07「確定をもって有効になる」)
  - planning:projects/ai-stock-trading/04_workflows/03_reporting-cycle.md (ドラフト提示 → 利用者の確定)
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (完全無人での方針変更は行わない)
---

# 仕様書: 再起動直後の 401 で報告書の入力が静かに縮退し、そのまま確定まで進む（#840）

## 起点

- #840（bug・稼働環境で実測）。ホスト再起動の直後、report-service が Keycloak からサービストークンを
  取得できず（Keycloak Pod は 60 秒前に再起動したばかり）、**認証なしで送信して 401 → 未供給**へ倒れた。
  建玉・OpenD 稼働率・運用段階が未供給、散文はプレースホルダになった。
- 値を騙らない倒れ方（「建玉なし」「稼働率 0%」と書かない）は設計どおりで正しい。**問題は、倒れたまま
  報告書が出来上がり、そのまま提示・確定まで進むこと**である。自動生成の時刻と重なれば、内容の薄い報告書が
  静かに確定され、翌日の取引方針になる。
- #839（上位方針が欠けたまま確定できる）は**別 issue であり本 PR では扱わない**。

## 母集合（着手前に自分で引いた。2026-09-19・`origin/develop` = 747ff4ca）

### A. サービストークン取得の失敗を握っている箇所

`grep -rn "GetTokenAsync\|AddAiStockTradingServiceToken\|AddAiStockTradingPlatformRealmToken\|CreatePlatformRealmTokenProvider\|new ServiceTokenHandler" backend --include=*.cs`（テスト・obj を除く）。

| # | 箇所 | 役割 | 本 PR |
| --- | --- | --- | --- |
| A1 | `TestSupport/…PlatformShim/Foundation/Auth/ClientCredentialsTokenProvider.cs`（4 分岐: 非 2xx・access_token 欠落・タイムアウト・例外） | 取得失敗を `null` で返しログに残す | **ログ文言だけ**直す（「認証なしで送信し」と言い切らない。挙動は不変） |
| A2 | `…/Auth/ServiceTokenHandler.cs` | `null` ならヘッダ無しで**送信する** | 触らない（理由は IADR-0352 決定 6） |
| A3 | `…/Grpc/GrpcClientExtensions.cs`（`ApplyServiceTokenAsync`） | `null` ならメタデータ無しで送信する | 触らない（同上） |
| A4 | `…/Auth/NoServiceAccessTokenProvider.cs` | 資格情報未整備＝常に `null` | 触らない（未整備は「取得失敗」ではない） |
| A5 | 登録点: `ServiceAuthExtensions` / `PlatformRealmAuthExtensions`（REST・gRPC）/ `NotificationService…DiscordOwnerAuthExtensions` / `Shared.KnowledgeBase…KnowledgeBaseAuthExtensions` | ハンドラを鎖へ挿す | 触らない |
| A6 | 消費側（`AddAiStockTradingServiceToken` の呼び出し）: CostControl 1・InformationCollection 1・MarketMonitor 1・**Report 2**（`risk-ledger` / `audit-ledger`）・TradeDecision 4 | 名前付き HttpClient | **Report の 2 本だけ**に門を足す |
| A7 | 消費側（MSP レルム）: **Report `report-llm`**（REST）・Report の LLM gRPC チャネル・TradeDecision ほか | 同上 | **Report `report-llm`（REST）だけ**に門を足す。gRPC は保留（未配備・下記） |

### B. 報告書サービスで「未供給」へ倒す箇所

`grep -rn "未供給として扱います\|数値 0 の報告書として生成を続けます\|プレースホルダ散文に倒します" backend/Services/ReportService --include=*.cs`（テストを除く）＋ `ReportAutoGenerator` の `Safe*`（12 本）。

| # | 入力 | 供給元（HTTP クライアント） | 不達時 | 使う種別（`ReportRenderer` / `ReportDraftService` を読んで確認） |
| --- | --- | --- | --- | --- |
| B1 | 期間約定 | `HttpPeriodFillSource`（risk-ledger） | **空列**（IADR-0115 決定5） | 日・週・月 |
| B2 | 自動縮小 | `NoMarginReductionRecordSource`（HTTP なし） | null | 日・月 |
| B3 | 強制買戻し（推定） | `HttpBuyInInferenceRecordSource`（risk-ledger） | null | 日・月 |
| B4 | 為替の情報源の状態 | `HttpFxSourceStatusSource`（audit-ledger） | null | 日・月 |
| B5 | LLM 利用実績 | `HttpLlmUsageRecordSource`（audit-ledger） | null | 日・月 |
| B6 | 借株料 | `HttpBorrowFeeRecordSource`（audit-ledger） | null | 日・月 |
| B7 | 判断根拠 | `HttpTradeRationaleSource`（audit-ledger） | null | 日・週 |
| B8 | 建玉 | `HttpOpenPositionSource`（risk-ledger） | null | 日 |
| B9 | OpenD 稼働率 | `HttpOpenDUptimeSource`（risk-ledger） | null | 日・月 |
| B10 | 運用段階 | `HttpStageProgressSource`（risk-ledger） | null | 月 |
| B11 | 期末レート | `FxRateSourcePeriodEndFxRateSource`（fx＝外部 API・s2s なし） | null | 日・月 |
| B12 | Stage 0 見積り承認額 | `ConfigurationStage0RecordingEstimateSource`（構成値） | null | 月 |
| B13 | 散文 | `HttpReportNarrativeDrafter`（report-llm / gRPC） | プレースホルダ散文 | 日・週・月 |

除外と理由:

- **B12 は「未供給の入力」に数えない。** 構成値であり、未設定（＝承認が無い）が通常の状態である。依存先の
  障害では変わらない。報告書本文は従来どおり「供給されていません」と書く。
- **B11（外部の為替 API）は再試行の判定に入れない**（クラスタ内の依存ではなく、門も掛からない）。
  未供給の表示には入れる。
- `Unsupplied*`（BaseUrl 未設定）は HTTP を出さないため再試行の判定に掛からない。未供給の表示には入る
  （その報告書に入力が欠けている事実は同じ）。
- KB 保存（`AddAiStockTradingKnowledgeBase`）は**確定後**の保存であり報告書の入力ではない。射程外。

## 決定（要約。論拠は IADR-0352）

1. **門（report-service だけ）**: `risk-ledger` / `audit-ledger` / `report-llm` の鎖の最外に
   `ReportDependencyHandler` を挿す。サービストークンが有効な構成で取得できなければ**送信せず**
   `ServiceTokenUnavailableException`（`HttpRequestException` 派生）を投げる。各供給元は既存の
   `catch (Exception)` で未供給へ倒れる。資格情報が未整備（門に供給元が無い）なら従来どおり素通し。
2. **観測**: 同じハンドラが失敗を分類して、生成 1 回ぶんの観測（`ReportDependencyProbe`・AsyncLocal）へ記録する。
   - 一過性: トークン取得不能・接続不能（`HttpRequestException`）・タイムアウト（LLM を除く）・5xx・408・429
   - 恒常: 401・403・その他の 4xx（**待っても変わらない**。`GrpcAssumptionsClient.IsRetryable` と同じ考え方）
3. **見送りと再試行**: 「その種別が使う入力が未供給」かつ「その入力の取得で一過性の失敗を観測した」とき、
   **保存も提示も通知もせず**見送る。散文（LLM）は入力が揃ってから呼ぶ（見送る回に LLM 費用を出さない）。
   再試行は常駐の巡回が担う（冪等の根拠は従来どおり PeriodKey の不在）。見送りがある間だけ巡回間隔を
   **30 秒 → 60 → 120 → 240 → …（上限は通常の巡回間隔）**へ縮める。**上限 5 回**（構成可）。
4. **上限到達・恒常的な失敗**: 縮退した報告書を**従来どおり生成・提示**し、常駐が警告を残す（沈黙しない）。
5. **見せ方**: 未供給だった入力を `reports.UnsuppliedInputs`（新列）へ記録し、
   - ドラフト通知（`ReportDraftPresented.Summary`）の数値行の直後に警告行を足す（散文より優先）
   - `GET /reports/{periodKey}/review` の応答へ `unsuppliedInputs`（表示名の配列）を足し、
     通知サービスの `/report show`・版番号なしの `/report approve`（確認ボタンの前段）が警告を併記する。
6. **共有の `ServiceTokenHandler` の挙動は変えない**（報告書サービスに閉じる）。共有側はログ文言だけ直す。

## 変更しないもの

- 共有の `ServiceTokenHandler` / gRPC の資格情報付与の**挙動**（他 5 サービスへ波及させない）。
- 報告書本文（Markdown）の描画。未供給の節は従来どおり節ごとに明記されている（ゴールデンを動かさない）。
- 確定の権限・版番号付き冪等・二重確定の吸収（IADR-0024 / IADR-0240）。**確定を機械的に拒否はしない**
  （気付ける形にする、が射程。拒否は #839 の議論と併せて別途）。
- `ReportDraftPresented` の契約（フィールドを増やさない。要約の本文で伝える）。

## 影響範囲

| ファイル | 変更 |
| --- | --- |
| `ReportService/Domain/ReportInput.cs`（新規） | 入力の語彙・表示名・種別ごとの適用・永続化形式 |
| `ReportService/Features/Reports/ReportDependencyProbe.cs`（新規） | 生成 1 回ぶんの依存失敗の観測（AsyncLocal） |
| `ReportService/Features/Reports/ReportGenerationDeferralTracker.cs`（新規） | 見送り回数と次の待ち時間（プロセス内） |
| `ReportService/Features/Reports/ReportAutoGenerator.cs` | 入力 → 判定 → 散文 → 判定 → 保存。結果へ Deferred / Degraded |
| `ReportService/Infrastructure/ExternalServices/ReportDependencyHandler.cs`（新規）ほか | 門＋観測、`ServiceTokenUnavailableException` |
| `ReportService/Hosted/*` | 見送り・縮退の警告ログ、巡回間隔の短縮、構成 2 項目 |
| `ReportService/Domain/TradingReport.cs` / `ReportSummary.cs` / `Infrastructure/Persistence/*` | `UnsuppliedInputs` の保持・要約の警告行・Migration |
| `ReportService/Features/Reports/GetReportReview/Endpoint.cs` / `ReportAppService.cs` | 応答へ `unsuppliedInputs` |
| `ReportService/Program.cs` | 門の配線・DI |
| `NotificationService/…/HttpReportReviewController.cs` | `/report show` の文言へ警告を併記（無害化つき） |
| `TestSupport/…/ClientCredentialsTokenProvider.cs` | ログ文言のみ |

## 受け入れ基準

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | トークン取得が一過性に失敗した巡回では**報告書を保存・提示・通知しない**／**上流へ 1 リクエストも出さない**（否定形） | 実 DI の配線試験 |
| 2 | 次の巡回でトークンが取れれば、**未供給を含まない報告書**が生成・提示される | 同上 |
| 3 | 一過性の失敗が続けば**上限回数で打ち切り**、縮退した報告書＋警告が出る。上限前には出ない（否定形） | 単体（生成器・常駐のログ） |
| 4 | 恒常的な失敗（403）は**見送らず**、その巡回で縮退した報告書＋警告が出る | 単体 |
| 5 | 見送る回は LLM を呼ばない（否定形） | 単体 |
| 6 | 未供給だった入力が**ドラフト通知の要約**に現れる／未供給が無ければ現れない（否定形） | 単体 |
| 7 | 未供給だった入力が **`/report show`** に現れる／無ければ現れない（否定形）／外部由来の文字列を素通ししない | 単体（報告書 API・通知サービス） |
| 8 | 種別が使わない入力（週報の建玉など）は未供給に数えない（否定形） | 単体 |
| 9 | 資格情報が未整備の構成では門は素通し（従来どおり送信する） | 単体 |
| 10 | `UnsuppliedInputs` が EF ストアで往復する・既存行（NULL）は空で読める | 単体 |

## 未検証・保留

IADR-0352「保留」を参照（gRPC 経路・トークン取得失敗の細分・上流の 401 が一過性であり得る窓・確定の機械的拒否）。
