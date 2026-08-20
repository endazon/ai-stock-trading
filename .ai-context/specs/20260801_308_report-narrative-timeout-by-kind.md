---
title: 報告書散文 LLM のタイムアウトを種別ごとに分ける（issue #308）
type: spec
status: done
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
  - IADR-0123
author: claude
created: 2026-08-01
updated: 2026-08-01
related_specs:
  - "../adr/IADR-0123_report-narrative-timeout-by-kind.md"
  - "../adr/IADR-0120_report-kind-purpose-and-parent-policy-feedforward.md"
  - "../adr/IADR-0115_report-auto-generation-scheduler.md"
  - "../adr/IADR-0071_report-service-remaining.md"
  - "./20260730_issue-291-293_report-model-and-feedforward.md"
---

# 仕様書: 報告書散文 LLM のタイムアウトを種別ごとに分ける（issue #308）

## 起点となる計画書（トレーサビリティ）

- 起点 issue: [#308](https://github.com/endazon/ai-stock-trading/issues/308)
  週報の LLM 所感が既定 30 秒タイムアウトで縮退し「LLM 未接続」プレースホルダになる。
- 傘 issue: [#279](https://github.com/endazon/ai-stock-trading/issues/279)（経路B SIMULATE の本番パリティ未達）。
- 直接の前提: [#295](https://github.com/endazon/ai-stock-trading/issues/295)（IADR-0120 種別別 purpose・feed-forward）、
  [#283](https://github.com/endazon/ai-stock-trading/issues/283)（IADR-0115 自動生成）。
- 計画根拠:
  - 04_workflows/03_reporting-cycle（計画リポ）
    （報告サイクル・**fixed**）。取引方針を月報→週報→日報の階層で管理する。上位方針の本文が空洞化すると下位が参照できない。
  - ADR-0011（計画リポ）
    （LLM モデルの固定・**Accepted**）。モデルの決定権は基盤の LlmRouter にあり、AST はモデル ID を持たない。
    種別ごとの所要時間差はこの割当に由来する。
  - ADR-0003（計画リポ）
    （AI 判断のガードレール・**Accepted**）。散文の欠落は数値の権威に影響しない。
  - リトライ等の耐障害は基盤の LLM ゲートウェイ側に一元化する（platform ADR-0010・既存実装 `HttpReportNarrativeDrafter`
    の注釈のとおり）。AST 側で重ねない。

## 背景と問題（原因の確定）

経路B の live 検証（2026-07-31）で、自動生成された**週報の所感だけ**が
「本節は LLM 未接続のため自動ドラフトされていません」（`ReportNarrativeDefaults.PlaceholderText`）のままになった。
report-service のログは要求開始から**ちょうど 30 秒**で以下を出している。

```
[09:05:32 INF] Start processing HTTP request POST http://llmgateway-service.../complete
[09:06:02 WRN] 報告書散文 LLM /complete がタイムアウト。プレースホルダ散文に倒します。
```

原因は 2 つが重なったものである。

### 1. タイムアウトが「サービスに 1 つ」しかない

`ReportService.Worker/Program.cs` は名前付き HttpClient `report-llm` を 1 本だけ作り、その `Timeout` に
`LlmGateway:TimeoutSeconds` を割り当てている。

```csharp
builder.Services.AddHttpClient("report-llm",
    c => c.Timeout = ParseTimeout(builder.Configuration["LlmGateway:TimeoutSeconds"]));

static TimeSpan ParseTimeout(string? value) =>
    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
        ? TimeSpan.FromSeconds(seconds)
        : TimeSpan.FromSeconds(30);   // 未設定/非正値は既定 30 秒
```

`HttpReportNarrativeDrafter` は `context.Kind` を **purpose の解決にだけ**使っており（IADR-0120 決定1）、
タイムアウトには一切反映していない。したがって日報・週報・月報は同じ 30 秒で切られる。

### 2. 種別ごとに所要時間が大きく違う

IADR-0120 / [MSP#422](https://github.com/endazon/microservices-platform/pull/422) により、基盤の
`Llm:Routing:PurposeModels` は種別ごとに別モデルを割り当てている（`report-daily`=`claude-sonnet-5` /
`report-weekly`=`claude-opus-5` / `report-monthly`=`claude-fable-5`）。
週報・月報は「上位ほど難度が高い」という設計どおり重いモデル・長い分量になり、30 秒に収まらない。
日報（sonnet-5）は収まるため、**種別によって成否が分かれる**。

`values-local.yaml` が `LlmGateway__TimeoutSeconds` に空文字を入れているのは「未設定＝安全既定」という
既存の書き方に沿ったものであり、それ自体は誤りではない。**既定値が種別を区別しない**ことが問題である。

### 縮退そのものは設計どおり

タイムアウト時にプレースホルダ散文へ倒すのは `HttpReportNarrativeDrafter` の fail-safe（IADR-0071）であり、
**数値の正しさは損なわれていない**（数値はコード集計が権威・FR-16）。是正対象は「週報・月報の所感が実質常に
生成されない」ことのみである。

## 対象範囲

### 変更する

1. `ReportService.Application`: 種別ごとのタイムアウトを解決する純関数 `ReportNarrativeTimeouts` を新設する。
   解決順は **種別別設定 → 全種別設定（`LlmGateway:TimeoutSeconds`）→ 組込既定**。
   組込既定は **日報 30 秒（据置）／週報・月報 120 秒**。
2. `ReportService.Worker`: `HttpReportNarrativeDrafter` が要求ごとに種別のタイムアウトを適用する
   （呼び出し側 `CancellationToken` と linked な `CancellationTokenSource`）。縮退ログに発火した秒数を残す。
3. `ReportService.Worker/Program.cs`: 名前付き HttpClient の `Timeout` は「解決値の最大」を上限として残し、
   実効の制御は要求ごとの CTS に委ねる。新しい設定点 `LlmGateway:TimeoutSecondsByKind:{Daily,Weekly,Monthly}` を配線する。
4. `deploy/helm/ai-stock-trading/values-local.yaml`: 経路B の report へ週報・月報 120 秒を**明示**する
   （既定値に依存せず、live 環境で効いている値をマニフェスト上で読めるようにする）。
5. `.github/workflows/helm.yml`: values-local の描画に種別別タイムアウトが出ることを検査に加える。
6. `appsettings.Development.json` のコメント（設定点の説明）を追随させる。

### 変更しない（意図的に対象外）

- **`deploy/helm/ai-stock-trading/values.yaml`**: 新しい env を既定階層へ足さない。既定描画は**バイト等価**を保つ
  （`helm template` の diff で実証する）。組込既定の改定によって本番も是正されるため、env を増やす必要が無い。
- **日報のタイムアウト**: 30 秒据置。現に成功しており、延ばすと遅延検知が鈍る。
- **trade-decision の `LlmGateway__TimeoutSeconds`**: 報告書散文とは別系統（IADR-0061）であり本 issue の対象外。
- **リトライ**: 既存方針どおりゲートウェイ側に一元化する（platform ADR-0010）。AST 側で重ねない。
- **縮退の可観測化（メトリクス／通知）**: issue #308 の案 3。WRN ログへ秒数を足すところまでに留め、
  メトリクス基盤への計上は本 PR の範囲外とする（別途起票）。
- **`MaxTokens`**: 4096 のまま（IADR-0101 / IADR-0120）。所要時間の主因はモデル割当であり出力上限ではない。

## 受け入れ基準

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | 種別別設定・全種別設定ともに未設定なら、日報 30 秒／週報 120 秒／月報 120 秒に解決する | 単体（`ReportNarrativeTimeoutsTests`） |
| 2 | `LlmGateway:TimeoutSeconds` を設定すると、種別別設定の無い種別すべてに適用される（既存デプロイの非破壊） | 単体 |
| 3 | 種別別設定は全種別設定より優先する | 単体 |
| 4 | 空文字・非数値・0・負値は「未設定」として扱い、より外側の既定へ倒す（fail-safe） | 単体 |
| 5 | 週報の要求が 30 秒を超えても打ち切られず応答が本文として返る（日報は 30 秒で打ち切る） | 単体（fake handler・仮想時間ではなく短い秒数へ縮尺） |
| 6 | タイムアウト縮退時の WRN ログに発火した秒数が出る | 単体 |
| 7 | `values.yaml` の描画が変更前と**バイト等価** | `helm template` diff（CI・手元） |
| 8 | `values-local.yaml` の描画に `LlmGateway__TimeoutSecondsByKind__Weekly=120` / `__Monthly=120` が出る | `helm.yml` の検査 |
| 9 | ビルド・全テスト・`dotnet format` が緑 | `/verify` |

## 実装方針（TDD）

1. `ReportNarrativeTimeoutsTests` を赤で書く（基準 1〜4）。
2. `ReportNarrativeTimeouts` を実装して緑にする。
3. `HttpReportNarrativeDrafterTests` に基準 5・6 を赤で追加する（遅延するハンドラ＋短いタイムアウト）。
4. `HttpReportNarrativeDrafter` へ要求ごとの CTS を実装して緑にする。
5. `Program.cs` の配線を更新し、Worker の配線テストで解決値を確認する。
6. Helm（values-local ＋ helm.yml）を更新し、`values.yaml` 描画のバイト等価を diff で確認する。

## テスト観点

- 解決順（種別別＞全種別＞組込既定）と fail-safe（空/不正/非正値）。
- 実効タイムアウトが**種別ごとに違う**こと（同一 drafter インスタンスで日報は落ち、週報は通る）。
- 呼び出し側のキャンセルは従来どおり伝播する（タイムアウトと取り違えて縮退しない）。
- 既存のプレースホルダ縮退（非 2xx・Sent=false・拒否・空応答）は不変。

## 完了条件（DoD）

- [x] 受け入れ基準 1〜9 を満たす
- [x] `dotnet build` / `dotnet test` / `dotnet format` 緑
- [x] `helm template`（既定）が変更前とバイト等価
- [x] IADR-0123 を作成し、決定と根拠を残す
- [x] PR に起点 ID（`Refs #308,#295,#283,#279`）を記載
