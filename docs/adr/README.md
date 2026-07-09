# 実装ADR（Implementation ADR）

本リポジトリ内の意思決定記録（Implementation ADR）の索引である。実装に閉じた技術・設計・運用の決定を `IADR-XXXX` として記録する（必須）。

## 計画ADR との違い

| | 計画ADR | 実装ADR |
| --- | --- | --- |
| 場所 | 計画リポ `projects/<name>/07_adr/` | 本リポ `docs/adr/` |
| ID | `ADR-XXXX` | `IADR-XXXX` |
| 対象 | 上流の意思決定（プロダクト全体） | 実装レベルの意思決定（内部設計・ライブラリ選定等） |

> 計画に影響する決定は、実装ADR に記録するのではなく `/plan-feedback` で計画側へ環流する。

## 運用ルール

- 1 ファイル = 1 意思決定。`IADR-<連番4桁>_<タイトル>.md`（雛形 `docs/templates/adr_template.md`、`/new-spec adr` で採番作成）。
- 連番はリポジトリ内で一意・昇順・欠番なし。
- 状態は `Proposed / Accepted / Deprecated / Superseded`。既存決定を覆す場合は新 IADR を作り、旧 IADR に `Superseded by IADR-XXXX` を追記する。
- 重要な実装判断は必ず IADR に残す（必須）。

## 一覧

| IADR | タイトル | 状態 |
| --- | --- | --- |
| IADR-0000 | 実装意思決定の記録方針 | Accepted |
| IADR-0001 | リポジトリ構成と技術スタック | Accepted |
| IADR-0002 | TradingDefaults の既定値は全体前提条件からの逆算値として明示する | Accepted |
| IADR-0003 | ポジションサイジングは取引判断サービスが行い、RiskEvaluator は検証のみとする | Accepted |
| IADR-0004 | エントリー/手仕舞いは建玉効果（PositionEffect）で判定し、売買方向から分離する | Accepted |
| IADR-0005 | 段階資金上限は保有取得額合計＋当該注文額（コストベース累計）で判定する | Accepted |
| IADR-0006 | 相場操縦パターン禁止はガード設定＋判定ポートの拡張点として用意する | Accepted |
