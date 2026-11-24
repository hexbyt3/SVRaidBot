using FluentAssertions;
using SysBot.Pokemon.SV.BotRaid.Helpers;
using System;
using Xunit;

namespace SysBot.Tests
{
    public class UpdateNoteTests
    {
        private static readonly DateTime Now = new(2026, 11, 24, 23, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void SurvivesTheTripThroughTheFile()
        {
            var note = new UpdateNote("v8.13.0", Now, true);
            UpdateNote.Parse(note.ToString()).Should().Be(note);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("v8.13.0")]
        [InlineData("v8.13.0|soon|1")]
        [InlineData("v8.13.0|2026-11-24T23:00:00Z|yes")]
        public void DamagedNotesAreIgnored(string? text) => UpdateNote.Parse(text).Should().BeNull();

        [Fact]
        public void OnlyTheSameReleaseWaitsBeforeAnotherTry()
        {
            var note = new UpdateNote("v8.13.0", Now, true);
            note.BlocksRetry("v8.13.0", Now.AddHours(1)).Should().BeTrue();
            note.BlocksRetry("v8.13.0", Now.AddHours(7)).Should().BeFalse();
            note.BlocksRetry("v8.14.0", Now.AddHours(1)).Should().BeFalse();
            note.BlocksRetry("v8.13.0", Now.AddHours(-1)).Should().BeFalse("the clock was set back");
        }

        [Fact]
        public void HostingOnlyComesBackRightAfterTheUpdate()
        {
            new UpdateNote("v8.13.0", Now, true).ShouldResume(Now.AddMinutes(2)).Should().BeTrue();
            new UpdateNote("v8.13.0", Now, true).ShouldResume(Now.AddHours(2)).Should().BeFalse();
            new UpdateNote("v8.13.0", Now, false).ShouldResume(Now.AddMinutes(2)).Should().BeFalse("it was not hosting");
        }
    }
}
