using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// <b>Does this chord arrive at all?</b> — asked of the framework's own byte-level input parser, not of
/// a memory of what terminals send.
/// <para>
/// This client has lost features to chords that collapse onto their ASCII bytes (Ctrl+H, Ctrl+I,
/// Ctrl+⏎, Shift+⏎ — see <c>CLAUDE.md</c> and <see cref="AdvertisedKeyHonestyTests"/>), and an
/// unreachable binding is worse than no binding: it reads as a broken feature rather than an absent
/// one. Before <c>Ctrl+Shift+arrow</c> could be given a meaning, something had to prove that a terminal
/// emitting <c>CSI 1;6 C</c> produces a <see cref="ConsoleKey.RightArrow"/> carrying <em>both</em>
/// modifier bits — distinctly from the plain arrow, from <c>Shift</c> alone and from the <c>Ctrl</c>
/// alone that already moves pane selection.
/// </para>
/// <para>
/// <b>Why the reflection.</b> <c>SharpConsoleUI.Drivers.Input.AnsiInputParser</c> is <c>internal</c>,
/// its <c>Parse</c> takes a <see cref="ReadOnlySpan{T}"/> (so <c>MethodInfo.Invoke</c> cannot call it),
/// and <c>NetConsoleDriver</c> constructs it as a local inside <c>Start()</c> — the same wall
/// <c>CLAUDE.md</c> documents under "the input stack cannot be extended from here". A
/// <see cref="DynamicMethod"/> with <c>skipVisibility</c> is the only way to ask the real decoder the
/// real question. If a future SharpConsoleUI renames or reshapes it this test fails loudly, which is
/// the correct outcome: the claim it is guarding would have become unverified.
/// </para>
/// </summary>
public class TerminalKeyArrivalTests
{
    /// <summary>The four arrows as a terminal spells them, with the CSI final byte for each.</summary>
    private static readonly (ConsoleKey Key, char Final)[] Arrows =
    {
        (ConsoleKey.UpArrow, 'A'),
        (ConsoleKey.DownArrow, 'B'),
        (ConsoleKey.RightArrow, 'C'),
        (ConsoleKey.LeftArrow, 'D'),
    };

    /// <summary>
    /// <c>CSI 1;6 &lt;final&gt;</c> — what xterm, kitty, WezTerm, Ghostty, foot and VTE all write for
    /// Ctrl+Shift+arrow — decodes to that arrow with Control and Shift set and Alt clear.
    /// </summary>
    [Test]
    public async Task CtrlShiftArrowArrivesAsAnArrowCarryingBothModifiers()
    {
        foreach (var (key, final) in Arrows)
        {
            var decoded = Decode($"\x1b[1;6{final}");
            await Assert.That(decoded.Count)
                .IsEqualTo(1)
                .Because($"ESC [ 1;6 {final} is one key event");

            var info = decoded[0];
            await Assert.That(info.Key).IsEqualTo(key);
            await Assert.That(info.Modifiers.HasFlag(ConsoleModifiers.Control))
                .IsTrue()
                .Because($"Ctrl+Shift+{key} must report Control");
            await Assert.That(info.Modifiers.HasFlag(ConsoleModifiers.Shift))
                .IsTrue()
                .Because($"Ctrl+Shift+{key} must report Shift");
            await Assert.That(info.Modifiers.HasFlag(ConsoleModifiers.Alt)).IsFalse();
        }
    }

    /// <summary>
    /// The chord is <em>distinct</em> from its neighbours, which is the property that matters: Ctrl
    /// alone already moves pane selection and Shift alone already scrolls, so a modifier code the parser
    /// flattened would take a key away from a live feature rather than adding one.
    /// </summary>
    [Test]
    public async Task TheModifierCodesTheNeighbouringChordsUseAreAllDifferent()
    {
        // CSI 1;<n> — 2 shift, 3 alt, 5 ctrl, 6 ctrl+shift (xterm's 1 + bitmask).
        var plain = Decode("\x1b[C").Single();
        var shift = Decode("\x1b[1;2C").Single();
        var alt = Decode("\x1b[1;3C").Single();
        var ctrl = Decode("\x1b[1;5C").Single();
        var both = Decode("\x1b[1;6C").Single();

        await Assert.That(plain.Modifiers).IsEqualTo((ConsoleModifiers)0);
        await Assert.That(shift.Modifiers).IsEqualTo(ConsoleModifiers.Shift);
        await Assert.That(alt.Modifiers).IsEqualTo(ConsoleModifiers.Alt);
        await Assert.That(ctrl.Modifiers).IsEqualTo(ConsoleModifiers.Control);
        await Assert.That(both.Modifiers)
            .IsEqualTo(ConsoleModifiers.Control | ConsoleModifiers.Shift)
            .Because("Ctrl+Shift+→ must not collapse onto Ctrl+→, which moves pane selection");
    }

    // --- reaching the internal parser ------------------------------------------------------------

    private static readonly Type ParserType =
        typeof(SharpConsoleUI.ConsoleWindowSystem).Assembly
            .GetType("SharpConsoleUI.Drivers.Input.AnsiInputParser", throwOnError: true)!;

    private static readonly Func<object, byte[], int, object> ParseInvoker = BuildInvoker();

    /// <summary>Runs a byte string through a fresh parser and returns the key events it produced.</summary>
    private static List<ConsoleKeyInfo> Decode(string sequence)
    {
        var bytes = Encoding.ASCII.GetBytes(sequence);
        var parser = Activator.CreateInstance(ParserType, nonPublic: true)!;
        var events = (System.Collections.IEnumerable)ParseInvoker(parser, bytes, bytes.Length);

        var keys = new List<ConsoleKeyInfo>();
        foreach (var evt in events)
        {
            if (evt.GetType().GetProperty("KeyInfo")?.GetValue(evt) is ConsoleKeyInfo info)
            {
                keys.Add(info);
            }
        }

        return keys;
    }

    /// <summary>
    /// Builds a call stub for <c>AnsiInputParser.Parse(ReadOnlySpan&lt;byte&gt;, int)</c>. A span cannot
    /// be boxed, so <c>MethodInfo.Invoke</c> is not an option; this emits the implicit
    /// <c>byte[] → ReadOnlySpan&lt;byte&gt;</c> conversion and the call.
    /// </summary>
    private static Func<object, byte[], int, object> BuildInvoker()
    {
        var parse = ParserType.GetMethod(
            "Parse",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("AnsiInputParser.Parse is gone — the arrival claim is unverified.");

        var toSpan = typeof(ReadOnlySpan<byte>).GetMethod(
            "op_Implicit",
            BindingFlags.Public | BindingFlags.Static,
            new[] { typeof(byte[]) })!;

        var stub = new DynamicMethod(
            "InvokeAnsiParse",
            typeof(object),
            new[] { typeof(object), typeof(byte[]), typeof(int) },
            typeof(TerminalKeyArrivalTests).Module,
            skipVisibility: true);

        var il = stub.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, ParserType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, toSpan);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, parse);
        il.Emit(OpCodes.Ret);

        return stub.CreateDelegate<Func<object, byte[], int, object>>();
    }
}
