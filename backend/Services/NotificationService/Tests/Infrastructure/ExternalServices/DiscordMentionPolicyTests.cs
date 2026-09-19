using System.Reflection;
using Discord;
using NotificationService.Infrastructure.ExternalServices;
using AwesomeAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Xunit;

namespace NotificationService.Tests;

// FR-09, FR-14, IADR-0359, #867: Discord へ出る本文がメンションを発火させないことを固定する。
// 実 Discord へは一切送らない（`IDiscordInteraction` のフェイクが引数を捕捉するだけ）。
public class DiscordMentionPolicyTests
{
    // 外部由来の値として本文に載り得る代表形（#867 の症状）。
    private const string Injected = "@everyone @here <@&123456789012345678> <@987654321098765432>";

    [Fact]
    public void 方針は_parse_を空にする形である()
    {
        var policy = DiscordMentionPolicy.SuppressAll;

        // 🔴 `AllowedTypes` が **null ではなく None** であることが要点。null だと REST モデルで
        // `parse: null` になり、空配列にならない（下のテストが Discord.Net 側の挙動を固定する）。
        policy.AllowedTypes.Should().Be(AllowedMentionTypes.None);
        policy.UserIds.Should().BeNullOrEmpty();
        policy.RoleIds.Should().BeNullOrEmpty();
        policy.MentionRepliedUser.Should().BeFalse();
    }

    // Discord.Net の `AllowedMentions.None` は**名前に反して** `AllowedTypes` が null である
    //（3.20.1 実測）。将来の版でこれが直ったとしても、本リポジトリは明示形を使い続ける。
    // ライブラリ側の落とし穴を記録として固定しておく（これが変わったら赤くなって気付ける）。
    [Fact]
    public void ライブラリの_AllowedMentions_None_は_AllowedTypes_が_null_であり使わない()
    {
        AllowedMentions.None.AllowedTypes.Should().BeNull();
    }

    // 🔴 **電線に載る形**を固定する（#867 の監査指摘 N4）。上の 2 本はメモリ上の `AllowedMentions` しか
    // 見ておらず、**Discord.Net が `AllowedMentionTypes.None` の写し方を変えたら誰も気付けない**。
    // ライブラリ自身の `ToModel` ＋ `DiscordContractResolver` で JSON まで写して固定する。
    // Webhook 側（素の JSON）は `DiscordWebhookNotificationSenderTests` が同じ形を固定している。
    [Fact]
    public void 方針は直列化すると_parse_が空配列になる()
    {
        SerializeAsDiscordNetDoes(DiscordMentionPolicy.SuppressAll)
            .Should().Be("""{"parse":[],"roles":[],"users":[],"replied_user":false}""");
    }

    [Fact]
    public void ライブラリの_AllowedMentions_None_は直列化すると_parse_が_null_になる()
    {
        // 空配列ではない＝「一切解釈しない」の正規形ではない。決定 2 が明示形を使う根拠。
        SerializeAsDiscordNetDoes(AllowedMentions.None)
            .Should().Be("""{"parse":null,"roles":[],"users":[]}""");
    }

    [Fact]
    public void 対照_AllowedMentions_All_は_everyone_を通す()
    {
        // 対照群。この形が出たらメンションは発火する（= 是正前の既定に相当する挙動）。
        SerializeAsDiscordNetDoes(AllowedMentions.All)
            .Should().Be("""{"parse":["everyone","roles","users"],"roles":[],"users":[]}""");
    }

    // Discord.Net が REST 要求へ載せるときと同じ経路（内部の `EntityExtensions.ToModel` ＋
    // `DiscordContractResolver`）で JSON へ写す。**実送信はしない。**
    // ライブラリ側が内部構造を変えたらここが落ちる——それが狙いである（黙って形が変わるより落ちるほうがよい）。
    private static string SerializeAsDiscordNetDoes(AllowedMentions allowedMentions)
    {
        var rest = typeof(Discord.Rest.DiscordRestClient).Assembly;

        var entityExtensions = rest.GetType("Discord.Rest.EntityExtensions");
        entityExtensions.Should().NotBeNull("Discord.Net.Rest の内部構造が変わった（IADR-0359 決定 2 の実測をやり直すこと）");
        var toModel = entityExtensions!
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "ToModel"
                && m.GetParameters() is [{ } p] && p.ParameterType == typeof(AllowedMentions));
        toModel.Should().NotBeNull("EntityExtensions.ToModel(AllowedMentions) が見つからない");

        var resolverType = rest.GetType("Discord.Net.Converters.DiscordContractResolver");
        resolverType.Should().NotBeNull("DiscordContractResolver が見つからない");

        var serializer = new JsonSerializer
        {
            ContractResolver = (IContractResolver)Activator.CreateInstance(resolverType!)!,
        };

        using var text = new StringWriter();
        using var writer = new JsonTextWriter(text);
        serializer.Serialize(writer, toModel!.Invoke(null, [allowedMentions]));
        return text.ToString();
    }

    [Fact]
    public void 方針は呼ぶたびに別の実体を返す()
    {
        // `AllowedMentions` は可変。共有すると呼び出し側の書き換えが全経路へ波及する。
        DiscordMentionPolicy.SuppressAll.Should().NotBeSameAs(DiscordMentionPolicy.SuppressAll);
    }

    [Fact]
    public async Task 初回応答は_メンションを発火させない方針つきで送られる()
    {
        var interaction = new CapturingInteraction();

        await interaction.RespondTextAsync($"この操作は許可されていません（{Injected}）。");

        interaction.RespondText.Should().Contain(Injected, "本文はサニタイズせずそのまま出す");
        ShouldSuppressAllMentions(interaction.RespondAllowedMentions);
        interaction.RespondEphemeral.Should().BeTrue();
    }

    [Fact]
    public async Task 初回応答はボタンつきでも方針が載る()
    {
        var interaction = new CapturingInteraction();
        var components = new ComponentBuilder().WithButton("確定する", "ast-test-button").Build();

        await interaction.RespondTextAsync($"確定しますか？（{Injected}）", components);

        ShouldSuppressAllMentions(interaction.RespondAllowedMentions);
        interaction.RespondComponents.Should().NotBeNull();
    }

    [Fact]
    public async Task 追送は_メンションを発火させない方針つきで送られる()
    {
        var interaction = new CapturingInteraction();

        await interaction.FollowupTextAsync($"確定しました（{Injected}）。");

        interaction.FollowupText.Should().Contain(Injected);
        ShouldSuppressAllMentions(interaction.FollowupAllowedMentions);
        interaction.FollowupEphemeral.Should().BeTrue();
    }

    [Fact]
    public async Task 応答の差し替えにも方針が載る()
    {
        var interaction = new CapturingInteraction();

        await interaction.ReplaceOriginalResponseAsync(
            $"解除しました（{Injected}）。", new ComponentBuilder().Build());

        interaction.Modified.Should().NotBeNull();
        interaction.Modified!.Content.Value.Should().Contain(Injected);
        interaction.Modified.AllowedMentions.IsSpecified.Should().BeTrue("編集でも本文は解釈し直される");
        ShouldSuppressAllMentions(interaction.Modified.AllowedMentions.Value);
    }

    private static void ShouldSuppressAllMentions(AllowedMentions? allowedMentions)
    {
        allowedMentions.Should().NotBeNull("既定（null）は本文を解釈して @everyone を発火させる");
        allowedMentions!.AllowedTypes.Should().Be(AllowedMentionTypes.None);
        allowedMentions.UserIds.Should().BeNullOrEmpty();
        allowedMentions.RoleIds.Should().BeNullOrEmpty();
    }

    // 応答の引数を捕捉するフェイク。**本クラスが実装する送信 API のうち、ラッパーが通す 3 つ以外は
    // すべて `NotSupportedException` を投げる**——ラッパーを迂回した新経路が増えたらテストが落ちる。
    private sealed class CapturingInteraction : IDiscordInteraction
    {
        public string? RespondText { get; private set; }
        public AllowedMentions? RespondAllowedMentions { get; private set; }
        public MessageComponent? RespondComponents { get; private set; }
        public bool RespondEphemeral { get; private set; }

        public string? FollowupText { get; private set; }
        public AllowedMentions? FollowupAllowedMentions { get; private set; }
        public bool FollowupEphemeral { get; private set; }

        public MessageProperties? Modified { get; private set; }

        public Task RespondAsync(
            string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
            AllowedMentions? allowedMentions = null, MessageComponent? components = null, Embed? embed = null,
            RequestOptions? options = null, PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
        {
            RespondText = text;
            RespondAllowedMentions = allowedMentions;
            RespondComponents = components;
            RespondEphemeral = ephemeral;
            return Task.CompletedTask;
        }

        public Task<IUserMessage> FollowupAsync(
            string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
            AllowedMentions? allowedMentions = null, MessageComponent? components = null, Embed? embed = null,
            RequestOptions? options = null, PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
        {
            FollowupText = text;
            FollowupAllowedMentions = allowedMentions;
            FollowupEphemeral = ephemeral;
            return Task.FromResult<IUserMessage>(null!);
        }

        public Task<IUserMessage> ModifyOriginalResponseAsync(
            Action<MessageProperties> func, RequestOptions? options = null)
        {
            var properties = new MessageProperties();
            func(properties);
            Modified = properties;
            return Task.FromResult<IUserMessage>(null!);
        }

        // ── ここから下はラッパーが通さない経路。呼ばれたら落とす（穴を静かに開けさせない）。
        private static NotSupportedException Unwrapped([System.Runtime.CompilerServices.CallerMemberName] string member = "")
            => new($"{member} は DiscordInteractionResponses を経由していない（IADR-0359）。");

        public Task RespondWithFilesAsync(
            IEnumerable<FileAttachment> attachments, string? text = null, Embed[]? embeds = null, bool isTTS = false,
            bool ephemeral = false, AllowedMentions? allowedMentions = null, MessageComponent? components = null,
            Embed? embed = null, RequestOptions? options = null, PollProperties? poll = null,
            MessageFlags flags = MessageFlags.None) => throw Unwrapped();

        public Task<IUserMessage> FollowupWithFilesAsync(
            IEnumerable<FileAttachment> attachments, string? text = null, Embed[]? embeds = null, bool isTTS = false,
            bool ephemeral = false, AllowedMentions? allowedMentions = null, MessageComponent? components = null,
            Embed? embed = null, RequestOptions? options = null, PollProperties? poll = null,
            MessageFlags flags = MessageFlags.None) => throw Unwrapped();

        public Task<IUserMessage> GetOriginalResponseAsync(RequestOptions? options = null) => throw Unwrapped();
        public Task DeleteOriginalResponseAsync(RequestOptions? options = null) => throw Unwrapped();
        public Task DeferAsync(bool ephemeral = false, RequestOptions? options = null) => Task.CompletedTask;
        public Task RespondWithModalAsync(Modal modal, RequestOptions? options = null) => Task.CompletedTask;
        public Task RespondWithPremiumRequiredAsync(RequestOptions? options = null) => throw Unwrapped();

        public ulong Id => 1UL;
        public InteractionType Type => InteractionType.ApplicationCommand;
        public IDiscordInteractionData Data => throw Unwrapped();
        public string Token => "token";
        public int Version => 1;
        public bool HasResponded => false;
        public IUser User => throw Unwrapped();
        public string UserLocale => "ja";
        public string GuildLocale => "ja";
        public bool IsDMInteraction => false;
        public ulong? ChannelId => 2UL;
        public ulong? GuildId => 3UL;
        public ulong ApplicationId => 4UL;
        public IReadOnlyCollection<IEntitlement> Entitlements => [];
        public IReadOnlyDictionary<ApplicationIntegrationType, ulong> IntegrationOwners =>
            new Dictionary<ApplicationIntegrationType, ulong>();
        public InteractionContextType? ContextType => InteractionContextType.Guild;
        public GuildPermissions Permissions => GuildPermissions.None;
        public ulong AttachmentSizeLimit => 0UL;
        public DateTimeOffset CreatedAt => DateTimeOffset.UnixEpoch;
    }
}
