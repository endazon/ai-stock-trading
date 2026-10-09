---
title: IADR-0522 方針の改訂だけ LLM の出力上限を 8192 へ上げ、他の用途は 4096 のまま据え置く。出力上限到達は方針の改訂でも警告ログへ出す
type: impl-adr
status: Accepted
related_ids: [FR-14, FR-07, FR-04, FR-06, NFR, ADR-0011, ADR-0042, IADR-0101, IADR-0104, IADR-0431, IADR-0432]
author: claude (Claude Code)
created: 2026-10-10
updated: 2026-10-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-14・FR-07)
  - planning:projects/ai-stock-trading/07_adr/ADR-0042_discord-apply-ai-watchlist-proposal-and-revision-limit.md (決定 3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md
related_specs:
  - ../specs/20261010_243_policy-revision-max-tokens.md
---

# IADR-0522: 方針の改訂だけ出力上限を 8192 へ上げ、出力上限到達を警告ログで観測できるようにする（#243）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-10
- 決定者: Claude Code（実装）。#243 の PoC 実測（2026-10-03・2026-10-09）の推奨「policy-revision の max_tokens の引き上げ」を受けた。

## 起点・関連

- 起票: [#243](https://github.com/endazon/ai-stock-trading/issues/243)（IADR-0101 のフォローアップ 1「出力トークン実測と 4096 の再調整」）
- 前提: IADR-0101（`MaxTokens` 1024 → 4096。思考トークンと本文の合算上限）・IADR-0104 決定 5（上限到達は拒否ではなく劣化。本文は捨てず警告ログ）・
  IADR-0431（方針の改訂の LLM 呼び出し）・IADR-0432（1 日の回数上限・`policy-revision` の計上区分・月次上限の対象外）
- 仕様書: `.ai-context/specs/20261010_243_policy-revision-max-tokens.md`

## コンテキスト

PoC の実測（費用計上ログのトークン数。#243 のコメント）で、用途ごとの出力の最大は次のとおりだった。

| 用途 | 出力の最大 | 4096 に対する割合 |
| --- | --- | --- |
| trade-decision-screening（haiku 4.5） | 792 / 739 | 約 19% |
| trade-decision（sonnet 5） | 1,017 / 930 | 約 25% |
| report-daily（sonnet 5） | 1,001 | 約 24% |
| report-weekly / report-monthly（opus 5） | 931 / 1,048 | 約 26% |
| **policy-revision**（sonnet 5 / opus 5） | **3,805** / 3,378 | **約 93%** / 約 82% |

方針の改訂だけが上限に近い。出力は「方針 2000 文字＋入れ替え案＋説明の JSON」に思考トークンが加わる（IADR-0101）。利確の数値化（#1129）で方針が長くなると打ち切られるおそれがある。
打ち切られると、途中で切れた JSON は案の検証が捨てる（案なし）。閉じた JSON の直後で切れた場合は案として通り得る。

一方、打ち切り（`stop_reason=max_tokens`）の有無は PoC で確かめられなかった。調べた結果:

- 基盤のゲートウェイは REST（`CompletionApiResponse.StopReason`）・gRPC（`CompleteResponse.stop_reason`）とも終了理由を返す（MSP develop 0172d9b7 で確認。OpenAI 互換の `finish_reason=length` も `max_tokens` へ正規化する）。
  本リポジトリの輸送（`RestLlmCompletionTransport`・`GrpcLlmCompletionTransport`）も `LlmCompletionPayload.StopReason` へ写している。
- 判断（`HttpLlmCompletionClient`。一次スクリーニングと本判断）と報告書の散文（`HttpReportNarrativeDrafter`）は、`max_tokens` を既に警告ログへ出す（IADR-0104 決定 5）。
- **方針の改訂（`LlmReportPolicyReviser`）だけが専用の警告を持たない。** 案の形式違反の警告に `stopReason` が載るだけで、案として通った打ち切りは全量ログ（既定オフ）にしか残らない。

## 決定

### 決定 1: 方針の改訂だけ `MaxTokens` を 4096 → 8192 にし、他の用途は 4096 のまま据え置く

- `LlmReportPolicyReviser.MaxTokens` を 8192 とする。用途キー（report-daily 等）・計上区分（`policy-revision`）は変えない。
- 8192 の根拠: 実測の最大 3,805 の 2 倍強で、方針が今の 2 倍の長さになっても思考の作業領域を残せる。4096 の 2 倍＝基盤の既定値（4096）の整数倍で、値の由来を追いやすい。
  `max_tokens` は上限であって固定消費ではない（IADR-0101）ため、打ち切られない応答の費用は変わらない。
- 判断・一次スクリーニング・報告書の散文は **4096 のまま**とする。実測の最大は 1,017（4096 の約 25%）で余裕は十分である。**これを #243 の「4096 の再調整（過剰／不足）」の結論とする**（不足は方針の改訂だけ、過剰として下げる理由は無い）。
  下げない理由: 上限は費用を減らさず（固定消費ではない）、下げると思考が伸びた回だけ本文が空になる（判断は Hold、散文は途中で切れる）。
- 用途別の上限を構成値にはしない（IADR-0101 選択肢 2 と同じ。値を変える要件は 1 件だけで、定数で足りる）。

### 決定 2: 方針の改訂でも `stopReason=max_tokens` を案の成否と独立に警告ログへ出す

- 拒否（`refusal`）の判定の後、案の検証の前に、`LlmStopReasons.IsMaxTokens(StopReason)` なら警告ログを 1 行出す（`stopReason`・`maxTokens`・`outputTokens`・`model`）。
- 扱いは変えない。途中で切れた JSON は従来どおり検証が捨てる（案なし・形式違反）。閉じた JSON は案として返す（IADR-0104 決定 5 と同じく、本文を捨てずに劣化として記録する）。
- 判断・報告書の散文は既に警告を出すため変更しない。これで LLM を呼ぶ 3 つの呼び出し元すべてで打ち切りがログから観測できる。
- ゲートウェイの契約は変えない（終了理由は既に返っている）。基盤（MSP#380）への依頼は要らない。

## 検討した選択肢

1. **方針の改訂だけ 8192（採用）** — 実測で上限に近いのは 1 用途だけ。変更は 1 定数。
2. 全用途を 8192 — 余裕が十分な用途まで異常時の最大費用（暴走時の 1 回の上限）を 2 倍にする。判断は回数が多く（1 夜に数百回）、効く範囲が違う。
3. 方針の出力を短くする（要約化・入れ替え案の件数の制限） — プロンプトと出力の形式の変更であり、計画（ADR-0042）の案の内容に関わる。上限だけで足りる今は採らない。
4. 用途別の上限を構成値にする — 選択肢 1 で足り、可変性の根拠が無い（#243 の 2026-09-04 コメントでも見送り）。
5. 打ち切りの警告を足さず、上限の引き上げだけにする — 8192 でも打ち切りは起こり得る。観測できなければ次の再調整の根拠が残らない。

## 結果

- 良い影響: 方針の改訂が打ち切られにくくなる。打ち切られたら警告ログで分かり、上限と出力トークンが記録に残る。
- 悪い影響 / トレードオフ:
  - 方針の改訂の 1 回の最大出力が 2 倍になる。実測（sonnet 5 で出力平均 2,342・1 回 4.578 円）から出力に比例させた概算で、上限まで出力した回は sonnet 5 で 1 回十数円、opus 5 ではその数倍になり得る。
    回数は 1 日 10 回（既定。IADR-0432）で抑えられ、費用は `policy-revision` の区分で月報 §7 に載る（月次 LLM 上限の対象外）。
  - 出力が伸びると所要時間も伸びる。上限時間（`Reports:PolicyRevision:TimeoutSeconds`・既定 60 秒）を超えれば打ち切りではなくタイムアウト（案なし・安全側）になる。
- 残余リスク:
  - 打ち切りの頻度は実運用のログで測る（本 IADR は観測できるようにするまで）。
  - PoC の 10/8 の改訂で 2 回目の呼び出し（opus 5 → sonnet 5）が起きた原因（ゲートウェイのフォールバックか、利用者の再実行か）は未確認。AST の改訂は再試行しないため、割当の逸脱なら `FallbackFired` の記録に残る。
  - 本番経路の LLM 単価の与え方（#243 の 2026-09-04 コメント）は本 IADR の範囲外で、#243 に残る。

## 試験

| ID | 固定する振る舞い | 試験 |
| --- | --- | --- |
| T-10-2503 | 方針の改訂の要求は 8192、報告書の散文の要求は 4096 | `LlmReportPolicyReviserTests` |
| T-10-2504 | `max_tokens` は案の成否と独立に警告（上限・出力トークンを運ぶ）。`end_turn`・`null` では出ない | `LlmReportPolicyReviserTests` |
| T-10-2505 | 判断の要求は一次スクリーニング・本判断とも 4096 | `HttpLlmCompletionClientTests` |

## 関連

- Supersedes: なし（IADR-0101 の決定は判断・報告書の散文について有効のまま。方針の改訂の上限だけ本 IADR が改める）
- Superseded by: なし
