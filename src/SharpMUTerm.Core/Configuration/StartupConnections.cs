namespace SharpMUTerm.Core.Configuration;

/// <summary>
/// One connection the client opens at launch: a world, and the character to open it as — or no
/// character at all, which is what a bare host typed on the command line is (an anonymous session).
/// </summary>
/// <param name="World">The world to dial.</param>
/// <param name="Character">
/// The character whose trigger sets, login line, on-connect lines and log the session runs, or
/// null for an anonymous session.
/// </param>
public readonly record struct StartupConnection(WorldDefinition World, CharacterDefinition? Character);

/// <summary>
/// What the client connects when it starts, and nothing else — resolved here, in Core, because it is a
/// question about configuration rather than about windows, and because "what does a fresh launch dial"
/// is worth being able to assert without a terminal.
/// <para>
/// The rule has two arms and they do not blend. <b>A host on the command line wins outright</b>: typing
/// one is explicit intent for this run, and it is dialled whatever any character is marked — a client
/// launched at a particular server must not also drag in every world you happen to have marked.
/// Otherwise the answer is <b>every character with <see cref="CharacterDefinition.ConnectAtStartup"/>
/// set</b>, in configuration order, across every world.
/// </para>
/// <para>
/// <b>An empty result is a real answer, not a failure.</b> Until this existed the client dialled
/// <c>config.Worlds.FirstOrDefault()</c> as its first character on every launch — so a user with three
/// worlds got the first one's first character whether or not they wanted a connection at all, and there
/// was no way to say which and no way to say no. Both of those are now sayable, and "no" is the default.
/// </para>
/// </summary>
public static class StartupConnections
{
    /// <summary>
    /// The connections a launch should open, in the order their windows are created: the command line's
    /// world alone when one was typed, otherwise every marked character in configuration order (worlds
    /// in their configured order, characters in theirs), otherwise none.
    /// </summary>
    /// <param name="config">The loaded configuration.</param>
    /// <param name="commandLine">
    /// The world built from the command line, or null when no host was given. It is passed in rather
    /// than parsed here because argument parsing belongs to the entry point; what belongs here is the
    /// precedence.
    /// </param>
    public static IReadOnlyList<StartupConnection> Resolve(
        AppConfiguration config, WorldDefinition? commandLine = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (commandLine is not null)
        {
            // Its own first character when it has one (a saved world handed straight in), else
            // anonymous — which is what a host and port typed at a shell is.
            return new[] { new StartupConnection(commandLine, commandLine.Characters.FirstOrDefault()) };
        }

        var connections = new List<StartupConnection>();
        foreach (var world in config.Worlds)
        {
            foreach (var character in world.Characters)
            {
                if (character.ConnectAtStartup)
                {
                    connections.Add(new StartupConnection(world, character));
                }
            }
        }

        return connections;
    }
}
