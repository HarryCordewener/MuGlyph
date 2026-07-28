using MoonSharp.Interpreter;

namespace SharpMUTerm.Scripting.Tests;

public class SandboxTests
{
    private static ScriptHost NewHost() => new(new FakeScriptWorld());

    [Test]
    public async Task Io_IsNil()
    {
        var host = NewHost();
        await Assert.That(host.Evaluate("return io == nil").Boolean).IsTrue();
    }

    [Test]
    public async Task OsExecute_IsNil()
    {
        var host = NewHost();
        await Assert.That(host.Evaluate("return os.execute == nil").Boolean).IsTrue();
    }

    [Test]
    public async Task OsExit_IsNil()
    {
        var host = NewHost();
        await Assert.That(host.Evaluate("return os.exit == nil").Boolean).IsTrue();
    }

    [Test]
    public async Task Require_IsNil()
    {
        var host = NewHost();
        await Assert.That(host.Evaluate("return require == nil").Boolean).IsTrue();
    }

    [Test]
    public async Task Dofile_IsNil()
    {
        var host = NewHost();
        await Assert.That(host.Evaluate("return dofile == nil").Boolean).IsTrue();
    }

    [Test]
    public async Task Loadfile_IsNil()
    {
        var host = NewHost();
        await Assert.That(host.Evaluate("return loadfile == nil").Boolean).IsTrue();
    }

    [Test]
    public async Task OsTime_IsAvailable()
    {
        var host = NewHost();
        await Assert.That(host.Evaluate("return os.time ~= nil").Boolean).IsTrue();
        await Assert.That(host.Evaluate("return type(os.time())").String).IsEqualTo("number");
    }

    [Test]
    public async Task StringAndMath_AreAvailable()
    {
        var host = NewHost();
        await Assert.That(host.Evaluate("return string.upper('hi')").String).IsEqualTo("HI");
        await Assert.That(host.Evaluate("return math.max(3, 7)").Number).IsEqualTo(7d);
    }

    [Test]
    public async Task TableModule_IsAvailable()
    {
        var host = NewHost();
        await Assert.That(host.Evaluate("local t = {1,2,3}; return #t").Number).IsEqualTo(3d);
    }

    [Test]
    public async Task OpeningAFile_FailsInSandbox()
    {
        var host = NewHost();
        // io is unavailable, so any attempt to reference it as a table errors at runtime.
        await Assert.That(() => host.Execute("local f = io.open('/etc/passwd', 'r')"))
            .Throws<ScriptException>();
    }
}
