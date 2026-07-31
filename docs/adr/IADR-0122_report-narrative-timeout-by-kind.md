---
title: IADR-0122 報告書散文 LLM のタイムアウトを報告書種別ごとに解決する
type: impl-adr
status: Accepted
related_ids:
  - FR-06
  - FR-07
  - FR-11
  - FR-16
  - UC-03
  - UC-04
  - UC-05
  - ADR-0003
  - ADR-0011
  - IADR-0032
  - IADR-0061
  - IADR-0071
  - IADR-0115
  - IADR-0120
author: claude
created: 2026-08-01
updated: 2026-08-01
plan_refs:
  - "../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md (報告サイクル: 月報→週報→日報→取引の方針階層・fixed)"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md (AI 判断のガードレール・Accepted)"
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0011_llm-model-pinning.md (取引判断の LLM モデル固定・Accepted)"
---

# IADR-0122: 報告書散文 LLM のタイムアウトを報告書種別ごとに解決する

- 状態: Accepted
- 日付: 2026-08-01
- 決定者: claude（実装）／利用者（issue #308 の指定＝報告書散文向けにタイムアウトを延長する）

## 起点・関連

- 起点 issue: [#308](https://github.com/endazon/ai-stock-trading/issues/308)
  （週報の LLM 所感が既定 30 秒タイムアウトで縮退しプレースホルダになる）。
- 傘 issue: [#279](https://github.com/endazon/ai-stock-trading/issues/279)（経路B SIMULATE の本番パリティ未達）。
- 前提: [IADR-0120](./IADR-0120_report-kind-purpose-and-parent-policy-feedforward.md)（種別別 purpose）、
  [IADR-0115](./IADR-0115_report-auto-generation-scheduler.md)（自動生成）、
  [IADR-0071](./IADR-0071_report-service-remaining.md)（実 LLM 散文ドラフトと fail-safe）。
- 基盤側: [microservices-platform#422](https://github.com/endazon/microservices-platform/pull/422)
  （`report-weekly`=`claude-opus-5` / `report-monthly`=`claude-fable-5` / `report-daily`=`claude-sonnet-5`）。
- 作業仕様書: [20260801_308_report-narrative-timeout-by-kind](../specs/20260801_308_report-narrative-timeout-by-kind.md)

## コンテキストと課題

### タイムアウトがサービスに 1 つしかない

`ReportService.Worker` は名前付き HttpClient `report-llm` を 1 本だけ作り、その `Timeout` に
`LlmGateway:TimeoutSeconds`（未設定・非正値は 30 秒）を割り当てている。`HttpReportNarrativeDrafter` は
報告書種別（`ReportNarrativeContext.Kind`）を **purpose の解決にだけ**使い（IADR-0120 決定1）、
タイムアウトには反映していない。結果、日報・週報・月報が同じ 30 秒で打ち切られる。

IADR-0120 で種別ごとに別モデルを割り当てた結果、所要時間は種別で大きく異なる。
週報（opus-5）は 30 秒に収まらず、日報（sonnet-5）は収まる。**同じ設定なのに種別で成否が分かれる**
状態になり、週報・月報の所感は恒常的にプレースホルダへ縮退していた（2026-07-31 の経路B live 検証で実測。
要求開始からちょうど 30 秒後に縮退の WRN が出る）。

### 影響は「上位方針の空洞化」

報告書は月報→週報→日報の階層をなす方針書であり（03_reporting-cycle・fixed）、IADR-0120 決定3 で
上位方針の本文を下位の散文へ feed-forward している。上位の所感がプレースホルダのままだと、
**下位が参照する上位方針の中身が空洞**になる。しかも縮退は WRN ログのみで報告書自体は正常に提示されるため
気付きにくい。

### 縮退そのものは正しい

タイムアウト時にプレースホルダ散文へ倒すのは fail-safe（IADR-0071）であり、数値の権威は
コード集計（FR-16）にあるため**数値の正しさは損なわれていない**。是正すべきは「所感が実質常に生成されない」
ことだけである。

## 検討した選択肢

### A. タイムアウトの持たせ方

| 案 | 内容 | 評価 |
| --- | --- | --- |
| A-1 | `values-local.yaml` の `LlmGateway__TimeoutSeconds` を 120 にするだけ | ✗ 日報の遅延検知が同時に鈍る。設定漏れの環境（本番 values）は 30 秒のまま是正されない |
| A-2 | 種別ごとに名前付き HttpClient を分ける（`report-llm-daily` 等） | △ 3 本のクライアント・3 通りの BaseAddress 設定が増える。プールも分かれる |
| **A-3** | **単一クライアントのまま、要求ごとに種別のタイムアウトを CTS で適用する** | ✅ 採用。クライアント構成は 1 本のまま、種別差だけを要求時に解決できる |

A-3 では `HttpClient.Timeout` を「解決値の最大」に設定して**上限としては残す**。要求ごとの CTS が壊れても
無制限に待たない（多層防御）。

### B. 既定値の置きどころ

| 案 | 内容 | 評価 |
| --- | --- | --- |
| B-1 | 組込既定は 30 秒のまま、Helm の env でのみ種別別に延長する | ✗ `values.yaml`（本番既定）へ env を足す必要があり既定描画が変わる。env を書き忘れた環境は是正されない |
| **B-2** | **組込既定を種別ごとに変える（日報 30 / 週報・月報 120）。env は上書きの手段として用意する** | ✅ 採用。設定を足さなくても是正され、本番既定の描画はバイト等価のまま |

### C. 設定キーの形

`LlmGateway:TimeoutSeconds` を節に格上げして `LlmGateway:TimeoutSeconds:Weekly` とする案は、
同一キーが「値」と「節」を兼ねることになり構成ツリー上あいまいになる（既存デプロイは値として設定済み）。
別キー `LlmGateway:TimeoutSecondsByKind:{Daily,Weekly,Monthly}` を採る。

## 決定

### 決定 1: タイムアウトは報告書種別ごとに解決する

解決順は **種別別設定 → 全種別設定（`LlmGateway:TimeoutSeconds`）→ 組込既定**。
解決は Application の純関数 `ReportNarrativeTimeouts` が担い、実 HTTP を伴う Worker 実装から分離して
単体テストで期待値を固定する（`ReportNarrativePurpose` と同じ方針）。

### 決定 2: 組込既定は 日報 30 秒 / 週報 120 秒 / 月報 120 秒 とする

日報は現に成功しているため据置（延ばすと遅延検知が鈍る＝退行を作らない）。週報・月報は重いモデルが
割り当てられており（IADR-0120）、30 秒では構造的に間に合わないため 120 秒とする。

120 秒という値は「30 秒で切られた事実」と「自動生成は閉場後の常駐から動く非対話処理である」ことから決めた
上限であり、正常時の所要時間そのものではない。**タイムアウトは異常検知の上限であって目標値ではない**。

### 決定 3: 全種別設定 `LlmGateway:TimeoutSeconds` は上書きとして残す

既に値を入れているデプロイを壊さない（IADR-0120 決定2 の `LlmGateway:Purpose` と同じ扱い）。
明示設定は種別別設定が無い種別すべてに適用される。空文字・非数値・0・負値は「未設定」として扱い、
より外側の既定へ倒す（既存 `ParseTimeout` の fail-safe を踏襲）。

### 決定 4: 本番 `values.yaml` の描画はバイト等価に保つ

新しい env を既定階層へ足さない。決定 2 により**設定を足さなくても本番も是正される**ため、
既定描画を変える必要が無い。経路B（`values-local.yaml`）にだけ 120 秒を明示し、live 環境で効いている値が
マニフェスト上で読めるようにする（＋ `helm.yml` の描画検査で固定）。

### 決定 5: 縮退ログに発火した秒数を残す

従来の WRN は「タイムアウトした」しか言わず、どの上限で切られたのかが運用中に分からなかった。
種別ごとに上限が変わる以上、秒数と種別をログに含める。メトリクス計上・通知（issue #308 案3）は
本決定の範囲外とし、別途扱う。

### 決定 6: 数値の権威・fail-safe は不変

タイムアウト延長は散文にのみ効く。数値はコード集計が権威（FR-16）であり、縮退時にプレースホルダ散文へ
倒す挙動、その他の縮退経路（非 2xx・`Sent=false`・拒否・空応答）も変えない。リトライは基盤側一元化のまま
（AST 側で重ねない）。

## 理由

- **種別ごとにモデルが違うのに上限が 1 つ**という不整合が事象の直接原因であり、上限を種別軸へ揃えるのが
  最小かつ本質的な是正である（案 A-1 の一律延長は日報の観測を鈍らせるだけで、設定漏れ環境は救わない）。
- 組込既定を変える（決定 2）ことで、**Helm の設定に依存せず**是正が効く。設定点は「延ばす手段」ではなく
  「環境ごとに調整する手段」として残る。
- 要求ごとの CTS（決定 1）は `HttpClient` の構成を増やさず、既存の縮退分岐
  （`catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)`）をそのまま使える。
  呼び出し側のキャンセル（停止要求）とタイムアウトの区別も従来どおり保たれる。

## 結果

- 週報・月報の所感が生成されるようになり、上位方針の feed-forward（IADR-0120 決定3）に実体が載る。
- 日報の挙動は変わらない（30 秒・現行成功）。
- 本番既定の Helm 描画はバイト等価。経路B は 120 秒を明示。
- 週報・月報の 1 回の生成が最大 120 秒ブロックし得る。自動生成は閉場後の常駐（`ReportAutoGenerationService`）
  から動く非対話処理であり、期間ごとに独立して捕捉される（IADR-0115）ため、遅延が他期間・他機能を巻き込まない。
- 120 秒でも足りない場合は再びプレースホルダへ縮退する（fail-safe は不変）。その場合は WRN の秒数から
  上限に当たったことが判別できる。

## 関連

- [IADR-0120](./IADR-0120_report-kind-purpose-and-parent-policy-feedforward.md): 種別別 purpose・上位方針の feed-forward
- [IADR-0115](./IADR-0115_report-auto-generation-scheduler.md): 報告書の自動生成（提示まで）
- [IADR-0071](./IADR-0071_report-service-remaining.md): 実 LLM 散文ドラフトと fail-safe
- [IADR-0061](./IADR-0061_llm-production-wiring.md): 取引判断の実 LLM 接続（タイムアウト構成化の先行例）
- [IADR-0100](./IADR-0100_route-b-values-local-standing-config.md): 経路B の values-local プロファイル
