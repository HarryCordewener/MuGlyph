namespace SharpMUTerm.Core.Configuration;

/// <summary>
/// One character's opt-in to a <see cref="TriggerSet"/>, and where it sits in that character's own
/// list. The position matters as much as the name: the character's order is what decides which set
/// wins a conflict (see <see cref="AppConfiguration.ResolveTriggerSets"/>), so anything that takes a
/// reference out has to be able to put it back exactly where it was rather than on the end.
/// </summary>
/// <param name="Character">The character that opted in.</param>
/// <param name="Index">The position of the name inside <see cref="CharacterDefinition.TriggerSets"/>.</param>
public readonly record struct TriggerSetReference(CharacterDefinition Character, int Index);

/// <summary>
/// The links between a <see cref="TriggerSet"/> and the characters that opted into it. A character
/// selects its automation by <em>name</em> (<see cref="CharacterDefinition.TriggerSets"/> is a list of
/// strings), so renaming or removing a set silently orphans every one of those references unless
/// something goes and fixes them — which is what this does.
/// <para>
/// The operations are split into find / act / put back rather than being one "rename" call, because
/// the settings screens undo by replaying: they capture the references first, mutate, and keep the
/// captured list as the way home. Anything that removes references walks them <em>backwards</em> and
/// anything that restores them walks forwards, so the indices stay meaningful while a character's list
/// is shifting under them.
/// </para>
/// </summary>
public static class TriggerSetReferences
{
    /// <summary>
    /// Every character assignment naming <paramref name="name"/>, in world → character → position
    /// order. Matching is case-insensitive because <see cref="AppConfiguration.ResolveTriggerSets"/>
    /// resolves that way: a reference that would resolve to this set is a reference to it, whatever
    /// case it was typed in.
    /// </summary>
    public static List<TriggerSetReference> Find(IReadOnlyList<WorldDefinition> worlds, string name)
    {
        ArgumentNullException.ThrowIfNull(worlds);
        ArgumentNullException.ThrowIfNull(name);

        var found = new List<TriggerSetReference>();
        foreach (var world in worlds)
        {
            foreach (var character in world.Characters)
            {
                for (var i = 0; i < character.TriggerSets.Count; i++)
                {
                    if (string.Equals(character.TriggerSets[i], name, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(new TriggerSetReference(character, i));
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Points every captured reference at <paramref name="name"/>, in place. This is what makes a set
    /// rename safe: the assignment keeps its position in the character's list, so the priority order a
    /// user built by hand survives a typo being fixed.
    /// </summary>
    public static void Rename(IReadOnlyList<TriggerSetReference> references, string name)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(name);

        foreach (var reference in references)
        {
            if (reference.Index >= 0 && reference.Index < reference.Character.TriggerSets.Count)
            {
                reference.Character.TriggerSets[reference.Index] = name;
            }
        }
    }

    /// <summary>
    /// Removes every captured reference, so a deleted set leaves no character pointing at a set that
    /// no longer exists. Walked newest-index-first, because removing the second entry of a list
    /// renumbers the third.
    /// </summary>
    public static void Detach(IReadOnlyList<TriggerSetReference> references)
    {
        ArgumentNullException.ThrowIfNull(references);

        for (var i = references.Count - 1; i >= 0; i--)
        {
            var reference = references[i];
            if (reference.Index >= 0 && reference.Index < reference.Character.TriggerSets.Count)
            {
                reference.Character.TriggerSets.RemoveAt(reference.Index);
            }
        }
    }

    /// <summary>
    /// The inverse of <see cref="Detach"/>: puts <paramref name="name"/> back at each captured
    /// position, walked forwards so each insertion restores the list the next index was measured
    /// against. Undoing a set deletion has to come through here — restoring the set alone would leave
    /// every character that used it unassigned, which is a second edit nobody asked for.
    /// </summary>
    public static void Reattach(IReadOnlyList<TriggerSetReference> references, string name)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(name);

        foreach (var reference in references)
        {
            var assigned = reference.Character.TriggerSets;
            assigned.Insert(Math.Clamp(reference.Index, 0, assigned.Count), name);
        }
    }
}
