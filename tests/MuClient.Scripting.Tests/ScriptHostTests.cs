using MoonSharp.Interpreter;

namespace MuClient.Scripting.Tests;

public class ScriptHostTests
{
    private static (ScriptHost Host, FakeScriptWorld World) NewHost()
    {
        var world = new FakeScriptWorld();
        return (new ScriptHost(world), world);
    }

    [Test]
    public async Task WorldSend_CapturesCommand()
    {
        var (host, world) = NewHost();
        host.Execute("world.send('look')");
        await Assert.That(world.Sent).HasSingleItem();
        await Assert.That(world.Sent[0]).IsEqualTo("look");
    }

    [Test]
    public async Task WorldPrint_CapturesText()
    {
        var (host, world) = NewHost();
        host.Execute("world.print('hello')");
        await Assert.That(world.Printed).HasSingleItem();
        await Assert.That(world.Printed[0]).IsEqualTo("hello");
    }

    [Test]
    public async Task OutputPrint_CapturesText()
    {
        var (host, world) = NewHost();
        host.Execute("output.print('hi')");
        await Assert.That(world.Printed).Contains("hi");
    }

    [Test]
    public async Task OutputPrintStyled_PrintsPlain()
    {
        var (host, world) = NewHost();
        host.Execute("output.printStyled('red text', { fg = 'red' })");
        await Assert.That(world.Printed).HasSingleItem();
        await Assert.That(world.Printed[0]).IsEqualTo("red text");
    }

    [Test]
    public async Task WorldName_ExposedToLua()
    {
        var (host, world) = NewHost();
        world.WorldName = "Aardwolf";
        // Re-create so the injected value reflects the change.
        var host2 = new ScriptHost(world);
        var value = host2.Evaluate("return world.name");
        await Assert.That(value.String).IsEqualTo("Aardwolf");
    }

    [Test]
    public async Task GlobalPrint_RoutesToWorld()
    {
        var (host, world) = NewHost();
        host.Execute("print('one', 'two')");
        await Assert.That(world.Printed).HasSingleItem();
        await Assert.That(world.Printed[0]).IsEqualTo("one\ttwo");
    }

    [Test]
    public async Task LogInfo_PrintsWithPrefix()
    {
        var (host, world) = NewHost();
        host.Execute("log.info('ready')");
        await Assert.That(world.Printed[0]).IsEqualTo("[info] ready");
    }

    [Test]
    public async Task Evaluate_ReturnsValue()
    {
        var (host, _) = NewHost();
        var value = host.Evaluate("return 2 + 3");
        await Assert.That(value.Number).IsEqualTo(5d);
    }

    [Test]
    public async Task TriggerAdd_RegistersWithWorld()
    {
        var (host, world) = NewHost();
        host.Execute("trigger.add('(\\\\w+) waves', function(all, who) world.send('wave '..who) end)");
        await Assert.That(world.Triggers).HasSingleItem();
        await Assert.That(world.Triggers[0].Pattern).IsEqualTo("(\\w+) waves");
    }

    [Test]
    public async Task TriggerDispatch_InvokesCallbackWithCaptures()
    {
        var (host, world) = NewHost();
        host.Execute("trigger.add('(\\\\w+) waves', function(all, who) world.send('wave '..who) end)");
        host.DispatchTrigger(world.LastTriggerId, "Bob waves", new[] { "Bob" });
        await Assert.That(world.Sent).HasSingleItem();
        await Assert.That(world.Sent[0]).IsEqualTo("wave Bob");
    }

    [Test]
    public async Task TriggerDispatch_PassesWholeMatchFirst()
    {
        var (host, world) = NewHost();
        host.Execute("trigger.add('.*', function(all, a, b) world.send(all..'|'..a..'|'..b) end)");
        host.DispatchTrigger(world.LastTriggerId, "Bob gives sword", new[] { "Bob", "sword" });
        await Assert.That(world.Sent[0]).IsEqualTo("Bob gives sword|Bob|sword");
    }

    [Test]
    public async Task TriggerDispatch_UnknownIdIsNoOp()
    {
        var (host, world) = NewHost();
        host.DispatchTrigger("nope#1", "x", Array.Empty<string>());
        await Assert.That(world.Sent).Count().IsEqualTo(0);
    }

    [Test]
    public async Task TriggerRuntimeError_RaisedViaErrorEvent()
    {
        var (host, world) = NewHost();
        ScriptException? captured = null;
        host.Error += (_, e) => captured = e;
        host.Execute("trigger.add('x', function() error('boom') end)");
        host.DispatchTrigger(world.LastTriggerId, "x", Array.Empty<string>());
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Message).Contains("boom");
    }

    [Test]
    public async Task AliasAdd_String_RegistersSubstitution()
    {
        var (host, world) = NewHost();
        host.Execute("alias.add('^gg$', 'get gold\\ndrop gold')");
        await Assert.That(world.Aliases).HasSingleItem();
        await Assert.That(world.Aliases[0].Substitution).IsEqualTo("get gold\ndrop gold");
        await Assert.That(world.Aliases[0].CallbackId).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task AliasAdd_Function_RegistersCallback()
    {
        var (host, world) = NewHost();
        host.Execute("alias.add('^hi$', function(all) world.send('wave') end)");
        await Assert.That(world.Aliases).HasSingleItem();
        await Assert.That(world.Aliases[0].CallbackId).IsNotNull();
        await Assert.That(world.Aliases[0].CallbackId).IsNotEqualTo(string.Empty);
    }

    [Test]
    public async Task AliasDispatch_InvokesCallback()
    {
        var (host, world) = NewHost();
        host.Execute("alias.add('greet (\\\\w+)', function(all, who) world.send('hello '..who) end)");
        host.DispatchAlias(world.LastAliasId, "greet Sue", new[] { "Sue" });
        await Assert.That(world.Sent[0]).IsEqualTo("hello Sue");
    }

    [Test]
    public async Task TimerAfter_SchedulesOnWorld()
    {
        var (host, world) = NewHost();
        host.Execute("timer.after(10, function() world.send('tick') end)");
        await Assert.That(world.Timers).HasSingleItem();
        await Assert.That(world.Timers[0].Recurring).IsFalse();
        await Assert.That(world.Timers[0].Interval).IsEqualTo(TimeSpan.FromMilliseconds(10));
    }

    [Test]
    public async Task TimerAfter_FiringInvokesCallback()
    {
        var (host, world) = NewHost();
        host.Execute("timer.after(10, function() world.send('tick') end)");
        world.Timers[0].Fire();
        await Assert.That(world.Sent).HasSingleItem();
        await Assert.That(world.Sent[0]).IsEqualTo("tick");
    }

    [Test]
    public async Task TimerEvery_SchedulesRecurring()
    {
        var (host, world) = NewHost();
        host.Execute("timer.every(1000, function() world.send('beat') end)");
        await Assert.That(world.Timers[0].Recurring).IsTrue();
        world.Timers[0].Fire();
        world.Timers[0].Fire();
        await Assert.That(world.Sent).Count().IsEqualTo(2);
    }

    [Test]
    public async Task TimerHandle_CancelStopsTimer()
    {
        var (host, world) = NewHost();
        host.Execute("local h = timer.every(1000, function() end); h:cancel()");
        await Assert.That(world.Timers[0].Cancelled).IsTrue();
    }

    [Test]
    public async Task TimerCallbackError_RaisedViaErrorEvent()
    {
        var (host, world) = NewHost();
        ScriptException? captured = null;
        host.Error += (_, e) => captured = e;
        host.Execute("timer.after(5, function() error('late') end)");
        world.Timers[0].Fire();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Message).Contains("late");
    }

    [Test]
    public async Task GmcpOn_ExactPackageDispatches()
    {
        var (host, world) = NewHost();
        host.Execute("gmcp.on('Char.Vitals', function(json) world.send('got:'..json) end)");
        host.DispatchGmcp("Char.Vitals", "{\"hp\":10}");
        await Assert.That(world.Sent).HasSingleItem();
        await Assert.That(world.Sent[0]).IsEqualTo("got:{\"hp\":10}");
    }

    [Test]
    public async Task GmcpOn_PrefixMatchesSubPackage()
    {
        var (host, world) = NewHost();
        host.Execute("gmcp.on('Char', function(json) world.send('char') end)");
        host.DispatchGmcp("Char.Vitals", "{}");
        await Assert.That(world.Sent).HasSingleItem();
    }

    [Test]
    public async Task GmcpOn_NonMatchingPackageIgnored()
    {
        var (host, world) = NewHost();
        host.Execute("gmcp.on('Char.Vitals', function(json) world.send('x') end)");
        host.DispatchGmcp("Room.Info", "{}");
        await Assert.That(world.Sent).Count().IsEqualTo(0);
    }

    [Test]
    public async Task SyntaxError_ThrowsScriptException()
    {
        var (host, _) = NewHost();
        await Assert.That(() => host.Execute("this is = = not lua")).Throws<ScriptException>();
    }

    [Test]
    public async Task RuntimeError_ThrowsScriptException()
    {
        var (host, _) = NewHost();
        await Assert.That(() => host.Execute("error('explicit failure')")).Throws<ScriptException>();
    }

    [Test]
    public async Task RuntimeError_MessageIsClean()
    {
        var (host, _) = NewHost();
        try
        {
            host.Execute("error('explicit failure')");
        }
        catch (ScriptException ex)
        {
            await Assert.That(ex.Message).Contains("explicit failure");
            return;
        }

        Assert.Fail("Expected ScriptException.");
    }

    [Test]
    public async Task NestedCaptures_MultipleTriggers()
    {
        var (host, world) = NewHost();
        host.Execute("trigger.add('a', function() world.send('A') end)");
        host.Execute("trigger.add('b', function() world.send('B') end)");
        host.DispatchTrigger(world.Triggers[0].CallbackId, "a", Array.Empty<string>());
        host.DispatchTrigger(world.Triggers[1].CallbackId, "b", Array.Empty<string>());
        await Assert.That(world.Sent).Count().IsEqualTo(2);
        await Assert.That(world.Sent[0]).IsEqualTo("A");
        await Assert.That(world.Sent[1]).IsEqualTo("B");
    }
}
