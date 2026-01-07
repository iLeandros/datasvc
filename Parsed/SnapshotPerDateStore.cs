using System.Collections.Generic;
using System.Linq;
using System;

namespace DataSvc.Parsed;

public sealed class SnapshotPerDateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<DateOnly, DataSnapshot> _byDate = new();

    public void Set(DateOnly date, DataSnapshot snap)
    {
        lock (_gate) _byDate[date] = snap;
    }

    public bool TryGet(DateOnly date, out DataSnapshot snap)
    {
        lock (_gate) return _byDate.TryGetValue(date, out snap!);
    }

    public void PruneTo(HashSet<DateOnly> keep)
    {
        lock (_gate)
        {
            var toRemove = _byDate.Keys.Where(d => !keep.Contains(d)).ToList();
            foreach (var d in toRemove) _byDate.Remove(d);
        }
    }
}
