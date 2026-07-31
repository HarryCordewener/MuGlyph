using SharpMUTerm.Core.Configuration;
using SharpMUTerm.Core.Workspaces;

namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Variations on <see cref="DemoScene"/> that more than one suite needs, built once here.
/// <para>
/// A fixture copied into three suites is three things that can drift, and the drift is silent: each copy
/// still builds a workspace and each suite still goes green, while they have quietly stopped agreeing
/// about what they are testing against.
/// </para>
/// </summary>
internal static class DemoConfigs
{
    /// <summary>
    /// The demo scene with its capture window taken out, leaving one window in one pane — the state where
    /// the rail's chord column has nothing to say and must therefore not be drawn, and where every ⌥ digit
    /// past the first is out of range.
    /// <para>
    /// The window is removed from <em>both</em> halves of the saved session: the registry
    /// (<see cref="WorkspaceState.Windows"/>) and the pane's tab list. Taking it out of one leaves a
    /// workspace that is internally inconsistent rather than smaller — a pane referencing a window that
    /// does not exist, or a window no pane holds, which is the <c>closed</c> state and not this one.
    /// </para>
    /// </summary>
    internal static AppConfiguration SingleWindow()
    {
        var config = DemoScene.Build();
        config.LastSession!.Windows.RemoveAll(w => w.Kind == WindowKind.Spawn);
        config.LastSession.Root.Tabs.RemoveAll(t => t.StartsWith("spawn:", StringComparison.Ordinal));
        return config;
    }
}
