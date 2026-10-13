using Discord;
using Discord.Net;
using FluentAssertions;
using SysBot.Pokemon.Discord;
using System;
using System.Net.WebSockets;
using Xunit;

namespace SysBot.Tests;

public class GatewayIntentPolicyTests
{
    [Theory]
    [InlineData(ApplicationFlags.GatewayMessageContent)]
    [InlineData(ApplicationFlags.GatewayMessageContentLimited)]
    public void KeepsMessageContentWhenTheAppHasIt(ApplicationFlags flags)
    {
        GatewayIntentPolicy.IntentsFor(flags).Should().HaveFlag(GatewayIntents.MessageContent);
    }

    [Fact]
    public void DropsOnlyMessageContentWhenTheAppLostIt()
    {
        var intents = GatewayIntentPolicy.IntentsFor(ApplicationFlags.GatewayPresence);

        intents.Should().Be(GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.GuildMessageReactions | GatewayIntents.DirectMessages);
    }

    [Fact]
    public void NeverAsksForPrivilegedMembersOrPresence()
    {
        // The raid-code ✅ DMs need reactions; nothing needs the member list.
        GatewayIntentPolicy.Full.Should().HaveFlag(GatewayIntents.GuildMessageReactions);
        GatewayIntentPolicy.Full.Should().NotHaveFlag(GatewayIntents.GuildMembers);
        GatewayIntentPolicy.Full.Should().NotHaveFlag(GatewayIntents.GuildPresences);
    }

    [Fact]
    public void DisallowedIntentCloseIsFatal()
    {
        var ex = new WebSocketException("WebSocket connection was closed",
            new WebSocketClosedException(4014, "Disallowed intent(s)."));

        GatewayIntentPolicy.GetCloseCode(ex).Should().Be(4014);
        GatewayIntentPolicy.IsFatalClose(ex).Should().BeTrue();
    }

    [Theory]
    [InlineData(4004)]
    [InlineData(4010)]
    [InlineData(4013)]
    public void OtherNonReconnectableClosesAreFatal(int code)
    {
        GatewayIntentPolicy.IsFatalClose(new WebSocketClosedException(code, null)).Should().BeTrue();
    }

    [Theory]
    [InlineData(4000)]
    [InlineData(4003)]
    [InlineData(4009)]
    public void ResumableClosesAreRetried(int code)
    {
        var ex = new WebSocketException("closed", new WebSocketClosedException(code, null));

        GatewayIntentPolicy.IsFatalClose(ex).Should().BeFalse();
    }

    [Fact]
    public void ServerRequestedReconnectIsRetried()
    {
        GatewayIntentPolicy.IsFatalClose(new Exception("Server requested a reconnect")).Should().BeFalse();
        GatewayIntentPolicy.IsFatalClose(null).Should().BeFalse();
    }
}
