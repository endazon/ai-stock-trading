---
title: IADR-0040 相場操縦パターン検知は自口座の直近発注統計に対する純関数ヒューリスティックで判定し、既定しきい値を保守側に置く
type: impl-adr
status: Accepted
related_ids: [FR-19, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
---

# IADR-0040: 相場操縦パターン検知は自口座の直近発注統計に対する純関数ヒューリスティックで判定する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-19（相場操縦とみなされ得る発注パターンの禁止）、ADR-0007（取引ガードを発注前に決定的強制）。
- 先行する実装 ADR: [IADR-0006](IADR-0006_manipulation-guard-extension-point.md)（拡張点＝ガード設定・理由コード・判定ポートを用意し、検知本体を後続スライスへ委ねた）。
- 実装仕様書: [20260711_manipulation-detector](../specs/20260711_manipulation-detector.md)。
- 対象 Issue: #49（`Refs #49`。IADR-0006 のフォローアップ）。
- 対象コード: `RiskManagementService.Domain`（`ManipulationPatternAnalyzer` ほか）、`RiskManagementService.Application`
  （`IOrderActivitySource`／`ManipulativeOrderPatternDetector`／`InMemoryOrderActivitySource`）、`TradingDefaults`。

## コンテキストと課題

IADR-0006 は判定ポート `IManipulativeOrderPatternDetector` を用意したが、検知アルゴリズム本体は「運用データが必要」として
後続に委ねられた。禁止対象は計画上 3 型（06_daytrading-review §2.3（計画リポ））:
**見せ玉（約定意思のない発注）**・**板を演出する型（レイヤリング）**・**過剰な注文訂正/取消の反復**。判定コアは決定的な
純関数（IADR-0003/0004）で、`RiskEvaluator` から同期的に呼べる必要がある。一方、注文履歴（発注・訂正・取消）の永続化は
別スライス（#13/#17）で、市場全体の板厚（他者注文）データも本スライスの供給範囲外である。

## 検討した選択肢

1. **市場全体の板厚（Level 2）と他者注文を含めた本格的な相場操縦検知** — データ供給（実気配・板の時系列）とモデルが重く、
   本スライスの範囲・データ前提を超える。誤検知の責任も重い。
2. **自口座の直近発注統計に対するヒューリスティック判定（純関数）＋供給アダプタの分離** — 見せ玉/過剰訂正取消/自己レイヤリングは
   自口座の発注ライフサイクル（発注・訂正・取消・約定・生存時間）だけで近似検知できる。純関数コアで決定的・テスト可能。
3. **閾値なしの単純ルール（例: 取消が 1 件でもあれば拒否）** — 正常なデイトレードの取消（値動きに応じた建て直し）まで
   ブロックし実用にならない。

## 決定

選択肢 2 を採用する。

- 判定は**自口座の直近窓（既定 5 分）の発注統計**に対する純関数 `ManipulationPatternAnalyzer.Analyze(window, settings)` で行う。
- 4 シグナル（`ExcessiveCancellations` / `ExcessiveAmendments` / `NoExecutionIntent` / `Layering`）のいずれか該当で嫌疑ありとする。
- **最小標本数（既定 5 発注）未満の窓は常に無嫌疑**とする（低頻度の正常取引での誤検知を防ぐ安全側の既定）。
- しきい値は `ManipulationDetectionSettings` に外出しし、`TradingDefaults.CreateManipulationDetectionSettings()` で既定値を与える。
- データ供給は `IOrderActivitySource`（同期ポート）で分離し、本スライスは `InMemoryOrderActivitySource`（プロセス内リングバッファ）を提供する。
  実注文履歴テレメトリ（発注・訂正・取消イベントの永続化 #13/#17）からの供給と本番ホスト DI 登録は後続で結線する。

### 既定しきい値と逆算根拠

自己資金・低頻度（30 分判断サイクル）のリテール運用を前提に、正常なデイトレード（値動きに応じた建て直し・数件の取消）を
誤検知せず、明らかに濫用的なパターンだけを捕捉する保守側の初期値とする。運用ログで較正する（フォローアップ）。

| 設定 | 既定 | 根拠 |
| --- | --- | --- |
| `LookbackWindow` | 5 分 | 見せ玉・レイヤリングは短時間の連続発注に現れる。判断サイクル（30 分）より十分短い突発窓 |
| `MinimumSampleSize` | 5 発注 | これ未満は統計的に濫用と正常を区別できない。数件の取消は正常運用でも起こり得る |
| `MaxCancellationRatio` | 0.7 | 窓内の約定なし取消が発注の 7 割超は、約定志向の運用として過剰 |
| `MaxAmendmentsPerOrder` | 3.0 | 1 発注あたり平均 3 回超の訂正反復は板操作的（正常な建て直しは通常 0〜1 回） |
| `MinFillRatio` | 0.1 | 窓内の約定/一部約定が発注の 1 割未満＝約定意思の希薄さ（見せ玉の兆候） |
| `ShortLivedCancelThreshold` | 2 秒 | 発注→即取消（2 秒以内）の反復は見せ玉の典型。人手・通常アルゴの反応より速い |
| `MaxShortLivedCancels` | 3 件 | 短命取消が 3 件以上で見せ玉パターンとみなす（`NoExecutionIntent` は低約定率と AND） |
| `LayeringOrderCount` | 3 本 | 同一方向・約定なし取消の**同時生存**が 3 本以上＝板に複数段を並べる見せ板の型 |

## 理由

- 純関数コア（`ManipulationPatternAnalyzer`）は決定的・テスト容易で、`RiskEvaluator` の同期判定に自然に組み込める（IADR-0003/0004 と整合）。
- しきい値の外出しと最小標本ガードで、正常なデイトレードを誤検知しない安全側に倒しつつ、濫用パターンを捕捉できる。
- データ供給をポート（`IOrderActivitySource`）で分離することで、実テレメトリ（#13/#17）の確定を待たずにアルゴリズムを固定・検証でき、
  結線先だけ後続で差し替えられる。

## 結果

- 良い影響: IADR-0006 の拡張点に対する検知本体が確定し、「フラグ ON＋該当→拒否」を CI（結合テスト）で担保できる。しきい値の根拠が残る。
- 悪い影響・トレードオフ:
  - 検知は**自口座の発注統計**に限定され、市場全体の板を用いた相場操縦（他者との協調・板厚操作）は対象外（本スライスのデータ前提の限界）。
  - 既定しきい値は運用データ前の初期値で、較正が必要。誤検知は正常な建て直しの拒否、見逃しは規制リスク残存というトレードオフを持つ。
  - 本番での実効化は実注文履歴テレメトリ（#13/#17）の永続化と `IOrderActivitySource` の実装差し替え・ホスト DI 登録が前提（切り分け）。
- フォローアップ: #13/#17 のテレメトリ確定後に実供給へ差し替え・本番 DI 登録・実 E2E（#82）で `Closes #49`。運用ログでしきい値を較正。
  該当シグナル詳細の監査記録（#17/#80 連動）。
- しきい値の較正経路: `ManipulationDetectionSettings` は現状 `TradingDefaults` の静的既定のみで実行時変更経路を持たない
  （生成AI・自動処理が改ざんできない安全側）。当面の較正はコード変更＋再デプロイで行い、設定ストア化（利用者のみ変更・履歴記録）は
  必要に応じて #19（FR-17）の設定管理に合流させるかを後続で判断する。
- 窓の母集団: 各比率の分母 `placements` は窓内の全レコード。ブローカー拒否（`OrderStatus.Rejected`）は板に載らず約定意思の指標に
  ならないため `IsCancelledWithoutFill` には含めない。拒否注文を母集団（分母）に含めるか否かは供給側（`IOrderActivitySource`・#13/#17）の
  記録方針で定める（本 PR の InMemory 実装では拒否注文を記録しない前提）。

## 関連

- Supersedes: なし（IADR-0006 を**補完**する。拡張点はそのまま）。
- Superseded by: なし
