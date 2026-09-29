using System.Text.RegularExpressions;
using AiStockTrading.Shared.Contracts.Ports;
using OrderExecutionService.Features.OrderExecution.GuardProtectiveStops;

namespace OrderExecutionService.Infrastructure.ExternalServices;

// FR-10, #1093, IADR-0458: moomoo の建玉照会の失敗を、ガードが照会し直してよいかで分ける。
// 🔴 **分からないものは Other（照会し直さない）へ倒す。** 照会し直しは、失敗も枠を消費する頻度制限の中で行うため
//    （IADR-0144 決定 5）、一時的と言い切れるものだけを通す。
public static partial class MoomooPositionQueryClassifier
{
    public static PositionQueryFailure Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            // 返信待ちの打ち切り（MMApiMoomooTradeClient.SendAsync の WaitAsync）。
            TimeoutException => PositionQueryFailure.Transient,
            // 接続の確立（InitConnect・口座の列挙）の失敗。失敗した接続は次の試行で作り直される（#732）。
            BrokerUnavailableException => PositionQueryFailure.Transient,
            MoomooTradeRequestException request => ClassifyRetType(request.RetType, request.RetMsg),
            _ => PositionQueryFailure.Other,
        };
    }

    private static PositionQueryFailure ClassifyRetType(int retType, string? retMsg) => retType switch
    {
        // SDK が合成する「返事を読めなかった」値（MoomooRetType の注記）。読み取りの照会なので照会し直してよい。
        MoomooRetType.TimeOut or MoomooRetType.DisConnect or MoomooRetType.Unknown => PositionQueryFailure.Transient,
        // OpenD が返事として返した失敗のうち、頻度制限だけを分ける（例: "Maximum 10 times per 30 seconds"）。
        MoomooRetType.Failed when retMsg is not null && RateLimitMessage().IsMatch(retMsg) => PositionQueryFailure.RateLimited,
        // 業務上の失敗・応答の読み損ね（Invalid）・未定義の値は照会し直さない。
        _ => PositionQueryFailure.Other,
    };

    // 頻度制限の文言（英語・中国語・日本語）。OpenD の文言は版で変わり得るため語の一部で引く。
    [GeneratedRegex(@"frequen|times per|频率|頻度", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RateLimitMessage();
}
