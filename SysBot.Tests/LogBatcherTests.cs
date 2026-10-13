using FluentAssertions;
using SysBot.Pokemon.Discord.Helpers;
using System.Linq;
using Xunit;

namespace SysBot.Tests;

public class LogBatcherTests
{
    [Fact]
    public void ShortLinesShareOneMessage()
    {
        var messages = LogBatcher.Pack(["a", "b", "c"], 1900, 5);

        messages.Should().Equal("a\nb\nc");
    }

    [Fact]
    public void EveryMessageStaysUnderTheCap()
    {
        var lines = Enumerable.Range(0, 60).Select(i => new string('x', 100) + i).ToList();

        var messages = LogBatcher.Pack(lines, 1900, 50);

        messages.Should().OnlyContain(m => m.Length <= 1900);
        string.Join("\n", messages).Split('\n').Should().Equal(lines);
    }

    [Fact]
    public void AnOverlongLineIsCut()
    {
        var messages = LogBatcher.Pack([new string('x', 5000)], 1900, 5);

        messages.Should().ContainSingle().Which.Length.Should().Be(1900);
    }

    [Fact]
    public void AFloodIsSummarizedInsteadOfQueued()
    {
        var lines = Enumerable.Range(0, 1000).Select(i => new string('y', 500)).ToList();

        var messages = LogBatcher.Pack(lines, 1900, 5);

        messages.Should().HaveCount(5);
        messages[^1].Should().Contain("skipped");
        messages.Should().OnlyContain(m => m.Length <= 1900);
    }
}
