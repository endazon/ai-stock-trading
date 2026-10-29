---
title: IADR-0603 CI の frontend-e2e は上限の時間を持ち、レポートを常に上げ、flaky を基盤と同一の検査器で数えて警告し、develop で同じ test が 3 回連続して flaky なら ci-failure-issue.yml で起票する（flaky を skip で緑にしない）
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0208, IADR-0565, MSP:IADR-0623]
author: claude (Claude Code)
created: 2026-10-30
updated: 2026-10-30
plan_refs:
  - MSP#2013（E-72 / E-73。standards.md §12〜§13）
related_specs:
  - ../specs/20261030_1421_frontend-e2e-report-and-flaky.md
---

# IADR-0603: frontend-e2e のレポート・上限・flaky の集計（#1421 / E-72）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-30
- 決定者: claude が起案（起点 #1421＝MSP#2013 の分割 E-72。親 #1410）。

## 起点・関連

- 起票: #1421。先行の E-20〜E-23（#1412〜#1415）は close 済み。
- 検査器は基盤の E-73（MSP#2037・MSP:IADR-0623）と同一の実装を置く（kit の同期の義務は無い）。
- 前提: IADR-0208（CI の速さと後段の起票の規約。`ci-failure-issue.yml`）。

## コンテキスト

- frontend-e2e は上限の時間を持たず（既定 360 分）、レポートは失敗時だけ上げていた。`retries: 1` の再試行で通った test（flaky）は緑に紛れ、誰にも見えなかった。

## 決定

### 決定 1: 上限の時間とレポートの常時の保存

- `timeout-minutes: 30` を置く（実測の所要は数分）。
- Playwright のレポートは、失敗時だけでなく常に上げる（キャンセルと、E2E を走らせなかったときを除く）。flaky の再試行の trace を後から読むためである。

### 決定 2: flaky を数えて警告する（PR は落とさない）

- E2E を `--reporter=list,html,json` で流し、JSON を `scripts/check-e2e-flaky.js` が読む（基盤と同一の実装。外部依存ゼロ）。
- 1 件でも flaky があれば、test ごとに `::warning::` を出し、件数を step summary に出す。終了コードは 0 で、flaky で PR を落とさない。レポートを読めない・test が 0 件のときは赤にする（0 件を「flaky 0」と誤らない）。
- 要約（`e2e-flaky-summary`）を成果物として 14 日残す。後の実行の連続の判定に使う。

### 決定 3: develop で同じ test が 3 回連続して flaky なら ci-failure-issue.yml で起票する

- develop への push の CI だけで、新しいジョブ `e2e-flaky-streak` が、直前の develop の CI の要約 2 つと今回の要約を突き合わせる。要約の無い実行に当たったら履歴をそこで切る（連続を作らない側）。
- 連続があればこのジョブを赤にし、`report-failure` が `ci-failure-issue.yml`（marker `e2e-flaky`）で起票する。重複はマーカーで 1 本へまとめ、自動ではクローズしない。
- 基盤は検査器の `--upsert-issue` で test ごとに issue を立てる。本リポジトリは、後段の失敗の起票を `ci-failure-issue.yml` に一本化している（integration・codeql・security と同じ）ので、そちらを使う。検査器は同一の実装のまま、起票の口だけを使わない。
- PR では起票しない。frontend-e2e が赤の実行では判定しない（その実行の要約は残り、次の判定に入る）。

### 決定 4: flaky を skip・fixme・quarantine で緑にしない

- 数えて、見せて、起票するだけである。直すのは人と次の PR である。

## 結果

- 良い点: flaky が PR の画面に警告として出て、続けば issue になる。E2E が止まったまま CI を占有しない。
- 悪い点: develop の CI に小さなジョブが 1 本（と起票のジョブ）増える。
- 残余リスク: 起票は ci-failure-issue.yml の 1 本にまとまるので、test ごとの issue にはならない。本文の失敗ジョブと run の URL から、警告の行で test を読む。
