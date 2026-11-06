using AiStockTrading.Shared.Contracts.Logging;
using NotificationService.Domain;
using NotificationService.Features.Notifications;
using NotificationService.Features.Notifications.AdoptPositionDrift;
using NotificationService.Features.Notifications.ClearGoodFaithViolations;
using NotificationService.Features.Notifications.OperateKillSwitch;
using NotificationService.Features.Notifications.OperateStageGate;
using NotificationService.Features.Notifications.OperateTradingPause;
using NotificationService.Features.Notifications.ReviewReport;
using NotificationService.Features.Notifications.RevisePolicy;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-14, UC-06, IADR-0062 決定1/2: Discord.Net による Gateway（WebSocket）常駐の実装。
// 詳細設計07 が採用した接続方式（アウトバウンドのみ・受信ポートを外部公開しない）。
//
// 本クラスは Discord.Net と Application の純粋コアを繋ぐ**変換層**に徹する。判断（多層認証・確認ステップ・
// 冪等）は一切持たず、すべて KillSwitchCommandHandler ほか各コマンドハンドラに委ねる（判断ロジックを実 Discord 非依存に
// 保つため。#871 の乖離の取り込みは PositionDriftAdoptionCommandHandler）。
//
// Intents は最小構成（Guilds のみ）。MessageContent Intent は要求しない（本 PR はスラッシュコマンドのみ。
// 自然文リプライの中継は #14 交差のため対象外・IADR-0062 決定2）。
//
// 実 Gateway への接続は本 PR では未検証（CI で外部 SaaS への WebSocket は張れない）。後続 E2E で検証する。
//
// 🔴 **本文を伴う応答は必ず `DiscordInteractionResponses` の拡張メソッド経由で出す**（#867 / IADR-0359）。
// 素の `RespondAsync` / `FollowupAsync` / `ModifyOriginalResponseAsync` は `allowedMentions` の既定が
// null＝**本文を解釈して `@everyone` を発火させる**。応答文には外部由来の値（ハンドラの整形済み Message・
// 利用者の入力）が載るため、既定が安全側になる出口へ寄せている。
public sealed class DiscordNetBotGateway : IDiscordBotGateway, IAsyncDisposable
{
    // 確認フレーズを受け取るモーダル・ボタンの識別子。
    // IADR-0097: 起動・解除でモーダル ID を分離する（送信ハンドラが ID 一致だけで種別を確定でき、誤操作の余地が無い）。
    private const string KillSwitchEngageButtonId = "ast-killswitch-engage-confirm";
    private const string KillSwitchDisengageButtonId = "ast-killswitch-disengage-confirm";

    // FR-10, FR-14, #1561, IADR-0620: 版つきの解除の確認ボタン（"<prefix><version>"）。版を照会できなければ上の版なしのボタン。
    internal const string KillSwitchDisengageButtonPrefix = "ast-ks-off-b-";
    // モーダルの ID と入力欄の ID は DiscordConfirmationModals が持つ（#1552: どの画面に確認コードの欄があるかを試験で固定する）。

    // FR-10, ADR-0009: 一時停止/再開の確認ボタン（確認フレーズは求めない＝pause は可逆のため）。
    private const string PauseConfirmButtonId = "ast-pause-confirm";
    private const string ResumeConfirmButtonId = "ast-resume-confirm";

    // FR-20, ADR-0008: 段階遷移（昇格/差し戻し）の確認ボタン。遷移先はボタンの CustomId に載せる（押下時に再解析）。
    // 例: "ast-stage-promote-2" / "ast-stage-demote-1"。
    private const string StagePromoteButtonPrefix = "ast-stage-promote-";
    private const string StageDemoteButtonPrefix = "ast-stage-demote-";

    // FR-19, UC-06, #464, ADR-0028 決定2/決定3: GFV 違反による停止の解除。
    // **kill switch と同水準**（確認ボタン → 確認フレーズのモーダル）。モーダル ID を専用に分けることで、
    // 送信ハンドラが ID 一致だけで種別を確定でき、誤って別操作を実行する余地が無い（IADR-0097 の規律）。
    private const string GfvClearButtonId = "ast-gfv-clear-confirm";

    // ADR-0028 §理由「解除の記録が監査ログに残ることで、**後から「なぜ解除したか」を追える**」。
    // 決定3 により Discord が唯一の窓口であるため、**理由を利用者が書けなければ全解除の理由が
    // 同一文字列になり、「なぜ」は監査から復元できない**（残るのは「解除者が確認済みと自称した」ことだけ）。

    // FR-07, FR-14, UC-03〜05, IADR-0240: 報告書の確定の確認ボタン。
    // **版番号を CustomId に載せる**（押下時に復元する）——詳細設計07 は「確定要求は 対象ID＋版番号 を必須とする」と
    // 定めており、ボタンを出した時点の版を運ばなければ、押すまでの間にドラフトが更新されたときに
    // **利用者が見ていない版を確定してしまう**。書式: "ast-report-approve-<periodKey>-<version>"。
    internal const string ReportApproveButtonPrefix = "ast-report-approve-";

    // FR-13, FR-14, ADR-0042 決定 1, #1025, IADR-0433 決定 5: `/policy` の確認ボタン（確定＋その版の入れ替え案の適用）。
    // 書式は `/report` と同じ "<prefix><periodKey>-<version>"。**`ast-report-approve-` で始まらない**（前方一致で取り違えない）。
    internal const string PolicyApproveButtonPrefix = "ast-policy-approve-";

    // FR-10, FR-11, FR-14, UC-06, ADR-0041 決定 4, #871, IADR-0423: 乖離の取り込み。**kill switch・GFV 解除と同水準**
    // （確認ボタン → 理由＋確認フレーズのモーダル）。対象（市場と銘柄コード）はボタンとモーダルの CustomId に載せ、
    // 押下・送信の時点で復元してハンドラが再解析する（書式: "<prefix><market>-<symbol>"。市場の語はハイフンを含まない）。
    // モーダル ID を専用に分けることで、送信ハンドラが ID の接頭辞だけで種別を確定でき、誤って別操作を実行する余地が無い
    // （IADR-0097 の規律）。
    private const string DriftAdoptButtonPrefix = "ast-drift-adopt-confirm-";

    private readonly DiscordSocketClient _client;
    private readonly KillSwitchCommandHandler _handler;
    private readonly PauseCommandHandler _pauseHandler;
    private readonly StageGateCommandHandler _stageGateHandler;
    private readonly GoodFaithViolationCommandHandler _gfvHandler;
    private readonly ReportCommandHandler _reportHandler;
    private readonly PositionDriftAdoptionCommandHandler _driftHandler;
    private readonly PolicyRevisionCommandHandler _policyHandler;
    private readonly PolicyApprovalCommandHandler _policyApprovalHandler;
    private readonly DiscordBotOptions _options;
    private readonly ILogger<DiscordNetBotGateway> _logger;

    public DiscordNetBotGateway(
        KillSwitchCommandHandler handler,
        PauseCommandHandler pauseHandler,
        StageGateCommandHandler stageGateHandler,
        GoodFaithViolationCommandHandler gfvHandler,
        ReportCommandHandler reportHandler,
        PositionDriftAdoptionCommandHandler driftHandler,
        PolicyRevisionCommandHandler policyHandler,
        PolicyApprovalCommandHandler policyApprovalHandler,
        DiscordBotOptions options,
        ILogger<DiscordNetBotGateway> logger)
    {
        _handler = handler;
        _pauseHandler = pauseHandler;
        _stageGateHandler = stageGateHandler;
        _gfvHandler = gfvHandler;
        _reportHandler = reportHandler;
        _driftHandler = driftHandler;
        _policyHandler = policyHandler;
        _policyApprovalHandler = policyApprovalHandler;
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
        _client.AutocompleteExecuted += OnAutocompleteAsync;
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

        // FR-19, UC-06, #464, ADR-0028 決定3: GFV 違反による停止の解除（**Discord Bot が唯一の窓口**）。
        // 副コマンドを clear の 1 つに絞る（暗黙に別の操作が生えない）。
        var gfv = new SlashCommandBuilder()
            .WithName("gfv")
            .WithDescription("GFV 違反による停止を解除します（違反記録そのものは消えません）")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("action")
                .WithDescription("操作")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .AddChoice("clear", "clear"))
            .Build();

        // FR-07, FR-14, UC-03〜05, IADR-0240: 報告書レビュー（版番号の照会・確定・差し戻し）。
        // 🔴 **設定値を変更する副コマンドは持たない。** FR-14 は「設定値の変更は Discord からは参照のみ」と定め、
        // 例外は kill switch と pause/resume だけである（`DiscordSettingsAreReadOnlyTests` が固定）。
        var report = new SlashCommandBuilder()
            .WithName("report")
            .WithDescription("報告書のレビュー（版番号の確認・確定・差し戻し・作り直し）を行います")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("action")
                .WithDescription("操作")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .AddChoice("show", "show")
                .AddChoice("approve", "approve")
                .AddChoice("request-changes", "request-changes")
                // FR-06, FR-14, 計画 ADR-0052 決定 1, #1156, IADR-0491 決定 1: 未確定の下書きの作り直し（所有者だけ・確定はしない）。
                .AddChoice("regenerate", "regenerate"))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("period")
                .WithDescription("会話キー（例: daily-2026-09-18 / weekly-2026-W38 / monthly-2026-09）")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                // FR-14, #834: 入力補完（候補は報告書サービスの一覧から・OnAutocompleteAsync が返す）。
                // 自由入力は従来どおり可能（補完は補助であり、候補外のキーも送れる）。
                .WithAutocomplete(true))
            .Build();

        // FR-10, FR-11, UC-06, ADR-0041 決定 4, #871, IADR-0423: 台帳とブローカーの乖離の取り込み（窓口は REST API と Bot の両方）。
        // 副コマンドを adopt の 1 つに絞る。**数量のオプションは持たない**（目標は最新の観測が決める。API と同じ）。
        var drift = new SlashCommandBuilder()
            .WithName("drift")
            .WithDescription("台帳とブローカーの乖離を取り込みます（台帳を観測へ合わせる・確認ボタンと確認フレーズが必要です）")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("action")
                .WithDescription("操作")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .AddChoice("adopt", "adopt"))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("symbol")
                .WithDescription("銘柄コード（台帳と同じ表記。例: 7203 / AAPL）")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("market")
                .WithDescription("市場")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .AddChoice("日本", "japan")
                .AddChoice("米国", "us"))
            .Build();

        // FR-07, FR-14, UC-03〜05, ADR-0003, #1016, IADR-0431: 方針の改訂（自由文の指示から AI が案を作る）。
        // **確定はしない**（案は承認待ちの新しい版になり、確定は確認ボタン＝版番号付き確定だけが行う）。
        // 🔴 **監視銘柄は変えない**（入れ替え案は表示のみ。FR-14 `DiscordSettingsAreReadOnlyTests`）。
        var policy = new SlashCommandBuilder()
            .WithName("policy")
            .WithDescription("指示から AI が方針と監視銘柄の入れ替えの案を作ります（確認ボタンで確定したときだけ適用）")
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("instruction")
                .WithDescription("改訂の指示（自由文・1000 文字まで）")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(true)
                .WithMaxLength(PolicyRevisionCommandHandler.MaxInstructionLength))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("period")
                .WithDescription("改訂する報告書の会話キー（省略時は当日〔JST〕の日報）")
                .WithType(ApplicationCommandOptionType.String)
                .WithRequired(false)
                .WithAutocomplete(true))
            .Build();

        try
        {
            await guild.CreateApplicationCommandAsync(killSwitch).ConfigureAwait(false);
            await guild.CreateApplicationCommandAsync(policy).ConfigureAwait(false);
            await guild.CreateApplicationCommandAsync(gfv).ConfigureAwait(false);
            await guild.CreateApplicationCommandAsync(drift).ConfigureAwait(false);
            await guild.CreateApplicationCommandAsync(report).ConfigureAwait(false);
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
            case "gfv":
                await OnGfvSlashAsync(command).ConfigureAwait(false);
                return;
            case "report":
                await OnReportSlashAsync(command).ConfigureAwait(false);
                return;
            case "drift":
                await OnDriftSlashAsync(command).ConfigureAwait(false);
                return;
            case "policy":
                await OnPolicySlashAsync(command).ConfigureAwait(false);
                return;
            default:
                return;
        }
    }

    // FR-07, FR-14, UC-03〜05, IADR-0240: /report → action で分岐。
    // show は参照のみのため直接実行。approve は破壊的（確定した方針が取引に適用される）ため、
    // **現在の版番号を照会してから確認ボタンを出す**（詳細設計07 §認証・認可 4項＝報告書確定は 2 段階）。
    // request-changes は安全方向・可逆のため直接実行する。
    // FR-06, 計画 ADR-0052, #1156, IADR-0491 決定 1: regenerate も直接実行する（確定しない・方針を変えない。版を上げて再提示するだけで、
    // 確定は従来どおり版番号つきの approve）。入力の取得と散文の LLM を待つため、先の Defer の後に追送で結果を返す。
    private async Task OnReportSlashAsync(SocketSlashCommand command)
    {
        var action = command.Data.Options.FirstOrDefault(o => o.Name == "action")?.Value as string;
        var period = command.Data.Options.FirstOrDefault(o => o.Name == "period")?.Value as string;

        // 許可外にはボタンすら出さない（kill switch / stage / gfv と同水準）。ハンドラ側でも再評価される。
        var context = ContextOf(command, $"/report {action} {period}");
        var auth = DiscordCommandAuthorizer.Authorize(context, _options);
        if (!auth.IsAllowed)
        {
            _logger.LogWarning(
                "Discord コマンドを拒否しました（User={UserId}・理由={Reason}）。", context.UserId, auth.Reason);
            await command.RespondTextAsync("この操作は許可されていません。").ConfigureAwait(false);
            return;
        }

        await command.DeferAsync(ephemeral: true).ConfigureAwait(false);

        // show / approve（版番号なし）はいずれも「現在の版番号の照会」に落ちる（ハンドラが決める）。
        var result = await _reportHandler.HandleAsync(context).ConfigureAwait(false);

        if (action != "approve")
        {
            await command.FollowupTextAsync(ReportResponseTextOf(result)).ConfigureAwait(false);
            return;
        }

        // 照会に失敗したら確認ボタンを出さない（版番号が分からないまま確定させない）。
        if (!result.WasExecuted || result.Version is not { } version)
        {
            await command.FollowupTextAsync(ReportResponseTextOf(result)).ConfigureAwait(false);
            return;
        }

        var builder = new ComponentBuilder().WithButton(
            $"版 {version} を確定する",
            ReportApproveButtonPrefix + $"{period}-{version}",
            // 確定は取引方針を有効化する破壊的操作（ADR-0003）のため危険色。
            ButtonStyle.Danger);

        await command.FollowupTextAsync(
            $"{result.Message}\n確定すると、この版の方針が取引に適用されます。確定しますか？",
            builder.Build()).ConfigureAwait(false);
    }

    // FR-07, FR-14, UC-03〜05, ADR-0003, #1016, IADR-0431: /policy → AI の改訂案を表示し、承認待ちにできた版だけ
    // 既存の確定の確認ボタン（`ast-report-approve-<periodKey>-<version>`）を出す。確定はボタン押下で
    // ReportCommandHandler が版番号付きで行う（OnBehalfOf つき）。LLM の所要時間があるため先に Defer する。
    private async Task OnPolicySlashAsync(SocketSlashCommand command)
    {
        var instruction = command.Data.Options.FirstOrDefault(o => o.Name == "instruction")?.Value as string;
        var period = command.Data.Options.FirstOrDefault(o => o.Name == "period")?.Value as string;

        // RawCommand には指示を載せない（解析の対象外・ログへ流さない）。
        var context = ContextOf(command, string.IsNullOrWhiteSpace(period) ? "/policy" : $"/policy {period}");
        var auth = DiscordCommandAuthorizer.Authorize(context, _options);
        if (!auth.IsAllowed)
        {
            _logger.LogWarning(
                "Discord コマンドを拒否しました（User={UserId}・理由={Reason}）。", context.UserId, auth.Reason);
            await command.RespondTextAsync("この操作は許可されていません。").ConfigureAwait(false);
            return;
        }

        await command.DeferAsync(ephemeral: true).ConfigureAwait(false);

        var result = await _policyHandler.HandleAsync(context, instruction).ConfigureAwait(false);

        // 🔴 ADR-0003: 方針の全文（分割あり）を**順に送り終えてから**、最後の通にだけ確認ボタンを付ける。
        // 送り順・ボタンの位置・途中失敗の知らせは PolicyRevisionReplySender が持つ（試験できる形に切り出した）。
        await PolicyRevisionReplySender.SendAsync(
            result,
            (text, components) => command.FollowupTextAsync(text, components),
            _logger).ConfigureAwait(false);
    }

    // FR-07, FR-14, UC-03〜05, #834: `/report` の period の入力補完。判断（多層認証・絞り込み・上限）は
    // すべて ReportCommandHandler / ReportPeriodSuggestions が持ち、ここは変換に徹する。
    //
    // 🔴 **何があっても例外を Discord.Net へ返さない（fail-safe）。** 補完が失敗しても候補なしで応答し、
    // `/report` 自体は従来どおり動く（#834 の射程。Bot を落とさない）。
    private async Task OnAutocompleteAsync(SocketAutocompleteInteraction interaction)
    {
        IReadOnlyList<string> suggestions = [];

        try
        {
            // 対象は /report の period だけ。他のコマンド・他のオプションには候補を出さない。
            // #1016: `/policy` の period も同じ候補（会話キー）を出す。
            if (interaction.Data.CommandName is "report" or "policy" && interaction.Data.Current.Name == "period")
            {
                // 許可判定は handler が行う（許可外には候補を返さない）。RawCommand に利用者の入力は載せない
                // ——補完は解析しないため不要であり、ログへ外部由来の文字列を混ぜない。
                var context = ContextOf(interaction, "/report autocomplete");
                suggestions = await _reportHandler
                    .SuggestPeriodsAsync(context, interaction.Data.Current.Value as string)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "報告書の入力補完に失敗しました（候補なしで応答します）。");
        }

        try
        {
            await interaction
                .RespondAsync(suggestions.Select(key => new AutocompleteResult(key, key)))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 応答期限切れ・二重応答。ここで落とすと Bot ごと巻き込むため、ログのみ残す。
            _logger.LogWarning(ex, "入力補完の応答に失敗しました。");
        }
    }

    // FR-19, FR-11, UC-06, #464, ADR-0028 決定2/決定3: /gfv clear → 確認ボタンを提示する。
    // **ここでは Risk を呼ばない。** 認証・確認フレーズはモーダル送信時にハンドラが評価する
    // （kill switch と同型＝ADR-0028 §結果 が求める「既存の破壊的統制操作と同じ確認水準」）。
    private async Task OnGfvSlashAsync(SocketSlashCommand command)
    {
        // 詳細設計07 / ADR-0028 §結果: 許可外の着信は無視しログのみ残す。**ここで早期に弾き、ボタンすら出さない。**
        // kill switch / pause / stage と同水準（**破壊的操作の窓口を許可外へ露出しない**）。
        // ハンドラ側の閂1 でも認証は再評価されるが、そこだけに頼ると許可外の利用者へ
        // Danger ボタンと確認モーダルが提示されてしまう。
        var context = ContextOf(command, "/gfv clear");
        var auth = DiscordCommandAuthorizer.Authorize(context, _options);
        if (!auth.IsAllowed)
        {
            _logger.LogWarning(
                "Discord コマンドを拒否しました（User={UserId}・理由={Reason}）。", context.UserId, auth.Reason);
            await command.RespondTextAsync("この操作は許可されていません。").ConfigureAwait(false);
            return;
        }

        var builder = new ComponentBuilder().WithButton(
            "GFV による停止を解除する", GfvClearButtonId, ButtonStyle.Danger);

        // 🔴 **確認の文面に「記録は消えない」ことを明示する。** ADR-0028 決定1 が「違反記録は失効させない」と
        // 定めており、解けるのは**停止**である。ここを曖昧にすると「記録を消す操作」として押される。
        await command.RespondTextAsync(
            "GFV 違反による停止を解除しますか？"
            + "**この操作は違反記録を消しません**（記録は監査証跡として残ります）。解除されるのは**停止**です。"
            + "原因の是正が済んでいることを確認したうえで実行してください。",
            builder.Build()).ConfigureAwait(false);
    }

    // FR-10, FR-11, UC-06, ADR-0041 決定 4, #871, IADR-0423: /drift adopt → 確認ボタンを提示する。
    // **ここではリスク管理を呼ばない。** 認証・確認フレーズ・理由はモーダル送信時にハンドラが評価する（GFV 解除と同型）。
    private async Task OnDriftSlashAsync(SocketSlashCommand command)
    {
        var action = command.Data.Options.FirstOrDefault(o => o.Name == "action")?.Value as string;
        var symbol = command.Data.Options.FirstOrDefault(o => o.Name == "symbol")?.Value as string;
        var market = command.Data.Options.FirstOrDefault(o => o.Name == "market")?.Value as string;

        // 許可外にはボタンすら出さない（kill switch / gfv と同水準。**台帳を書き換える窓口を許可外へ露出しない**）。
        var context = ContextOf(command, $"/drift {action} {symbol} {market}");
        var auth = DiscordCommandAuthorizer.Authorize(context, _options);
        if (!auth.IsAllowed)
        {
            _logger.LogWarning(
                "Discord コマンドを拒否しました（User={UserId}・理由={Reason}）。", context.UserId, auth.Reason);
            await command.RespondTextAsync("この操作は許可されていません。").ConfigureAwait(false);
            return;
        }

        // 書式外の銘柄コード・未知の市場はボタンを出さない（CustomId へ載せる値を parser の値域に限る）。
        var parsed = BotCommandParser.Parse(context.RawCommand);
        if (parsed is not { Kind: BotCommandKind.PositionDriftAdopt, Symbol: { } target, Market: { } targetMarket })
        {
            await command.RespondTextAsync(
                "銘柄コード（英数字・ピリオド・ハイフンの 1〜16 文字）と市場（日本／米国）を指定してください。").ConfigureAwait(false);
            return;
        }

        var payload = $"{BotCommandParser.MarketToken(targetMarket)}-{target}";
        var builder = new ComponentBuilder().WithButton(
            "台帳を観測へ合わせる", DriftAdoptButtonPrefix + payload, ButtonStyle.Danger);

        // 🔴 **確認の文面に、この操作が何をし何をしないかを明示する**（数量は選べない・減らす乖離だけ・実現損益は記録しない）。
        await command.RespondTextAsync(
            $"{target}（{HttpPositionDriftAdoptionController.MarketLabel(targetMarket)}）の台帳の建玉を、"
            + "最新のブローカー建玉の観測へ合わせますか？\n"
            + "・取り込めるのは台帳の建玉を**減らす**乖離だけです（数量は最新の観測で決まり、指定できません）。\n"
            + "・システム外の売買の約定価格は分からないため、**実現損益は記録しません**。\n"
            + "・この操作は取引台帳を書き換え、操作者と理由が監査台帳に残ります。",
            builder.Build()).ConfigureAwait(false);
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
            await command.RespondTextAsync("この操作は許可されていません。").ConfigureAwait(false);
            return;
        }

        if (!isOff)
        {
            await command.RespondTextAsync(
                "本当に全取引を停止しますか？",
                new ComponentBuilder().WithButton("全取引を停止する", KillSwitchEngageButtonId, ButtonStyle.Danger).Build()).ConfigureAwait(false);
            return;
        }

        // FR-10, FR-14, #1561, IADR-0620: 解除は、確認を出す時点の kill switch の版を照会してボタンに載せる（見た状態の版を解除の要求へ添える）。
        // 照会は応答の期限（3 秒）を越え得るので先に Defer する。照会に失敗したら版なしのボタン（段 1 では従来どおり通る）。
        await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
        var version = KillSwitchVersions.Normalize(await _handler.GetDisengageVersionAsync(context).ConfigureAwait(false));
        await command.FollowupTextAsync(
            "本当に停止を解除しますか？",
            new ComponentBuilder().WithButton(
                "停止を解除する",
                version is null ? KillSwitchDisengageButtonId : KillSwitchDisengageButtonPrefix + version,
                ButtonStyle.Danger).Build()).ConfigureAwait(false);
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
            await command.RespondTextAsync("この操作は許可されていません。").ConfigureAwait(false);
            return;
        }

        var builder = new ComponentBuilder().WithButton(
            isResume ? "一時停止を解除する" : "新規建てを一時停止する",
            isResume ? ResumeConfirmButtonId : PauseConfirmButtonId,
            // pause は kill switch ほど重くない可逆操作のため Primary（危険色にしない）。
            ButtonStyle.Primary);

        await command.RespondTextAsync(
            isResume ? "一時停止を解除しますか？" : "新規建てを一時停止しますか？（手仕舞い・損切りは継続します）",
            builder.Build()).ConfigureAwait(false);
    }

    // FR-10, UC-07, ADR-0009: /status → 参照のみ。認証を通過していれば現在状態を表示する（確認ステップ無し）。
    private async Task OnStatusSlashAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
        var result = await _pauseHandler
            .HandleAsync(ContextOf(command, "/status"))
            .ConfigureAwait(false);
        await command.FollowupTextAsync(PauseResponseTextOf(result)).ConfigureAwait(false);
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
            await command.RespondTextAsync("この操作は許可されていません。").ConfigureAwait(false);
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
                    await command.FollowupTextAsync(StageResponseTextOf(result)).ConfigureAwait(false);
                    return;
                }

            case "promote":
            case "demote":
                {
                    // 遷移先が無い・範囲外なら実行しない（Discord 側の 0〜3 制約に加え防御的に確認）。
                    if (stageValue is not (long or int) || !TryStage(stageValue, out var target))
                    {
                        await command.RespondTextAsync(
                            "promote/demote には遷移先の段階（0〜3）を指定してください。").ConfigureAwait(false);
                        return;
                    }

                    var isPromote = action == "promote";
                    var button = (isPromote ? StagePromoteButtonPrefix : StageDemoteButtonPrefix) + target;
                    var builder = new ComponentBuilder().WithButton(
                        isPromote ? $"Stage {target} へ昇格する" : $"Stage {target} へ差し戻す",
                        button,
                        // 昇格は実弾方向のため危険色、差し戻しは安全方向のため Primary。
                        isPromote ? ButtonStyle.Danger : ButtonStyle.Primary);

                    // #466, FR-20, SC-02, §4.1 追補3（質問票 第15回 Q13-a）, IADR-0180 決定5:
                    // **確認を出す前に**最小取引件数の引き下げ警告を届ける。
                    //
                    // 裁定は「『承認前に status を読む』は**人の運用に依存する前提**であり、読まなければ
                    // 警告が届かない」と述べている。**ボタンを押した後にだけ警告が出る形は、この批判が
                    // そのまま当てはまる**——押した時点で遷移は既に受理・台帳追記・イベント発行まで済んでいる。
                    // 昇格のときだけ引く（差し戻しは安全側の操作・決定2）。照会失敗は null＝警告なしで、
                    // 確認そのものは止めない（警告は昇格を妨げない）。
                    var promoteWarning = isPromote
                        ? await _stageGateHandler
                            .GetPromotionWarningAsync(ContextOf(command, $"/stage promote {target}"))
                            .ConfigureAwait(false)
                        : null;

                    var prompt = isPromote
                        ? $"本当に Stage {target} へ昇格しますか？（実弾方向の段階前進です）"
                        : $"Stage {target} へ差し戻しますか？";

                    await command.RespondTextAsync(
                        promoteWarning is null ? prompt : $"{promoteWarning}\n{prompt}",
                        builder.Build()).ConfigureAwait(false);
                    return;
                }

            default:
                await command.RespondTextAsync("不明な操作です。").ConfigureAwait(false);
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
                await component.RespondWithModalAsync(DiscordConfirmationModals.KillSwitchEngage()).ConfigureAwait(false);
                return;

            case KillSwitchDisengageButtonId:
                // IADR-0097: 解除も起動と同じくフレーズ入力モーダルを提示する（確認ボタンのみでは解除しない）。
                // NFR-06, #1552: 解除の画面には確認コードの欄もある（任意）。
                await component.RespondWithModalAsync(DiscordConfirmationModals.KillSwitchDisengage()).ConfigureAwait(false);
                return;

            case GfvClearButtonId:
                // #464, ADR-0028: kill switch と同じく確認フレーズのモーダルを要求する（確認ボタンのみでは統制を解かない）。
                // ADR-0028 決定2 の「原因の是正が済んでいることの確認」として、何を是正したかを書かせ監査へ残す。
                await component.RespondWithModalAsync(DiscordConfirmationModals.GfvClear()).ConfigureAwait(false);
                return;

            case PauseConfirmButtonId:
                {
                    // FR-10, ADR-0009: 確認ボタン押下で pause を実行する（確認フレーズは無い）。
                    // 認証はハンドラ側で再評価される（押下者のすり替え防止）。
                    await component.DeferAsync(ephemeral: true).ConfigureAwait(false);
                    var result = await _pauseHandler
                        .HandleAsync(ContextOf(component, "/pause"))
                        .ConfigureAwait(false);
                    await DisableComponentsAsync(component, PauseResponseTextOf(result)).ConfigureAwait(false);
                    return;
                }

            case ResumeConfirmButtonId:
                // NFR-06, #1552, IADR-0615: 再開は確認コードの欄の画面を挟む（ボタンだけでは再開しない。欄は任意）。
                await component.RespondWithModalAsync(DiscordConfirmationModals.Resume()).ConfigureAwait(false);
                return;

            default:
                // FR-10, FR-14, #1561, IADR-0620: 版つきの解除の確認ボタン → 版つきの確認の画面（版は送信で解除の要求へ添える）。
                if (component.Data.CustomId.StartsWith(KillSwitchDisengageButtonPrefix, StringComparison.Ordinal))
                {
                    await component.RespondWithModalAsync(DiscordConfirmationModals.KillSwitchDisengage(
                        component.Data.CustomId[KillSwitchDisengageButtonPrefix.Length..])).ConfigureAwait(false);
                    return;
                }

                // FR-10, #871, IADR-0423: 乖離の取り込みの確認ボタン → 理由＋確認フレーズのモーダル（確認ボタンのみでは取り込まない）。
                if (component.Data.CustomId.StartsWith(DriftAdoptButtonPrefix, StringComparison.Ordinal))
                {
                    // API の reason と同じ欄。なぜ台帳を合わせるのかを利用者に書かせ、監査へそのまま残す。
                    await component.RespondWithModalAsync(
                        DiscordConfirmationModals.DriftAdopt(component.Data.CustomId[DriftAdoptButtonPrefix.Length..])).ConfigureAwait(false);
                    return;
                }

                // FR-13, ADR-0042 決定 1, #1025: `/policy` の確認ボタン（確定＋入れ替え案の適用）。
                // NFR-06, #1552, IADR-0615: 確定は確認コードの欄の画面を挟む（ボタンだけでは確定しない。欄は任意）。
                if (component.Data.CustomId.StartsWith(PolicyApproveButtonPrefix, StringComparison.Ordinal))
                {
                    if (PayloadOf(component.Data.CustomId, PolicyApproveButtonPrefix) is { } policyPayload)
                        await component.RespondWithModalAsync(DiscordConfirmationModals.PolicyApprove(policyPayload)).ConfigureAwait(false);
                    return;
                }

                // FR-07, IADR-0240: 報告書の確定の確認ボタン（CustomId に periodKey と版番号を載せる）。
                if (component.Data.CustomId.StartsWith(ReportApproveButtonPrefix, StringComparison.Ordinal))
                {
                    if (PayloadOf(component.Data.CustomId, ReportApproveButtonPrefix) is { } reportPayload)
                        await component.RespondWithModalAsync(DiscordConfirmationModals.ReportApprove(reportPayload)).ConfigureAwait(false);
                    return;
                }

                // FR-20, ADR-0008: 昇格（実弾方向）は確認コードの欄の画面を挟む（NFR-06, #1552）。差し戻しは従来どおりボタンで実行する。
                if (component.Data.CustomId.StartsWith(StagePromoteButtonPrefix, StringComparison.Ordinal))
                {
                    await component.RespondWithModalAsync(
                        DiscordConfirmationModals.StagePromote(component.Data.CustomId[StagePromoteButtonPrefix.Length..])).ConfigureAwait(false);
                    return;
                }

                // FR-20, ADR-0008: 段階遷移の確認ボタン（CustomId に遷移先を載せる）。押下で promote/demote を実行する。
                // 認証はハンドラ側で再評価される（押下者のすり替え防止）。二重押下はボタン無効化＋Risk 側連番検証で弾く。
                await OnStageButtonAsync(component).ConfigureAwait(false);
                return;
        }
    }

    // FR-07, FR-14, UC-03〜05, IADR-0240: 確定の確認ボタンの CustomId の "<periodKey>-<version>" を取り出す（書式外は null。規則は
    // DiscordConfirmationModals.VersionedOf と同じ 1 つ）。
    private static string? PayloadOf(string customId, string prefix) =>
        DiscordConfirmationModals.VersionedOf(customId, prefix) is { } v ? $"{v.Key}-{v.Version}" : null;

    // FR-20, UC-06: 差し戻しの確認ボタンの押下（昇格は確認の画面を挟む＝OnStagePromoteModalAsync）。末尾の遷移先を復元する。
    private async Task OnStageButtonAsync(SocketMessageComponent component)
    {
        var customId = component.Data.CustomId;
        if (!customId.StartsWith(StageDemoteButtonPrefix, StringComparison.Ordinal))
            return; // 未知のボタン（他機能）。

        await component.DeferAsync(ephemeral: true).ConfigureAwait(false);
        var result = await _stageGateHandler
            .HandleAsync(ContextOf(component, $"/stage demote {customId[StageDemoteButtonPrefix.Length..]}"))
            .ConfigureAwait(false);
        await DisableComponentsAsync(component, StageResponseTextOf(result)).ConfigureAwait(false);
    }

    // 確認の画面の送信 → 最終実行。認証・フレーズ・理由・確認コードの検証はハンドラ（と提供側）が行う。
    // IADR-0097, NFR-06, #1552: 振り分け（ID → 種別）と入力の取り出しは DiscordConfirmationModals.Route の純関数が持ち、ここは結果に従うだけ。
    // ID が書式外（他機能・対象の欠けた ID）なら何もしない（従来どおり）。
    private async Task OnModalAsync(SocketModal modal)
    {
        var submission = DiscordConfirmationModals.Route(
            modal.Data.CustomId, id => modal.Data.Components.FirstOrDefault(c => c.CustomId == id)?.Value);
        if (submission is null)
            return;

        await modal.DeferAsync(ephemeral: true).ConfigureAwait(false);
        var context = ContextOf(modal, submission.RawCommand);
        var code = submission.SecondFactorCode;

        switch (submission.Kind)
        {
            case ConfirmationKind.KillSwitchEngage:
            case ConfirmationKind.KillSwitchDisengage:
                // FR-10, #1561, IADR-0620: 版は版つきの解除の画面だけが持つ（起動・版なしの画面は null）。
                await modal.FollowupTextAsync(ResponseTextOf(
                    await _handler.HandleAsync(context, submission.Phrase, code, submission.ExpectedVersion).ConfigureAwait(false))).ConfigureAwait(false);
                return;

            case ConfirmationKind.GfvClear:
                {
                    var result = await _gfvHandler.HandleAsync(context, submission.Phrase, submission.Reason, code).ConfigureAwait(false);
                    // 拒否理由（内部の層名）はそのまま出さず一般化する（kill switch と同じ規律）。
                    await modal.FollowupTextAsync(result.WasExecuted ? result.Message : "この操作は許可されていません。").ConfigureAwait(false);
                    return;
                }

            case ConfirmationKind.DriftAdopt:
                await modal.FollowupTextAsync(DriftResponseTextOf(
                    await _driftHandler.HandleAsync(context, submission.Phrase, submission.Reason, code).ConfigureAwait(false)))
                    .ConfigureAwait(false);
                return;

            // ボタンだけで実行していた 4 操作は、元のメッセージを結果で差し替えてボタンを取り除く（二重押下の吸収の 1 段目。
            // 2 段目はハンドラの版番号ガードと提供側の冪等）。
            case ConfirmationKind.Resume:
                await ReplaceOriginalAsync(modal, PauseResponseTextOf(
                    await _pauseHandler.HandleAsync(context, code).ConfigureAwait(false))).ConfigureAwait(false);
                return;

            case ConfirmationKind.StagePromote:
                await ReplaceOriginalAsync(modal, StageResponseTextOf(
                    await _stageGateHandler.HandleAsync(context, code).ConfigureAwait(false))).ConfigureAwait(false);
                return;

            case ConfirmationKind.ReportApprove:
                await ReplaceOriginalAsync(modal, ReportResponseTextOf(
                    await _reportHandler.HandleAsync(context, code).ConfigureAwait(false))).ConfigureAwait(false);
                return;

            case ConfirmationKind.PolicyApprove:
                await ReplaceOriginalAsync(modal,
                    (await _policyApprovalHandler.HandleAsync(context, code).ConfigureAwait(false)).Message).ConfigureAwait(false);
                return;
        }
    }

    // FR-10, #871: 取り込みの結果文言。**拒否（閂で止まった＝リスク管理を呼んでいない）と、リスク管理の応答を読み分ける。**
    // 閂の内部の層名は返さないが、台帳が変わっていないことは明示する。リスク管理の拒否（422）の理由はそのまま返す
    // （利用者が次に何をすればよいかが書いてある）。
    private static string DriftResponseTextOf(PositionDriftAdoptionCommandResult result) =>
        result.WasExecuted
            ? result.Message
            : "この操作は実行されませんでした（許可・確認フレーズ・理由の入力のいずれかを満たしていません）。台帳は変わっていません。";

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

    // FR-07, FR-14, IADR-0240: 報告書レビュー系の結果文言。
    // **拒否（多層認証・解釈不能）と失敗（呼び出しエラー・版落ち）を読み分ける。**
    // 拒否理由（内部の層名）は返さないが、**失敗はそのまま返す**——利用者が対処できる情報
    //（「最新ドラフトを確認してください」「HTTP 409」）であり、伏せると確定できない理由が分からなくなる。
    private static string ReportResponseTextOf(ReportCommandResult result) =>
        result.IsDenied
            ? "この操作は実行されませんでした（許可されていません）。"
            : result.Message;

    private static async Task DisableComponentsAsync(SocketMessageComponent component, string text)
    {
        // ボタンを取り除く＝再押下できない。編集でも Discord は本文を解釈し直すため、
        // ラッパー（IADR-0359）がメンション方針を載せる。
        await component.ReplaceOriginalResponseAsync(text, new ComponentBuilder().Build()).ConfigureAwait(false);
    }

    // NFR-06, #1552: ボタンから開いた確認の画面の送信。DeferAsync はこのとき「元のメッセージの更新」の予約になる（Discord.Net の
    // SocketModal.DeferAsync の注記）ため、元のメッセージを結果で差し替えてボタンを取り除く（ボタンで実行していたときと同じ見え方）。
    // 🔴 実 Discord での確認は未了（#570）。差し替えが拒まれても結果だけは必ず届ける（追送へ落とす）。
    private Task ReplaceOriginalAsync(SocketModal modal, string text) =>
        DiscordInteractionResponses.ReplaceOriginalOrFollowupAsync(
            () => modal.ReplaceOriginalResponseAsync(text, new ComponentBuilder().Build()),
            () => modal.FollowupTextAsync(text),
            _logger);

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

    // #843 項目5: `GuildId` は基底 `SocketInteraction` のプロパティであり、スラッシュコマンド・ボタン・モーダル・
    // 入力補完のいずれも同じ値を返す（派生型での再宣言は無い。Discord.Net 3.20.1 で実測）。型ごとの分岐は要らず、
    // 新しい種類の interaction を足しても「GuildId が欠けて DM 扱いになる」ことは起きない。
    private static string? GuildIdOf(SocketInteraction interaction) => interaction.GuildId?.ToString();

    private Task OnLogAsync(LogMessage message)
    {
        // NFR, IADR-0316, #708: Discord.Net のログ文言は gateway 応答・例外メッセージをそのまま載せる
        // ＝外部由来である。行指向のログへ偽の行を注入され得るため、発生源で正規化する（CWE-117）。
        _logger.LogInformation("Discord.Net: {Message}", LogSanitizer.Sanitize(message.ToString()));
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
