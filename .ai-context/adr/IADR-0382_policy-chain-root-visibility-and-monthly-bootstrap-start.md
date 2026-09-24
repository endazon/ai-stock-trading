---
title: IADR-0382 方針連鎖の欠落は「未供給の入力」として記録・警告し、初回月報は保存＋提示まで進める導線を持つ（確定は拒否しない）
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-07, FR-09, FR-14, UC-03, ADR-0003, IADR-0032, IADR-0071, IADR-0115, IADR-0116, IADR-0125, IADR-0240, IADR-0352]
author: claude (Claude Code)
created: 2026-09-23
updated: 2026-09-23
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-06 報告書 / FR-07 方針の確定)
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md (UC-03 報告書の確定)
  - planning:projects/ai-stock-trading/04_workflows/03_reporting-cycle.md (方針階層・上位方針の欠落)
---

# IADR-0382: 方針連鎖の欠落を未供給の入力として警告し、初回月報の導線を保存＋提示まで通す

- 状態: Accepted
- 日付: 2026-09-23
- 決定者: claude (Claude Code) / [#839](https://github.com/endazon/ai-stock-trading/issues/839)

## 起点・関連

- 起票: [#839](https://github.com/endazon/ai-stock-trading/issues/839)（FR-06 / FR-07 / UC-03）
- 関連する実装 ADR:
  [IADR-0352](IADR-0352_report-defers-on-transient-dependency-failure.md)（未供給だった入力の記録と提示。
  **決定 5「確定を機械的に拒否はしない」を覆さない**）、
  [IADR-0071](IADR-0071_report-service-remaining.md)（初回月報ブートストラップ 決定4・対話的確定 決定5）、
  [IADR-0115](IADR-0115_report-auto-generation-scheduler.md)（自動生成。冪等の根拠は PeriodKey の存在＝決定3）、
  [IADR-0116](IADR-0116_report-draft-discord-notification.md)（提示通知）、
  [IADR-0125](IADR-0125_report-policy-carryover-substance.md)（方針文は実体だけを持つ）、
  [IADR-0240](IADR-0240_discord-report-review-window-and-idempotent-confirm.md)（Discord の `/report`）
- 関連する実装仕様書: `.ai-context/specs/20260923_839_policy-chain-root-and-monthly-bootstrap.md`

## コンテキストと課題

PoC 運用中、利用者が確定した週報 `weekly-2026-W38` の方針文が**空**だった。

```
参照できる確定済みの週報方針がありません。

上位方針（月報）は確定済みのものがないため参照していません。
```

報告書テーブルに月報（`Kind=2`）の行は 1 件も無かった。**方針の連鎖（月報 → 週報 → 日報）が最上位で切れ、
中身の無い週報がそのまま確定できてしまった。** 原因は 2 つある。

1. **方針連鎖の欠落が「未供給の入力」の語彙に無い。** `ReportInput`（IADR-0352 決定 5 の閉じた語彙）は
   **供給元から取りに行く入力**だけを持ち、親（上位方針）・前期が入っていない。そのため
   `reports.UnsuppliedInputs` に残らず、Discord の提示通知の要約にも `/report show` にも警告が出ない
   ——**中身が無いことが確定の妨げにならない。**
2. **初回月報を作る導線が実質存在しない。** 自動生成（`ReportSchedule.Due`）は月報を**当月の最終営業日
   17:00** に初めて due にし、`GET /reports/monthly-bootstrap` はドラフトを**返すだけ**で保存も提示もしない。
   したがって承認待ちに並ばず、`/report approve` の対象にもならない。

## 決定

### 決定 1: 「警告する」を採り、上位方針が空の確定を拒否しない

issue のコメントが挙げた分岐（警告だけか／確定を拒否するか）で、**警告**を採る。

- [IADR-0352](IADR-0352_report-defers-on-transient-dependency-failure.md) 決定 5 が既に
  「確定を機械的に拒否はしない」と裁定している。覆すのは**利用者が唯一の確定者である**（ADR-0003）
  という前提に触れる —— 拒否すると、運用開始直後や上位が遅れている局面で**利用者が何も確定できなくなる**。
- issue の受け入れ基準（案）も「提示・`/report show` でそれと分かる」であり、拒否を求めていない。
- 🔴 **拒否へ変えるなら別途の裁定が要る。**

### 決定 2: 方針連鎖を `ReportInput` の語彙へ入れる（既存の警告経路へそのまま乗せる）

| 追加した値 | 表示名 | 意味 |
| --- | --- | --- |
| `ParentPolicy` | 上位方針（親の確定済み報告書） | 日報→週報 / 週報→月報 / 月報→前月の月報 |
| `PreviousPolicy` | 前期の確定済み方針 | 同種別の直近確定済み（継続案の素） |

`AppliesTo` は**全種別**。`ReportAutoGenerator.GenerateAsync` が、既に取得している `parent` / `previous` が
`null` のときに入れる。これで

- `reports.UnsuppliedInputs` に記録される
- Discord の提示通知の要約に **Warning** の警告行が出る（#866 で Warning へ上げた経路）
- `/report show` と版番号なしの `/report approve`（確認ボタンの前段）に併記される

が**自動的に付く**（記録・要約・レビュー応答は語彙を引くだけの共通経路である）。

🔴 **月報は上位＝前期（前月の月報）と同一**（`ParentKind(Monthly) == Monthly`）。両方を数えると
**同じ事実を 2 つの表示名で 2 回警告する**ため、`parentKind != due.Kind` のときだけ `PreviousPolicy` を数える。

🔴 **見送り（リトライ）の対象にしない。** 供給元は自リポジトリのストアであり HTTP を出さない
——観測が無いので `TryDefer` は反応しない。**待っても確定済みの上位は増えない**（確定は利用者の行為である）。

### 決定 3: 初回月報は `POST /reports/monthly-bootstrap` で保存＋提示まで進める

`GET` は**変えない**（下見用・生成のみ）。新設の `POST`（OwnerOnly）が

1. 確定済み月報があれば **409**（不要）、当月の月報行があれば **409**（上書きしない）
2. ドラフトを保存（`UpsertDraft(expectedVersion: 0)`）
3. **提示**（`ApplyReview(Present)`）まで進める。🔴 **確定はしない**（ADR-0003・IADR-0115 決定1）
4. 提示できたら通知する（既存の `IReportDraftPresentedNotifier` / `ReportDraftPresented`）

承認待ちに並ぶので **Discord の `/report show <periodKey>` / `/report approve <periodKey>` がそのまま使える**。
🔴 **Bot に新しいスラッシュコマンドを足さない**（FR-14 の周辺に不要な面を増やさない。IADR-0240 の体系を動かさない）。

🔴 **提示要約に数値を書かない。** ブートストラップは集計を 1 つも持たない ——
ゼロの `PnlSummary` で `ReportSummary.Build` を呼ぶと「実現損益 0 USD ／ 取引 0 件」と**騙る**。
専用の文面（`MonthlyBootstrap.PresentationSummary`）を使い、「数値の集計・散文はありません
（**集計 0 ではありません**）」と明記する。

🔴 **通知の失敗を成功に見せない**（`MonthlyBootstrapResult.NotificationFailed`）。保存・提示は巻き戻さない。

## 影響・残余リスク

- 🔴 **ブートストラップは当月の月報の枠（`monthly-YYYY-MM`）を使う**（`MonthlyBootstrap.BuildDraft` が
  当月を採る＝IADR-0071 決定4）。自動生成の冪等は「行があれば作らない」（IADR-0115 決定3）なので、
  **その月の月末に、データに基づく月報は別途生成されない。** 運用開始の月にだけ起きる一度きりの事象であり、
  その月はどのみち部分的な期間であるため**受容する**。**T-06-012 で固定した**（黙って起きないようにする）。
  前月の枠へ倒す案は採らない —— 当月の方針を「前月の月報」と名乗らせるのは期間についての嘘になる。
- **運用開始直後は、ほぼすべての報告書が `Degraded`（未供給つき）として警告される。** 連鎖がまだ無いので
  事実であり、意図した挙動である（利用者が確定を進めれば警告は消える）。
  既存の見送り試験（`ReportAutoGeneratorDependencyRetryTests`）は「欠けたのは建玉だけ」という否定形を
  持つため、`Rig` で上位・前期を確定済みに張った（検証対象を混ぜない）。
- **`ReportAppService` に通知ポートを注入した**（`bootstrapNotifier`、既定 `null`＝通知経路が未構成）。
  確定時の `ReportConfirmed` 発行とは別経路であり、確定の作法は 1 バイトも動かしていない。
- **上位方針が空でも確定できる状態は残る**（決定 1）。**拒否へ変えるなら裁定が要る。**
- テスト ID は `T-06-001`〜`T-06-014` を新設した（走査の結果、本リポジトリに `T-06` 帯は 1 件も存在しなかった。
  `docs/tests/` に FR-06 のテスト仕様書は無い＝網羅裁定 #211 の必須範囲外のため、採番の記録は
  本 ADR と作業仕様書が持つ）。
