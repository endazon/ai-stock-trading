---
title: 定時サイクルの再試行の連鎖全体（起点の待ち ＋ 試行 × ハンドラの上限 ＋ 再試行の待ち）がブローカの consumer_timeout に収まる試行の上限を起動時に導き、収まらない構成では起動を止める（#1194）
type: spec
status: accepted
related_ids: [FR-04, FR-02, NFR-02, NFR-13, ADR-0013, IADR-0505, IADR-0490, IADR-0129, IADR-0023, IADR-0483]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-02, FR-04, NFR-02, NFR-13)
---

# 仕様書: 定時サイクルの再試行の連鎖全体を consumer_timeout に収める（#1194）

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-04**（AI による売買判断）・FR-02（定時の取引サイクル）。非機能: NFR-02（定時サイクル 1 回の所要 10 分以内）・NFR-13（LLM 費用の統制）
- 計画 ADR: ADR-0013（メッセージングは Wolverine）
- 関連 IADR: IADR-0490（定時サイクルの上限 T の導出・前提の上限の式。本件の既知の穴の出所）・IADR-0129（共通の失敗方針 2s/10s/30s → `_error`）・
  IADR-0023（1 銘柄の失敗でサイクルを再配送させない）・IADR-0483（銘柄ごとの最終の失敗の報告）。参考: MSP の IADR-0478 決定 7（`EnsureRetryChainFits`）
- 決定の記録: **IADR-0505**（新設）
- 起票: [#1194](https://github.com/endazon/ai-stock-trading/issues/1194)（PR #1193 の独立監査 🟡1）。Refs #1169
- ブランチ: `fix/FR-04-1194-inline-retry-budget`
- 基点コミット: `origin/develop` `c20e6b9c`

## 目的・背景

IADR-0490 の 2026-10-07 追記は、定時サイクル（`InformationCollected` 1 通で監視銘柄を順に判断）のハンドラの上限 T について
「鮮度の上限 ＋ T ＜ `consumer_timeout`」で監視銘柄数の前提の上限（経路B で 12）を決めた。しかしこれは **1 回の試行**についてである。
共通の失敗方針 `OnAnyException().RetryWithCooldown(2s, 10s, 30s).Then.MoveToErrorQueue()` は**同じ配信の中で**再試行し（ack しない）、
サイクルの打ち切りや銘柄の catch の外へ漏れた例外では、再試行 1 回ごとに最大 T が足される。

## 調査（基点コミット）

| 事実 | 出典 |
| --- | --- |
| 共通の失敗方針は全ハンドラに 4 回（初回＋再試行 3 回）・待ちの和 42 秒 | `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Extensions/WolverineExtensions.cs:38-39`・`:216-220` |
| RabbitMQ の受信は Inline・1 本ずつ・先読みあり。再試行は同じ配信の中で回り、ack は最後の試行の後 | IADR-0490 決定 3（Wolverine `RabbitMqQueue.cs`）・MSP の `ConsumerHandlerTimeouts.EnsureRetryChainFits` の注記（WolverineFx.RabbitMQ 6.24.4） |
| 定時サイクルの上限 T: 既定 960 秒・経路B 1,140 秒 | `ScheduledCycleBudget.cs`・`values-local.yaml:466` |
| ハンドラの外へ漏れる失敗: サイクルの上限での打ち切り（前提超過のとき）・停止・監視銘柄の照会より前の例外。銘柄ごとの例外は catch が分離する | `InformationCollectedHandler.cs`（`catch (Exception ex) when (!cancellationToken.IsCancellationRequested)`） |
| 握りの実例: 経路B で打ち切りが続くと 600 ＋ 4 × 1,140 ＋ 42 ＝ 5,202 秒（`consumer_timeout` 1,800 秒の約 3 倍） | 導出 |
| 情報収集の巡回は本番既定・経路B とも 300 秒（鮮度の宣言 600 秒） | `deploy/helm/ai-stock-trading/values.yaml:363`・`values-local.yaml` |
| Wolverine はチェーンの失敗の規則を共通の規則より先に当てる | 本件の実測（T-10-2405: 試行の上限 1 の規則を載せたチェーンは、共通の 3 回の再試行をせず `_error` へ送った） |

## 決定（詳細は IADR-0505）

1. **握り(n) ＝ 起点の待ち W ＋ n × T ＋ 共通の待ちの先頭 n − 1 個の和**。W は鮮度の上限の既定 600 秒（判断される起点は鮮度の上限以内で始まる）。
2. 定時サイクルの試行の上限 n は、共通の上限（4）以下で **握り(n) ＜ `consumer_timeout`** となる最大の n。`consumer_timeout` は
   `Messaging:BrokerConsumerTimeoutSeconds`（既定 1,800 秒・MSP と同じキー。未設定・不正・非正値は既定）。
3. **n ＝ 1 でも収まらなければ起動を止める**（`InvalidOperationException`。等しいときも止める）。
4. `ScheduledCycleRetryPolicy` が `InformationCollected` のチェーンにだけ失敗の規則を載せる（n ＝ 1 は `MoveToErrorQueue`、n ≧ 2 は
   共通の間隔の先頭 n − 1 個で再試行 → `_error`）。他のハンドラは共通の規則のまま。
5. 既定（T 960 → 握り 1,560）・経路B（T 1,140 → 1,740）はいずれも **n ＝ 1＝定時サイクルは再試行しない**。経路B で前提を 13 にすると
   1,830 ≧ 1,800 で**起動しない**（IADR-0490 追記の「前提 12 が最大」をコードで強制する）。
6. 監視銘柄数の前提の上限の式を「**W ＋ n × T ＋ Σ待ち ＜ `consumer_timeout`**」へ改める（n ＝ 1 なら従来の式と同値）。

## 窓の形の検討（規則 11）

窓 ＝ 配信（先読み）から ack まで。増える側のプローブ P1・P2、減る側のプローブ P3・P4。

- P1（増える側: 再試行が T を足す）: 経路B（T 1,140）で打ち切りが続く。実際の握り 5,202 秒 → **弾く／試行を減らす**のが期待
- P2（増える側: 起点の待ち）: 前提 13（T 1,230）・1 回の試行。実際の握り 600 ＋ 1,230 ＝ 1,830 → **弾く**のが期待
- P3（減る側: 待ちが無い）: 巡回間隔 ≧ T の構成（例: 巡回 1,800 秒・T 1,500）。実際の握り 1,500 → **通す**のが期待
- P4（減る側: 小さい T で再試行が収まる）: T 340。4 回は 600 ＋ 1,360 ＋ 42 ＝ 2,002 で超え、3 回は 1,632 で収まる → **3 回にする**のが期待

| 形 | P1 | P2 | P3 | P4 |
| --- | --- | --- | --- | --- |
| 後の端だけ（T ＜ CT。IADR-0490 本文） | ✗（4 回のまま 5,202） | ✗（1,230 ＜ 1,800 で通す） | ✓ | ✗（4 回のまま） |
| 前の端だけ（W ＋ T ＜ CT。IADR-0490 追記） | ✗（再試行を数えない） | ✓ | ✗（2,100 で誤って弾く） | ✗（4 回のまま） |
| **両端（W ＋ 握り(n) ＜ CT。本件）** | ✓（n ＝ 1） | ✓（起動しない） | ✗（誤って弾く） | ✓（n ＝ 3） |

- **両端の形を採る。** P3 の誤検出は残余リスクとして受容する: 配備の巡回は本番既定・経路B とも 300 秒（T ＞ 巡回間隔）で P3 の構成は無い。
  受け手（trade-decision）は起動時に発行側の巡回間隔を知らない。巡回を延ばすときは W の前提（IADR-0505 決定 1）を引き直す。
- 実行時に起点ごとの宣言（`NewsStatusValidFor`）から W を読む形は、待ちが起動時に決まらず「起動時に止める」に合わないため採らない。

## 母集合（規則 9・10）

**誤りの側の文字列**（「1 回の試行について」「既知の穴」「連鎖全体」「#1194」「同じ巡回の再試行」「`consumer_timeout`」）で `git grep` した（`CHANGELOG.md` を除く）。

| ファイル | 扱い |
| --- | --- |
| `.ai-context/adr/IADR-0490_*.md:155-158`（既知の穴） | 日付つき追記を足す（原文は残す） |
| `.ai-context/adr/README.md` の IADR-0490 行 | 追記ブロックを足す |
| `.ai-context/adr/IADR-0129_*.md`（決定 5: 全ハンドラに共通の再試行） | 日付つき追記で定時サイクルの上書きを記録し、索引行にも足す |
| `docs/operations/operations.md:281-283`（既知の穴・後続の課題） | 本件の式へ改める |
| `docs/operations/operations.md:576`（「直後の同じ巡回の再試行」） | 再試行せず `_error` へ送る挙動へ改める |
| `deploy/helm/ai-stock-trading/README.md:216-217` | 本件の検査へ改める |
| `deploy/helm/ai-stock-trading/values-local.yaml:464-465`（注記） | 本件の検査へ改める（値は変えない＝描画は不変） |
| `docs/tests/FR-10_risk-controls-tests.md`（IADR-0490 の節） | T-10-2403〜T-10-2406 を足す |
| `.ai-context/specs/20261007_1169_*.md:84-86`・`20261006_1169_*.md` | **除外**（確定済みの作業仕様書＝point-in-time の記録） |
| `.github/workflows/helm.yml:459`（12 を超えると 1 回の試行の式で超える） | **除外**（記述は本件の後も正しい。n ＝ 1 で同値） |
| `InformationCollectedHandler.cs:51`（滞留と consumer_timeout） | **除外**（鮮度の判定の注記で、本件の式と矛盾しない） |
| `operations.md:320`（重複排除の保持の根拠「自動再試行 約 42 秒」） | **除外**（上限側の根拠で、定時サイクルの試行が減っても保持は足りる） |

規則 10（この変更で新たに誤りになる自分の記述）: 試験 T-10-2246 の注記は「打ち切り → 共通の再試行」の経路を本番の挙動として読ませる
→ 追記で「本番の定時サイクルは T-10-2405」と明記した。
既存の試験 `LlmCompletionClientSelectionTests` の明示設定の例（LLM の timeout 90 秒）は、既定の前提 10 銘柄で T 2,160 秒となり本件の検査で起動しなくなる
（全件の実行で 1 件赤）→ 主題（構成値の解釈）を保ったまま 40 秒へ改めた（T 1,160 秒・握り 1,760 秒）。既定の前提での timeout の上限は 41 秒。運用手順書の「試行の上限」の行は起動時のログ文言と一致させた。

## 受け入れ基準

- [x] AC1: 起動時に、再試行の連鎖全体（起点の待ち ＋ 各試行のハンドラの上限 ＋ 再試行の待ち）が `consumer_timeout` に収まる試行の上限を導く（T-10-2403）
- [x] AC2: 試行 1 回でも収まらない構成では起動を止める（T-10-2404。純関数と本番の組み立ての両方）
- [x] AC3: 定時サイクルのハンドラにだけ導いた試行の上限を適用し、既定・経路B では再試行しない（T-10-2405 実行時・T-10-2406 組み立て）。他のハンドラは変えない
- [x] AC4: 監視銘柄数の前提の上限の式を再試行を含めた形に改める（IADR-0505 決定 5・運用手順書・chart の README と注記）

## テスト（T-10-2403〜T-10-2406）

| ID | 試験 | 置き場所 |
| --- | --- | --- |
| T-10-2403 | 試行の上限・握り・待ちの導出（境界ちょうど・待ちの有無・構成値の読み） | `ScheduledCycleRetryChainTests` |
| T-10-2404 | 収まらない構成は導かない／起動しない（前提 13・ちょうど 1,800・`consumer_timeout` を延ばすと起動する） | `ScheduledCycleRetryChainTests`・`ScheduledCycleTimeoutCompositionTests` |
| T-10-2405 | 試行の上限 1 は打ち切られても再試行せず `_error`、2 なら 1 回だけ再試行（受信の実行器を通す） | `ScheduledCycleRedeliveryTests` |
| T-10-2406 | 本番の組み立てでチェーンに載る規則（既定・経路B は 1 回、T 340 は 3 回、`consumer_timeout` 3,600 は 2 回）。価格変動は共通のまま | `ScheduledCycleTimeoutCompositionTests` |

変異（5 件・すべて赤）: R1 ポリシーを登録しない（T-10-2406）／R2 起点の待ちを数えない（T-10-2403・T-10-2404）／
R3 「＜」を「≦」にする（T-10-2403 境界・T-10-2404・T-10-2405）／R4 全ハンドラへ規則を載せる（T-10-2406 他のハンドラ）／
R5 n ＝ 1 でも 1 回再試行する（T-10-2405・T-10-2406）。

## 再配備

trade-decision-service（挙動の変更）。共通の配線（shim）は読み取り専用の公開を 1 つ足しただけで、他サービスの挙動は変わらない。
