using MuClient.Core.Protocols;

namespace MuClient.Core.Tests.Protocols;

/// <summary>
/// Regression coverage for interaction (link/command) leakage across line boundaries in the
/// MXP and Pueblo parsers — an unclosed or nested interaction must not make later lines clickable.
/// </summary>
public class ParserBoundaryTests
{
    [Test]
    public async Task Mxp_BareSend_DoesNotLeakToNextLine()
    {
        var parser = new MxpParser();
        var lines = parser.Feed("<SEND HREF=\"go\">walk\nmore\n");
        var second = lines[1];
        await Assert.That(second.Spans.All(s => !s.IsInteractive)).IsTrue();
    }

    [Test]
    public async Task Mxp_NestedFormattingInsideSend_DoesNotLeakInteractionAcrossNewline()
    {
        var parser = new MxpParser();
        // Bold opened inside the SEND and closed on the next line must not resurrect the command.
        var lines = parser.Feed("<SEND HREF=\"go\"><B>walk\nmore</B> tail\n");
        var second = lines[1];
        await Assert.That(second.Spans.All(s => !s.IsInteractive)).IsTrue();
    }

    [Test]
    public async Task Pueblo_UnclosedAnchor_DoesNotLeakToNextLine()
    {
        var parser = new PuebloParser();
        var lines = parser.Feed("<A XCH_CMD=\"look\">here\nnext\n");
        var second = lines[1];
        await Assert.That(second.Spans.All(s => !s.IsInteractive)).IsTrue();
    }

    [Test]
    public async Task Pueblo_UnclosedAnchor_DoesNotLeakIntoFlushedPrompt()
    {
        var parser = new PuebloParser();
        parser.Feed("<A XCH_CMD=\"look\">here\n");
        parser.Feed("prompt> ");
        var prompt = parser.Flush();
        await Assert.That(prompt).IsNotNull();
        await Assert.That(prompt!.Spans.All(s => !s.IsInteractive)).IsTrue();
    }
}
