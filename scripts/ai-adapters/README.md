# ai-adapters — worker スロットの CLI アダプタ

orchestrator（司令塔）が worker（実装担当エンジン）を CLI で起動するための共通エントリと、
エンジン別実装を置く。役割スロットの正本は [`docs/ai-orchestration.md`](../../docs/ai-orchestration.md)、
配役とフォールバック連鎖は [`ai-roster.json`](../../ai-roster.json)。

```text
run-worker.sh           # 共通エントリ。ロースターを読み、エンジン実装へ委譲し、フォールバックを判定する
run-worker-claude.sh    # Claude 実装（claude -p）。既定
run-worker-codex.sh     # Codex 実装（codex exec）。差し替えの例示
run-worker-<engine>.sh  # 追加はこの命名で（codex 実装を雛形にする）
```

## 共通契約（全エンジン実装が守る入出力）

### 入力

| 引数 | 必須 | 意味 |
| --- | --- | --- |
| `--prompt-file <path>` | どちらか必須 | タスクのプロンプト（issue 本文相当） |
| `--issue <番号>` | 〃 | `gh issue view` で本文を取得しプロンプトにする（`gh` が要る） |
| `--worktree <絶対パス>` | 必須 | orchestrator が用意した git worktree。**この外に書かない** |
| `--branch <名>` | 任意 | 作業ブランチ名。未指定なら worktree の現在ブランチ |
| `--engine <名>` | 任意（`run-worker.sh` のみ） | ロースターの worker.engine を上書き |
| `--no-fallback` | 任意（`run-worker.sh` のみ） | 失敗時の自動切り替えを無効化 |
| 環境変数 `WORKER_TIMEOUT` | 任意 | タイムアウト秒。既定 2700（= GitHub 面 `claude-coding.yml` の 45 分と同じ） |

### 出力

- **成果**: worktree 内の**コミット済み** diff。コミットには
  `Co-Authored-By: <エンジン名> <モデル名>` トレーラを付ける（帰属の機械可読記録）。
- **終了コード**: 0 = 完了 / 非 0 = 失敗（**黙って部分成果を残さない**。失敗時の未コミット変更は
  呼び出し側が破棄する）。タイムアウトは `timeout(1)` の 124。
- **stdout**: 末尾に成果サマリ 1 行。`run-worker.sh` はフォールバック発生時に機械可読 1 行
  `FALLBACK <from> -> <to> reason=<exit|timeout|rate-limit>` を出す（orchestrator が台帳へ記録し、
  成果 PR に `fallback-from:<engine>` ラベルを付ける）。

### 禁止・安全策

- worktree 外への書き込み・force push・中間成果物（質問票等）のコミットは禁止（契約は
  `AGENTS.md` §worker スロットとして動く場合の契約。**共通エントリが各エンジンへのプロンプト末尾に
  この契約を自動で添付する**）。
- 🔴 `.claude/hooks/` のガードは Claude Code 専用である。**非 Claude エンジンでは、エンジン自身の
  サンドボックス（例: Codex の `--sandbox workspace-write`）を必ず指定する**。指定できないエンジンを
  アダプタ化しない。

## フォールバックの動作（run-worker.sh）

1. 試行順は「`--engine`（指定時）または ai-roster.json の `worker.engine`」→ `worker.fallback` の先頭から順。
2. 失敗の判定: ①非 0 終了 ②タイムアウト（124） ③stderr にレート制限・過負荷パターン
   （`429` / `rate limit` / `overloaded`。**success でもレート制限で空振りする CLI があるため exit 0 でも検査する**）。
3. 切り替え前に worktree を割当時点（起動時に記録した HEAD）へ戻し、未コミットの変更・未追跡ファイルを
   破棄する。**worktree はアダプタ専用の隔離場所であり、この破棄は手作業の成果を巻き込まない**
   （リポジトリ本体の「破壊的 git 操作の禁止」は共有ブランチ・共有作業場所を守る規約であり、
   専用 worktree の巻き戻しはその対象外。ただしコミット済みの成果は破棄しない）。
4. `--no-fallback` 時、または連鎖を使い切ったら非 0 で終了する（`WORKER_FAILED engines=<試行列>`）。

## 動作確認の手順（検査器は作らない。手で確認する）

エンジン実装をスタブ（`exit 0` / `exit 1` / `sleep` / stderr へ `429` を出す）に差し替え、
次の 4 点を確認する: ①正常系が exit 0 で成果サマリを出す ②失敗時に `FALLBACK` 行が出て次エンジンが
走る ③タイムアウトが 124 で判定される ④切り替え前に worktree が起動時 HEAD へ戻る。
（検査器化しない理由: 事故 2 回条件。確認結果は作業仕様書に残す）
