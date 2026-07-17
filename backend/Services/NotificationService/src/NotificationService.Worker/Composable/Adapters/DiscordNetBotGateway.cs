using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.Services;
using AiStockTrading.Notification.Application.State;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Worker.Composable.Adapters;

// FR-14, UC-06, IADR-0063 決定1/2: Discord.Net による Gateway（WebSocket）常駐の実装。
// 詳細設計07 が採用した接続方式（アウトバウンドのみ・受信ポートを外部公開しない）。
//
// 本クラスは Discord.Net と Application の純粋コアを繋ぐ**変換層**に徹する。判断（多層認証・確認ステップ・
// 冪等）は一切持たず、すべて KillSwitchCommandHandler に委ねる（判断ロジックを実 Discord 非依存に保つため）。
//
// Intents は最小構成（Guilds のみ）。MessageContent Intent は要求しない（本 PR はスラッシュコマンドのみ。
// 自然文リプライの中継は #14 交差のため対象外・IADR-0063 決定2）。
//
// 実 Gateway への接続は本 PR では未検証（CI で外部 SaaS への WebSocket は張れない）。後続 E2E で検証する。
internal sealed class DiscordNetBotGateway : IDiscordBotGateway, IAsyncDisposable
{
    // 確認フレーズを受け取るモーダル・ボタンの識別子。
    private const string KillSwitchEngageButtonId = "ast-killswitch-engage-confirm";
    private const string KillSwitchDisengageButtonId = "ast-killswitch-disengage-confirm";
    private const string KillSwitchModalId = "ast-killswitch-modal";
    private const string KillSwitchPhraseInputId = "ast-killswitch-phrase";

    private readonly DiscordSocketClient _client;
    private readonly KillSwitchCommandHandler _handler;
    private readonly DiscordBotOptions _options;
    private readonly ILogger<DiscordNetBotGateway> _logger;

    public DiscordNetBotGateway(
        KillSwitchCommandHandler handler,
        DiscordBotOptions options,
        ILogger<DiscordNetBotGateway> logger)
    {
        _handler = handler;
        _options = options;
        _logger = logger;

        // IADR-0063 決定2: 最小 Intents。Guilds のみでスラッシュコマンドは受けられる。
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

        try
        {
            await guild.CreateApplicationCommandAsync(killSwitch).ConfigureAwait(false);
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
        if (command.Data.Name != "killswitch")
            return;

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

    // 確認ボタン押下 → 起動は確認フレーズのモーダルを出す。解除はそのまま実行する
    // （詳細設計07:「解除のみ確認ステップを追加」＝起動はフレーズまで要求する）。
    private async Task OnButtonAsync(SocketMessageComponent component)
    {
        switch (component.Data.CustomId)
        {
            case KillSwitchEngageButtonId:
                {
                    var modal = new ModalBuilder()
                        .WithTitle("全取引の停止")
                        .WithCustomId(KillSwitchModalId)
                        .AddTextInput("確認フレーズを入力してください", KillSwitchPhraseInputId, required: true)
                        .Build();
                    await component.RespondWithModalAsync(modal).ConfigureAwait(false);
                    return;
                }

            case KillSwitchDisengageButtonId:
                {
                    await component.DeferAsync(ephemeral: true).ConfigureAwait(false);
                    var result = await _handler
                        .HandleAsync(ContextOf(component, "/killswitch off"), confirmationPhrase: null)
                        .ConfigureAwait(false);
                    // 押されたボタンは無効化し、結果をメッセージ編集で明示する（詳細設計07）。
                    await DisableComponentsAsync(component, ResponseTextOf(result)).ConfigureAwait(false);
                    return;
                }

            default:
                return;
        }
    }

    // 確認フレーズの入力 → 最終実行。認証はハンドラ側で再評価される。
    private async Task OnModalAsync(SocketModal modal)
    {
        if (modal.Data.CustomId != KillSwitchModalId)
            return;

        await modal.DeferAsync(ephemeral: true).ConfigureAwait(false);

        var phrase = modal.Data.Components
            .FirstOrDefault(c => c.CustomId == KillSwitchPhraseInputId)?.Value;

        var result = await _handler
            .HandleAsync(ContextOf(modal, "/killswitch"), phrase)
            .ConfigureAwait(false);

        await modal.FollowupAsync(ResponseTextOf(result), ephemeral: true).ConfigureAwait(false);
    }

    // 実行結果の文言。拒否理由（内部の層名）はそのまま出さず、一般化した文言にする。
    private static string ResponseTextOf(KillSwitchCommandResult result) =>
        result.WasExecuted
            ? result.Result!.Message
            : "この操作は実行されませんでした（許可・確認ステップを満たしていません）。";

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
