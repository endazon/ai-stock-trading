using AiStockTrading.Shared.Kernel.Trading;
using Microsoft.Extensions.Options;

namespace OrderExecutionService.Features.OrderExecution.CancelUnfilledDecisionCloses;

// 🔴 FR-10, FR-19, ADR-0067 決定2・6, 05_trading-assumptions §5, #1339, IADR-0539: 判断由来の決済の指値の未約定時の取消の構成。
//
// **既定は無効**（ADR-0067 決定2）。有効にするのは #1214（保護逆指値の GTC）の実弾確認と PoC の確認の後で、利用者が決める（決定6）。
// 本番の配備（Helm の values.yaml）にも有効化を書かない（PoC の経路の values-local.yaml だけ、利用者裁定 2026-10-21 で有効）。
// 値の変更は §5 の他の統制値と同じ手続き（方針確定プロセスまたは利用者の設定変更）であり、生成 AI と自動処理は変えられない。
//
// **有効なときだけ**範囲を検証し、外れたら起動で止める（ValidateOnStart。黙ってクランプしない——統制値を書き間違えたまま動かさない）。
//   - N（UnfilledMinutes）: 相場操縦の検知の窓（Shared.Kernel の定数・既定 5 分）より**長く**、上端 390 分以下。
//   - 回数（MaxCancelsPerPositionPerDay）: 1 以上 3 以下。
public sealed class DecisionCloseUnfilledCancelOptions : UnfilledDecisionOrderCancelOptions
{
    public const string SectionName = "DecisionCloseUnfilledCancel";

    public DecisionCloseUnfilledCancelOptions()
    {
        Enabled = DecisionCloseUnfilledCancelDefaults.Enabled;
        UnfilledMinutes = DecisionCloseUnfilledCancelDefaults.UnfilledMinutes;
        MaxCancelsPerPositionPerDay = DecisionCloseUnfilledCancelDefaults.MaxCancelsPerPositionPerDay;
    }

    protected override string Section => SectionName;
}

/// <summary>
/// 🔴 FR-10, FR-19, ADR-0067 決定2, #1339, IADR-0539 / #1486, IADR-0594 決定1: 判断由来の注文（決済・新規建て）の指値の未約定時の取消の構成の共通部
/// （値・範囲の検証。検証は決済と新規建てで同じ: N は相場操縦の検知の窓より長く 390 分以下、回数は 1〜3）。
/// </summary>
public abstract class UnfilledDecisionOrderCancelOptions
{
    /// <summary>構成の節の名前（誤りの文言に使う）。</summary>
    protected abstract string Section { get; }

    /// <summary>取消を有効にするか（既定: <b>無効</b>）。</summary>
    public bool Enabled { get; set; }

    /// <summary>発注からこの分数たっても約定しない判断由来の指値を取り消す。</summary>
    public int UnfilledMinutes { get; set; }

    /// <summary>1 建玉・1 市場の現地取引日あたりの取消の上限。上限に達した後は取り消さず DAY の失効に任せる。</summary>
    public int MaxCancelsPerPositionPerDay { get; set; }

    /// <summary>1 巡回で取り消す最大件数（暴走の歯止め。保有建玉の数で頭打ちになるので通常は届かない）。</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>N。</summary>
    public TimeSpan Unfilled => TimeSpan.FromMinutes(UnfilledMinutes);

    /// <summary>1 巡回の処理上限（下限 1・上限 1000）。</summary>
    public int EffectiveBatchSize => Math.Clamp(BatchSize, 1, 1_000);

    /// <summary>
    /// 構成の誤りを列挙する（空なら正しい）。<b>無効なら何も検証しない</b>（ADR-0067 決定2 の下端は「有効時の N」に掛かる）。
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        if (!Enabled)
            return [];

        var errors = new List<string>();
        var window = DecisionCloseUnfilledCancelDefaults.ManipulationLookbackWindowMinutes;
        if (UnfilledMinutes <= window)
        {
            errors.Add(
                $"{Section}:UnfilledMinutes={UnfilledMinutes} は相場操縦の検知の窓（{window} 分）以下です。"
                + $"有効時の N は窓より長い値にしてください（{window + 1} 以上。ADR-0067 決定2——窓の内側の取消は検知の集計に現れ得ます）。");
        }

        if (UnfilledMinutes > DecisionCloseUnfilledCancelDefaults.MaxUnfilledMinutes)
        {
            errors.Add(
                $"{Section}:UnfilledMinutes={UnfilledMinutes} は上端（{DecisionCloseUnfilledCancelDefaults.MaxUnfilledMinutes} 分）を超えています"
                + "（通常取引時間より長い N は DAY の失効より後になり、取り消す機会がありません）。");
        }

        if (MaxCancelsPerPositionPerDay < DecisionCloseUnfilledCancelDefaults.MinCancelsPerPositionPerDayBound
            || MaxCancelsPerPositionPerDay > DecisionCloseUnfilledCancelDefaults.MaxCancelsPerPositionPerDayBound)
        {
            errors.Add(
                $"{Section}:MaxCancelsPerPositionPerDay={MaxCancelsPerPositionPerDay} は範囲外です"
                + $"（{DecisionCloseUnfilledCancelDefaults.MinCancelsPerPositionPerDayBound}〜"
                + $"{DecisionCloseUnfilledCancelDefaults.MaxCancelsPerPositionPerDayBound}。取消の反復を構造的に起こさないための上限です）。");
        }

        return errors;
    }
}

/// <summary>
/// 🔴 ADR-0067 決定2, #1339, IADR-0539: <see cref="DecisionCloseUnfilledCancelOptions.Validate"/> を起動の検証へ写す
/// （<c>ValidateOnStart</c> と組み合わせ、誤った統制値のまま常駐を始めない）。
/// </summary>
public sealed class DecisionCloseUnfilledCancelOptionsValidator : UnfilledDecisionOrderCancelOptionsValidator<DecisionCloseUnfilledCancelOptions>;

/// <summary>#1486, IADR-0594 決定1: <see cref="UnfilledDecisionOrderCancelOptions.Validate"/> を起動の検証へ写す（決済・新規建ての共通）。</summary>
public class UnfilledDecisionOrderCancelOptionsValidator<TOptions> : IValidateOptions<TOptions>
    where TOptions : UnfilledDecisionOrderCancelOptions
{
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = options.Validate();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
