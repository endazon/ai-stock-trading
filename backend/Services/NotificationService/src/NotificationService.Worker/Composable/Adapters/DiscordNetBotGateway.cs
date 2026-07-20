using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Application.State;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Worker.Composable.Adapters;

// FR-14, UC-06, IADR-0062 決定1/2: Discord.Net による Gateway（WebSocket）常駐の実装。
// 詳細設計07 が採用した接続方式（アウトバウンドのみ・受信ポートを外部公開しない）。
//
// 本クラスは Discord.Net と Application の純粋コアを繋ぐ**変換層**に徹する。判断（多層認証・確認ステップ・
// 冪等）は一切持たず、すべて KillSwitchCommandHandler に委ねる（判断ロジックを実 Discord 非依存に保つため）。
//
// Intents は最小構成（Guilds のみ）。MessageContent Intent は要求しない（本 PR はスラッシュコマンドのみ。
// 自然文リプライの中継は #14 交差のため対象外・IADR-0062 決定2）。
//
// 実 Gateway への接続は本 PR では未検証（CI で外部 SaaS への WebSocket は張れない）。後続 E2E で検証する。
internal sealed class DiscordNetBotGateway : IDiscordBotGateway, IAsyncDisposable
{
    // 確認フレーズを受け取るモーダル・ボタンの識別子。
    // IADR-0097: 起動・解除でモーダル ID を分離する（送信ハンドラが ID 一致だけで種別を確定でき、誤操作の余地が無い）。
    private const string KillSwitchEngageButtonId = "ast-killswitch-engage-confirm";
    private const string KillSwitchDisengageButtonId = "ast-killswitch-disengage-confirm";
    private const string KillSwitchEngageModalId = "ast-killswitch-modal";
    private const string KillSwitchDisengageModalId = "ast-killswitch-disengage-modal";
    private const string KillSwitchPhraseInputId = "ast-killswitch-phrase";

    // FR-10, ADR-0009: 一時停止/再開の確認ボタン（確認フレーズは求めない＝pause は可逆のため）。
    private const string PauseConfirmButtonId = "ast-pause-confirm";
    private const string ResumeConfirmButtonId = "ast-resume-confirm";

    // FR-20, ADR-0008: 段階遷移（昇格/差し戻し）の確認ボタン。遷移先はボタンの CustomId に載せる（押下時に再解析）。
    // 例: "ast-stage-promote-2" / "ast-stage-demote-1"。
    private const string StagePromoteButtonPrefix = "ast-stage-promote-";
    private const string StageDemoteButtonPrefix = "ast-stage-demote-";

    private readonly DiscordSocketClient _client;
    private readonly KillSwitchCommandHandler _handler;
    private readonly PauseCommandHandler _pauseHandler;
    private readonly StageGateCommandHandler _stageGateHandler;
    private readonly DiscordBotOptions _options;
    private readonly ILogger<DiscordNetBotGateway> _logger;

    public DiscordNetBotGateway(
        KillSwitchCommandHandler handler,
        PauseCommandHandler pauseHandler,
        StageGateCommandHandler stageGateHandler,
        DiscordBotOptions options,
        ILogger<DiscordNetBotGateway> logger)
    {
        _handler = handler;
        _pauseHandler = pauseHandler;
        _stageGateHandler = stageGateHandler;
        _options = options;
        _logger = logger;

        // IADR-0062 決定2: 最小 Intents。Guilds のみでスラッシュコマンドは受けられる。
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            AlwaysDownloadUsers = false,
            LogLevel = LogSeverity.Info,
        });

        _client.Log += OnLogAsync;
        _client.Ready += OnReadyAsync;
        _client.SlashCommandExecuted += OnSlashCommandAsync;
        _client.ButtonExecuted += OnButtonAsync;
        _client.ModalSubmitted += OnModalAsync;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _client.LoginAsync(TokenType.Bot, _options.Token).ConfigureAwait(false);
        await _client.StartAsync().ConfigureAwait(false);
        _logger.LogInformation("Discord Bot の Gateway 接続を開始しました。");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _client.StopAsync().ConfigureAwait(false);
        await _client.LogoutAsync().ConfigureAwait(false);
    }

    // Ready 後に専用サーバーへスラッシュコマンドを登録する（ギルドコマンドは即時反映。グローバル登録はしない
    // ＝他サーバーに露出させない）。
    private async Task OnReadyAsync()
    {
        if (!ulong.TryParse(_options.GuildId, out var guildId))
        {
            _logger.LogWarning("GuildId が不正のためスラッシュコマンドを登録しません（{GuildId}）。", _options.GuildId);
            return;
        }

        var guild = _client.GetGuild(guildId);
        if (guild is null)
        {
            _logger.LogWarning("専用サーバー（{GuildId}）が見つからないためコマンドを登録しません。", guildId);
            return;
        }

        var killSwitch = new SlashCommandBuilder()
            .WithName("killswitch")
            .WithDescription("全取引を停止します（確認ボタンと確認フレーズが必要です）")
            .AddOption(
                "off",
                ApplicationCommandOptionType.Boolean,
                "true で停止を解除します",
                isRequired: false)
            .Build();

        // FR-10, ADR-0009: 一時停止（pause）・再開（resume）・状態照会（status）。pause/resume は kill switch より
        // 軽い統制で、確認ボタンのみ（確認フレーズは求めない）。status は表示専用。
        var pause = new SlashCommandBuilder()
            .WithName("pause")
            .WithDescription("新規建てを一時停止します（手仕舞い・損切りは継続します）")
            .Build();
        var resume = new SlashCommandBuilder()
            .WithName("resume")
            .WithDescription("一時停止を解除します")
            .Build();
        var status = new SlashCommandBuilder()
            .WithName("status")
            .WithDescription("稼働状態（統制・段階・当日損益・上限使用率・ポジション）を表示します")
            .Build();

        // FR-20, UC-06, ADR-0008: 段階ゲート（現況照会・昇格/差し戻し・撤退評価）。action で操作を選び、
        // promote/demote は遷移先（Stage 0〜3）を stage オプションで受ける。promote/demote は確認ボタンを要する。
        var stage = new SlashCommandBuilder()
            .WithName("stage")
            .WithDescription("段階ゲート（現況・昇格/差し戻し・撤退評価）を操作します")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("action")
                .WithDescription("操作")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .AddChoice("status", "status")
                .AddChoice("promote", "promote")
                .AddChoice("demote", "demote")
                .AddChoice("withdrawal", "withdrawal"))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("stage")
                .WithDescription("遷移先の段階（promote/demote 時に必須・0〜3）")
                .WithType(ApplicationCommandOptionType.Integer)
                .WithRequired(false)
                .WithMinValue(0)
                .WithMaxValue(3))
            .Build();

        try
        {
            await guild.CreateApplicationCommandAsync(killSwitch).ConfigureAwait(false);
            await guild.CreateApplicationCommandAsync(pause).ConfigureAwait(false);
            await guild.CreateApplicationCommandAsync(resume).ConfigureAwait(false);
            await guild.CreateApplicationCommandAsync(status).ConfigureAwait(false);
            await guild.CreateApplicationCommandAsync(stage).ConfigureAwait(false);
            _logger.LogInformation("スラッシュコマンドを登録しました（guild={GuildId}）。", guildId);
        }
        catch (Exception ex)
        {
            // 登録失敗で Bot ごと落とさない（通知は継続する）。
            _logger.LogWarning(ex, "スラッシュコマンドの登録に失敗しました。");
        }
    }

    // /killswitch → 確認ボタンを提示する（詳細設計07: 高リスク操作は2段階）。
    // ここでは Risk を呼ばない。認証は最終実行時にも再評価する（ボタン押下者のすり替えを防ぐ）。
    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        switch (command.Data.Name)
        {
            case "killswitch":
                await OnKillSwitchSlashAsync(command).ConfigureAwait(false);
                return;
            case "pause":
            case "resume":
                await OnPauseSlashAsync(command, isResume: command.Data.Name == "resume").ConfigureAwait(false);
                return;
            case "status":
                await OnStatusSlashAsync(command).ConfigureAwait(false);
                return;
            case "stage":
                await OnStageSlashAsync(command).ConfigureAwait(false);
                return;
            default:
                return;
        }
    }

    private async Task OnKillSwitchSlashAsync(SocketSlashCommand command)
    {
        var isOff = command.Data.Options.Any(o => o.Name == "off" && o.Value is true);

        // 詳細設計07: 許可外の着信は無視しログのみ残す。ここで早期に弾き、ボタンすら出さない。
        var context = ContextOf(command, isOff ? "/killswitch off" : "/killswitch");
        var auth = DiscordCommandAuthorizer.Authorize(context, _options);
        if (!auth.IsAllowed)
        {
            _logger.LogWarning(
                "Discord コマンドを拒否しました（User={UserId}・理由={Reason}）。", context.UserId, auth.Reason);
            // 応答しないと Discord 側にエラーが残るため、ephemeral で最小限の応答のみ返す（理由は返さない）。
            await command.RespondAsync("この操作は許可されていません。", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var builder = new ComponentBuilder().WithButton(
            isOff ? "停止を解除する" : "全取引を停止する",
            isOff ? KillSwitchDisengageButtonId : KillSwitchEngageButtonId,
            ButtonStyle.Danger);

        await command.RespondAsync(
            isOff ? "本当に停止を解除しますか？" : "本当に全取引を停止しますか？",
            components: builder.Build(),
            ephemeral: true).ConfigureAwait(false);
    }

    // FR-10, ADR-0009: /pause・/resume → 確認ボタンを提示する（確認フレーズは求めない）。ここでは Risk を呼ばない。
    // 認証は最終実行時（ボタン押下）にも再評価する（押下者のすり替えを防ぐ）。
    private async Task OnPauseSlashAsync(SocketSlashCommand command, bool isResume)
    {
        var context = ContextOf(command, isResume ? "/resume" : "/pause");
        var auth = DiscordCommandAuthorizer.Authorize(context, _options);
        if (!auth.IsAllowed)
        {
            _logger.LogWarning(
                "Discord コマンドを拒否しました（User={UserId}・理由={Reason}）。", context.UserId, auth.Reason);
            await command.RespondAsync("この操作は許可されていません。", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var builder = new ComponentBuilder().WithButton(
            isResume ? "一時停止を解除する" : "新規建てを一時停止する",
            isResume ? ResumeConfirmButtonId : PauseConfirmButtonId,
            // pause は kill switch ほど重くない可逆操作のため Primary（危険色にしない）。
            ButtonStyle.Primary);

        await command.RespondAsync(
            isResume ? "一時停止を解除しますか？" : "新規建てを一時停止しますか？（手仕舞い・損切りは継続します）",
            components: builder.Build(),
            ephemeral: true).ConfigureAwait(false);
    }

    // FR-10, UC-07, ADR-0009: /status → 参照のみ。認証を通過していれば現在状態を表示する（確認ステップ無し）。
    private async Task OnStatusSlashAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
        var result = await _pauseHandler
            .HandleAsync(ContextOf(command, "/status"))
            .ConfigureAwait(false);
        await command.FollowupAsync(PauseResponseTextOf(result), ephemeral: true).ConfigureAwait(false);
    }

    // FR-20, UC-06, ADR-0008: /stage → action で分岐。status/withdrawal は参照・安全側のため直接実行。
    // promote/demote は破壊的（実弾方向）／手動の段階変更のため確認ボタンを提示する（ここでは Risk を呼ばない）。
    private async Task OnStageSlashAsync(SocketSlashCommand command)
    {
        var action = command.Data.Options.FirstOrDefault(o => o.Name == "action")?.Value as string;
        var stageValue = command.Data.Options.FirstOrDefault(o => o.Name == "stage")?.Value;

        // 認証は最終実行時（ボタン押下）にも再評価するが、許可外にはボタンすら出さない。
        var context = ContextOf(command, $"/stage {action}");
        var auth = DiscordCommandAuthorizer.Authorize(context, _options);
        if (!auth.IsAllowed)
        {
            _logger.LogWarning(
                "Discord コマンドを拒否しました（User={UserId}・理由={Reason}）。", context.UserId, auth.Reason);
            await command.RespondAsync("この操作は許可されていません。", ephemeral: true).ConfigureAwait(false);
            return;
        }

        switch (action)
        {
            case "status":
            case "withdrawal":
                {
                    await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
                    var result = await _stageGateHandler
                        .HandleAsync(ContextOf(command, $"/stage {action}"))
                        .ConfigureAwait(false);
                    await command.FollowupAsync(StageResponseTextOf(result), ephemeral: true).ConfigureAwait(false);
                    return;
                }

            case "promote":
            case "demote":
                {
                    // 遷移先が無い・範囲外なら実行しない（Discord 側の 0〜3 制約に加え防御的に確認）。
                    if (stageValue is not (long or int) || !TryStage(stageValue, out var target))
                    {
                        await command.RespondAsync(
                            "promote/demote には遷移先の段階（0〜3）を指定してください。", ephemeral: true).ConfigureAwait(false);
                        return;
                    }

                    var isPromote = action == "promote";
                    var button = (isPromote ? StagePromoteButtonPrefix : StageDemoteButtonPrefix) + target;
                    var builder = new ComponentBuilder().WithButton(
                        isPromote ? $"Stage {target} へ昇格する" : $"Stage {target} へ差し戻す",
                        button,
                        // 昇格は実弾方向のため危険色、差し戻しは安全方向のため Primary。
                        isPromote ? ButtonStyle.Danger : ButtonStyle.Primary);

                    await command.RespondAsync(
                        isPromote
                            ? $"本当に Stage {target} へ昇格しますか？（実弾方向の段階前進です）"
                            : $"Stage {target} へ差し戻しますか？",
                        components: builder.Build(),
                        ephemeral: true).ConfigureAwait(false);
                    return;
                }

            default:
                await command.RespondAsync("不明な操作です。", ephemeral: true).ConfigureAwait(false);
                return;
        }
    }

    // Discord の整数オプションは long で届く。0〜3 のみ受け付ける。
    private static bool TryStage(object? value, out int stage)
    {
        stage = value switch
        {
            long l => (int)l,
            int i => i,
            _ => -1,
        };
        return stage is >= 0 and <= 3;
    }

    // 確認ボタン押下 → 起動・解除ともに確認フレーズのモーダルを出す（IADR-0097・planning#35 裁定）。
    // ここでは Risk を呼ばない。モーダル送信時に Handler がフレーズを検証し、認証も再評価する。
    private async Task OnButtonAsync(SocketMessageComponent component)
    {
        switch (component.Data.CustomId)
        {
            case KillSwitchEngageButtonId:
                {
                    var modal = new ModalBuilder()
                        .WithTitle("全取引の停止")
                        .WithCustomId(KillSwitchEngageModalId)
                        .AddTextInput("確認フレーズを入力してください", KillSwitchPhraseInputId, required: true)
                        .Build();
                    await component.RespondWithModalAsync(modal).ConfigureAwait(false);
                    return;
                }

            case KillSwitchDisengageButtonId:
                {
                    // IADR-0097: 解除も起動と同じくフレーズ入力モーダルを提示する（確認ボタンのみでは解除しない）。
                    var modal = new ModalBuilder()
                        .WithTitle("停止の解除")
                        .WithCustomId(KillSwitchDisengageModalId)
                        .AddTextInput("確認フレーズを入力してください", KillSwitchPhraseInputId, required: true)
                        .Build();
                    await component.RespondWithModalAsync(modal).ConfigureAwait(false);
                    return;
                }

            case PauseConfirmButtonId:
            case ResumeConfirmButtonId:
                {
                    // FR-10, ADR-0009: 確認ボタン押下で pause/resume を実行する（確認フレーズは無い）。
                    // 認証はハンドラ側で再評価される（押下者のすり替え防止）。
                    var isResume = component.Data.CustomId == ResumeConfirmButtonId;
                    await component.DeferAsync(ephemeral: true).ConfigureAwait(false);
                    var result = await _pauseHandler
                        .HandleAsync(ContextOf(component, isResume ? "/resume" : "/pause"))
                        .ConfigureAwait(false);
                    await DisableComponentsAsync(component, PauseResponseTextOf(result)).ConfigureAwait(false);
                    return;
                }

            default:
                // FR-20, ADR-0008: 段階遷移の確認ボタン（CustomId に遷移先を載せる）。押下で promote/demote を実行する。
                // 認証はハンドラ側で再評価される（押下者のすり替え防止）。二重押下はボタン無効化＋Risk 側連番検証で弾く。
                await OnStageButtonAsync(component).ConfigureAwait(false);
                return;
        }
    }

    // FR-20, UC-06: 段階遷移確認ボタンの押下。CustomId のプレフィックスで昇格/差し戻しを判別し、末尾の遷移先を復元する。
    private async Task OnStageButtonAsync(SocketMessageComponent component)
    {
        var customId = component.Data.CustomId;
        string? raw = null;
        if (customId.StartsWith(StagePromoteButtonPrefix, StringComparison.Ordinal))
            raw = $"/stage promote {customId[StagePromoteButtonPrefix.Length..]}";
        else if (customId.StartsWith(StageDemoteButtonPrefix, StringComparison.Ordinal))
            raw = $"/stage demote {customId[StageDemoteButtonPrefix.Length..]}";

        if (raw is null)
            return; // 未知のボタン（他機能）。

        await component.DeferAsync(ephemeral: true).ConfigureAwait(false);
        var result = await _stageGateHandler
            .HandleAsync(ContextOf(component, raw))
            .ConfigureAwait(false);
        await DisableComponentsAsync(component, StageResponseTextOf(result)).ConfigureAwait(false);
    }

    // 確認フレーズの入力 → 最終実行。認証・フレーズ検証はハンドラ側で行われる。
    // IADR-0097: 起動用・解除用でモーダル ID が分かれており、ID により渡すコマンドを出し分ける
    // （送信ハンドラは ID 一致だけで種別を確定でき、誤って別操作を実行する余地が無い）。
    private async Task OnModalAsync(SocketModal modal)
    {
        var rawCommand = modal.Data.CustomId switch
        {
            KillSwitchEngageModalId => "/killswitch",
            KillSwitchDisengageModalId => "/killswitch off",
            _ => null,
        };
        if (rawCommand is null)
            return;

        await modal.DeferAsync(ephemeral: true).ConfigureAwait(false);

        var phrase = modal.Data.Components
            .FirstOrDefault(c => c.CustomId == KillSwitchPhraseInputId)?.Value;

        var result = await _handler
            .HandleAsync(ContextOf(modal, rawCommand), phrase)
            .ConfigureAwait(false);

        await modal.FollowupAsync(ResponseTextOf(result), ephemeral: true).ConfigureAwait(false);
    }

    // 実行結果の文言。拒否理由（内部の層名）はそのまま出さず、一般化した文言にする。
    private static string ResponseTextOf(KillSwitchCommandResult result) =>
        result.WasExecuted
            ? result.Result!.Message
            : "この操作は実行されませんでした（許可・確認ステップを満たしていません）。";

    // FR-10, UC-07: 一時停止系（pause/resume/status）の結果文言。拒否時は理由を出さず一般化する。
    // status は Result を持たないため Message（整形済み状態テキスト）をそのまま表示する。
    private static string PauseResponseTextOf(PauseCommandResult result) =>
        result.WasExecuted
            ? result.Message
            : "この操作は実行されませんでした（許可されていません）。";

    // FR-20, UC-06: 段階ゲート系（status/promote/demote/withdrawal）の結果文言。拒否時は理由を出さず一般化する。
    // 実行済み（照会・撤退評価・遷移受理/422 拒否）はハンドラが組んだ整形済み Message をそのまま表示する。
    private static string StageResponseTextOf(StageGateCommandResult result) =>
        result.WasExecuted
            ? result.Message
            : "この操作は実行されませんでした（許可されていません）。";

    private static async Task DisableComponentsAsync(SocketMessageComponent component, string text)
    {
        await component.ModifyOriginalResponseAsync(m =>
        {
            m.Content = text;
            m.Components = new ComponentBuilder().Build(); // ボタンを取り除く＝再押下できない。
        }).ConfigureAwait(false);
    }

    // Discord.Net の相互作用を Application の素の文脈へ変換する。DM は GuildId が null になるため
    // IsDirectMessage を明示的に判定して渡す（Authorizer が最優先で拒否する）。
    private static DiscordCommandContext ContextOf(SocketInteraction interaction, string rawCommand)
    {
        var guildId = GuildIdOf(interaction);
        return new DiscordCommandContext(
            guildId,
            interaction.ChannelId?.ToString(),
            interaction.User?.Id.ToString(),
            IsDirectMessage: guildId is null,
            rawCommand);
    }

    private static string? GuildIdOf(SocketInteraction interaction) => interaction switch
    {
        SocketSlashCommand c => c.GuildId?.ToString(),
        SocketMessageComponent c => c.GuildId?.ToString(),
        SocketModal m => m.GuildId?.ToString(),
        _ => null,
    };

    private Task OnLogAsync(LogMessage message)
    {
        _logger.LogInformation("Discord.Net: {Message}", message.ToString());
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
