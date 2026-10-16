---
title: 相場スナップショット（InformationKind.Quote）を KB へ保存せず、RAG の取り出しでも除外する。旧文書は MSP で一括アーカイブする（#1366）
type: spec
status: done
related_ids: [FR-01, FR-02, FR-04, FR-08, ADR-0003, ADR-0004, ADR-0020, IADR-0068, IADR-0099, IADR-0247, IADR-0451, IADR-0454, IADR-0456, IADR-0474, IADR-0541, IADR-0545]
author: claude (Claude Code)
created: 2026-10-16
updated: 2026-10-16
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-01, FR-08)
  - planning:projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
---

# 仕様書: 相場スナップショットを RAG の参考情報から外す（#1366）

> 本仕様書は実装着手前に作成した（着手 2026-10-16）。基点は AST `origin/develop` `eabff721`。

## 起点となる計画書（トレーサビリティ）

- 起点 ID: **FR-08**（KB/RAG）、**FR-01**（情報収集・KB 保存）。関連: FR-02・FR-04（判断の入力の現在値）、ADR-0004、ADR-0020（欠測・巡回の起点）。
- 起点 issue: **#1366**（同件の検索: open issue・open PR に同じ題の作業は無い。並行は #1363〔`FinnhubSymbolSelector`〕・#1364〔`TradeDecisionPromptBuilder`〕で、触るファイルは重ならない）。
- 新規 IADR: **IADR-0545**（develop の最大は IADR-0542。並行作業が 0543・0544 を予定するため 0545。欠番可・最大＋1）。

## 症状と原因

- 情報収集は 5 分ごとに Finnhub の現在値を `InformationKind.Quote`（表題 `{symbol} 現在値`・本文 `current=…;high=…;low=…;prevClose=…`・タグ `Quote` / `finnhub`・属性 `symbol`）として KB へ保存する（`FinnhubInformationSource`）。保存の重複止め（IADR-0456）は内容が同じときだけ効くため、場中は値が動くたびに保存される（10-15 は 950 件）。
- 判断の取り出し（`KnowledgeBaseRetrievalContextProvider`）の ① 銘柄の検索は `attributes["symbol"]` で絞るだけで、種別で絞らない。新しい順（IADR-0454 決定 4）なので、5 分ごとに書かれる Quote が上位 K 件を占め、足切り（168 時間）の内側の先週の値まで参考情報に入る。
- LLM は参考情報の現在値（古い）と入力の現在値（`ICurrentPriceProvider`）の食い違いを「データの矛盾」と読み、見送る（10-14 は 72 件、10-15 は 85 件）。

## 母集合（規則 9）: Quote 文書を読む・作る・前提にする箇所

引き方（誤りの側の文字列で `backend/` 全体を走査）: `grep -rn "InformationKind"`、`grep -rn "現在値\"\|current="`、`grep -rn "\"Quote\"\|\"kind\"\|\.Tags"`、KB を読む全箇所 `grep -rln "IKnowledgeBaseSearch\|IRetrievalContextProvider\|IKnowledgeDocumentCatalog"`、収集結果の件数の使い道 `grep -rn "ItemCount\|InformationCollected"`、文書側 `grep -rn "InformationKind\|Quote\|現在値" docs`。

| # | 箇所 | Quote 文書との関係 | 本件で変えるか |
| --- | --- | --- | --- |
| 1 | `FinnhubInformationSource`（収集） | Quote を作る唯一の源（`finnhub` 源の項目は Quote だけ） | **変えない**（取得は続ける。下の #3・#4 のため） |
| 2 | `KnowledgeBaseWriterSink`（KB 保存） | Quote を KB 文書へ写像して保存する | **変える**: Quote は保存しない |
| 3 | `InformationCollectionAppService` → `CollectionResult.ItemCount` → `CollectionPollingService` | **件数 > 0 のときだけ `InformationCollected`（定時の判断サイクルの起点）を発行する**。Quote は毎巡回の件数を支えている | 変えない（件数は保存前の正規化件数のまま。源で止めると、ニュースが 0 件の巡回で定時の判断が起きなくなる） |
| 4 | `InformationSourceCatalog` / `DegradationEvaluator` | `finnhub` は `market-live` 区分の必須源。**源の成否**（項目の中身ではない）で欠測を判定する | 変えない（取得を続けるので成否の観測は不変） |
| 5 | `KnowledgeBaseRetrievalContextProvider`（判断の RAG。本判断・一次スクリーニングが使う） | ① 銘柄の検索に Quote が入る（本件の事象） | **変える**: タグ `Quote` の文書を取り出しから除く（旧文書の防御） |
| 6 | `ScreeningContextAssembler`（一次スクリーニングの縮退） | タグ `finnhub` を「保護（市況）」として削らない。Quote はこの保護で一次にも必ず残っていた | 変えない（#5 で入らなくなる。`finnhub` の保護の語は `moomoo` と並ぶ市況源の語彙として残す） |
| 7 | `RetrievalSourcePolicy`（出典限定） | `finnhub` を許可。Quote はこれを通っていた | 変えない（出典の信頼の判定であり、種別の要否ではない） |
| 8 | `DecisionReferenceRecord` → `TradeDecisionMade/Held.References` → 報告書 `DecisionReferenceFeed`（日報 §5。IADR-0541） | プロンプトが見た参照を記録する。Quote が見えていれば §5 に「{symbol} 現在値」が出典として並ぶ | 変えない（#5 で入らなくなり、§5 からも消える） |
| 9 | Stage 0 の as-of 記録（`AsOfReferenceFeeds`・`AsOfReferenceAssembler`） | **KB を読まない**。Finnhub のニュース・SEC EDGAR・FRED を直接引く（タグ `News` / `Disclosure` / `MacroIndicator`）。Quote は無い | 変えない |
| 10 | 報告書の KB 連携（`CatalogReportDraftKnowledgeCopy`・`ReportKnowledgeReingestService`） | 一覧を `periodKey`・`kind`（報告書種別）で絞る。Quote は対象外 | 変えない |
| 11 | 情報収集の `IKnowledgeBaseSearch` の配線（`Program.cs`） | 配線だけで、読み手は無い | 変えない |
| 12 | 判断の現在値（`ICurrentPriceProvider` → `MarketDataCurrentPriceProvider`） | **Quote 文書を使わない**。共有 `IMarketDataSource`（Finnhub）から直接取り、前日終値・始値・高値・安値も返す（IADR-0099・IADR-0451）。PoC は `MarketData__Provider=finnhub`（`values-local.yaml`） | 変えない（Quote 文書の中身はこの入力と同じ項目の、より古い値である） |
| 13 | `KnowledgeTagVocabulary.CollectionKinds`（`Quote` を含む） | 基盤の辞書へ登録するタグの一覧 | 変えない（旧文書が持つタグであり、列挙体の値でもある。`KnowledgeTagVocabularyTests` が列挙体との一致を検査する） |
| 14 | `docs/` | Quote・現在値の KB 保存を記述する文書は無い（`kb-tag-dictionary-runbook.md` は `InformationKind` の追随手順のみ） | 変えない |

- 結論: **Quote 文書を判断の材料として必要とする経路は無い**（#12 が現在値を別に渡している）。Quote の**取得**は #3・#4 が前提にしているので止めない。

## 判断（IADR-0545 に記録）

1. **保存も止め、取り出しでも除く（両方）**。保存を止めれば新しい汚染が止まり、取り出しの除外は 168 時間の足切りの内側に残る旧文書（と、何かの経路で今後入る Quote）を防ぐ。
2. 保存の止め方は `KnowledgeBaseWriterSink` で `Quote` を送らない（取得・正規化件数・欠測判定は不変）。
3. 取り出しの除外はタグ `Quote`（`KnowledgeBaseWriterSink` が種別を `Tags` へ載せる。`KnowledgeTagVocabulary.CollectionKinds` の語）で、3 本の検索の結果すべてに掛ける。大小は無視する。
4. 既存の Quote 文書: 判断の取り出しは除外で即時に止まる。ただし旧文書は基盤の索引に残り、**検索の候補（関連度の上位 4×TopK）を占め続ける**——AST は「属性が Quote でない」を検索の条件にできない（単値の完全一致だけ）ため、取り出した後に落とすしかなく、候補に入れなかったニュースは戻らない。足切り（AST 側・取り出した後）では候補から消えない。よって **MSP 側で一括アーカイブ**（アーカイブ済みは検索・RAG から外れる。MSP#1964）を MSP#1974 で依頼した。

## 受け入れ基準と試験の写像

| 受け入れ基準 | 試験 |
| --- | --- |
| Quote は KB へ保存しない。ニュース・開示・需給・マクロ・収集状態は従来どおり保存する | `KnowledgeBaseWriterSinkTests.相場スナップショットは保存せず他の種別は保存する` |
| 送った件数（ログの分母）に Quote を数えない（運用の `0/N` の判定を変えない） | 同上（writer の受け取り件数で確認） |
| 取り出しは Quote（銘柄の検索・市場の検索・補充のいずれから来ても）を参考情報に入れない | `KnowledgeBaseRetrievalContextProviderTests.相場スナップショットはどの検索から来ても参考情報に入れない` |
| ニュース・開示は従来どおり入る（並びも従来どおり） | 同上（同じ試験で残りの並びを `Equal` で固定） |
| タグの大小が違っても除く | `KnowledgeBaseRetrievalContextProviderTests.相場スナップショットのタグは大小を無視して除く` |
| 除外の語は KB のタグ語彙（＝列挙体の値）と一致する | `KnowledgeBaseRetrievalContextProviderTests.除外の語は収集の種別の語彙と一致する` |
| Quote の取得は続き、巡回の件数（定時の起点）は変わらない | 既存 `CollectionPollingServiceTests`（Quote だけの巡回で `InformationCollected` が出る）が不変で通る |

- 変異: 保存の除外を外す／取り出しの除外を外すと、それぞれの試験が赤になることを確かめる（下の検証結果）。

## 検証結果

- `dotnet build`（InformationCollectionService.Tests・TradeDecisionService.Tests・Architecture.Tests）: 警告 0・エラー 0。
- `dotnet test`: InformationCollectionService.Tests・TradeDecisionService.Tests・Architecture.Tests 全件成功。
- 変異: sink の `Quote` 除外を外す → 保存の試験が赤。provider の除外を外す → 取り出しの 2 試験が赤。戻して緑。
- `dotnet format --verify-no-changes`（対象プロジェクト）: 差分なし。

## PoC での確かめ方

- 配備後、情報収集のログ `KB 保存: N/M 件` の M が巡回あたり監視銘柄の数だけ減る（Quote を送らない）。document-service の POST 201 の件数が日あたり約 950 件減る。
- 取引判断のログ（Loki）で、理由に価格の矛盾（「参考情報」「現在値」「矛盾」「食い違」）を挙げた見送りの件数が、10-14 の 72 件・10-15 の 85 件から減る。
- 日報 §5 の出典に `{symbol} 現在値` が並ばない。

## 範囲外

- Quote の取得そのものの停止（定時の起点・欠測の観測が依存する。止めるなら起点の条件を別に決める）。
- 旧 Quote 文書のアーカイブの実施（MSP 側の作業。MSP#1974 で依頼した）。
