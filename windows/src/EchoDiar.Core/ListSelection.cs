namespace EchoDiar.Core;

/// <summary>
/// Swift <c>ListSelection</c>: the selected rows of a list. Ctrl-click toggles one row, Shift-click adds the span from the
/// anchor (the last Ctrl-clicked row) to the clicked one, in list order.
/// </summary>
public sealed class ListSelection
{
    private readonly HashSet<Guid> _ids = [];

    public IReadOnlySet<Guid> Ids => _ids;
    /// <summary>Where a Shift-click span starts.</summary>
    public Guid? Anchor { get; private set; }

    public bool IsEmpty => _ids.Count == 0;

    /// <summary>Selects <paramref name="id"/>, or deselects it when selected, and makes it the anchor.</summary>
    public void Toggle(Guid id)
    {
        if (!_ids.Remove(id)) _ids.Add(id);
        Anchor = id;
    }

    /// <summary>
    /// Adds the inclusive span between the anchor and <paramref name="id"/> in <paramref name="order"/>, either direction;
    /// the anchor stays. Without an anchor in <paramref name="order"/>, selects <paramref name="id"/> and makes it the anchor.
    /// Does nothing when <paramref name="id"/> is not in <paramref name="order"/>.
    /// </summary>
    public void Extend(Guid id, IReadOnlyList<Guid> order)
    {
        var end = IndexOf(order, id);
        if (end < 0) return;
        var start = Anchor is { } anchor ? IndexOf(order, anchor) : -1;
        if (start < 0)
        {
            _ids.Add(id);
            Anchor = id;
            return;
        }
        for (var i = Math.Min(start, end); i <= Math.Max(start, end); i++) _ids.Add(order[i]);
    }

    /// <summary>Drops the ids, and the anchor, not in <paramref name="order"/>.</summary>
    public void Prune(IReadOnlyList<Guid> order)
    {
        var kept = order.ToHashSet();
        _ids.IntersectWith(kept);
        if (Anchor is { } anchor && !kept.Contains(anchor)) Anchor = null;
    }

    public void Clear()
    {
        _ids.Clear();
        Anchor = null;
    }

    private static int IndexOf(IReadOnlyList<Guid> order, Guid id)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == id) return i;
        }
        return -1;
    }
}
