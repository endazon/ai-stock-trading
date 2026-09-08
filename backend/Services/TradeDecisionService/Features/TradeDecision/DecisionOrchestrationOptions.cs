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

    // FR-02, FR-04, #567, IADR-0313 決定1: スクリーニング入力のコンテキスト予算の既定値（文字数プロキシ）。
    //
    //   200,000 トークン（claude-haiku-4-5 のコンテキスト） × 1.0 文字/トークン × 0.75（安全率） = 150,000 文字
    //
    // 係数 1.0 は「入力が全量日本語で、かつ日本語の最も重い側（漢字 1 文字 ≈ 1 トークン）に張り付いた場合」で
    // あり、本リポの実プロンプト骨格の実測（日本語 54.2% / ASCII 45.8% ＝ 約 2.15 文字/トークン）に対して
    // 2 倍以上保守側である。安全率 0.75 は出力トークン・骨格見積り（ScreeningContextAssembler の 750）の
    // 誤差・トークナイザの版差を確保する。**実 LLM による実測は残件**（算出過程は IADR-0313 決定1）。
    public const int DefaultScreeningContextBudgetChars = 150_000;

    private readonly int? _screeningContextBudgetChars;

    // #337, IADR-0247: スクリーニング入力のコンテキスト予算（文字数プロキシ。claude-haiku-4-5 の 200K
    // トークン制約に対応する運用値）。null＝縮退制御なし＝参考情報をスクリーニングへ載せない現行プロンプト
    // （IADR-0072 決定2 の従来挙動）。設定時のみ、市況・参考情報つきのスクリーニング入力を組み、
    // 超過時に ① 分割 → ② RAG → ③ ニュースの縮退順序を適用する。
    //
    // #567, IADR-0313 決定2: **本レコードの既定は null のまま**である（Default は単体テストの「現行挙動」
    // 基準値としても共用されているため。IADR-0278 と同じ線引き）。**本番の構成既定を既定有効
    // （DefaultScreeningContextBudgetChars）にするのは DecisionOptionsLoader の側**である。
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
