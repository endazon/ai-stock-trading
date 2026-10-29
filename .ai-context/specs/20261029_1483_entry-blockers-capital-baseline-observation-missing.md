---
title: LLM の前の関所（新規建ての可否の口）に、基準資金の直前の暦日の観測の欠け（CapitalBaselineUnavailable）を足し、審査の拒否理由と口の理由の分類を試験で閉じる
type: spec
status: done
related_ids: [FR-04, FR-10, ADR-0003, ADR-0041, ADR-0073, IADR-0354, IADR-0463, IADR-0471, IADR-0495, IADR-0572, IADR-0581, IADR-0592]
author: claude (Claude Code)
created: 2026-10-29
updated: 2026-10-29
---

# 仕様書: 新規建ての可否の口に基準資金の観測の欠けを足す（#1483）

> 本仕様書は実装着手前に作成した（着手 2026-10-29。AST `origin/develop` `a003a190`）。

## 起点（トレーサビリティ）

- 起点: #1483（稼働 PoC 2026-10-26。ADR-0073 の統制で審査がすべての新規建てを拒否する日に、口がこの理由を返さず LLM を一次 458 回・二次 94 回呼んだ。約 288 円）。
- 計画: FR-10（リスク統制）、FR-04（方針の範囲で判断）、ADR-0003（判断のガードレール）、ADR-0041 決定 2、ADR-0073 決定 1・2（直前の ET 暦日に成功した観測が無ければ新規建てを拒否・解除は自動）。
- 前提の IADR: IADR-0463（口。審査と同じ述語・判断側は読むだけ・審査は残す・**不明は返さない**＝裁定 3）、IADR-0471、IADR-0495、IADR-0572、IADR-0354、IADR-0581（観測の欠けの判定と読み出しの帰結）。
- 本件の判断: IADR-0592（新規）。

## 原因（コードで確認・develop `a003a190`）

- 審査（`RiskEvaluator.Evaluate`）は `snapshot.Capital is null` の新規建てを `CapitalBaselineUnavailable` で拒否する。`Capital` は `PortfolioSnapshotBuilder` が `ICapitalBaselineStore.GetCurrent()` から入れ、
  ADR-0073 の判定（`UnavailableMissingPreviousDay`）・鮮度切れ（`UnavailableStale`）・行なし・0 以下のいずれでも null になる。
- 口（`EntryStateBlockers.Determine`）は `CapitalBaselineUnavailable` を対象に入れていない。IADR-0463 の母集合（作業仕様書 `20260930_1113_entry-blockers-before-llm` の表の 1 行目）が
  「照会できていない＝**不明**」として除外したためである。
- 🔴 **ADR-0073 の後は前提が変わった。** 欠けの判定は「直前の ET 暦日に成功した観測が 1 件も無い」であり、当日の観測は直前の暦日の行を書かない（行は観測時刻の暦日に畳む。IADR-0354 決定 2）。
  よって欠けは**その ET 暦日のあいだ確定した状態**であり、「照会できていないから分からない」ではない。読み出しの帰結（`CapitalBaselineReading.ObservationMissing`）はそれを既知の事実として持つ。

## 母集合（規則 9。走査してから挙げる）

### 審査が新規建てを拒否し得る理由（`RejectionReason` 35 値の全部の分類）

走査したコマンド:

```bash
grep -n "reasons.Add\|RejectionReason\.\w*" -o backend/Services/RiskManagementService/Domain/RiskEvaluator.cs \
  backend/Services/RiskManagementService/Domain/ShortSellEvaluator.cs backend/Services/RiskManagementService/Domain/StageProductPolicy.cs \
  backend/Services/RiskManagementService/Features/RiskManagement/OrderScreeningService.cs | sort -u
grep -n "^    [A-Z][A-Za-z]*,$" backend/Shared/AiStockTrading.Shared.Contracts/Trading/RejectionReason.cs
```

IADR-0463 の 23 行の表（`20260930_1113`）と IADR-0572（締め切り・テーマ）を引き継ぎ、変わるのは 1 行だけである。

| 理由 | 本件の判定 | 理由 |
| --- | --- | --- |
| `CapitalBaselineUnavailable` | **対象（観測の欠けのときだけ）** | 欠け（`UnavailableMissingPreviousDay`・`UnavailableStale`）は ET 暦日のあいだ確定する。行なし（初回の配備・Runbook の投入待ち）と 0 以下（人手の意図した停止）は従来どおり返さない（残余。IADR-0592 決定 3） |
| その他 34 値 | 変えない | IADR-0463・IADR-0495・IADR-0572・IADR-0521 の分類のまま |

🔴 **この分類を試験で閉じる**（求め 2）: `RejectionReason` の全値を「口の対象（`EntryStateBlockers.Determinable`）」「全注文の口（`OrderStateBlockers`）」「除外（理由つき）」の
いずれか 1 つへ置く表を試験に持つ。列挙に値を足して表へ置かなければ赤になる（新しい理由を審査に足した者が、口へ足すか除外の理由を書くかを必ず決める）。

### 「10 理由」の追随（規則 9・10。誤りの側の文字列で走査）

```bash
git grep -n "10 理由\|ENTRY_BLOCKER_THEME\|EntryStateBlockers.Determinable\|返さない理由" -- backend docs
```

| 箇所 | 扱い |
| --- | --- |
| `EntryStateBlockers.cs`（`Determinable`・冒頭の注記） | 10 → 11 理由 |
| `RiskReadWireMapping.ToProto`・proto `EntryBlocker`・`GrpcEntryBlockersProvider.Reason`・`scripts/proto-contract-baseline.json` | 値 13 を足す（非破壊） |
| `TradeDecisionPromptBuilder.EntryBlockerLabel` | 日本語名を足す |
| `HeldAddOnBlockersTests`（11 理由を名付ける）・`EntryBlockersBeforeLlmTests`（理由ごとの見送り） | 追随（理由の列挙を足した） |
| `EntryBlockersEndpointTests`（`Determinable` を走査して送り手の写像を確かめる）・`EntryBlockersProviderTests`（proto の全値を走査して受け手の写像を確かめる） | **変更不要**（走査で自動追随。値 13 の写像が無ければこの 2 本が赤になる） |
| `docs/functional/FR-10_risk-controls.md` §LLM を呼ぶ前に見送る（対象の理由・返さない理由） | 追随 |
| `docs/tests/FR-10_risk-controls-tests.md` | 試験 ID を足す |
| 除外: `.ai-context/specs/`・`.ai-context/adr/` の過去の記録 | point-in-time の記録（書き換えない） |

## 設計

1. **読み（審査と同じ読み）**: `PortfolioSnapshotBuilder` は従来どおり `GetCurrent()` で `Capital` を入れ、`Capital` が null のときだけ `Read()` の
   `ObservationMissing` を `PortfolioSnapshot.CapitalObservationMissing` へ入れる（`Capital` が値を持てば偽）。審査と口は同じ組み立てを通る。
   日付の境目で 2 回の読みが食い違っても、旗は「`Capital` が null」かつ「欠け」の連言なので、口が審査より多く止めることは無い（安全側＝LLM を呼ぶ側）。
2. **述語の共有**: `EntryStateBlockers.CapitalUnavailable(snapshot)`（`Capital is null`。判定コアが呼ぶ）と `EntryStateBlockers.CapitalObservationMissing(snapshot)`
   （その上で欠け。口が呼ぶ）を置く。判定コアの `CapitalBaselineUnavailable` は前者を同じ位置で呼ぶ（振る舞いは変えない）。
3. **口**: `Determinable` の先頭（審査の到達順）に `CapitalBaselineUnavailable` を置き、`Determine` は欠けのとき**両方向**に返す（資金は方向を問わない）。
4. **線上**: proto `ENTRY_BLOCKER_CAPITAL_BASELINE_UNAVAILABLE = 13`。REST は数値の往復で変わらない。受け手が古ければ未知の値として不明に倒し LLM を呼ぶ（従来どおり）。
5. **判断側**: 規則を持たない。保有 0 の見送り（`EntryBlockedByRiskControls`）と保有中の買い増しの塞がり（プロンプト）が口の答えを読む。日本語名を足す。
6. **審査は変えない**（両端で止める）。

## 受け入れ基準（→ 試験）

- [x] 直前の暦日の観測が無い日（`UnavailableMissingPreviousDay`）・鮮度切れの日に、口が両方向に `CapitalBaselineUnavailable` を返し、審査も同じ理由で拒否する（T-10-3072）。
- [x] 観測がある日は返さない。行なし・0 以下（欠けではない未供給）も返さない（T-10-3072）。
- [x] 全組み合わせで口と審査が一致する（健全性・完全性。資金の次元を「既知／欠け／その他の未供給」の 3 値にする）（T-10-1782 の拡張・T-10-3073）。
- [x] `RejectionReason` の全値が「口の対象／全注文の口／除外（理由つき）」のどれか 1 つに置かれている。置かれていない値があれば赤（T-10-3074）。
- [x] 線上で写り、判断側で読み、保有 0 なら LLM を 0 回で見送る。プロンプトの文言に日本語名が出る（T-10-3075）。

## 残余

- 未供給の日だけ、スナップショットの組み立てで `Read()` の読みが 1 回増える（`AccountEquityDays` の当日より前の先頭 1 行）。組み立ては口・審査・資金の照会・SC-03 の状態で呼ばれ、
  判断は 1 件ごとに資金の照会（LLM の前の口より先に読む）と口でそれぞれ 1 回組み立てるので、巡回 1 回で判断対象 N 件なら増えるのはおよそ 2N 回
  （監視 12 件なら約 24 回）。ほかに決済の審査（新規建ては審査でも拒否される）・SC-03 の状態の表示ごとに 1 回。
  平常の日（供給あり）は増えない。2 回の読みは連言なので食い違っても安全側（口が審査より多く止めない）に倒れ、コードは変えない。

## 検証

- `dotnet build backend/backend.slnx`・`dotnet test`（RiskManagementService・TradeDecisionService・Shared）・`dotnet format --verify-no-changes`。
- 文書検査（`check-trace-blocks`・`gen-knowledge-graph --check`・`check-doc-links`・`check-cross-repo-refs`・`check-plan-id-qualification`）・`REQUIRE_REPO_TESTS=1 node scripts/scripts.test.js`・proto の契約検査。

## 配備

- リスク管理と取引判断の両方の版を上げる（順序は問わない。片方だけ古い間は従来どおり LLM を呼ぶ）。設定の追加は無い。
