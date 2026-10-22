---
title: IADR-0570 連結ローカルの起動器はメッシュ参入（サイドカー注入）を AST_MESH_INJECTION で受け、未指定の再実行は現行のリリースの値を引き継ぐ。名前空間の注入ラベルは起動器が貼り、OpenD の注入対象外の注釈は注入の有無に依らず描く
type: impl-adr
status: Accepted
related_ids: [NFR, ADR-0001, IADR-0314, IADR-0283, IADR-0457, IADR-0439]
author: claude (Claude Code)
created: 2026-10-23
updated: 2026-10-23
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md（基盤の再利用。メッシュは基盤の構成に乗る）
related_specs:
  - ../specs/20261023_GHSA-j4qv_mesh-injection-handover.md
---

# IADR-0570: 起動器のメッシュ参入の受け口と引き継ぎ

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-10-23
- 決定者: 実装エージェント（受け口の形）。基盤の段（MSP#2092・基盤の IADR-0590）のフォローアップ 1 を AST 側で受ける。

## 起点・関連

- 起点: Refs GHSA-j4qv-2w99-xg5p（多層防御の強化。基盤のメッシュの mTLS の既定を STRICT にした段の前提を AST 側で整える）。
- 前提: [IADR-0314](IADR-0314_mesh-sidecar-injection-switch.md)（chart の設定点 `mesh.sidecarInjection.*`。既定 off）。
- 関連: [IADR-0283](IADR-0283_deploy-value-preservation-and-kb-realm-fix.md)（起動器は `--reuse-values` を使わず、値ごとに前回値を引き継ぐ）、
  [IADR-0457](IADR-0457_deploy-changed-services-selection.md)（作り直すイメージの絞り込みと restart の範囲）、
  [IADR-0439](IADR-0439_helm-release-drift-read-only-check.md)（配備の前後の差の確認。OpenD の差は終了コード 3）。
- 作業仕様書: [20261023_GHSA-j4qv_mesh-injection-handover](../specs/20261023_GHSA-j4qv_mesh-injection-handover.md)。

## コンテキスト

- 基盤の順序は「AST を先にメッシュへ入れてから、基盤を STRICT へ上げる」である。AST をメッシュへ入れるには
  (1) 名前空間の注入ラベル、(2) chart の `mesh.sidecarInjection.enabled=true`（OpenD を注入対象外にする注釈）、
  (3) OpenD 以外の Pod の作り直し、が要る。
- 起動器（`scripts/k8s-local-deploy.sh`）は `--reuse-values` を使わず、値ごとに前回値を引き継ぐ（IADR-0283）。
  `mesh.sidecarInjection.enabled` はその対象に無く、受け口も無かった。手で与えた値は次の配備で既定へ戻る。
- 起動器は名前空間を自前で作り `namespace.create=false` で chart を描くため、chart は注入ラベルを描かない（IADR-0314 決定 1）。
  この経路で注入を決めるのは名前空間のラベルであり、chart の `enabled` が変えるのは OpenD の注釈（と CronJob の注釈）だけである。
- IADR-0314 決定 2 は OpenD の注釈を「`enabled` かつ `excludeOpend`」で描く。`enabled` が外れると注釈が消えて OpenD の
  Pod テンプレートが変わり、OpenD が作り直される（認証済みのセッションが切れる）。ラベルが残っていれば、作り直された
  OpenD にサイドカーが入る。逆に `enabled` を入れるときも、注釈が増えて OpenD が作り直される。

## 検討した選択肢

1. **chart の値を手で `--set` する運用で済ませる** — 次の配備で外れる（上記）。採らない。
2. **起動器に `--reuse-values` を使う** — IADR-0283 が採らないと決めた（`-f values-local.yaml` との合成順序が読みにくい）。採らない。
3. **既存の値の引き継ぎ（`AST_VALUE_KEYS`）に 1 行足すだけ** — 真偽値の検証・名前空間のラベル・作り直しの範囲・OpenD の固定を
   扱えない。採らない。
4. **専用の受け口（`AST_MESH_INJECTION`）を足し、名前空間のラベルを起動器が扱い、OpenD の注釈を `enabled` から外す（採用）。**
5. **OpenD の除外を基盤のメッシュの設定（注入器の `neverInjectSelector`）で持つ** — AST の Pod テンプレートを変えずに済むが、
   AST の配備の前提を基盤の Istio の導入値へ置くことになり、AST の chart だけで閉じない。採らない。

## 決定

- **決定 1（受け口と優先順位）**: `AST_MESH_INJECTION=1|0`（`true|false` も可）。優先順位は「明示 → 現行の helm リリースの
  `mesh.sidecarInjection.enabled` → 既定（無効）」。それ以外の値（空を含む）は exit 2。前回値が真偽値として読めなければ、
  明示が無い限り止める（黙って無効へ戻さない）。既定（未指定・前回に宣言なし）では helm へ何も足さない（従来と同じ引数）。
- **決定 2（OpenD は常に注入対象外）**: 有効なとき、起動器は `mesh.sidecarInjection.excludeOpend=true` を必ず渡す（プロファイルの
  上書きを許さない）。chart は `excludeOpend=true` なら **`enabled` に依らず** OpenD の Pod へ `sidecar.istio.io/inject: "false"`
  を描く（IADR-0314 決定 2 の描画条件の改め）。これで注入の切り替えで OpenD の Pod テンプレートは変わらない。
  起動器は従来どおり OpenD を restart しない。`excludeOpend=false`（OpenD を注入対象に含める）は chart の値として残す（起動器からは使わない）。
- **決定 3（名前空間のラベルは起動器が扱う）**: 有効なら `istio-injection=enabled` を貼る（無ければ）。明示の 0 なら剥がす。
  引き継いだ無効・既定では触らず、手で貼ったラベルが残っていれば WARN だけを出す。ラベルは helm upgrade と restart の前に扱う。
  読み書きに失敗したら止める。手順に残す（手で貼る）案は、貼り忘れ・剥がし忘れで chart の値とラベルが食い違うため採らない。
- **決定 4（作り直しの範囲）**: 注入の状態が変わった回（ラベルを貼った・剥がした、または chart の値が前回から変わった・前回値が
  読めなかった）は、作り直すイメージの絞り込み（IADR-0457）があっても OpenD を除く全 Deployment を restart する。注入は Pod の
  作成時にしか効かないため、絞ると作り直さない Pod がメッシュの外（内）に残る。

## 理由

- 起動器の他の値（`broker.tier` 等）と同じ「明示 → 現行 → 既定」で、再実行が黙って状態を戻さない。
- 起動器の経路で注入を決めるのは名前空間のラベルなので、ラベルと chart の値を同じ受け口で動かせば食い違わない。
- OpenD の注釈を `enabled` から外せば、注入の切り替えと OpenD の作り直しが切り離される。

## 結果

- 良い影響: 基盤を STRICT へ上げる前に、AST を 1 回の明示で入れ、以後の再実行で保てる。外すのも 1 回の明示で済む。
- 悪い影響・トレードオフ:
  - 🔴 **注釈を初めて描く配備（本変更を入れた最初の配備）では、`opend.enabled=true` の OpenD の Pod テンプレートが 1 度だけ変わり、
    OpenD が作り直される**（認証済みのセッションが切れ、再検証が要り得る）。配備は閉場後に行う。配備の前の差の確認
    （IADR-0439）は OpenD の差（注釈 1 つ）で終了コード 3 になる。この 1 回だけは意図して進める。以後は変わらない。
  - 名前空間のラベルは helm upgrade の前に変える（作り直す Pod にサイドカーを入れるため）。upgrade が失敗すると「ラベルは新しい状態・
    chart の値は前回のまま」が残り得る。ロールバックはしない。起動器はこの回にラベルか値を変えたときだけ、失敗時に案内を出す。
    復旧は原因を直してから `AST_MESH_INJECTION=1` か `0` を**明示して**再実行する（未指定だと前回の値を引き継ぎ、ラベルと食い違ったままになり得る）。
  - chart の既定の描画は、`opend.enabled=true` のときだけ注釈 1 つ分変わる（`opend.enabled=false` の既定の描画は変わらない）。
  - OpenD（サイドカー無し）へ届く経路（発注執行の API・基盤の BFF から認証サイドカー）は、送り手のサイドカーの auto-mTLS が
    平文で出す想定である（**実測要**。PoC の手順で確かめる）。`platform-infra` 宛の非 HTTP も IADR-0314 決定 3 のまま実測要。
- 検証: `scripts/k8s-local-deploy.test.sh` の T-mesh-01〜11、`.github/workflows/helm.yml` の IADR-0570 の検査（注入の有無で
  OpenD の描画を丸ごと突き合わせる。陰性対照つき）。いずれも変異で赤になることを確かめた。
