---
title: 2026-08-07 の計画改訂へ実装文書を追随させる（ADR-0024 / ADR-0021 / ADR-0022 / ADR-0023）
type: spec
status: review
related_ids: [ADR-0024, ADR-0021, ADR-0022, ADR-0023, ADR-0004, IADR-0053, IADR-0056, IADR-0060, IADR-0153, IADR-0157, IADR-0167, FR-10, FR-19]
author: endazon (with Claude Code)
created: 2026-08-07
updated: 2026-08-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0021_us-account-type-dual-support.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0022_fx-rate-source-and-freshness.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0023_us-daily-ohlc-history-source.md
---

# 仕様書: 2026-08-07 の計画改訂への文書追随

> 本仕様書は実装着手前に作成する。

## 起点となる計画書（トレーサビリティ）

- 計画 ADR: **ADR-0024（計画リポ）（新設・`Accepted`）** ／
  ADR-0021（計画リポ） 決定4-5（2026-08-07 改訂）／
  ADR-0022（計画リポ）（`Accepted` へ）／
  ADR-0023（計画リポ） 決定5 ／ ADR-0004
- 機能要求（FR）: FR-10（リスク統制）・FR-19（取引ガード）※拒否理由の記述のみ
- 実装 ADR: **[IADR-0167](../adr/IADR-0167_opend-unattended-restart-followup.md)（本作業で新設）** ／
  [IADR-0053](../adr/IADR-0053_moomoo-opend-dockerization.md)（改訂対象）／
  [IADR-0056](../adr/IADR-0056_moomoo-simulate-poc-complete-real-gated.md)・[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)・
  [IADR-0153](../adr/IADR-0153_broker-account-type-supply-and-fail-closed.md)（記述の追随）
- 起点 issue: [#426](https://github.com/endazon/ai-stock-trading/issues/426)

## 目的・背景

planning submodule が `06fa163` → **`a4616a8`** へ進み、質問票 第 13 回の裁定が計画へ反映された。本作業は**コード変更を伴わない文書の追随だけ**を扱う（挙動変更は #420〜#425 が個別に扱う）。

**最重要は ADR-0024 である。** 実装リポジトリが 2026-07-15 に行った追検証（#13 / #130）が、計画側の `Accepted` な記述を反証していたにもかかわらず計画へ到達していなかった件の決着であり、**その反証を最初に観測した当の実装文書（`IADR-0053`）に、いまも反証された結論が「確定」として残っている**。

## 対象範囲

### 1. ADR-0024（OpenD の無人再起動は条件付き成立）への追随

**`IADR-0053` が矛盾の震源である。** 同 IADR は初回 PoC の結論をそのまま保持しており、ADR-0024 決定2 が名指しで「誤りである」とした命題を**「確定」と書いている**。

| 対象 | 現行の記述 | 是正方針 |
| --- | --- | --- |
| `docs/adr/IADR-0053_*.md` 決定5・PoC 結果・未確定 | 「完全無人（自動再起動）は**不可（確定）**」「検証は **IP/セッション依存**で、永続化では回避できない」「限定的成立（起動時有人・以降常駐）」 | **初回 PoC の記述は消さず**、撤回であることを明示したうえで ADR-0024 決定1 の条件付き成立へ改める。誤りの所在（**Pod IP と egress IP の混同**）を残す |
| `deploy/opend/README.md:24` | 「これが『無人運用の成立性』の要点（**ADR-0002 未決**）」 | ADR-0002 は `Accepted`。ADR-0024 決定1 で決着済みへ改める |
| `deploy/opend/README.md:174-175`／`deploy/helm/.../README.md`／`.github/workflows/helm.yml` | 「**livenessProbe を付けない**理由 = OpenD は**再起動＝対話再検証**」 | **付けない結論は変えない。理由を差し替える**（後述の判断 2） |
| `deploy/opend/README.md:144` | 「IP/セッション変化時は再検証が要る**前提は維持する**」 | ADR-0024 決定5-1 は**未検証**と定める。断定を「未検証ゆえ安全側に有人を想定」へ |
| `deploy/opend/k8s/experiment-appdata.yaml` | 「再認証が IP 依存なら本実験でも回避できない」 | 初回 PoC の否定結果そのものが撤回対象である旨を注記 |
| `docs/adr/IADR-0060_*.md:63`／`docs/adr/IADR-0056_*.md:58` | 「**ADR-0002 も Proposed** のまま」「ADR-0002 の Accepted 化は上流の triage に委ねる」 | ADR-0002 は 2026-08-01 に `Accepted`。追随する |
| `docs/blocked-tasks.md` | ADR-0024 決定5 の未検証 3 点が未登録 | B-4（計画側の裁定待ち）ではなく **A 群（実機が要るもの）** へ登録する |

**落としてはならないもの（ADR-0024 決定3）**: 「**再起動の最小化は維持する**」。本 ADR は「再起動しても復旧できる」ことを認めるものであって、「**再起動してよい**」と言うものではない。

### 2. ADR-0021 決定4-5 改訂（拒否理由 1 種 → 3 種）への追随

**実装は既に計画と一致している**（序数 25 / 26 / 27・クラス A / B / A）。是正するのは**「計画との差異」という枠組みの記述**である。実装が先行し、計画が 2026-08-07 に同名・同クラスで**追認した**（`StopOrderRequired` と同じ形）。

対象 7 箇所: `RejectionReason.cs:116-125` ／ `docs/functional/FR-10_risk-controls.md:411` ／ `docs/adr/IADR-0153_*.md:58,208` ／ `docs/specs/20260806_375_cash-account-support.md:195` ／ `docs/adr/README.md:197` ／ `feedback/20260806_adr0021-*.md:37`。

> **「総数 10 種 → 12 種」は実装リポジトリに該当記述が無い**（`grep` 0 件）。計画側の「12 種」は**空売り 9 種＋現金 3 種**の意味であり、実装の `RejectionReason` 全 28 メンバとは軸が違う。**無い記述を新設しない**（後述の判断 3）。

### 3. `Proposed` を根拠にした保留の見直し

`docs/blocked-tasks.md:63-71` が「ADR-0016 / 0018 / 0019 / **0022** / 0023 はいずれも `Proposed`」と書いている。**pin `a4616a8` で全 `status:` を実測したところ、2 件が誤っていた**（issue が挙げたのは ADR-0022 だけだが、`ADR-0019` も `Accepted` である）。

| ADR | 実測（pin `a4616a8`） | 文書の記述 |
| --- | --- | --- |
| ADR-0016 / ADR-0018 / ADR-0023 | `Proposed` | ✅ 一致 |
| **ADR-0019** | **`Accepted`** | ❌ 誤り |
| **ADR-0022** | **`Accepted`**（2026-08-07） | ❌ 誤り |
| ADR-0021 | `Proposed` | （記載なし） |
| ADR-0002 / ADR-0024 / ADR-0025 | `Accepted` | （記載なし） |

**`Proposed` を根拠に何かを保留していないかを確認する。** 保留していたなら誤りである（planning `.claude/rules/adr.md`「`Proposed` は決定の効力を停止しない」）。

**確認結果: 状態表記だけを根拠に保留しているものは無い。** `IADR-0156`・`docs/specs/20260806_382`・`20260806_375` はいずれも「`Proposed` は待ちの根拠にならない」と明記済みである。`IADR-0016`・`IADR-0056` の「ADR-0002 が Proposed」は**当時の記録**であり、既に `IADR-0056` が解除している。**唯一の実質的な保留は `IADR-0053` の `Proposed` だが、その根拠は状態表記ではなく Hetzner 未検証という実体**である（判断 1）。

### 4. ADR-0023 決定5 / ADR-0004 と `IADR-0157` の整合確認

**確認のみ。矛盾が無ければ変更しない**（無理に書き足さない）。

**結果: 矛盾は無い。変更しない。** 実測した対応は次のとおり。

| 計画（ADR-0023 決定5） | 実装側の記述 |
| --- | --- |
| 米国株の日足 OHLC は moomoo 履歴 K 線（`QotRequestHistoryKL`・`KLType_Day`・`RehabType_Forward`） | `IADR-0157` 決定1 が同じ構成で `MoomooHistoricalBarSource` を実装 |
| **追加費用なし。ADR-0005 の有料枠へ移らない** | `docs/functional/FR-15_backtest.md:77,97`・`docs/specs/20260806_382_moomoo-ohlc-adapter.md:56`・`docs/blocked-tasks.md` A-3 が同旨 |
| **月次データ費用上限（0 円配分）の見直しは不要** | 実装側に見直しを要するとした記述は無い（`grep` で確認） |
| 実装側で確認を要する 2 点（取得枠の単位・前復権と費用モデルの整合） | `IADR-0157` が前提として転記済み。**既定は `none` のまま**であり本番のバックテストへは流していない |

**`IADR-0157` に有料データソース・月次上限への直接の言及は無い**（該当は FR-15 と作業仕様書）。これは分担として正しく、書き足す必要は無い。

### 対象外（意図的にやらない）

- **コードの挙動変更**（#420〜#425 が扱う）。本作業のコード差分は**コメントのみ**
- **ADR-0023 / ADR-0021 を `Accepted` へ移すこと**（計画リポジトリ側の作業・利用者の判断）
- **「再起動してよい」と読める記述へ緩めること**（ADR-0024 決定3 が明確に否定）
- **`livenessProbe` を付けること**（判断 2）
- **planning submodule の gitlink 更新**（既に `a4616a8`。#419 で取り込み済み）

## 実装上の判断（IADR-0167 に記録する）

| # | 判断 | 内容 |
| --- | --- | --- |
| 1 | **`IADR-0053` は `Proposed` のまま据え置く** | 昇格条件のうち「無人運用の一次確認」は ADR-0024 で満たされたが、「**Hetzner 接続の一次確認**」は ADR-0024 決定5-2 で**依然未検証**である。片方だけで昇格させない |
| 2 | **`livenessProbe` は引き続き付けない。ただし理由を差し替える** | 旧理由「再起動＝対話再検証」は ADR-0024 決定2 で**否定された**。新理由は決定3（再起動の最小化の維持）と決定4（**SPOF であること自体は変わらない**）である。**結論が同じでも、否定された理由を残してはならない**——次に誰かが理由を確かめたとき、根拠が崩れているのに結論だけが残る |
| 3 | **拒否理由の「総数 12 種」を実装側へ新設しない** | 実装リポジトリに総数の記述が無く、計画の「12 種」は空売り 9＋現金 3 の意味で `RejectionReason` の全メンバ数（28）とは軸が違う。**無い記述を新設すると、二重の数え方を持ち込む**ことになる |
| 4 | **初回 PoC の記述を削除せず、撤回として残す** | `docs/blocked-tasks.md` の方針（「解消の過程で記述が誤っていたと判明した場合は、誤りも消さずに訂正として残す」）に揃える。**誤りの所在（Pod IP と egress IP の混同）が最も再発しやすい形**であり、消すと同じ誤りを繰り返す |

## 受け入れ基準

- [x] `IADR-0053` が ADR-0024 決定1/決定2 に追随し、**「不可（確定）」という断定が残っていない**
- [x] **再起動の最小化（決定3）が落ちていない**——「再起動してよい」と読める記述を作っていない
- [x] `livenessProbe` を付けない理由が、**否定された旧理由ではなく決定3/決定4** になっている
- [x] ADR-0024 決定5 の未検証 3 点が `docs/blocked-tasks.md` に登録されている
- [x] 拒否理由の「計画が明示したのは 1 種のみ」が全 7 箇所で追認済みの記述へ変わっている
- [x] `Proposed` の状態表記が pin `a4616a8` と一致し、**`Proposed` を根拠に保留しているものが無い**
- [x] `IADR-0157` と ADR-0023 決定5 / ADR-0004 の整合を確認した（変更要否を明記）
- [x] `node scripts/check-doc-links.js` ／ `dotnet build` ／ `dotnet format` が緑

## テスト方針

**本作業はコードの挙動を変えないため、新規テストは追加しない。** 担保は次の 2 つである。

| 担保 | 内容 |
| --- | --- |
| `check-doc-links.js` | 追加・改名したリンク（IADR-0167・plan ADR-0024）の相対リンクが壊れていないこと |
| 既存の `RejectionReasonClassificationTests` | 拒否理由の**分類と序数**は変更していない（コメントのみの変更であることの裏返し）。実走して緑を確認する |

## 残余リスク

1. **ADR-0024 決定5-1（egress IP 変更時の再検証の要否）が未検証のまま**である。本作業は記述を計画へ揃えるだけであり、**実機確認は `docs/blocked-tasks.md` A 群へ積む**。Hetzner 移行（#24 / #132）に直結する。
2. **「条件付き成立」は単一の真偽値でないため、読み手が条件を落としやすい**（ADR-0024 §結果 の悪い影響そのもの）。本作業では**条件 (1)(2) を必ず併記する**書式で統一するが、将来の追記で片方が落ちる余地は残る。
3. **`IADR-0053` を `Proposed` のまま残す**ため、「土台が `Proposed` なのに上物の `IADR-0060` が `Accepted`」という状態は解消しない（判断 1）。これは Hetzner 未検証の反映であり、状態の不整合ではない。
