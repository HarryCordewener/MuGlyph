namespace SharpMUTerm.Tui.Tests;

/// <summary>
/// Turns a rendered frame — cursor-addressed ANSI, the way the driver emits it — back into a grid of
/// cells, so a test can ask what is <em>on screen</em> at a row and column.
/// <para>
/// It exists because the questions worth asking about the input area are about painted cells. A caret
/// test written against <c>InputBarControl.GetLogicalCursorPosition</c> agrees with the code it is
/// testing and can disagree with the screen for as long as nobody looks; the frame cannot lie. Only the
/// characters are kept — colour is <c>RailRendererTests</c>' and <c>FocusIndicationTests</c>' business,
/// and both already read the SGR they need out of the same escapes.
/// </para>
/// </summary>
internal static class FrameGrid
{
    /// <summary>Decodes <paramref name="frame"/> into <paramref name="height"/> rows of text.</summary>
    internal static IReadOnlyList<string> Decode(string frame, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var cells = new char[height, width];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                cells[y, x] = ' ';
            }
        }

        int row = 0, column = 0, i = 0;
        while (i < frame.Length)
        {
            if (frame[i] == '\u001b' && i + 1 < frame.Length && frame[i + 1] == '[')
            {
                var end = i + 2;
                while (end < frame.Length && !char.IsLetter(frame[end]))
                {
                    end++;
                }

                // Only cursor addressing moves the write head; every other sequence here is styling.
                if (end < frame.Length && frame[end] == 'H')
                {
                    var parts = frame[(i + 2)..end].Split(';');
                    row = parts.Length > 0 && int.TryParse(parts[0], out var r) ? r - 1 : 0;
                    column = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c - 1 : 0;
                }

                i = end + 1;
                continue;
            }

            var ch = frame[i];
            i++;
            if (ch == '\n')
            {
                row++;
                column = 0;
                continue;
            }

            if (ch == '\r')
            {
                column = 0;
                continue;
            }

            if (row >= 0 && row < height && column >= 0 && column < width)
            {
                cells[row, column] = ch;
            }

            column++;
        }

        var lines = new List<string>(height);
        for (var y = 0; y < height; y++)
        {
            var line = new System.Text.StringBuilder(width);
            for (var x = 0; x < width; x++)
            {
                line.Append(cells[y, x]);
            }

            lines.Add(line.ToString());
        }

        return lines;
    }
}
