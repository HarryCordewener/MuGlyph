namespace MuClient.Scripting.Tests;

public class ReloadTests
{
    [Test]
    public async Task LoadFile_ExecutesAndRegisters()
    {
        var world = new FakeScriptWorld();
        var host = new ScriptHost(world);
        var path = Path.Combine(Path.GetTempPath(), $"muglyph_{Guid.NewGuid():N}.lua");
        await File.WriteAllTextAsync(path, "trigger.add('aaa', function() world.send('one') end)");
        try
        {
            host.LoadFile(path);
            await Assert.That(world.Triggers).HasSingleItem();
            await Assert.That(world.Triggers[0].Pattern).IsEqualTo("aaa");
            await Assert.That(host.LoadedFiles).HasSingleItem();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Reload_ReRegistersFromChangedFile()
    {
        var world = new FakeScriptWorld();
        var host = new ScriptHost(world);
        var path = Path.Combine(Path.GetTempPath(), $"muglyph_{Guid.NewGuid():N}.lua");
        try
        {
            await File.WriteAllTextAsync(path, "trigger.add('aaa', function() world.send('v1') end)");
            host.LoadFile(path);
            await Assert.That(world.Triggers[0].Pattern).IsEqualTo("aaa");

            await File.WriteAllTextAsync(path, "trigger.add('bbb', function() world.send('v2') end)");
            host.Reload();

            // A new trigger for the v2 pattern is registered.
            await Assert.That(world.Triggers.Exists(t => t.Pattern == "bbb")).IsTrue();

            // Dispatching the newest registration runs the v2 body.
            host.DispatchTrigger(world.LastTriggerId, "bbb", Array.Empty<string>());
            await Assert.That(world.Sent).Contains("v2");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Reload_DropsOldCallbacks()
    {
        var world = new FakeScriptWorld();
        var host = new ScriptHost(world);
        var path = Path.Combine(Path.GetTempPath(), $"muglyph_{Guid.NewGuid():N}.lua");
        try
        {
            await File.WriteAllTextAsync(path, "trigger.add('aaa', function() world.send('v1') end)");
            host.LoadFile(path);
            var oldId = world.LastTriggerId;

            await File.WriteAllTextAsync(path, "trigger.add('bbb', function() world.send('v2') end)");
            host.Reload();

            // The old callback id no longer resolves after reload; dispatching it is a no-op.
            host.DispatchTrigger(oldId, "aaa", Array.Empty<string>());
            await Assert.That(world.Sent).DoesNotContain("v1");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task Reload_DisposesOldTimers()
    {
        var world = new FakeScriptWorld();
        var host = new ScriptHost(world);
        var path = Path.Combine(Path.GetTempPath(), $"muglyph_{Guid.NewGuid():N}.lua");
        try
        {
            await File.WriteAllTextAsync(path, "timer.every(1000, function() end)");
            host.LoadFile(path);
            var firstTimer = world.Timers[0];

            host.Reload();
            await Assert.That(firstTimer.Cancelled).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
