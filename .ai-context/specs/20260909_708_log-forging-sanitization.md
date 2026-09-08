---
title: 外部由来文字列のログ出力前正規化（log forging / CWE-117）
type: spec
status: fixed
related_ids: [NFR, IADR-0061, IADR-0071, IADR-0116, IADR-0316]
author: Claude Code (worker)
created: 2026-09-09
updated: 2026-09-09
plan_refs:
  - https://github.com/endazon/project-planning/tree/develop/projects/ai-stock-trading/02_requirements
---

# 仕様書: 外部由来文字列のログ出力前正規化（#708）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: なし（横断の非機能。実装箇所は FR-06/16 の報告書散文・FR-11 の取引判断 LLM・FR-08 の KB 保存・FR-09 の通知）
- 非機能要件（NFR）: **無採番**。計画 `02_requirements/` の非機能要件表に「ログ出力の無害化」に当たる番号が無い。
  規約整備・脆弱性是正のメタ作業ではなく**製品の統制**であるため本来は 1 と 2 の中間だが、既存の
  セキュリティ系 NFR（NFR-05 / NFR-06 / NFR-10）はいずれもアクセス制御・秘密情報・監査であり、
  **ログの完全性（改竄されない）を指す番号は無い**。無理に近い番号を付けない（配布規約 §起点 ID の種別）。
  → **計画への環流候補**（後述「計画書との差異」）。
- ユースケース（UC）: なし
- 画面（SC）: なし
- 関連 ADR: ADR-0003（生成 AI の売買判断のガードレール）／ADR-0010（LLM ゲートウェイ経由）
- 関連 IADR: IADR-0061（プロンプト・生出力の全量記録と**既定オフ**）／IADR-0071（報告書散文の LLM 委譲）／
  IADR-0116 決定3（`ReportSummarySanitizer`＝投稿本文の無害化）／IADR-0022（`PromptSafetySanitizer`）／
  **IADR-0316（本作業で起こす。共有ヘルパの置き場と上限値）**
- 起票: #708（`MSP#1015` の受け皿）

## 目的・背景

CodeQL が `cs/log-forging`（CWE-117）を 2 件検出した。
`backend/Services/ReportService/Infrastructure/ExternalServices/HttpReportNarrativeDrafter.cs` が
`logPrompts=true` のとき、(1) プロンプト本文と (2) LLM の生出力を `ILogger` の引数へ**素通し**している。
どちらも改行・CR・ESC・U+2028 を含み得るため、**行指向のログへ偽の行を注入できる**。

**既定オフ（IADR-0061 決定1）は代替にならない。** 障害調査で有効化した瞬間に露出する。
統制は**発生源で正規化する**ことで成立させる（CLAUDE.md「統制を定める記述には現在の実現手段を併記する」）。

## 対象範囲

- 対象: 外部由来（LLM 生出力・LLM へ渡すプロンプト・外部 API 由来の文字列・Discord 由来の文言）の文字列を
  **構造化ログの引数へ素通ししている箇所**。共有ヘルパの新設と、走査で見つかった全箇所の是正。
- 対象外:
  - **ログ基盤側の対策**（Serilog の sink・OTel collector での正規化）。基盤（microservices-platform）の管掌であり、
    本リポジトリからは配備できない。発生源の正規化はそれと排他ではない（多層防御）。
  - **秘密情報のマスキング**。別の関心事であり、`RedactedUriHttpClientLogger`・`guard-secrets.js` が受け持つ。
  - **`ReportSummarySanitizer` / `PromptSafetySanitizer` の統合**。宛先（Discord 投稿・LLM プロンプト）と
    脅威モデルが違い、あちらは**改行を残す**（人が読む本文であるため）。ログ値は 1 行に収める必要があり、要件が逆である。

## 走査（母集合の取り方）

配布規約 §是正・追随の母集合の取り方（規則 1〜8）に従う。**軸を 1 本で終わらせない**。
走査基準: `develop@9519f3b`。除外パスは `--include` ではなくパス指定で行い、テスト（`Tests/`）は
「ログへ書く実装」ではないため除外した。

| 軸 | 走査式 | 生の結果 |
| --- | --- | --- |
| 1（プレースホルダ名） | `rg -nE "Log(Information\|Warning\|Error\|Debug\|Trace\|Critical)\(.*\{(Text\|Prompt\|Body\|Content\|Response\|Message\|Title\|Summary\|Narrative\|Reason\|Payload\|Raw\|Output\|Name\|Description\|Error)[A-Za-z]*\}" backend/`（`Tests/` 除外） | 13 行 |
| 2（引数名） | 上記のうち `prompt\|text\|content\|body\|title\|summary\|rationale\|raw\|payload\|narrative\|message\|headline\|snippet` を引数に持つもの | 13 行（軸 1 と 12 行が重複、`PriceMovementDetectedHandler` の `{Symbol}` が +1） |
| 3（サービス全走査） | `rg -nE "_?logger\.Log" backend/Services/NotificationService/ backend/Services/InformationCollectionService/`（Discord・収集は外部入力の入口） | 80 行超を目視 |
| **4（多行走査。軸 1 を `-U --multiline-dotall` で引き直す）** | 軸 1 と同じ語彙を `Log…\((?:[^;]{0,600}?)\{…\}` で引く | **67 行 / 23 ファイル** |

> 🔴 **軸 4 は「軸を 1 本で終わらせない」規則が実際に効いた例である。** 軸 1〜3 は**行内に収まる呼び出ししか拾えず**、
> `logger.LogWarning(` と書式文字列が別行になっている呼び出しを構造的に取りこぼしていた。
> 取りこぼしていたのは 2 件（`HttpKnowledgeBaseWriter.cs` の本文上限警告・`StooqHistoricalBarSource` 系の欠測警告）で、
> **どちらも是正対象だった**。軸 1 だけで終わっていれば「9 箇所直した」と報告して 4 箇所を残していた。

**是正対象と判定したもの（13 箇所 / 8 ファイル）**

| ファイル | 箇所 | 外部由来である理由 |
| --- | --- | --- |
| `ReportService/Infrastructure/ExternalServices/HttpReportNarrativeDrafter.cs` | 2（`prompt` / `dto.Text`） | **#708 本体。** LLM 生出力とそれを含むプロンプト |
| `TradeDecisionService/Infrastructure/ExternalServices/HttpLlmCompletionClient.cs` | 2（`prompt` / `dto.Text`） | **完全な同型。** 同じ IADR-0061 決定1 の全量記録 |
| `NotificationService/Infrastructure/ExternalServices/LoggingNotificationSender.cs` | 1（`Title` / `Content`） | 通知本文には LLM 生成の報告書要約が入る |
| `NotificationService/Infrastructure/ExternalServices/DiscordNetBotGateway.cs` | 1（Discord.Net の `LogMessage`） | ライブラリが gateway 応答・例外文言をそのまま載せる |
| `Shared.KnowledgeBase/Adapters/HttpKnowledgeBaseWriter.cs` | 5（`document.Title`。うち 1 件は**軸 4 でのみ見つかった**多行呼び出し） | KB 文書の表題には**収集した外部ニュースの見出し**が入る（`PromptSafetySanitizer` は改行を残す） |
| `Shared.KnowledgeBase/Adapters/NoOpKnowledgeBaseWriter.cs` | 1（`document.Title`） | 同上 |
| `BacktestService/Infrastructure/ExternalServices/StooqHistoricalBarSource.cs` | 1（`reason`。**軸 4 でのみ発見**） | `StooqDailyCsvParser` が診断のため**外部 CSV 応答の生の行**を理由文へ埋め込む |
| `BacktestService/Infrastructure/ExternalServices/MoomooHistoricalBarSource.cs` | 1（`reason`。**軸 4 でのみ発見**） | OpenD 由来の例外メッセージを理由文へ埋め込む |

**除外したものと理由（黙って落とさない）**

| 除外 | 理由 |
| --- | --- |
| `TradeDecisionService/.../PlaceholderProviders.cs:31` の `logger.LogWarning("{Message}", message)` | `message` は**呼び出し側のリテラル定数のみ**（`WarnOnce` は internal・引数はすべて const 文字列）。外部由来ではない |
| `CostControlService/.../LlmCostIncurredHandler.cs:49` の `{MessageId}` | Wolverine の `Envelope.Id`＝`Guid`。文字列化しても制御文字を含み得ない |
| `PriceMovementDetectedHandler.cs:38` の `{Symbol}` | 銘柄コードは内部の型付き値で、外部文字列がそのまま入る経路ではない |
| Discord 系ハンドラの `{Actor}` / `{Reason}` / `{Kind}` | `Actor` は**構成（許可リスト）由来**、`Reason` / `Kind` は内部の列挙・定型文言。利用者が打った自由文（GFV 解除理由 `clearanceReason`）は**ログへ出していない**（実測。`GoodFaithViolationCommandHandler` は `auth.Actor` しか書かない） |
| `RedactedUriHttpClientLogger` の `{Destination}` | URI 型を経由するため制御文字が入らない。既に別目的（秘密情報）の Redact 済み |
| `OrderExecutionService` の `{Reason}` 3 箇所（`OrderApprovedHandler` / `OrderAmendmentDispatcher` / `TradeExpenseRecordingLog`） | いずれも内部の列挙（`OrderDispatchForgoneReason`）・定型文言・例外**型名**であり、外部文字列が入る経路が無い（`UnavailableReason` の生成点 2 箇所を実測） |
| `TradeDecisionService` の `PublishingLlmGovernanceReporter` / `PublishingLlmUsageReporter` の `{Reason}` `{Model}` `{Purpose}` | 内部の列挙・用途名・モデル ID。モデル ID は上流由来だが**識別子の語彙**であり、本件の是正は `dto.Text` 側で足りる |
| `Tests/` 配下 | ログへ書く実装ではない |
| **例外を `logger.LogXxx(ex, "…")` の第 1 引数として渡す箇所（全リポジトリ）** | **意図的に対象外とした。** 例外は構造化ログの**引数**ではなく専用スロットへ渡っており、CodeQL の `cs/log-forging` も現状これを追跡していない。ただし外部呼び出しの `ex.Message` が本文へ載る点は同型であり、**残余リスクとして未決事項へ記す**（`ex.Message` を**文字列へ埋めて引数にしていた** 2 箇所は上表のとおり是正した） |

### 走査の陽性対照

**陰性だけで終わらせない。** 走査式が本当にヒットすることを、是正前の実ツリーで確認した ——
軸 1 の式は是正前の `HttpReportNarrativeDrafter.cs` の 2 行を実際に返しており（軸 1 の 13 行に含まれる）、
**同型を含むフィクスチャを別途作る必要がない**（実ツリーが陽性対照そのものだった）。
是正後も同じ式は同じ行数を返すが、該当行は `LogSanitizer.Sanitize(...)` を経由する形へ変わる（差分で確認できる）。

> ⚠️ **走査式は「素通し」と「正規化済み」を区別しない。** 検査器化（`scripts/`）は行わない
> （規約「検査器・規約の追加は同型事故 2 回から」。本件は 1 回目）。再発は CodeQL が拾う。

## 設計

**`backend/Shared/AiStockTrading.Shared.Contracts/Logging/LogSanitizer.cs`**（新規・純関数・外部依存ゼロ）。

```
Sanitize(string? value, int maxLength = 4000) -> string?
```

1. `char.IsControl(ch)`（C0・C1。**U+0085 NEXT LINE を含む**）と U+2028 / U+2029 を `'_'` へ 1:1 置換する。
2. `maxLength` で切り、`…(truncated N chars)` を末尾へ付ける。サロゲートペアは割らない。
3. `null` は `null` のまま返す（構造化ログの null 表現を壊さない）。

置き場と上限値の決定根拠は **IADR-0316**（`Shared.Infrastructure` を採らなかった理由を含む）。

**方針は「無害化であって検閲ではない」。** 値をログから消す実装にはしない —— 消すと
「正規化した」と「そもそも書かなかった」が区別できず、IADR-0061 決定1 の全量記録という目的自体が失われる。

## 受け入れ基準

- [x] `logPrompts=true` で改行・CR・ESC・U+2028・U+0085 を含むプロンプト／生出力を与えても、
      **実際に書かれたログ行に生の制御文字が 1 つも含まれない**（`RecordingLogger` が受け取った実レコードで検査）
- [x] 上限（既定 4,000 文字）で切られ、切ったことと落とした文字数がログ行に明示される
      （`…(truncated 25 chars)` を実測）
- [x] **陽性対照**: 同じ値を sanitize せずに書けば改行が**含まれる**ことを、同じテスト土台で示す
      （報告書側・取引判断側の 2 本）
- [x] **「書かない」で逃げていないこと**: sanitize 後もログ行に本文の識別可能な部分が**残っている**
- [x] 変異試験（実測）: 正規化呼び出しを外すと **`ReportService` 3 件・`TradeDecisionService` 1 件が赤**
      （報告書は 26/29 合格 → 3 失敗、取引判断は 31/32 合格 → 1 失敗）。**戻して両方とも全緑**（29/29・32/32）
- [x] 走査で見つかった同型 **13 箇所 / 8 ファイル**すべてが同じヘルパを通る
- [x] `dotnet build backend/backend.slnx` 緑（0 警告 0 エラー）・`dotnet format backend/backend.slnx --verify-no-changes`
      差分ゼロ・`AiStockTrading.Architecture.Tests` 緑（86 合格 / 1 skip）
- [x] `node scripts/check-trace-blocks.js` 緑（42 件）。あわせて `check-doc-links` / `check-cross-repo-refs` /
      `check-plan-id-qualification` / `gen-knowledge-graph --check` / `scripts.test.js`（320 件）も緑
- [x] 触れたプロジェクトの全テスト: 契約 382 / KB 39 / 報告書 840 / 取引判断 474 / 通知 398 / バックテスト 251 —— すべて緑

## テスト方針

| 対象 | テスト | 種別 |
| --- | --- | --- |
| `LogSanitizer` 単体 | 制御文字（LF/CR/TAB/ESC/NUL）・U+0085・U+2028/U+2029 の置換、非制御文字の保存、上限切りと注記、サロゲートペア非分割、`null` 透過、`maxLength<1` の例外 | 境界値・否定形 |
| `HttpReportNarrativeDrafter` | `RecordingLogger` で**実際に書かれたログ行**を検査。陰性（制御文字なし）＋**陽性対照**（素通しなら改行が入る）＋**残存**（本文先頭がログに残る）＋上限切り | 3 点セット |
| `HttpLlmCompletionClient` | 同型のため同じ 3 点を最小構成で置く | 同上 |

`NotificationService` / `Shared.KnowledgeBase` / `BacktestService` の 9 箇所は、**ヘルパ自体の単体テスト
（21 件）と差分で担保する**（各サービスに同型のログ検査を重ねるのは冗長であり、規約「同型・低リスクの変更は
1 PR に束ねる」に沿う）。**この判断の代償**は、それらの呼び出しが将来外されても赤くならないことである
（報告書・取引判断の 2 サービスだけが変異試験で load-bearing を実証している）。

## 計画書との差異

- 差異: あり（**計画側に「ログの完全性」に当たる NFR 番号が無い**）。
  対応: 本作業は無採番 `NFR` で進め、**計画への環流を報告に上げる**（起票はしない。親が判断する）。
  内容: 非機能要件表のセキュリティ区分へ「ログへ書き出す外部由来文字列の無害化（CWE-117）」を
  番号付きで加えるべきか。加えないなら、以後の同種是正も無採番で通る旨を明示すべきである。

## 未決事項

- 上限 4,000 文字は**調査に足りる長さ**として置いた暫定値である（プロンプトは数万文字になり得る）。
  実運用でプロンプト全量が要ると分かった時点で、`maxLength` は呼び出し側から上げられる（引数で受けてある）。
- **`logger.LogXxx(ex, "…")` の例外スロット経由で `ex.Message` がログ本文へ載る経路は残っている**
  （除外表のとおり意図的に対象外とした）。外部呼び出しの例外メッセージにはリモート由来の断片が入り得るため、
  厳密には同型である。CodeQL がこれを指摘した時点、または同型事故の 2 回目が起きた時点で再検討する。
- `MSP#1015` のクローズ（基盤側の追随）は本作業の範囲外。**AST 側の是正のみ**を行った。
- 計画への環流候補（起票はしない・報告に上げる）: 非機能要件表に「ログの完全性」に当たる ID が無い。
