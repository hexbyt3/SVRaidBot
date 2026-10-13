using FluentAssertions;
using SysBot.Pokemon;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SysBot.Tests;

public class RemoteControlAccessListTests
{
    [Fact]
    public void RemovingByIdTakesTheEntryOut()
    {
        var bans = new RemoteControlAccessList();
        bans.AddIfNew([new RemoteControlAccess { ID = 42, Name = "Cheater" }, new RemoteControlAccess { ID = 7, Name = "Other" }]);

        bans.RemoveAll(z => z.ID == 42).Should().Be(1);

        bans.Contains(42).Should().BeFalse();
        bans.Find(7)!.Name.Should().Be("Other");
        bans.Count.Should().Be(1);
    }

    [Fact]
    public void ConfigJsonKeepsItsShape()
    {
        var list = new RemoteControlAccessList { AllowIfEmpty = false };
        list.AddIfNew([new RemoteControlAccess { ID = 1, Name = "a" }]);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(list));
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["List", "AllowIfEmpty"]);

        var back = JsonSerializer.Deserialize<RemoteControlAccessList>(JsonSerializer.Serialize(list))!;
        back.AllowIfEmpty.Should().BeFalse();
        back.Contains(1).Should().BeTrue();
    }

    [Fact]
    public async Task EnumeratingWhileCommandsEditDoesNotThrow()
    {
        var list = new RemoteControlAccessList();
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            ulong id = 0;
            while (!stop.IsCancellationRequested)
            {
                list.AddIfNew([new RemoteControlAccess { ID = id++ }]);
                if (id % 50 == 0)
                    list.RemoveAll(_ => true);
            }
        });

        for (int i = 0; i < 2000; i++)
        {
            foreach (var _ in list) { }
            list.Contains(3);
        }

        stop.Cancel();
        await writer;
    }
}
