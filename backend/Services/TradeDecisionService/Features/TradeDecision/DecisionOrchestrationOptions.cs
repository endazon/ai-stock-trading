namespace TradeDecisionService.Features.TradeDecision;

// FR-04, ADR-0003, IADR-0039: 多数決・二段オーケストレーションの構成。
// Default（VoteCount=1・スクリーニング無効・モデル未指定）は単発判断（IADR-0017）と等価＝現行挙動。
public sealed record DecisionOrchestrationOptions
{
    private readonly int _voteCount = 1;

    // 二次本判断で同一入力を何回実行して多数決するか。1 以上（既定 1＝単発）。
    public int VoteCount
    {
        get => _voteCount;
        init
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(VoteCount), value, "VoteCount は 1 以上でなければならない。");
            }

            _voteCount = value;
        }
    }

    // true なら一次スクリーニング（軽量モデル 1 回）を行い、Hold なら二次をスキップ（費用統制・L129）。
    public bool EnableScreening { get; init; }

    private readonly int? _screeningContextBudgetChars;

    // #337, IADR-0247: スクリーニング入力のコンテキスト予算（文字数プロキシ。claude-haiku-4-5 の 200K
    // トークン制約に対応する運用値を構成で与える）。null（既定）＝縮退制御なし＝参考情報をスクリーニングへ
    // 載せない現行プロンプト（IADR-0072 決定2 の従来挙動）。設定時のみ、市況・参考情報つきの
    // スクリーニング入力を組み、超過時に ① 分割 → ② RAG → ③ ニュースの縮退順序を適用する。
    public int? ScreeningContextBudgetChars
    {
        get => _screeningContextBudgetChars;
        init
        {
            if (value is <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ScreeningContextBudgetChars), value, "予算は正の値でなければならない（無効化は null）。");
            }

            _screeningContextBudgetChars = value;
        }
    }

    // 一次スクリーニング用モデル識別子（軽量）。実解決はゲートウェイの構成に委ねる（L34）。
    public string? PrimaryModel { get; init; }

    // 二次本判断用モデル識別子（高性能）。
    public string? SecondaryModel { get; init; }

    public static DecisionOrchestrationOptions Default { get; } = new();
}
