using FluentAssertions;
using SysBot.Pokemon;
using SysBot.Pokemon.SV.BotRaid;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SysBot.Tests
{
    public class WebRaidRulesTests
    {
        [Theory]
        [InlineData("3739A70B", true)]
        [InlineData("0000abcd", true)]
        [InlineData("3739A70", false)]
        [InlineData("3739A70B1", false)]
        [InlineData("3739G70B", false)]
        [InlineData("", false)]
        public void SeedMustBeEightHexDigits(string seed, bool valid) =>
            WebRaidRules.IsValidSeed(seed).Should().Be(valid);

        [Theory]
        [InlineData(3, 3, true)]
        [InlineData(4, 3, false)]
        [InlineData(4, 4, true)]
        [InlineData(5, 4, false)]
        [InlineData(5, 5, true)]
        [InlineData(6, 5, false)]
        [InlineData(6, 6, true)]
        [InlineData(7, 6, true)]
        [InlineData(2, 6, false)]
        [InlineData(3, 2, false)]
        public void StarsMustFitStoryProgress(int stars, int progress, bool fits) =>
            WebRaidRules.StarsFitProgress(stars, progress).Should().Be(fits);

        [Theory]
        [InlineData(3, false, TeraCrystalType.Base)]
        [InlineData(5, true, TeraCrystalType.Distribution)]
        [InlineData(6, false, TeraCrystalType.Black)]
        [InlineData(7, true, TeraCrystalType.Might)]
        public void CrystalMatchesTheDiscordCommand(int stars, bool isEvent, TeraCrystalType expected) =>
            WebRaidRules.CrystalFor(stars, isEvent).Should().Be(expected);

        [Theory]
        [InlineData("Chien-Pao", "ChienPao", true)]
        [InlineData("Iron Valiant", "IronValiant", true)]
        [InlineData("ting-lu", "Ting-Lu", true)]
        [InlineData("Chien-Pao", "Chi-Yu", false)]
        public void EventNamesMatchHoweverTheyAreWritten(string a, string b, bool same)
        {
            EventSpeciesComparer.Instance.Equals(a, b).Should().Be(same);
            if (same)
                EventSpeciesComparer.Instance.GetHashCode(a).Should().Be(EventSpeciesComparer.Instance.GetHashCode(b));
        }

        [Theory]
        [InlineData("Charizard", "Charizard", true)]
        [InlineData("charizard ", "Charizard", true)]
        [InlineData("Mr. Mime", "MrMime", true)]
        [InlineData("Paldean Tauros", "Tauros", true)]
        [InlineData("Flabébé", "Flabebe", true)]
        [InlineData("Nidoran♀", "NidoranF", true)]
        [InlineData("Farfetch'd", "Farfetchd", true)]
        [InlineData("Charizard", "Gengar", false)]
        [InlineData("Nidoran♂", "NidoranF", false)]
        public void SpeciesNamesCompareLoosely(string typed, string generated, bool same) =>
            WebRaidRules.SameSpecies(typed, generated).Should().Be(same);
    }

    public class WebRaidRequestClientTests
    {
        private static readonly WebRaidBotInfo Bot = new("abcdef0123456789-0123456789ab", "Host", "v8.8.0", "Violet", "Paldea", ["Vaporeon"]);

        [Fact]
        public async Task ClaimReturnsTheRequest()
        {
            var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """
                {"ok":true,"request":{"id":42,"lease":"abc","seed":"3739A70B","stars":6,"storyProgress":6,"species":"Charizard","isEvent":false,"requesterName":"ash"}}
                """));
            var client = new WebRaidRequestClient(_ => { }, new HttpClient(handler));

            var (result, request) = await client.TryClaim(Bot, CancellationToken.None);

            result.Should().Be(WebClaimResult.Claimed);
            request!.Id.Should().Be(42);
            request.Seed.Should().Be("3739A70B");
            request.StoryProgress.Should().Be(6);
            request.Species.Should().Be("Charizard");

            var sent = JsonDocument.Parse(handler.Bodies[0]).RootElement;
            sent.GetProperty("game").GetString().Should().Be("Violet");
            sent.GetProperty("map").GetString().Should().Be("Paldea");
            sent.GetProperty("eventSpecies")[0].GetString().Should().Be("Vaporeon");
            handler.Paths[0].Should().EndWith("/bot/claim.php");
        }

        [Fact]
        public async Task EmptyQueueIsNothingWaiting()
        {
            var client = new WebRaidRequestClient(_ => { }, new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.OK, """{"ok":true,"request":null}"""))));
            (await client.TryClaim(Bot, CancellationToken.None)).Result.Should().Be(WebClaimResult.NothingWaiting);
        }

        [Fact]
        public async Task ForbiddenMeansHostNotApproved()
        {
            var client = new WebRaidRequestClient(_ => { }, new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.Forbidden, """{"ok":false}"""))));
            (await client.TryClaim(Bot, CancellationToken.None)).Result.Should().Be(WebClaimResult.NotApproved);
        }

        [Fact]
        public async Task ServerErrorsAndBadJsonAreUnreachable()
        {
            var down = new WebRaidRequestClient(_ => { }, new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.InternalServerError, ""))));
            (await down.TryClaim(Bot, CancellationToken.None)).Result.Should().Be(WebClaimResult.Unreachable);

            var html = new WebRaidRequestClient(_ => { }, new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.OK, "<html>maintenance</html>"))));
            (await html.TryClaim(Bot, CancellationToken.None)).Result.Should().Be(WebClaimResult.Unreachable);

            var offline = new WebRaidRequestClient(_ => { }, new HttpClient(new FakeHandler(_ => throw new HttpRequestException("no route"))));
            (await offline.TryClaim(Bot, CancellationToken.None)).Result.Should().Be(WebClaimResult.Unreachable);
        }

        [Fact]
        public async Task ReportsGoOutInOrderAndStopAfterTheEnd()
        {
            var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"ok":true}"""));
            var client = new WebRaidRequestClient(_ => { }, new HttpClient(handler));
            var request = new WebRaidRequest { Id = 7, Lease = "lease" };

            client.Report(request, "preparing", "loading");
            client.Report(request, "lobby", "open", "ABC123");
            client.Report(request, "battling");
            client.Report(request, "done");
            client.Report(request, "failed", "too late");
            await client.Flush(TimeSpan.FromSeconds(5));

            var statuses = handler.Bodies.ConvertAll(b => JsonDocument.Parse(b).RootElement.GetProperty("status").GetString());
            statuses.Should().Equal("preparing", "lobby", "battling", "done");

            var lobby = JsonDocument.Parse(handler.Bodies[1]).RootElement;
            lobby.GetProperty("code").GetString().Should().Be("ABC123");
            lobby.GetProperty("lease").GetString().Should().Be("lease");
            lobby.GetProperty("id").GetInt64().Should().Be(7);
            request.Finished.Should().BeTrue();
            request.IsHosted.Should().BeFalse();
        }

        [Fact]
        public async Task ConflictMeansTheRequestIsNoLongerOurs()
        {
            var handler = new FakeHandler(_ => Json(HttpStatusCode.Conflict, """{"ok":false}"""));
            var client = new WebRaidRequestClient(_ => { }, new HttpClient(handler));
            var request = new WebRaidRequest { Id = 8, Lease = "lease" };

            client.Report(request, "preparing");
            await client.Flush(TimeSpan.FromSeconds(5));
            request.Lost.Should().BeTrue();

            client.Report(request, "lobby", "", "ABC123");
            await client.Flush(TimeSpan.FromSeconds(5));
            handler.Bodies.Should().HaveCount(1);
        }

        [Fact]
        public async Task ConfirmWaitsForQueuedReportsAndSpotsALostRequest()
        {
            var handler = new FakeHandler(req => req.Content!.ReadAsStringAsync().Result.Contains("\"preparing\"") && req.Content!.ReadAsStringAsync().Result.Contains("\"id\":2")
                ? Json(HttpStatusCode.Conflict, """{"ok":false}""")
                : Json(HttpStatusCode.OK, """{"ok":true}"""));
            var client = new WebRaidRequestClient(_ => { }, new HttpClient(handler));
            var earlier = new WebRaidRequest { Id = 1, Lease = "a" };
            var mine = new WebRaidRequest { Id = 1, Lease = "a" };

            client.Report(earlier, "lobby", "", "AAA111");
            (await client.Confirm(mine, "preparing", "", CancellationToken.None)).Should().BeTrue();
            handler.Bodies.Should().HaveCount(2);
            JsonDocument.Parse(handler.Bodies[0]).RootElement.GetProperty("status").GetString().Should().Be("lobby");

            var taken = new WebRaidRequest { Id = 2, Lease = "b" };
            (await client.Confirm(taken, "preparing", "", CancellationToken.None)).Should().BeFalse();
            taken.Lost.Should().BeTrue();
        }

        [Fact]
        public async Task ConfirmIsUnknownWhenTheSiteIsDown()
        {
            var client = new WebRaidRequestClient(_ => { }, new HttpClient(new FakeHandler(_ => throw new HttpRequestException("down"))));
            var request = new WebRaidRequest { Id = 3, Lease = "c" };
            (await client.Confirm(request, "preparing", "", CancellationToken.None)).Should().BeNull();
            request.Lost.Should().BeFalse();
        }

        [Fact]
        public async Task ANetworkBlipIsRetried()
        {
            int calls = 0;
            var handler = new FakeHandler(_ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new HttpRequestException("blip");
                return Json(HttpStatusCode.OK, """{"ok":true}""");
            });
            var client = new WebRaidRequestClient(_ => { }, new HttpClient(handler));
            var request = new WebRaidRequest { Id = 9, Lease = "lease" };

            client.Report(request, "lobby", "", "XYZ789");
            await client.Flush(TimeSpan.FromSeconds(10));

            calls.Should().Be(2);
            request.Lost.Should().BeFalse();
            request.IsHosted.Should().BeTrue();
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            private readonly ConcurrentQueue<(string Path, string Body)> _seen = new();

            public List<string> Bodies => [.. System.Linq.Enumerable.Select(_seen, s => s.Body)];
            public List<string> Paths => [.. System.Linq.Enumerable.Select(_seen, s => s.Path)];

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
                _seen.Enqueue((request.RequestUri!.AbsolutePath, body));
                return respond(request);
            }
        }
    }
}
