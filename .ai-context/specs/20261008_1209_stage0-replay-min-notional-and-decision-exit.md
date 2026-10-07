---
title: Stage 0 の記録器・バックテストの再生に、最小の名目額と判断由来の決済の後の同日・同方向の再エントリー禁止を本番と同じ判定で再現させる（#1209）
type: spec
status: accepted
related_ids: [FR-10, FR-15, FR-04, ADR-0033, ADR-0054, ADR-0011, IADR-0507, IADR-0495, IADR-0318, IADR-0498, IADR-0351, IADR-0387, IADR-0043, IADR-0260]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 リスク統制・FR-15 バックテスト)
  - planning:projects/ai-stock-trading/07_adr/ADR-0033 (Stage 0 は記録・再生方式。再生は純関数)
  - planning:projects/ai-stock-trading/07_adr/ADR-0054 (決定 3・4: Stage 0 は本番と同じ系で走らせる)
---

# Stage 0 の記録・再生に最小の名目額と判断由来の決済の後の再エントリー禁止を再現させる（#1209）

## 背景

- #1176（PR #1191・IADR-0495）が本番に 2 統制を入れた: (1) サイジングの結果の名目額が equity × `Sizing:MinEntryNotionalRatio`（既定 1%）に満たない新規建ては
  `SizedBelowMinimumNotional` で見送る（取引判断サービス）、(2) 判断由来の決済の後、その市場の現地取引日のうち同じ方向の新規建ては `DecisionExitSameDay` で拒否する（リスク管理）。
- IADR-0495 §結果は「Stage 0 の記録・バックテストは本件の 2 統制を再現しない」と残した。#1191 の独立監査（2026-10-07）が後続の起票を求め、本 issue（#1209）になった。
- 前提の #1196（記録器の二段化・PR #1211）はマージ済み（2026-10-07）。同じファイル（`Stage0DecisionRecorder.cs`）を触るが、衝突は無い。

## 受け入れ基準（issue より）

1. Stage 0 の記録・バックテストの再生で、名目額が 1% 未満の新規建ては本番と同じ理由（`SizedBelowMinimumNotional`）で見送られる。
2. 判断由来の決済のあとの同日・同方向の新規建ては、本番と同じ理由（`DecisionExitSameDay`）で見送られる。
3. 上の 2 点を、本番の判定と同値であることを示すテストで固定する。

## 計画の確認（読み取り専用）

- FR-10（起点）。FR-15（バックテスト）は再生の地点として触る。計画 ADR-0033 決定 2「再生は純関数（IADR-0043 の契約を覆さない）」、ADR-0054 決定 3「Stage 0 は本番と同じ系で走らせる」。
- 2 統制は計画（FR-10 / 05_trading-assumptions §5）に載っていない実装側の統制である（IADR-0495 §フォローアップ）。本件は計画の裁定を要しない ——
  本番に既にある統制を Stage 0 で同じ判定に通すだけであり、統制の値・範囲・方向は変えない。

## 実測（origin/develop 930b1ff7）

- 記録器（`Stage0DecisionRecorder.RecordOneAsync`）は銘柄 × 判断日ごとに独立で、プロンプトへ**保有なし**（`HeldPosition.None`）を渡す（IADR-0351 決定 7）。
  したがって記録した Buy / Sell はすべて**新規建ての枝**の判断であり、`SignedQuantity` は新規建てとしてのサイジング（`PositionSizer.CalculateCappedQuantity`）の結果である。
  名目額の判定は無い。
- 再生器（`RecordedDecisionReplayStrategy`）は記録の `SignedQuantity` を `BacktestOrder`（**差分**）としてそのまま写す。建玉はシミュレータ（`BacktestSimulator`）が
  `SignedInventory` で積む。判断日 D の注文は**翌取引日の始値**で約定する（D に注文を決め、D+1 の始値で約定）。記録は (銘柄, 市場, 判断日) で 1 件に畳む。
- したがって再生の時間軸では、D の判断で建玉を減らした（決済した）注文は D+1 の始値で約定し、D+1 の判断が同じ方向の新規建てを出すと、
  **本番の述語（`DecisionExitProjection`: 決済の承認**または約定**が当日なら数える）をそのまま当てると拒否される場面**になる。
  逆に、同じ判断日に決済と再エントリーが並ぶことは無い（1 日 1 件に畳むため）。
- 同じ戦略インスタンスを基準・コスト 2 倍・ウォークフォワードの各窓で使い回す（`Stage0ReplayEvaluation.Prepare`）。各走行は建玉ゼロから始まる。
  → 再生器は**走行をまたいで状態を持てない**。判定に要る過去の建玉と決済は、`BacktestContext.History`（その走行で当日までに渡されたバー）から決定的に組み直す。
- 再生側（BacktestService）はリスク管理サービスを参照できない（サービス間の直接参照は禁止。取引判断サービスだけが extern alias で読む既存の例外）。
  共有カーネル（`AiStockTrading.Shared.Kernel`）は両方から読める。
- 記録はまだ 1 件も作られていない（`docs/blocked-tasks.md` B-7: as-of 入力の実供給が未実装・承認値未設定）。記録の形を足しても既存の記録の読み替えは生じない。
- 採番: origin/develop の IADR の最大は 0504、開いている PR #1232 が IADR-0505 と T-10-2402〜T-10-2406 を使う。本件は **IADR-0507**・**T-10-2410〜**。

## 設計

### A. 最小の名目額（記録器で判定し、記録に残す）

| 対象 | 変更 |
| --- | --- |
| `Stage0DecisionRecord`（共有契約） | 末尾に `bool? EntryBelowMinimumNotional = null`。記録器が「新規建てとして発注すれば最小の名目額に満たない」と判定したか。null は判定していない（Hold・数量 0・本項目より前の記録） |
| `Stage0DecisionRecorder` | 構成 `MinimumEntryNotionalOptions`（本番の DI の単一の値。省略可能・未指定は既定 1%）を受ける。サイジングの結果が数量 > 0 なら、**本番と同じ `MinimumEntryNotional.IsBelow`**（名目額＝数量 × 参照価格〔基準通貨〕、equity＝サイジングの資金）で判定して記録へ載せる。**数量は 0 にしない** |
| `Stage0StrategyIdentity` | 判定が null でないときだけ同一性に含める（旧記録の戦略 ID を変えない） |
| 再生器 | その注文が再生の時点で**新規建て**（建玉 0、または建玉と同じ符号）で、判定が true なら注文を写さない（理由 `SizedBelowMinimumNotional`） |

- 記録器で数量を 0 にしない理由: 再生では記録の Sell / Buy が保有の決済として働くことがある（記録器は保有を知らない）。本番は決済に名目額の判定を掛けない（FR-10「手仕舞いは止めない」）。
  数量を消すと、再生で決済が消えて建玉が開いたまま残る。判定（記録器）と適用（再生器。建玉を知る側）を分ければ、新規建てにだけ効く。
- 記録器の判定に使う値（数量・参照価格・換算レート・資金）は記録器のサイジングと同じ値であり、本番の判定（`TradeDecisionAppService` のサイジングの直後）と同じ関数・同じしきい値の構成を通る。

### B. 判断由来の決済の後の同日・同方向（再生器で判定する）

| 対象 | 変更 |
| --- | --- |
| `AiStockTrading.Shared.Kernel.Trading.DecisionExitReentry`（新規・純関数） | 「当日の判断由来の決済の有無（方向ごと）」と「新規建ての方向がそれに当たるか」の述語。入力は決済ごとの (決済の売買方向, 承認の取引日, 約定の取引日の列) と当日の取引日 |
| `RiskManagementService.Features.RiskManagement.DecisionExitProjection` | 由来・市場で絞り、時刻を `TradingDay.Of(・, market)` で取引日へ写してから、上の述語へ委ねる（挙動は不変。既存の試験 T-10-2315〜T-10-2321 がそのまま通ること） |
| `DecisionExitReentrySupply.ForEntry` | 上の述語の方向の規則へ委ねる（挙動は不変） |
| 再生器 | 当日までのバーから、その走行の建玉と決済を判断日ごとに組み直す。建玉を減らす注文（符号が建玉と逆）を**判断由来の決済**とし、承認の取引日＝判断日、約定の取引日＝約定したバーの日とする。新規建てで同じ方向の決済が当日（承認または約定）にあれば注文を写さない（理由 `DecisionExitSameDay`） |

- 再生の注文はすべて記録した AI 判断から出るので、建玉を減らす注文は判断由来の決済である（損切り・利用者の手仕舞いは再生に無い）。
- 建玉を跨ぐ注文（ロング 5 に売り 10）は決済として扱う（新規建ての判定を掛けない）。本番の判断の決済は保有の全量で跨がないため、跨ぐのは再生の近似だけである。
- 両方に当たる注文は `DecisionExitSameDay` を先に評価する（本番では新規建ての可否の口が LLM の前に止め、名目額の判定まで届かない）。

### C. 見送りの観測

- 再生器に `Replay(history, asOf)`（当日の注文と、その走行で当日までに見送った新規建ての一覧〔判断日・銘柄・数量・理由〕）を足す。`DecideOrders` はその当日の注文を返す。
- 理由の型は本番の列挙をそのまま使う（`DecisionSkipReason.SizedBelowMinimumNotional` / `RejectionReason.DecisionExitSameDay`）。

## 母集合（規則 9〜11）

規則 9: 誤りの側の文字列（「再現しない」「そのまま注文へ写す」「そのまま BacktestOrder へ写す」「記録した判断列をそのまま」）で `docs/`・`.ai-context/adr/`・`backend/` を走査した。

| 箇所 | 種別 | 扱い |
| --- | --- | --- |
| `.ai-context/adr/IADR-0495_*.md` §結果「Stage 0 の記録・バックテストは本件の 2 統制を再現しない」 | 凍結記録 | 本文は書き換えず、日付つき追記（IADR-0507 で再現した）を足す |
| `docs/tests/FR-10_risk-controls-tests.md` の #1176 節の残余リスク「疑似の検証（Stage 0 の記録）・バックテストは本節の 2 統制を再現しない」 | 生きた文書 | 是正する（再現した。残る差を書く）。本件の試験の表を足す |
| `backend/Services/BacktestService/Domain/RecordedDecisionReplayStrategy.cs` の冒頭「記録した判断列をそのまま注文へ写すだけの純関数」 | コード | 是正する（2 統制の見送りを除いて写す。サイジングは再計算しない点は不変） |
| `backend/Shared/.../Stage0DecisionRecord.cs` の `SignedQuantity`「再生はこの値をそのまま `BacktestOrder` へ写す」 | コード | 是正する |
| `Stage0DecisionRecorder.SignedQuantity` の注記「決済（Close）判定・採算ゲートは記録に含めない」 | コード | 不変（決済判定・採算ゲートは引き続き含めない）。名目額の判定を足した旨を並べる |
| `docs/tests/FR-15_backtest-tests.md` T-15-79「符号付き数量をそのまま使う＝再生側でサイジングを再計算しない」 | 生きた文書 | 誤りではない（数量は再計算しない）。本件の再生の試験はこの近くに置かず FR-10 側へまとめる |
| `docs/functional/FR-15_backtest.md` の記録・再生の評価文脈 | 生きた文書 | 「記録・再生で本番の統制のうち何を再現するか」の行を足す |
| `docs/functional/FR-10_risk-controls.md` の #1176 の 2 節 | 生きた文書 | Stage 0 の行を足す（損切り幅の下限の節にある Stage 0 の行と同じ形） |

規則 10（この変更で新たに誤りになる自分の記述）: IADR-0495 決定 4「本項目を持たない旧いメッセージは false」と同じ向きで、記録の新項目は null を「判定していない」と読む ——
再生器は null の記録に名目額の判定を掛けない。これを「再現した」と書くと旧記録について誤りになるので、文書には「判定を持つ記録だけ」と書く（旧記録は存在しない）。

規則 11（窓）: 「当日」の窓は承認（判断日）と約定（約定したバーの日）の 2 端を持つ。増える側（止める）と減る側（止めない）のプローブを書き、
3 通りの形で実測した（行＝形、列＝プローブ。○＝本番の述語〔リスク管理の射影〕と同じ答え）。

| 形 | P1: D に決済・D+1 約定・D+1 に同方向（本番は止める） | P2: D に決済・D+1 約定・D+2 に同方向（止めない） | P3: D に決済・約定せず・D+1 に同方向（止めない） | P4: 同じ日に決済の承認と同方向（止める。再生では 1 日 1 件で生じない・共有の述語の試験で固定） |
| --- | --- | --- | --- | --- |
| 承認（判断日）だけを見る | × 通す | ○ | ○ | ○ |
| 約定だけを見る | ○ | ○ | ○ | × 通す（約定前の承認を数えない） |
| **承認または約定（本番と同じ）** | ○ | ○ | ○ | ○ |

→ 本番の述語（承認または約定）をそのまま採る。

## 影響範囲

- 契約: `Stage0DecisionRecord`（末尾の省略可能項目。JSON は無ければ null）。
- サービス: 取引判断（記録器）・バックテスト（再生器）・リスク管理（射影の委譲。挙動不変）。共有カーネル（新規の純関数）。
- 再配備: 取引判断・バックテスト・リスク管理（いずれも挙動の変わるのは Stage 0 の記録・再生だけ。リスク管理は委譲のみ）。

## 試験

| ID | 内容 | 置き場 |
| --- | --- | --- |
| T-10-2410 | 共有の述語: 承認または約定が当日なら、決済の方向に立つ。同じ方向だけを塞ぐ。翌日・前日は数えない | `DecisionExitReentryTests`（Kernel） |
| T-10-2411 | 本番と同値: 同じ決済の並びを本番の射影（時刻）と共有の述語（取引日）に通して、両方向の答えが一致する | `DecisionExitProjectionTests`（リスク管理） |
| T-10-2412 | 記録器: 名目額が 1% に満たない判断に判定 true、ちょうど 1% は false、Hold は null。数量は 0 にしない。しきい値 0 は false。構成の既定は 1% | `Stage0MinimumEntryNotionalTests` |
| T-10-2413 | 記録器と本番の同値: 同じ資金・価格・幅・残枠で、本番が `SizedBelowMinimumNotional` で見送る判断に記録器は true、本番が発注する判断に false | 同上 |
| T-10-2414 | 再生: 判定 true の新規建ては写さず `SizedBelowMinimumNotional` で見送る。同じ判定 true の注文が建玉の決済なら写す | `RecordedDecisionReplayControlsTests` |
| T-10-2415 | 再生: D の決済（D+1 約定）の後、D+1 の同じ方向の新規建ては `DecisionExitSameDay` で見送る。反対方向・D+2・決済が約定しなかった場合は止めない | 同上 |
| T-10-2416 | 再生: シミュレータを通した端から端で、見送った注文は約定に現れず、走行をまたいで同じ答え（基準・コスト 2 倍・窓） | 同上 |
| T-10-2417 | 同一性: 判定を持つ記録は戦略 ID に入る。持たない記録の戦略 ID は変わらない。JSON を往復する | `Stage0DecisionRecordTests`（契約） |

## 検証（2026-10-08・worktree の HEAD は origin/develop 930b1ff7 ＋本変更）

- `dotnet build backend/backend.slnx`: 警告 0・エラー 0。`dotnet format backend/backend.slnx --verify-no-changes`: 差分なし。
- `dotnet test backend/backend.slnx`: 全 22 アセンブリで失敗 0（合格 12,106・skip 16。リスク管理 2,231・取引判断 1,578・バックテスト 385・契約 547・共有カーネル 515）。
- 変異（1 本ずつ当てて戻した）: 再生で名目額の判定を見ない（赤 4）・決済の約定日を記録しない（赤 3）・決済を新規建てと区別しない（赤 4）・記録器が判定しない（赤 5）。生存 0。
- node 検査: check-trace-blocks / gen-knowledge-graph --check / check-adr-index-sync / check-adr-index-addendum-loss / check-test-traceability /
  check-cross-repo-refs / check-plan-id-qualification / check-reading-budget / check-doc-links / check-banned-libraries / check-wall-clock-timeout-tests /
  check-proto-contracts / check-consumer-endpoint-names がすべて OK。
