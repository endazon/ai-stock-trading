---
title: LLM ゲートウェイの Sent=false を「機密区分による縮退」と断定せず、申告（理由・本文の要約・原因の種類）のまま残し、連続を運用者へ通知する（#1267）
type: spec
status: accepted
related_ids: [FR-04, FR-09, FR-11, FR-06, FR-14, UC-01, UC-02, ADR-0003, ADR-0010, ADR-0017, IADR-0517, IADR-0017, IADR-0104, IADR-0196, IADR-0216, IADR-0248, IADR-0316, IADR-0323, IADR-0332, IADR-0452]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04 判断根拠を必ず記録する・FR-09 エラーを Discord に通知・FR-11 後から監査できる)
  - planning:projects/ai-stock-trading/07_adr/ADR-0010 (platform LLM ゲートウェイの越境ルーティング)
  - planning:projects/ai-stock-trading/07_adr/ADR-0017 (決定2 見送りは沈黙させない・決定4 埋もれない経路)
---

# LLM ゲートウェイの Sent=false の理由を残し、連続を通知する（#1267）

## 起点

- [#1267](https://github.com/endazon/ai-stock-trading/issues/1267)（🔴 PoC の事象。2026-10-07 18:11〜19:59 UTC、取引判断の LLM 呼び出し 132 件〔22 サイクル × 保有 6〕が
  `LLM ゲートウェイが送信不可（Sent=false・機密区分による縮退）` のログだけを残して Hold に固定された。同じ purpose・機密区分で 18:06 まで成功していたため誤帰属の可能性が高い）。
- 計画: FR-04（判断根拠を必ず記録）・FR-09（エラーを Discord に通知）・FR-11（後から監査できる）・ADR-0010・ADR-0017 決定2/決定4（沈黙させない）。計画の裁定は要らない
  （計画は「Sent=false＝機密区分」とは定めていない。断定は実装の誤り）。
- 並行: MSP#1819（ゲートウェイが Sent=false の原因の種類〔EgressDenied / ProviderMissing / UpstreamError〕と上流の状態コードを構造化して返す。**フィールド名は未確定**）。

## 現況（origin/develop 5a7a1473 で確認）

| # | 事実 | 場所 |
| --- | --- | --- |
| 1 | `!dto.Sent` を一律「機密区分による縮退」とログに書き、`Text` / `RoutingReason` を捨てて定数の `HoldFallback`（「LLM ゲートウェイ送信不可のため見送り」）を返す | `TradeDecisionService/.../HttpLlmCompletionClient.cs:169-172` |
| 2 | 輸送に依らない応答 `LlmCompletionPayload` に `RoutingReason` が無い（REST の DTO も gRPC の写像も読んでいない） | `Shared.Contracts/Llm/LlmCompletionTransport.cs`・`RestLlmCompletionTransport.cs`・`GrpcLlmCompletionTransport.cs` |
| 3 | 基盤は越境の拒否（`Text`＝`RoutingReason`＝`decision.Reason`）・プロバイダ未登録（`Text`＝「呼び出し先プロバイダ X が未登録…」）・上流の不調（`Text`＝「呼び出し先 X が現在利用できません。」・`RoutingReason`＝送信可の判定理由）をどれも `Sent=false`・HTTP 200 で返す | MSP `CompletionUseCase.cs:48-138`（隣接クローン 0172d9b7） |
| 4 | Hold の判断理由（rationale）は `TradeDecisionParser` を通り、一次の打ち切り（`一次スクリーニングで見送り: rationale=`）と本判断（`LLM 判断: … rationale=`）の FR-11 ログに出る。**利用者が見る判断の理由はこれである** | `DecisionOrchestrator`・`TradeDecisionAppService` |
| 5 | 台帳の Hold の行（`TradeDecisionHeld`）は理由の名前（`LlmHold`）だけを運び、根拠を運ばない（IADR-0452 決定5） | `AuditEntryFactory.From(TradeDecisionHeld)` |
| 6 | Sent=false を運用者へ知らせる経路が無い（通知・台帳とも） | — |

## 母集合（規則 9・10。誤りの側の文字列で全文書を走査）

`git grep -n "機密区分による"`（`.ai-context/specs` を除く全追跡ファイル）と、LLM 応答の `Sent` を読む箇所
（`git grep -nE "\.Sent\b|bool Sent"` のうち `LlmCompletionPayload` / 応答 DTO のもの）を引いた。

| 箇所 | 同じ誤り | 扱い |
| --- | --- | --- |
| `TradeDecisionService/.../HttpLlmCompletionClient.cs`（ログ・`HoldFallback`） | ✅ | **是正**（ログ・判断理由・通知） |
| `ReportService/.../HttpReportNarrativeDrafter.cs:148`（ログ「機密区分による縮退」）・同 :133 のコメント | ✅ | **是正**（ログに申告。倒れ先〔プレースホルダ散文〕は不変） |
| `ReportService/.../LlmReportPolicyReviser.cs:76-77`（ログ「機密区分による縮退」・利用者への理由「縮退中」） | ✅ | **是正**（ログと利用者への理由に申告。失敗の種別 `Refused` は不変） |
| `ReportService/Features/Reports/IReportPolicyReviser.cs:39`（`Refused` の説明「機密区分による縮退」） | ✅（文書） | **是正** |
| `RestLlmCompletionTransport` / `GrpcLlmCompletionTransport`（`RoutingReason` を落とす） | 原因（材料を捨てる） | **是正**（`RoutingReason` を運ぶ。REST は予定のフィールドも寛容に読む） |
| `Shared.Contracts/Llm/LlmCompletionTransport.cs`（`Sent` の説明は「越境拒否・プロバイダ未登録・上流不調」で正しい） | — | 対象外（誤りなし） |
| `.ai-context/adr/IADR-0071/0104/0123/0332`（`Sent=false` を縮退として言及） | 「機密区分」と断定していない | 対象外（凍結記録・誤りなし） |
| MSP `CompletionDto.cs:44`（`Sent=false は機密区分による送信拒否`） | ✅（基盤） | **範囲外**（別リポ。MSP#1819 の範囲） |
| `OrderFeeQueryResult.Sent`・`ReportKnowledgeReingestService`・`PresentedNotice.Sent` | — | 対象外（LLM ゲートウェイの応答ではない同名） |
| 通知（Sent=false の連続） | 取引判断だけ | **報告書の 2 か所は通知の対象外**: 報告書は日に数回で連続の監視にならず、散文はプレースホルダとして報告書に、方針の改訂は利用者への理由として既に表に出る。取引判断だけが 5 分ごと・無言で Hold に固定される |

規則 10（是正で新たに誤りになる自分の記述）: `HttpLlmCompletionClient` 末尾の写像の注記「Sent=false は送信拒否（縮退）」を同時に直した。
`HoldFallback` は非 2xx・タイムアウト・例外の倒れ先として残り、文言「送信不可」は正しいまま。既存の試験
「拒否・送信拒否・空応答・上限到達は相互に区別できる理由になる」は新しい理由でも一意（緑を確認）。

規則 11（窓）: 該当しない。通知の判定は時間の窓ではなく**連続の回数**であり、期間は連続の最初の Sent=false から回復までを 1 つの組で運ぶ
（受け手が別の行を探して引き算しない。為替・情報源の回復イベントと同じ形）。

## 設計（IADR-0517）

1. **応答の材料**: `LlmCompletionPayload` に `RoutingReason`・`FailureKind`（`LlmGatewayUnsentKind?`）・`UpstreamStatusCode`（`int?`）を末尾の省略可能な引数で足す（既存の呼び出しは不変）。
   - REST: `routingReason` は現行の基盤が返す。`failureKind` / `upstreamStatusCode`（別名 `upstreamStatus`）は **`JsonElement?` で受けて寛容に読む** —
     文字列の既知の値（大小・`_`・`-` を無視）だけを種類にし、未知の値・数値（序数は双方で揃う保証が無い）・オブジェクト・`null` は `null`。状態コードは 100〜599 の整数（数値・数字の文字列）だけ。
     🔴 enum / int で受けると未知の値で `JsonException` になり、Sent=false の 1 件が「応答不正」へ化ける（変異 M5 で実測）。
   - gRPC: `routing_reason`（既存のフィールド 7）を運ぶ（空文字は null）。原因の種類・状態コードは proto の写しに無いため null のまま。
   - 🔴 **フィールド名は MSP#1819 の PR で確定してからマージ前に突き合わせる**（利用者から最終名の連絡を受ける）。
2. **要約**（`LlmGatewayUnsent.Summarize`・3 か所の呼び出し元で共通）: Bearer トークン・`sk-`/`pk-`/`rk-` 形式の鍵・`key=値`（api_key / token / secret / password / authorization）・
   32 文字以上の連続した英数字列を伏せ、`LogSanitizer` で行区切りを潰して 160 文字で切る（正規表現へ渡す前に 2,000 文字で切る・タイムアウト 100 ms）。
3. **記録する場所（3 つ）**:
   - **ログ**（Warning）: `purpose`・`failureKind`（無ければ「不明」）・`upstreamStatus`・`routingReason`・`gatewayText` を構造化して出す。「機密区分」と書かない。
   - **判断の理由**（利用者が見る・FR-11 ログの rationale）: `LLM ゲートウェイが送信しなかったため見送り（種別: …／上流 N／理由: …／ゲートウェイ: …）`。
     種別が無ければ「種別不明」、理由が無ければ「（ゲートウェイの申告なし）」と書き、推測で埋めない。本文が理由と同じ（越境の拒否）なら重ねない。JSON は直列化器で組む。
   - **台帳**: 連続の検知と回復を `LlmGatewayUnsentDetected` / `LlmGatewayUnsentRecovered` として AuditService が記録する（同じ相関＝連続の最初の時刻）。
     🔴 `TradeDecisionHeld` は変えない（IADR-0452 決定5「根拠は運ばない」を維持）。Hold の行ごとの根拠は従来どおり FR-11 ログ、Sent=false が続いた事実と原因は台帳の 2 行。
4. **通知**（`LlmGatewayUnsentEpisodeTracker`・`PublishingLlmGatewayUnsentNotifier`〔singleton・`TimeProvider`〕）:
   - **しきい値: Sent=false が 5 回連続**（`LlmGateway:UnsentAlertThreshold`。未設定・不正・1 未満は 5）。サイクルを数えず呼び出しの順だけを見る —
     定時の 1 サイクル（保有 6 件の一次＝6 回）の全件なら 1 サイクル内で、急変の 1 銘柄のサイクル（1〜数回）なら複数サイクルにまたがって達する。
   - **抑止: 連続 1 回につき Warning 1 通**（Discord）。Sent=true が来たら、通知済みなら**回復（Info）1 通**（期間・件数）、未通知なら黙って数え直す。
   - 非 2xx・タイムアウト・応答不正・例外は連続を進めも戻しもしない（別の Hold の系統。IADR-0104 / 0216 / 0323）。
   - 発行の失敗は状態を戻して投げ直し、`HttpLlmCompletionClient` は best-effort で握る（Hold・応答は不変）。
   - 重大度: Warning（損切りは別機構で動いており発注は出ない。Critical にすると止まった事象が埋もれる）。
5. **報告書の 2 か所**: ログ（と方針の改訂の利用者への理由）に同じ要約を載せる。倒れ先（プレースホルダ散文・案なし `Refused`）は不変。

## 受け入れ基準

1. Sent=false の各変種（越境の拒否・上流の不調＋状態コード・プロバイダ未登録・種別なし・申告なし）で、判断は Hold、理由に申告が載り「機密区分」と断定しない。
2. 予定のフィールドは寛容に読む（既知の値・別名を読み、未知の値・序数・型違い・範囲外は null で例外にしない）。
3. 要約は秘密を伏せ、1 行・切り詰め。
4. ログ・判断の理由（一次・二次）・台帳・通知へ申告が届く。
5. 5 回連続で Warning 1 通、続く間は黙る、送信で回復 1 通、未通知の途切れは何も出さない、交互は通知しない、発行の失敗は再発行する。
6. 報告書の散文・方針の改訂のログ（と利用者への理由）も申告を運ぶ。

## 試験（T-04 帯。`docs/tests` が FR-04 を採番していないため検査 5 の対象外。origin/develop で `T-0?4-` の使用 0 件を確認）

| ID | 内容 | ファイル |
| --- | --- | --- |
| T-04-001 | 越境の拒否: 種別と理由・本文が同じなら重ねない | `TradeDecisionService/Tests/.../LlmGatewayUnsentReasonTests.cs` |
| T-04-002 | 上流の不調: 状態コード・本文・機密区分と書かない | 同上 |
| T-04-003 | プロバイダ未登録 | 同上 |
| T-04-004 | 種別なし（現行の基盤）は「種別不明」・申告なしは「申告なし」（否定形） | 同上 |
| T-04-005 | 原因の種類: 既知の値（3）／未知・序数・型違い・null・bool（6。否定形） | 同上（REST の実 JSON） |
| T-04-006 | 上流の状態コード: 数値・文字列・別名（3）／範囲外・非数・小数・配列（5。否定形） | 同上 |
| T-04-007 | 要約: 秘密の伏せ字・行区切り・切り詰め（ログと理由の両方） | 同上 |
| T-04-008 | Warning ログに申告・機密区分と書かない | 同上 |
| T-04-009 | 一次の打ち切り・二次の判断の理由へ届く／引用符で JSON が壊れない | 同上 |
| T-04-010 | 通知ポート: Sent=false は原因つき・Sent=true は回復の契機／非 2xx・不正・例外は渡さない（否定形） | 同上 |
| T-04-011 | 通知の失敗で Hold・応答を壊さない | 同上 |
| T-04-012 | しきい値 5（境界 4/5）・連続の間は黙る・しきい値 1・0 は不可・既定値 | `.../LlmGatewayUnsentEpisodeTests.cs` |
| T-04-013 | 回復 1 回・期間・件数・次の連続は再通知 | 同上 |
| T-04-014 | しきい値未満の途切れ・送信だけ・交互は何も出さない（否定形） | 同上 |
| T-04-015 | 通知・回復の発行の失敗の巻き戻し | 同上 |
| T-04-016 | 通知の全文ゴールデン（Warning / Info） | `NotificationService/Tests/.../NotificationTemplateGoldenTests.cs` |
| T-04-017 | 発行: 132 件 → Warning 1・回復 1（期間 109 分を偽の時計で）／発行の失敗は投げ直して再発行 | `.../LlmGatewayUnsentEpisodeTests.cs` |
| T-04-018 | 台帳: 要約に申告・機密区分と書かない・連続と回復は同じ相関 | `AuditService/Tests/Domain/LlmGatewayUnsentAuditEntryTests.cs` |
| T-04-019 | gRPC: `routing_reason` を運び空文字は null | `Shared.Infrastructure.Tests/GrpcLlmCompletionTransportTests.cs` |
| T-04-020 | 報告書の散文のログ | `ReportService/Tests/.../LlmGatewayUnsentReportTests.cs` |
| T-04-021 | 方針の改訂の利用者への理由 | 同上 |

網羅の検査（`AuditCycleCompletenessTests`・`NotificationTemplateGoldenTests`・`EventMessageTypeNameTests`・`event-schemas.baseline.json`）へ新しい 2 イベントを足した。

### 変異の実測（変えて赤を確かめ、戻した）

| 変異 | 赤になった試験 |
| --- | --- |
| M1 Sent=false で旧い定数 `HoldFallback` を返す | T-04-001〜009 の多数（理由に申告が無い） |
| M2 しきい値の比較 `<` → `<=` | T-04-012・013・015・017（6 件） |
| M3 抑止（`_notified`）を外す | T-04-012・017 |
| M4 `Rollback` で通知済みを戻さない | T-04-015・017 |
| M5 REST の `FailureKind` を `string?` で受ける | T-04-005 の否定形 3 件（数値・オブジェクト・bool で応答不正へ化ける） |
| M6 伏せ字を外す | T-04-007 |
| M7 Sent=true で回復の契機を渡さない | T-04-010 |

## 残余

- **MSP#1819 のフィールド名・直列化（文字列か序数か）は未確定。** マージ前に MSP の PR で突き合わせる。序数で返る場合は種類が「種別不明」のままになる（安全側）。
- gRPC の写しの proto に原因の種類・状態コードが無い（MSP#1819 の確定後に写しを更新する）。
- 報告書の 2 か所は通知しない（上表）。
- しきい値の env（`LlmGateway__UnsentAlertThreshold`）は helm の values に載せていない（既定 5 で動く）。
- 通知は取引判断のプロセスごとの状態（レプリカが複数なら各 1 通）。現行は 1 レプリカ。
