---
title: 窓開けで保護逆指値がエントリーより先に約定したとき、記録を完了させる前にエントリーを照会し、反対方向の建玉を知らせる（#1454）
type: spec
status: done
related_ids: [FR-10, UC-02, ADR-0040, IADR-0210, IADR-0344, IADR-0412, IADR-0428, IADR-0576, IADR-0580]
author: claude (Claude Code)
created: 2026-10-23
updated: 2026-10-23
---

# 仕様書: 逆指値がエントリーより先に約定したときのガードと検知（#1454）

> 本仕様書は着手前に作成した（AST `origin/develop` `6c5369a8`）。判断は [IADR-0580](../adr/IADR-0580_stop-filled-before-entry.md) に残す。

## 起点（トレーサビリティ）

- 起点: #1454（親 #1423「試験の穴 3」）。
- 計画: FR-10（逆指値なしの建玉を持たない／建玉なき逆指値を残さない）、ADR-0040 決定 1（S0 / S3 はブローカー側の注文）、
  業務フロー 02 §損切りの実行機構（二重決済で残る注文が反対方向の建玉を生む）。
- 前提（覆さない）: IADR-0428（2026-09-26 追記。建玉 0 を消滅と読む前にエントリーを見る）、IADR-0344 追記(9)・IADR-0412（帰属不明の検知）、
  IADR-0576 決定 3（完了は最新の行が同じレグのときだけ）。
- 範囲: issue の推奨 1・2。**推奨 3（エントリーと逆指値を同時に置かない）は ADR-0040 の射程であり、実装しない。**
  自動の買い戻し・エントリーの取消もしない（別途の判断）。
- 試験 ID: develop の最大 T-10-3026 の次から T-10-3027〜。T-10-3025・T-10-3026 は期待を書き換える（消さない・skip しない）。

## 母集合（規則 9。誤りの側の文字列で引いた）

| 引いた文字列 | 場所 | 判定 |
| --- | --- | --- |
| `order.Status == OrderStatus.Filled`（`ProtectiveStopGuard.cs`） | `EvaluateAsync` の Filled 分岐（:378） | **是正する**（本件の中心） |
| `after.Status == OrderStatus.Filled` | `CancelOrphanedStopAndConfirmAsync`（:447）。到達前に `HoldUnlessPositionGoneAsync` がエントリーの非終端・不明を据え置いている＝エントリーは終端で、取消の窓の約定は Error ログ済み | 是正しない（反対建玉は下の検知が拾うようになる） |
| `record.Status is ... OrderStatus.Filled`（:905） | 送信結果待ちの逆指値を突合で採用する経路。`Replaced` を返し、次の巡回は通常の評価（＝是正後の Filled 分岐）へ入る | 是正しない（次の巡回で同じ分岐を通る） |
| `S1 の足跡が無い群`（`ProtectiveStopNetting.cs:642`） | `DetectUnattributedPositions` の除外。呼び出し元はガード（`ProtectiveStopGuard.cs:270`）と建玉観測の常駐（`UnattributedPositionDetector.cs:35`）の 2 つ | **是正する**（同じ関数なので 2 経路とも効く） |
| 同上（試験） | T-10-488（`SoftwareStopBlockingRegressionTests`。S0 だけ・同方向の超過は知らせない） | 壊さない（同方向の S0 だけの群は従来どおり触れない） |
| `S1 の足跡\|S0 だけの` （docs） | `docs/tests/FR-10_risk-controls-tests.md` の窓開けの節（現行の挙動の記録） | **書き換える** |
| `帰属不明の建玉` の検知の行 | `docs/functional/FR-10_risk-controls.md`（どの記録も主張していない建玉の行） | **追記する**（逆方向の S0 の足跡） |
| 新しい列挙値 | `ProtectiveStopRemediation` の消費者: `NotificationFormatter`・`AuditEntryFactory`・`ProtectiveStopGuardService.PublishAllAsync`（発行失敗で記憶を捨てる）・台帳（`CloseIntent` が null なら何もしない）・`docs/data/audit-events.md` | **追随する** |

## 受け入れ基準

1. 逆指値が `Filled` のとき、記録を完了させる前にエントリーの状態を照会する（発注記録が終端ならそれを使い、非終端ならブローカーへ照会する。既存の `ObserveEntryAsync`）。
2. エントリーが生きている（未約定・一部約定）: 記録は **Active のまま**残し、取消・成行・張り直しを送らない。Error ログと
   `ProtectiveStopCoverageLost`（Remediation=`StopFilledBeforeEntry`・Critical）を出す。通知はこのプロセスで未通知のときと 1 時間ごとだけ（巡回ごとに重ねない）。
3. エントリーが終端（全量約定・取消・失効・拒否）: 従来どおり完了させる。反対方向の純額が見えていれば Error ログを残す（事実は 5 の検知が出す）。
4. エントリーの状態が分からない: 反対方向の純額が見えていれば 2 と同じく据え置いて知らせる。見えていなければ従来どおり完了させる（Warning ログ）。
5. 帰属不明の検知: 建玉の方向と**逆の方向をエントリーとする S0 の記録**（有効・完了を問わない）がある群は、S1 の足跡が無くても検知の対象にする。
   通知済みの印は S0 の行が持ち、印の読み戻しは「S0 の行は逆方向の群」に対応させる（S1 の行は従来どおり同方向）。
6. 同方向の S0 だけの群（T-10-488）・S1 の群の既存の挙動は変えない。
7. T-10-3025・T-10-3026 の期待を是正後の挙動へ書き換える。

## 試験

| ID | 内容 |
| --- | --- |
| T-10-3025（書き換え） | 逆指値が先に約定・エントリーは未約定 → 記録は Active・エントリーを照会・取消も成行も無し・StopFilledBeforeEntry を 1 回。エントリーが全量約定して建玉 0 → 完了 |
| T-10-3026（書き換え） | 同上でエントリーが失効 → 完了・売り建玉 10 を帰属不明として知らせる（同じ巡回） |
| T-10-3027 | 据え置きの通知は巡回ごとに重ねない（5 分後は出ない・1 時間後に出る） |
| T-10-3028 | エントリーの状態が不明・売り建玉あり → 据え置き＋StopFilledBeforeEntry |
| T-10-3029 | エントリーの状態が不明・反対建玉なし → 従来どおり完了（通知なし） |
| T-10-3030 | 通常の損切り（エントリーの記録が Filled・建玉 0）→ 従来どおり完了・ブローカーへエントリーを照会しない・事実なし |
| T-10-3031 | 検知（建玉観測の経路）: 完了した S0 の買いの記録と売り建玉 → 知らせる。印は S0 の行・1 時間は重ねない・解消で印を消す |
| T-10-3032 | 検知: 同方向の S0 だけの群は従来どおり触れない（逆方向の記録が無い） |
| T-10-3033 | 通知（`NotificationFormatter`）・監査（`AuditEntryFactory`）・契約の序数（末尾） |
| T-10-3034 | 発行に失敗したら据え置きの記憶を捨てる（`PublishAllAsync`） |

変異: Filled 分岐のエントリー照会を外す（T-10-3025・3026 赤）／通知の間隔を外す（T-10-3027 赤）／検知の逆方向の足跡を外す（T-10-3026・3031 赤）。

## 検証

- `dotnet build` / `dotnet test` を OrderExecutionService・NotificationService・AuditService・Shared.Contracts のテストに絞って実行する。
- `dotnet format --verify-no-changes`、文書系検査（trace ブロック・ADR 索引）。
