namespace AiStockTrading.TradeDecision.Application.Services;

// FR-04, ADR-0003, IADR-0037: 多数決・二段オーケストレーションの構成。
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

    // 一次スクリーニング用モデル識別子（軽量）。実解決はゲートウェイの構成に委ねる（L34）。
    public string? PrimaryModel { get; init; }

    // 二次本判断用モデル識別子（高性能）。
    public string? SecondaryModel { get; init; }

    public static DecisionOrchestrationOptions Default { get; } = new();
}
