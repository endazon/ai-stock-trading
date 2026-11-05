---
title: IADR-0613 Discord を経ない停止は「既存の kill switch の保持」とし、構成（管理の外の Secret）で入れて起動時に起動し、保持の間は解除を拒む。スクリプトは Pod を作り直すだけで Deployment を変えない
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-03, NFR-06, FR-10, FR-09, UC-06, ADR-0006, ADR-0009, ADR-0024, IADR-0062, IADR-0567, IADR-0344, IADR-0341]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10・NFR-03)
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_pause-resume-and-lockout-states.md (優先順位・解除の非対称)
---

# IADR-0613: Discord を経ない停止（kill switch の保持）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-05
- 決定者: Claude Code（実装）／ オーケストレーター（既存の kill switch を使う・`kubectl` の経路・手順書と一緒に、の指示）

## 起点・関連

- issue: #1530。基盤側の外部の死活監視は MSP#2402（別のセッション）。
- 前提: IADR-0062（ボットの多層の認可）・IADR-0567（発注の直前の停止の照会。不明なら送らない）・IADR-0341（Reloader）・IADR-0344（ホスト側の死活監視。クラウドでは使わない）。
- 実装仕様書: [`.ai-context/specs/20261105_1530_out-of-band-stop-and-incident-runbook.md`](../specs/20261105_1530_out-of-band-stop-and-incident-runbook.md)

## コンテキストと課題

クラウドの単一ノードでは画面を持たず、kill switch の窓口は Discord のボットだけになる。Discord に届かないとき、または Discord のアカウントを他人に使われている疑いがあるとき（Discord から止めても Discord から解除され得る）に、取引を止める手段が要る。
ノードへの SSH は残る（ファイアウォールは SSH だけ）。所有者のトークンはボットの中にしか無く、SSH の側から REST の kill switch を呼ぶことはできない。

## 検討した選択肢

1. **構成で入れる保持＋起動時に既存の kill switch を起動＋保持の間は解除を拒む（採用）** — 既存の kill switch と同じ意味（新規建てだけを止め、手仕舞い・損切りは止めない）で、変更履歴に残る。止める窓口が Discord と独立する。
2. Pod の中で 2 本目のプロセスを起動して kill switch を起動するコマンド（JasperFx のコマンド） — ホストを組み立て直すので 512Mi の容器でメモリが足りず、Pod ごと落ちる恐れがある（止める操作が統制の担い手を落とす）。解除の拒否も別に要る。
3. DB へ直接 SQL を書く — スキーマに結び付き、変更履歴を飛ばす。DB の資格情報を扱う。
4. 発注執行を 0 台にする — 損切り（ソフトウェアの逆指値）まで止まる。手順書の「最後の手段」にだけ置く。

構成の運び方:

- a. **管理の外の Secret を optional の secretKeyRef で読み、Pod を消して作り直す（採用）** — chart の既定の振る舞いは変わらない（Secret が無ければ env も無い）。Deployment の spec を変えないので helm upgrade・ArgoCD の selfHeal と食い違わない。Reloader を有効にした構成では Secret の作成・削除で自動でも再起動する。
- b. `kubectl set env` — helm の所有権の競合（`kubectl-set` のフィールドマネージャ。chart の注記）と、ArgoCD の selfHeal による巻き戻しが起きる。
- c. ConfigMap — chart に `configMapKeyRef` の描画の口を新しく足す必要がある。Secret の口は既にある。

## 決定

1. 構成 `RiskManagement:OperatorKillSwitchHold:Enabled` / `Reason`（`OperatorKillSwitchHoldOptions`）。既定は無効。
2. 有効なら、起動時（移行の後・受け付けの前）に `KillSwitchService.ApplyOperatorHold` が kill switch を起動する。actor は `operator-hold`、理由が空なら既定の理由。既に起動していれば上書きしない（先の主体・理由を保つ）。印 `operator-kill-switch-hold: active` をログへ出す。
3. 有効の間、`KillSwitchService.Disengage` は `KillSwitchHeldByOperatorException` を投げる。`RiskControlEndpoints.MapException` が 409 へ写す（gRPC は同じ写しで ABORTED）。起動は受け付ける。
4. 外しても kill switch は起動したまま残る。保持は「止める」だけで、再開は従来の解除（確認ボタン＋確認フレーズ）に任せる。
5. chart の risk-management の env に `RiskManagement__OperatorKillSwitchHold__{Enabled,Reason}` を Secret `ast-operator-hold` の optional の secretKeyRef で足す（values.yaml と、extraEnv を全要素写す values-local.yaml）。
6. `scripts/ast-trading-stop.sh`: 変える操作は `--live` が無ければ何もせず 3。`hold` は Secret を作り（理由は 0600 の一時ファイルから）、Pod を消し、ログの印を待つ（既定 180 秒。出なければ 1）。`release` は Secret を消して Pod を作り直す（解除はしない）。`isolate-discord` / `restore-discord` は notification-service の台数だけを変え、ArgoCD の Application があれば自動同期の巻き戻しを警告する。

## 結果

- 良い影響: Discord に依らずに新規建てを止められ、止めた状態を Discord から解かれない。止める操作は既存の統制と同じ意味で監査に残る。
- 悪い影響: 保持を入れると risk-management の Pod が 1 回作り直される（数十秒）。その間、発注執行は送る直前の停止の照会が不明になり、新規建てを送らない（安全側）。
- 残る制約:
  1. ノードと SSH が生きていることが前提。risk-management が起動しない間は保持も起動しない（発注執行の「不明なら送らない」に頼る）。
  2. 保持の間も、Discord から締める向き以外の操作（段階の昇格・報告書の確定など）はできる。取引には kill switch が効くが、外した後に効く。アカウントを使われている疑いがあるときはボットを止める（`isolate-discord`）。危険な操作そのものの確かめ方は別の決定（オーナーの裁定待ち）。
  3. ArgoCD の自動同期が有効なら、ボットの台数は宣言へ戻る（手順書に自動同期の止め方を書いた）。
  4. 並行の作業の profile（values-cloud）が risk-management の extraEnv を写すなら、同じ 2 行が要る。

## フォローアップ

- 外部の監視サービスの通知先に Discord 以外を入れる（基盤の作業とオーナーの登録。手順書 §クラスタの外からの検知）。
