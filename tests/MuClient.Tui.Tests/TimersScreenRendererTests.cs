using MuClient.Core.Automation;
using MuClient.Core.Configuration;
using MuClient.Tui;

namespace MuClient.Tui.Tests;

public class TimersScreenRendererTests
{
    private static List<TriggerSet> Scene() => new()
    {
        new TriggerSet
        {
            Name = "Comms",
            Timers = new List<TimerDefinition>
            {
                new()
                {
                    Name = "heartbeat",
                    IntervalSeconds = 30,
                    Command = "idle",
                    OneShot = false,
                    Enabled = true,
                },
                new()
                {
                    Name = "reminder",
                    IntervalSeconds = 5.5,
                    Command = "say brb",
                    OneShot = true,
                    Enabled = false,
                },
            },
        },
        new TriggerSet
        {
            Name = "Trade",
            Timers = new List<TimerDefinition>
            {
                new()
                {
                    Name = "restock",
                    IntervalSeconds = 120,
                    Command = "check market",
                    OneShot = false,
                    Enabled = true,
                },
            },
        },
    };

    [Test]
    public async Task Render_TitleAndHeaderPresent()
    {
        var lines = TimersScreenRenderer.Render(Scene(), 0);
        await Assert.That(lines[0]).Contains("Timers");
        await Assert.That(lines[0]).Contains("F6");
        await Assert.That(lines.Any(l => l.Contains("on  name  every  → command"))).IsTrue();
    }

    [Test]
    public async Task Render_FlattensAcrossSetsAndShowsOwningSet()
    {
        var lines = TimersScreenRenderer.Render(Scene(), 0);
        await Assert.That(lines.Any(l => l.Contains("heartbeat") && l.Contains("▪ Comms"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("restock") && l.Contains("▪ Trade"))).IsTrue();
    }

    [Test]
    public async Task Render_RecurringVsOneShotSchedule()
    {
        var lines = TimersScreenRenderer.Render(Scene(), 0);
        var heartbeat = lines.Single(l => l.Contains("[bold]heartbeat[/]"));
        var reminder = lines.Single(l => l.Contains("[bold]reminder[/]"));
        await Assert.That(heartbeat).Contains("every 30s");
        await Assert.That(reminder).Contains("once after 5.5s");
    }

    [Test]
    public async Task Render_EnabledAndDisabledMarkersDiffer()
    {
        var lines = TimersScreenRenderer.Render(Scene(), 0);
        var heartbeat = lines.Single(l => l.Contains("[bold]heartbeat[/]"));
        var reminder = lines.Single(l => l.Contains("[bold]reminder[/]"));
        await Assert.That(heartbeat).Contains("✓");
        await Assert.That(reminder).Contains("[dim]·[/]");
    }

    [Test]
    public async Task Render_SelectedTimerShowsEditorFields()
    {
        var lines = TimersScreenRenderer.Render(Scene(), 0);
        var heartbeat = lines.Single(l => l.Contains("[bold]heartbeat[/]"));
        await Assert.That(heartbeat).Contains("▸");
        await Assert.That(lines.Any(l => l.Contains("interval (seconds)"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("command"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("one-shot"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("[x][/] enabled"))).IsTrue();
    }

    [Test]
    public async Task Render_OneShotCheckboxReflectsSelectedTimer()
    {
        var lines = TimersScreenRenderer.Render(Scene(), 1);
        await Assert.That(lines.Any(l => l.Contains("[x][/] one-shot"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("[dim][ ] enabled[/]"))).IsTrue();
    }

    [Test]
    public async Task Render_FooterPresent()
    {
        var lines = TimersScreenRenderer.Render(Scene(), 0);
        await Assert.That(lines[^1]).Contains("Cancel");
        await Assert.That(lines[^1]).Contains("Save");
    }

    [Test]
    public async Task Render_EmptyShowsNoTimers()
    {
        var lines = TimersScreenRenderer.Render(Array.Empty<TriggerSet>(), 0);
        await Assert.That(lines.Any(l => l.Contains("no timers"))).IsTrue();
    }

    [Test]
    public async Task Render_EscapesMarkupBrackets()
    {
        var sets = new List<TriggerSet>
        {
            new()
            {
                Name = "Comms",
                Timers = new List<TimerDefinition>
                {
                    new()
                    {
                        Name = "od[d]",
                        IntervalSeconds = 1,
                        Command = "say [hi]",
                    },
                },
            },
        };

        var lines = TimersScreenRenderer.Render(sets, 0);
        await Assert.That(lines.Any(l => l.Contains("od[[d]]"))).IsTrue();
        await Assert.That(lines.Any(l => l.Contains("say [[hi]]"))).IsTrue();
    }
}
