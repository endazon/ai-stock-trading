using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Infrastructure.Composable.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace OrderExecutionService.Infrastructure.Steps;

// 🔴 NFR, FR-10, #1527, IADR-0610 決定2: 受け取った注文の承認（OrderApproved）の発行元の印を確かめる。
// 発注執行は承認を受けたら発注へ進む（決済は kill switch でも止めない＝ADR-0009 決定 4・FR-10）。したがって
// 「この承認がリスク管理の出したものか」を確かめる場所は、ここ（発注の手前）である。
//   - 段 1（Report・既定）: 確かめて、合わない・無い・確かめられないを Warning で残し、**通す**（従前の挙動）。
//   - 段 2（Enforce）: 合わない・無いものは**発注しない**。構造化した Warning（EventId OrderApprovalRejected）で残す。例外にしない（再配送しても結論は変わらない）。
// 鍵の値も印の値もログに出さない（出すのは DecisionId・銘柄・建玉効果・結果だけ）。
public sealed class OrderApprovalVerifier(
    OrderApprovalSigningMode mode,
    OrderApprovalSignature? signature,
    ILogger<OrderApprovalVerifier> logger)
{
    /// <summary>段 2 で拒否した承認の記録（構造化した Warning）。</summary>
    public static readonly EventId RejectedEvent = new(15270, "OrderApprovalRejected");

    /// <summary>段 1 で確かめられなかった承認の記録。</summary>
    public static readonly EventId UnverifiedEvent = new(15271, "OrderApprovalUnverified");

    /// <summary>発注へ進めてよいか。段 1 では常に true（記録だけ）。</summary>
    public bool Admit(OrderApproved approved, Envelope? envelope)
    {
        ArgumentNullException.ThrowIfNull(approved);

        string? presented = null;
        if (envelope?.Headers is { } headers)
            headers.TryGetValue(OrderApprovalSignature.HeaderName, out presented);

        var result = signature is null
            ? OrderApprovalVerification.Unverifiable
            : signature.Verify(approved, presented);

        if (result == OrderApprovalVerification.Valid)
            return true;

        if (mode == OrderApprovalSigningMode.Enforce)
        {
            // 🔴 #1527, IADR-0610 決定2: 拒否は構造化した Warning（監査の記録）で残す。項目名は固定（EventId と同じく検索の鍵になる）。
            logger.LogWarning(
                RejectedEvent,
                "注文の承認の発行元を確かめられないため発注しません（段 2）: DecisionId={DecisionId} 銘柄={Symbol} 建玉効果={Effect} 出どころ={Origin} 結果={Result}",
                approved.DecisionId, approved.Intent?.Symbol, approved.Intent?.PositionEffect, approved.Origin, result);
            return false;
        }

        logger.LogWarning(
            UnverifiedEvent,
            "注文の承認の発行元を確かめられませんでした（段 1・報告のみで発注は続けます）: DecisionId={DecisionId} 銘柄={Symbol} 建玉効果={Effect} 出どころ={Origin} 結果={Result}",
            approved.DecisionId, approved.Intent?.Symbol, approved.Intent?.PositionEffect, approved.Origin, result);
        return true;
    }
}

// NFR, #1527, IADR-0610 決定3: 検証の登録。構成 OrderApprovalSigning を起動時に読む。段 2 で鍵が無ければここで例外（起動で止める）。
public static class OrderApprovalVerifierRegistration
{
    public static IServiceCollection AddOrderApprovalVerifier(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(OrderApprovalSigningOptions.SectionName).Get<OrderApprovalSigningOptions>()
            ?? new OrderApprovalSigningOptions();
        var signature = options.Resolve();
        return services.AddSingleton(sp => new OrderApprovalVerifier(
            options.Mode, signature, sp.GetRequiredService<ILogger<OrderApprovalVerifier>>()));
    }
}
