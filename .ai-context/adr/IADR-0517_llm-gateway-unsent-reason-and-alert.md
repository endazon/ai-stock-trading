---
title: IADR-0517 LLM ゲートウェイの Sent=false は「機密区分による縮退」と断定せず、申告（理由・本文の要約・原因の種類）のままログ・判断の理由・台帳へ残し、5 回連続で Discord の Warning を 1 通（回復で Info を 1 通）出す
type: impl-adr
status: Accepted
related_ids: [FR-04, FR-09, FR-11, FR-06, FR-14, UC-01, UC-02, ADR-0003, ADR-0010, ADR-0017, IADR-0017, IADR-0104, IADR-0196, IADR-0216, IADR-0248, IADR-0316, IADR-0323, IADR-0332, IADR-0452]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-04 判断根拠を必ず記録・FR-09 エラーを Discord に通知・FR-11 後から監査できる)
  - planning:projects/ai-stock-trading/07_adr/ADR-0010 (platform LLM ゲートウェイの越境ルーティング)
  - planning:projects/ai-stock-trading/07_adr/ADR-0017 (決定2 見送りを沈黙させない・決定4 埋もれない経路)
related_specs:
  - ../specs/20261008_1267_llm-gateway-unsent-reason-and-alert.md
---

# IADR-0517: LLM ゲートウェイの Sent=false の理由を残し、連続を通知する（#1267）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-08
- 決定者: Claude Code（実装）。計画 FR-04・FR-09・FR-11・ADR-0017 決定2/決定4 の範囲の実装判断であり、計画の裁定は要らない。

## 起点・関連

- 起票: [#1267](https://github.com/endazon/ai-stock-trading/issues/1267)（PoC 2026-10-07: 判断 132 件が「機密区分による縮退」という誤った理由だけを残して 1 時間 49 分 Hold に固定され、通知も無かった）
- 作業仕様書: [`.ai-context/specs/20261008_1267_llm-gateway-unsent-reason-and-alert.md`](../specs/20261008_1267_llm-gateway-unsent-reason-and-alert.md)（現況・母集合・試験・変異の実測）
- 並行: MSP#1819（ゲートウェイが原因の種類と上流の状態コードを構造化して返す。応答契約は MSP#1824〔head 9c348848・未マージ〕で確定）
- 前提: [IADR-0104](./IADR-0104_llm-refusal-explicit-hold.md) 決定3（Hold の理由を系統別に分ける）・[IADR-0452](./IADR-0452_baseline-advances-on-judged-skip.md) 決定5（`TradeDecisionHeld` は根拠を運ばない）・
  [IADR-0196](./IADR-0196_fx-source-visibility.md)（遷移で 1 回だけ発行し、失敗は巻き戻す）

## 背景

基盤の LLM ゲートウェイは、越境の拒否・プロバイダ未登録・上流の不調（429・401・5xx）を、どれも HTTP 200 ＋ `Sent=false` で返す。
本リポの 3 か所の呼び出し元（取引判断・報告書の散文・方針の改訂）は `Sent=false` を一律「機密区分による縮退」とログに書き、
ゲートウェイが返す `Text`（原因の説明）と `RoutingReason` を捨てていた。輸送（REST / gRPC）も `RoutingReason` を運んでいなかった。

## 決定

1. **申告を運ぶ。** `LlmCompletionPayload` に `RoutingReason`・`FailureKind`（`LlmGatewayUnsentKind?`: EgressDenied / ProviderMissing / UpstreamError）・`UpstreamStatusCode` を
   省略可能な引数で足す。REST は MSP#1824 の確定名 `failureKind`（"egress_denied" / "provider_missing" / "upstream_error"）/ `upstreamStatusCode` を `JsonElement?` で受け、
   文字列の既知の値・100〜599 の整数だけを読む（未知の値・序数・型違い・欠落〔旧い基盤〕は null＝原因不明。応答全体を不正にしない）。gRPC は `routing_reason` だけを運ぶ。
2. **断定しない。** `Sent=false` のとき、ログ（Warning・構造化）と Hold の判断の理由（FR-11 ログの rationale＝利用者が見る理由）へ
   `種別: …／上流 N／理由: …／ゲートウェイ: …` を載せる。種別が無ければ「種別不明」、申告が無ければ「申告なし」と書き、推測で埋めない。
   要約は共通の `LlmGatewayUnsent.Summarize`（秘密の伏せ字・行区切りの無害化・160 文字）。報告書の 2 か所も同じ要約をログへ載せる。
   方針の改訂の利用者への理由は**原因の種類の定型文だけ**（`AI へ送信できませんでした（上流の不調。詳細は運用ログを参照）`）にし、上流の生の文言を画面に出さない（PR #1268 の AI レビュー 🟢）。
3. **台帳。** 取引判断は連続の検知と回復を `LlmGatewayUnsentDetected` / `LlmGatewayUnsentRecovered` として publish し、AuditService が同じ相関（連続の最初の時刻）で記録する。
   `TradeDecisionHeld` は変えない（IADR-0452 決定5 を維持。行ごとの根拠は FR-11 ログ）。
4. **通知。** `Sent=false` が **5 回連続**（`LlmGateway:UnsentAlertThreshold`。不正・1 未満は 5）で Discord の **Warning を 1 通**、通知済みの連続の後の `Sent=true` で
   **回復の Info を 1 通**（期間・件数）。サイクルを数えず呼び出しの順だけを見る（定時の 1 サイクルの全件でも、急変の小さなサイクルの連続でも達する）。
   **用途（一次 `trade-decision-screening`／二次 `trade-decision`）も分けずに 1 本で数え、用途別の件数（`UnsentByPurpose`）を通知・台帳・回復へ載せる**（PR #1268 の AI レビュー 🟡）。
   `Purpose` はしきい値に達した呼び出しの用途である。
   通知前に途切れた連続は黙って数え直す。非 2xx・タイムアウト・応答不正・例外は連続を進めも戻しもしない。状態は singleton（`TimeProvider`）、発行の失敗は巻き戻して投げ直し、
   呼び出し元は best-effort で握る（Hold は不変）。報告書の 2 か所は通知しない（日に数回で、結果は報告書・利用者への理由として既に表に出る）。

## 却下した案

- **呼び出しごとに通知する。** 2026-10-07 なら 132 通。埋もれて読まれなくなる。
- **サイクル単位で判定する（1 サイクルの全件・N サイクル連続）。** サイクルの境界を判定器へ運ぶ経路が定時・急変・再配信で分かれ、片方だけ漏れる。連続回数なら境界が要らず、
  定時の保有 6 件の 1 サイクルで 5 回目に達する。
- **用途ごとに連続を数える**（PR #1268 の AI レビュー 🟡 の案の 1 つ）。Sent=false はゲートウェイの状態であって用途の性質ではなく（2026-10-07 も同じ purpose・機密区分で直前まで成功していた）、
  一次と二次は同じサイクルで交互に呼ばれ得る。用途ごとに数えると同じ障害の検知が遅れ（5 回の連続が 3 と 2 に割れて通知されない）、通知も用途ごとに 2 通に割れる。
  内訳は件数として載せれば読める（安価な方の案を採った）。
- **`TradeDecisionHeld` に根拠を足す。** IADR-0452 決定5 を覆し、判断の理由（LLM の出力）を台帳の全 Hold 行へ広げることになる。今回欠けていたのは「ゲートウェイが送らなかった事実と原因」であり、
  それは連続の 2 行で台帳に残る。
- **原因の種類を enum / int で受ける。** 未知の値で `JsonException` になり、Sent=false の 1 件が「応答不正」へ化ける（変異 M5 で実測）。MSP の PR のマージ順にも依存する。
- **Critical で通知する。** 発注は出ず損切りは別機構で動く。止まった事象（損切り到達等）の通知が埋もれる。

## 結果

- 良い影響: Sent=false の原因（越境・未登録・上流の状態コード・ゲートウェイの説明）がログ・判断の理由・台帳・通知に残り、2026-10-07 の型は Warning 1 通と回復 1 通で運用者に届く。
- 悪い影響 / トレードオフ: 一過性の 5 回以上の連続（上流の短い 429 の嵐）でも 1 通出る。しきい値は構成で上げられる。
- 残余リスク:
  - 名前は MSP#1824（未マージ）の確定名。同 PR が変われば追随が要る。マージ前の基盤では両フィールドが無く「種別不明」（後方互換・安全側）。gRPC の proto の写しには原因の種類が無い（基盤の proto が足したら写しを更新する）。
  - 状態はプロセスごと（レプリカが複数なら各 1 通。現行は 1 レプリカ）。再起動で連続は 0 から数え直す（回復の通知は出ない）。
  - しきい値の env は helm の values に載せていない（既定 5）。
