using SharpMUTerm.Core.Configuration;
using SharpConsoleUI;
using SharpConsoleUI.Builders;
using SharpConsoleUI.Controls;
using SharpConsoleUI.Layout;

namespace SharpMUTerm.Tui;

/// <summary>
/// Composes the F5 Worlds &amp; Characters screen from real panels (grids) rather than one merged
/// markup blob: a header band, a WORLDS-list / detail body, and — for the selected character — a
/// full-width editing pane whose left panel holds the character form (left-aligned) and whose right
/// panel holds the assigned trigger sets (right-aligned), over its own elevated background. A footer
/// action bar is pinned to the last row. The markup for each panel comes from the pure
/// <see cref="WorldsScreenRenderer"/> so the content stays unit-tested; this only lays it out.
/// </summary>
internal static class WorldsScreenView
{
    public static IWindowControl Build(
        IReadOnlyList<WorldDefinition> worlds,
        IReadOnlyList<TriggerSet> triggerSets,
        int selectedWorld,
        int selectedCharacter,
        int width,
        ScreenFocus? focus = null,
        string fkey = WorldsScreenRenderer.FKey,
        int selectedSet = 0,
        int height = 0,
        bool info = false)
    {
        // Both panes end in button rows, so a raw cursor can point past its list; resolving once here
        // keeps every block of the screen agreeing on which world and character are selected.
        (selectedWorld, selectedCharacter) =
            WorldsScreenRenderer.Resolve(worlds, selectedWorld, selectedCharacter);
        var accent = WorldsScreenRenderer.AccentFor(worlds, selectedWorld);

        // The model is rebuilt here only to derive the header's hints, so the INFO row's presence is all
        // it needs of the action — a no-op stands in for it, and the delegate stays with the session.
        var model = WorldsScreenRenderer.Model(
            worlds, triggerSets, selectedWorld, selectedCharacter, selectedSet, info ? _ => { } : null);
        var header = ScreenChrome.Band(
            WorldsScreenRenderer.HeaderLine(width, model, focus, fkey), ScreenPalette.HeaderBg);
        var footer = ScreenChrome.Band(
            WorldsScreenRenderer.FooterLine(worlds, selectedWorld, selectedCharacter, accent, width, focus),
            ScreenPalette.FooterBg);

        // How many rows the WORLDS/detail body has once the header, the character band, the gap under
        // it and the action bar have taken theirs. The detail column is built against that number, so a
        // pane taller than its slot compacts and then scrolls rather than losing its tail silently.
        var band = WorldsScreenRenderer.HasCharacter(worlds, selectedWorld, selectedCharacter)
            ? Math.Max(
                WorldsScreenRenderer.FormColumn(
                    worlds[selectedWorld].Characters[selectedCharacter], accent, focus, selectedCharacter).Count,
                WorldsScreenRenderer.TriggersColumn(
                    worlds[selectedWorld].Characters[selectedCharacter],
                    triggerSets,
                    accent,
                    focus,
                    selectedSet,
                    worlds).Count) + 2
            : 1;
        var rows = height <= 0 ? 0 : Math.Max(1, height - 1 - band);

        // Body: WORLDS list │ detail, as two real columns.
        var worldsCol = ScreenChrome.Stretch(new MarkupControl(
            WorldsScreenRenderer.WorldsColumn(worlds, selectedWorld, focus, rows, info).ToList()));
        var detailCol = ScreenChrome.Stretch(new MarkupControl(
            WorldsScreenRenderer
                .DetailColumn(worlds, triggerSets, selectedWorld, selectedCharacter, accent, focus, rows)
                .ToList()));
        var body = Controls.HorizontalGrid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill)
            .Column(c => c.Width(30).Add(worldsCol))
            .Column(c => c.Width(1).Add(ScreenChrome.VerticalRule()))
            .Column(c => c.Width(1).Add(ScreenChrome.Filler()))
            .Column(c => c.Flex(1).Add(detailCol))
            .Build();

        var root = Controls.Grid()
            .WithAlignment(HorizontalAlignment.Stretch)
            .WithVerticalAlignment(VerticalAlignment.Fill);

        if (WorldsScreenRenderer.HasCharacter(worlds, selectedWorld, selectedCharacter))
        {
            var character = worlds[selectedWorld].Characters[selectedCharacter];
            var form = WorldsScreenRenderer.FormColumn(character, accent, focus, selectedCharacter).ToList();
            var triggers = WorldsScreenRenderer
                .TriggersColumn(character, triggerSets, accent, focus, selectedSet, worlds)
                .ToList();
            var editHeight = Math.Max(form.Count, triggers.Count);

            // Form panel on the left; the trigger checklist pushed to the right by a flex spacer so its
            // block sits on the right while its checkboxes stay left-aligned (an Auto column hugs it, not
            // per-row right-justify, which would ragged the left edge). The edit grid's own background
            // gives the full-width elevated band behind both.
            var formPanel = new MarkupControl(ScreenChrome.Indent(form))
            {
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var trigPanel = new MarkupControl(triggers) { HorizontalAlignment = HorizontalAlignment.Left };
            var edit = Controls.HorizontalGrid()
                .WithAlignment(HorizontalAlignment.Stretch)
                .WithVerticalAlignment(VerticalAlignment.Fill)
                .Column(c => c.Width(48).Add(formPanel))
                .Column(c => c.Flex(1).Add(ScreenChrome.Filler()))
                .Column(c => c.Add(trigPanel))
                .Column(c => c.Width(2).Add(ScreenChrome.Filler()))
                .Build();
            edit.BackgroundColor = new Color(ScreenPalette.EditBg);

            // A one-row panel-background gap sits between the editing pane and the footer so the footer
            // reads as a separate bar, not the last row of the character setup section.
            root.Rows(
                    GridLength.Cells(1), GridLength.Star(1), GridLength.Cells(editHeight),
                    GridLength.Cells(1), GridLength.Cells(1))
                .Columns(GridLength.Star(1));
            root.Place(header, 0, 0, 1, 1);
            root.Place(body, 1, 0, 1, 1);
            root.Place(edit, 2, 0, 1, 1);
            root.Place(footer, 4, 0, 1, 1);
        }
        else
        {
            root.Rows(GridLength.Cells(1), GridLength.Star(1), GridLength.Cells(1)).Columns(GridLength.Star(1));
            root.Place(header, 0, 0, 1, 1);
            root.Place(body, 1, 0, 1, 1);
            root.Place(footer, 2, 0, 1, 1);
        }

        return root.Build();
    }
}
