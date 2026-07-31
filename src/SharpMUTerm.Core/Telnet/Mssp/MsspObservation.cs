namespace SharpMUTerm.Core.Telnet.Mssp;

/// <summary>
/// What one server endpoint has told us about itself, and when — the unit the INFO screen renders and
/// the unit <see cref="MsspCache"/> stores.
/// <para>
/// <b>Two timestamps, because there are three states and two would only distinguish two.</b>
/// <see cref="ConnectedAt"/> is the last time a session reached this endpoint at all;
/// <see cref="ObservedAt"/> is when the report in <see cref="Report"/> arrived, and is null exactly
/// when no report ever has. So:
/// </para>
/// <list type="bullet">
/// <item>no observation for an endpoint — <em>never connected</em>;</item>
/// <item>an observation with a null <see cref="Report"/> — <em>connected, and the server published no
/// MSSP</em>, which on a MUSH is the ordinary case and not a fault;</item>
/// <item>an observation with a report — <em>here is what it said, as of <see cref="ObservedAt"/></em>.</item>
/// </list>
/// <para>
/// Collapsing the first two into "no data" is the easy mistake and it makes the screen untrustworthy:
/// a client that says the same thing about a server it has never spoken to and a server that answered
/// is a client whose screen you have to go and verify elsewhere.
/// </para>
/// <para>
/// The two are kept apart rather than one being derived from the other because they drift on purpose.
/// A server that published MSSP in January and stopped in March leaves a March
/// <see cref="ConnectedAt"/> beside a January <see cref="ObservedAt"/>, and the screen dates the
/// <em>report</em> — anything else would present a stale player count as current, which is the whole
/// reason a capture time is recorded at all.
/// </para>
/// </summary>
/// <param name="Endpoint">The normalised <c>host:port</c> this describes — see <see cref="MsspCache.Key"/>.</param>
/// <param name="ConnectedAt">When a session last reached this endpoint.</param>
/// <param name="Report">The last MSSP report it published, or null when it has never published one.</param>
/// <param name="ObservedAt">When <paramref name="Report"/> arrived; null exactly when it is null.</param>
public sealed record MsspObservation(
    string Endpoint,
    DateTimeOffset ConnectedAt,
    MsspData? Report,
    DateTimeOffset? ObservedAt)
{
    /// <summary>
    /// True when this endpoint has been reached and published nothing — the state the screen has to
    /// word as "optional, and normally absent" rather than as an error.
    /// </summary>
    public bool PublishesNothing => Report is null;
}
