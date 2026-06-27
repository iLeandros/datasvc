// Parsed/SnapshotPerDateStore.cs
using System;
using System.Linq;
using System.Collections.Generic;

using DataSvc.Models;
using DataSvc.ModelHelperCalls;
using DataSvc.VIPHandler;
using DataSvc.Auth; // AuthController + SessionAuthHandler namespace
using DataSvc.MainHelpers; // MainHelpers
using DataSvc.Likes; // MainHelpers
using DataSvc.Services; // Services
using DataSvc.Analyzer;
using DataSvc.ClubElo;
using DataSvc.MainHelpers;
using DataSvc.Parsed;
using DataSvc.Details;
using DataSvc.LiveScores;

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
