---
title: 方針の連鎖の欠落を未供給の入力として記録し、初回月報ブートストラップを保存・提示できる導線を用意する（#839）
type: spec
status: accepted
related_ids: [FR-06, FR-07, UC-03, ADR-0003, IADR-0032, IADR-0071, IADR-0115, IADR-0116, IADR-0125, IADR-0240, IADR-0352, IADR-0382]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-06 報告書 / FR-07 方針の確定)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-03 報告書の確定)
  - planning:projects/ai-stock-trading/04_workflows/03_reporting-cycle.md (方針階層・上位方針の欠落)
---

# 仕様書: 方針の連鎖の欠落を可視化し、初回月報の導線を用意する（#839）

## 起点

- **#839**。起点 ID: **FR-06**（報告書）・**FR-07**（方針の確定）・**UC-03**。
- 関連 IADR: [IADR-0071](../adr/IADR-0071_report-service-remaining.md)（初回月報ブートストラップ 決定4・
  対話的確定 決定5）、[IADR-0352](../adr/IADR-0352_report-defers-on-transient-dependency-failure.md)（未供給だった
  入力の記録と提示。**決定 5 は「警告するだけで確定を機械的に拒否はしない」**）、
  [IADR-0115](../adr/IADR-0115_report-auto-generation-scheduler.md)（自動生成・冪等の根拠は PeriodKey の存在）、
  [IADR-0125](../adr/IADR-0125_report-policy-carryover-substance.md)（方針文は実体だけを持つ）、
  [IADR-0240](../adr/IADR-0240_discord-report-review-window-and-idempotent-confirm.md)（Discord の `/report`）。
- 本作業で起草する実装 ADR: **IADR-0382**。

## 診断（コードの実測）

### 症状（issue 本文・PoC 運用中の実測）

確定した週報 `weekly-2026-W38` の方針文が空だった。

```
参照できる確定済みの週報方針がありません。

上位方針（月報）は確定済みのものがないため参照していません。
```

報告書テーブルに月報（`Kind=2`）の行は 1 件も無い。

### 原因 1: 方針連鎖の欠落が「未供給の入力」の語彙に無い

`ReportInput`（`Domain/ReportInput.cs`・13 語）は**供給元から取りに行く入力**だけを持ち、
**方針連鎖（月報 → 週報 → 日報）の親・前期**が入っていない。したがって

- `reports.UnsuppliedInputs` に残らない
- Discord の提示通知の要約に警告行が出ない（#866 で Warning へ上げた経路）
- `/report show` と版番号なしの `/report approve`（確認ボタンの前段）にも併記されない

**中身が無いことが確定の妨げにならない。** `ReportAutoGenerator.GenerateAsync` は
`store.GetLatestConfirmed(parentKind)` が `null` でもそのまま進む（同 126〜129 行）。

### 原因 2: 初回月報を作る導線が実質存在しない

- 自動生成（`ReportSchedule.Due`）は月報を**当月の最終営業日 17:00** に初めて due にする
  （`Domain/ReportSchedule.cs` 47〜53 行）。運用開始が月の途中だと、その月の途中まで月報が 1 件も生まれない。
- `GET /reports/monthly-bootstrap`（OwnerOnly）はドラフトを**返すだけ**で、保存も提示もしない
  （`Features/Reports/GetMonthlyBootstrap/Endpoint.cs`・`ReportAppService.BuildMonthlyBootstrap`）。
  したがって承認待ちに並ばず、`/report approve` の対象にもならない。

## 決める点

🔴 **「警告するだけ」か「上位方針が空の確定を拒否する」か。** issue のコメントが挙げた分岐である。
**本作業は前者（警告）を採る。** [IADR-0352](../adr/IADR-0352_report-defers-on-transient-dependency-failure.md)
決定 5 が既に「確定を機械的に拒否はしない」と裁定しており、これを覆すのは
**利用者が唯一の確定者である**（ADR-0003）という前提に触れる —— 拒否すると、運用開始直後や
上位が遅れている局面で**利用者が何も確定できなくなる**（=止まる）。issue の受け入れ基準（案）も
「提示・`/report show` でそれと分かる」であり、拒否を求めていない。**拒否へ変えるなら別途の裁定が要る。**

## 実装

### 1. 方針連鎖を `ReportInput` の語彙へ入れる（警告経路へそのまま乗る）

| 追加する値 | 表示名 | 意味 |
| --- | --- | --- |
| `ParentPolicy` | 上位方針（親の確定済み報告書） | 日報→週報 / 週報→月報 / 月報→前月の月報 |
| `PreviousPolicy` | 前期の確定済み方針 | 同種別の直近確定済み（継続案の素） |

- `AppliesTo` は**全種別**（方針連鎖はどの種別も持つ）。
- `ReportAutoGenerator.GenerateAsync` が、既に取得している `parent` / `previous` が `null` のときに入れる。
- 🔴 **月報は上位＝前期（前月の月報）と同一**（`ParentKind(Monthly) == Monthly`）。
  2 つ数えると**同じ事実を 2 回警告する**ため、`parentKind != due.Kind` のときだけ `PreviousPolicy` を数える。
- 🔴 **見送り（リトライ）の対象にしない。** 供給元は自リポジトリのストアであり HTTP を出さない
  ——観測が無いので `TryDefer` は反応しない（`OpeningInventory` と同じ形）。**待っても確定済みの上位は増えない。**

これで `reports.UnsuppliedInputs` への記録・Discord 提示通知の Warning・`/report show`・
版番号なしの `/report approve` の警告が**自動的に付く**（記録・要約・レビュー応答は語彙を引くだけの共通経路）。

### 2. 初回月報ブートストラップを保存・提示する導線

`POST /reports/monthly-bootstrap`（OwnerOnly）を新設する。`GET` は**変えない**（生成のみ・下見用）。

1. 確定済み月報が既にあれば **`409`**（ブートストラップ不要。`GET` の 404 と揃えず、
   「作れなかった」ことを区別できる形にする）。
2. 当月の月報行が既にあれば **`409`**（利用者が手で作った・差し戻し中のドラフトを踏まない。
   自動生成の冪等の規則と同じ考え方）。
3. ドラフトを保存（`UpsertDraft(expectedVersion: 0)`）し、**提示**（`ApplyReview(Present)`）まで進める。
4. 提示できたら通知する（`IReportDraftPresentedNotifier`。既存の `ReportDraftPresented` 経路）。
   🔴 **要約に数値を書かない。** ブートストラップは集計を持たない —— `ReportSummary.Build` を
   ゼロの `PnlSummary` で呼ぶと「実現損益 0 USD / 取引 0 件」と**騙る**ことになる。専用の文面を使う。

確定は従来どおり利用者のみ（ADR-0003）。承認待ちに並ぶので **Discord の `/report show <periodKey>` /
`/report approve <periodKey>` がそのまま使える**（新しいスラッシュコマンドは増やさない）。

🔴 **既知の帰結（受容する）**: ブートストラップは**当月の月報の枠**（`monthly-YYYY-MM`）を使う
（`MonthlyBootstrap.BuildDraft` が当月を採る＝IADR-0071 決定4）。自動生成の冪等は「行があれば作らない」
（IADR-0115 決定3）なので、**その月の月末に、データに基づく月報は別途生成されない**。
運用開始の月にだけ起きる一度きりの事象であり、その月はどのみち部分的な期間である。
**テストで固定し、ADR に残す**（黙って起きないようにする）。

## テスト（xUnit v3・注入した時計・壁時計の待ちなし）

**テスト ID 帯**: `T-06-001`〜 を本作業で新設する。走査（`grep -rnoE "\bT-06-[0-9]+\b"`）の結果、
**本リポジトリに `T-06` 帯は 1 件も存在しない**。`node scripts/check-test-traceability.js` が出す
採番の最大値にも `T-06` は現れない（採番の単一情報源は `docs/tests/*.md` であり、FR-06 のテスト仕様書は
網羅裁定 #211 の必須範囲外で存在しない）。`T-10-6xx` は他レーンが押さえているため使わない。
**同日に別レーンで動く #892 は `T-16` 帯**であり、互いに素である。

| ID | 何を固定するか |
| --- | --- |
| T-06-001 | 上位方針が無いまま生成した週報は `ParentPolicy` を未供給として記録する |
| T-06-002 | 前期方針が無いときは `PreviousPolicy` も記録する |
| T-06-003 | 月報は上位＝前期のため `PreviousPolicy` を二重に数えない |
| T-06-004 | 上位・前期が揃っていればどちらも記録しない |
| T-06-005 | 方針連鎖の欠落では**見送らない**（生成・提示は進む） |
| T-06-006 | 提示通知の要約に警告行（表示名）が出る |
| T-06-007 | `/report show` の応答（`GetReviewView`）に表示名が出る |
| T-06-008 | `POST /reports/monthly-bootstrap` がドラフトを保存し承認待ちへ並べる |
| T-06-009 | 確定済み月報があれば作らない（409） |
| T-06-010 | 当月の月報行があれば上書きしない（409） |
| T-06-011 | ブートストラップの提示要約が数値を騙らない（「実現損益 0」と書かない） |
| T-06-012 | ブートストラップが当月の枠を使うと、月末の自動生成はその期間をスキップする（既知の帰結） |
| T-06-013 | エンドポイントの疎通と認可（OwnerOnly） |
| T-06-014 | 通知の失敗を成功に見せない（保存・提示は巻き戻さない） |

## 受け入れ基準（issue の案に対応）

1. 上位方針が欠けたまま生成された報告書は、**提示通知・`/report show`** でそれと分かる。
2. 初回の月報ドラフトを**利用者が確定できる導線**がある（保存＋提示＋既存の `/report approve`）。
3. **既存の確定済み報告書は書き換えない**（ブートストラップは行が無いときだけ作る）。
4. `dotnet build` / `dotnet test`（ReportService）・`dotnet format --verify-no-changes`・文書検査が通る。

## 射程外（本作業で**やらないこと**）

- **上位方針が空の確定を拒否すること**（前掲「決める点」。IADR-0352 決定 5 を覆すには裁定が要る）。
- **Discord に新しいスラッシュコマンド（`/report bootstrap` 等）を足すこと。** 提示まで進めれば
  既存の `/report show` / `/report approve` が使えるため、Bot のコマンド体系・Gateway の登録には触らない
  （FR-14 の「設定値の変更は Discord からは参照のみ」の周辺に不要な面を増やさない）。
- **月報の生成境界（`MonthlyAt`）を変えること。** 計画の報告サイクルに触れる。
- **確定済み報告書の書き換え。**
