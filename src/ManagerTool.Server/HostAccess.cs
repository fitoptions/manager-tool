namespace ManagerTool.Server;

/// <summary>
/// The single answer to "may this admin see this PC?". Every hub method and data endpoint that
/// names a host goes through here, so a blocked workstation is absent from the host list, the
/// live view, activity history, reports, recordings and alerts alike — not merely hidden in the UI.
///
/// Owners are never restricted: they are the accounts that hand out access in the first place.
/// </summary>
public sealed class HostAccess
{
    private readonly HostAccessStore _store;
    private readonly TokenService _tokens;

    public HostAccess(HostAccessStore store, TokenService tokens)
    {
        _store = store;
        _tokens = tokens;
    }

    public bool CanSee(string username, string hostId) =>
        _tokens.IsOwner(username) || !_store.DeniedFor(username).Contains(hostId);

    /// <summary>Host ids to exclude from an unfiltered query. Empty for owners.</summary>
    public IReadOnlyCollection<string> DeniedFor(string username) =>
        _tokens.IsOwner(username) ? Array.Empty<string>() : _store.DeniedFor(username).ToArray();

    public IEnumerable<T> Visible<T>(string username, IEnumerable<T> items, Func<T, string> hostIdOf)
    {
        if (_tokens.IsOwner(username))
            return items;
        var denied = _store.DeniedFor(username);
        return items.Where(i => !denied.Contains(hostIdOf(i)));
    }
}
