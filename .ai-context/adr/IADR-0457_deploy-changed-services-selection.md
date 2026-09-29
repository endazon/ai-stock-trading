---
title: IADR-0457 ローカル配備は git の差分から作り直すイメージを選び（迷ったら全件）、ランタイムに無いイメージは必ず作り、restart も作り直した分に絞る
type: impl-adr
status: Accepted
related_ids: [NFR, IADR-0052, IADR-0295, IADR-0322, IADR-0208]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs: []
---

# IADR-0457: ローカル配備で変わったサービスだけを作り直す（#1094）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-09-29
- 決定者: Claude Code（実装）。ローカル配備の手順の内側の判断であり、計画の制約に触れない

## 起点・関連

- 関連する計画書 ID: NFR（配備の運用性。当たる FR/UC は無い）
- 関連する実装仕様書: [`.ai-context/specs/20260929_1094_deploy-changed-services.md`](../specs/20260929_1094_deploy-changed-services.md)
- 前提: [IADR-0052](IADR-0052_k8s-helm-chart-shared-infra.md)（`:latest` 固定・ローカル投入）、
  [IADR-0295](IADR-0295_discord-bot-value-carryover-and-rollout-restart.md)（helm upgrade 後の rollout restart・OpenD 除外）、
  [IADR-0322](IADR-0322_opend-auth-sidecar-command-allowlist.md)（opend-auth-gateway は OpenD Pod のサイドカー）、
  [IADR-0208](IADR-0208_ci-pr-latency-reduction.md) 決定 11（`detect-changed-areas.js` の「迷ったら走らせる」と同じ倒し方）

## 背景

`k8s-local-deploy.sh` は毎回 12 イメージをすべて作り直し、最大 30 分かかっていた（#1094）。タグは `:latest` 固定・helm はタグを渡さず・
`imagePullPolicy: IfNotPresent` なので、作り直さないサービスは前回のイメージのまま動き続けられる。選ぶ規則と、選び損ねたときの倒し方を決める必要がある。

## 決定

1. **作り直す対象は git の差分から `scripts/select-changed-services.js` が選ぶ。迷ったら全件（`ALL`）へ倒す。**
   全件: Dockerfile が COPY するルートのファイル（`global.json` / `Directory.*.props`）と `nuget.config`・`.dockerignore`・`backend/Dockerfile`・
   `backend/Shared/**`・`backend/TestSupport/**`・`backend/` の下で分類できないパス・差分 0 件・git の失敗。
   サービス単位: `backend/Services/<Dir>/**`（`Tests/**` を除く）。無視: `backend/` の外・`backend/**/*.md`・`backend/Bff/**`・`backend/Tests/**`・
   共有物のテストプロジェクト・`backend.slnx`。作り直し過ぎは遅いだけで気付けるが、作り直し漏れは古いイメージが緑のまま動き続けるためである。
2. **写しを持たない。** MAPPING は `k8s-local-images.sh` の配列を読み、サービス間の依存は毎回 csproj の ProjectReference から導いて**推移閉包**を取る。
   「Worker の入力ではない」とした領域（Bff・backend/Tests・共有物のテスト）も固定の無視にせず、起動時に csproj を読んで Worker から参照があれば全件へ倒す。
3. **選ばれていなくても、ランタイムに `:latest` が無いイメージは作る。** rancher は `nerdctl --namespace k8s.io image inspect`（ビルド先＝k3s の containerd）。
   k3d は**作るかを `docker image inspect`、import するかをノードの `crictl inspecti`** で分けて見る。docker だけを見るとクラスタの作り直しで import 漏れになり、
   ノードだけを見ると docker に残るイメージまで作り直す。ノードの確認が失敗したら import する側へ倒す（import は作り直しより桁で安い）。
4. **絞り込んだときの restart は、ランタイムへ新たに供給した（作った・import した）サービスの Deployment だけにする。** 絞り込みが無い・選択器が `ALL`・
   選択器が動かない場合は従来どおり全件 restart。OpenD は従来どおり除外し、opend-auth-gateway の作り直しは案内だけ出す。
   **従来経路（`AST_ESO=0`）で `ast-secrets` の値を変え得るパッチを送った回（env の明示指定か、非空の既定値を入れた回。平文は読み比べないので同値の明示指定も含む）は、絞り込みを解いて全件 restart する。** 空の既定値を空か不在のキーへ入れ直すだけのパッチ（毎回起きる）は数えない。 Secret を参照する env は Pod の再起動でしか
   反映されず、全件 restart が副次的にそれを届けていた（Reloader の無いクラスタでは、絞ると値の更新が黙って効かない）。ESO 所有の経路はパッチしない。
5. **口は `SERVICES=a,b`（env）と `--changed-since <ref>` の 2 つ。** 未知の名前は何も作らずに exit 2、併用も exit 2。`SERVICES` 未設定は全件（後方互換）。
   選択が 0 件のときの中継値は `SERVICES=none`（空は「全件」と区別できないため）。

## 採らなかった案

- **A. イメージタグを git SHA にして helm へ渡す**: 変わらないサービスも Pod テンプレートが変わるか、SHA の対応表が要る。helm・values-local の追随が要り、#1094 の範囲を超える。
- **B. ビルド入力の内容ハッシュで要否を決める**: 入力の列挙が Dockerfile の写しになり黙ってずれる。
- **C. k3d でも docker の有無だけを見る**: クラスタを作り直した直後に import 漏れで Pod が起動しない。
- **D. 選択があっても全件 restart する**: 作り直していないサービスは同じイメージのままで restart の効果が無く、稼働中のサイクルを無用に中断する。
- **E. Dockerfile で csproj を先に COPY して restore のレイヤを分ける**: 個々のビルドを短くするが別の変更（#1094 の補足。別 issue の候補）。

## 結果

- 1 サービスの変更なら、作り直しは 1〜2 本（依存するサービスを含む）で済む。共有物の変更は従来どおり全件。
- **残余 1**: Dockerfile の COPY を増やしたら、決定 1 の全件の表も追随が要る（Dockerfile の変更自体は全件になるので、その回は取りこぼさない）。
- **残余 2**: 選択器の前提は「クラスタで動いているイメージ ＝ ref の状態 ＋ 作業ツリーの差分」である。前回配備した状態がこれに含まれないと取りこぼす
  （前回配備の ref を記録する仕組みは持たない）。例: 前回配備より新しい ref を与える／ブランチ A で配備した後に develop 由来のブランチ B で
  `--changed-since origin/develop` を使う／未コミットの試行を配備した後にその変更を取り消す。迷ったら指定なし（全件）で配備する。
- **残余 2b**: 前回の配備が作り直しの後・restart の前に失敗した、または `k8s-local-images.sh` を単独で実行した後に、別の絞り込みで配備すると、
  先に作ったイメージは restart されない（従来の全件 restart なら拾えていた）。その場合は指定なしで配備するか、該当の Deployment を手で restart する。
- **残余 3**: 実クラスタでの短縮の実測は未実施（利用者の次回配備で確かめる）。
