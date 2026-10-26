using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using ValheimVehicles.Shared.Constants;

namespace ValheimVehicles.Fork;

/// <summary>A read-only, frozen selection of colocated saved ship-portal records.</summary>
public sealed class PortalDuplicatePlan
{
  public const float Tolerance = 0.001f;

  public sealed class Snapshot
  {
    public readonly ZDO Record;
    public readonly ZDOID Id;
    public readonly int Ship, Prefab, Frame;
    public readonly Vector3 Local;
    public readonly bool HasLocal;

    public Snapshot(ZDO record)
    {
      Record = record;
      Id = record.m_uid;
      Ship = record.GetInt(VehicleZdoVars.MBParentId);
      Prefab = record.GetPrefab();
      Frame = record.GetInt(VehicleZdoVars.SwivelParentId);
      HasLocal = record.GetVec3(VehicleZdoVars.MBPositionHash, out Local) && Finite(Local);
    }

    public bool Matches()
    {
      if (!ReferenceEquals(ZDOMan.instance.GetZDO(Id), Record) || !PortalRegistry.IsShipPortal(Record)) return false;
      var now = new Snapshot(Record);
      return Ship == now.Ship && Prefab == now.Prefab && Frame == now.Frame && HasLocal == now.HasLocal &&
        (!HasLocal || (Local.x.Equals(now.Local.x) && Local.y.Equals(now.Local.y) && Local.z.Equals(now.Local.z)));
    }

    public string PositionText => HasLocal
      ? string.Format(CultureInfo.InvariantCulture, "({0:R},{1:R},{2:R})", Local.x, Local.y, Local.z) : "missing/invalid";
  }

  public readonly Dictionary<ZDOID, Snapshot> Records;
  public readonly Dictionary<ZDOID, ZDOID> KeepForRemoval = new();
  public readonly List<Snapshot> Removals = new();
  public int Groups { get; private set; }
  public int Skipped { get; private set; }

  public PortalDuplicatePlan(IEnumerable<ZDO> records)
  {
    Records = records.Select(z => new Snapshot(z)).ToDictionary(s => s.Id);
    var cells = new Dictionary<(int, int, int, long, long, long), List<Snapshot>>();
    var keepersWithCopies = new HashSet<ZDOID>();
    // Prefer an existing name, then a deterministic ID. Only keepers enter the
    // spatial index, so a large stack does not cause an all-pairs comparison.
    foreach (var item in Records.Values.OrderByDescending(s => !string.IsNullOrWhiteSpace(s.Record.GetString(ZDOVars.s_tag)))
               .ThenBy(s => s.Id.ToString(), StringComparer.Ordinal))
    {
      if (!item.HasLocal || !Cell(item.Local, out var x, out var y, out var z)) { Skipped++; continue; }
      Snapshot? keeper = null;
      for (var dx = -1; dx <= 1 && keeper == null; dx++)
      for (var dy = -1; dy <= 1 && keeper == null; dy++)
      for (var dz = -1; dz <= 1 && keeper == null; dz++)
        if (cells.TryGetValue((item.Ship, item.Prefab, item.Frame, x + dx, y + dy, z + dz), out var candidates))
          keeper = candidates.FirstOrDefault(s => DistanceSquared(s.Local, item.Local) <= (double)Tolerance * Tolerance);
      if (keeper != null)
      {
        KeepForRemoval.Add(item.Id, keeper.Id);
        Removals.Add(item);
        keepersWithCopies.Add(keeper.Id);
      }
      else
      {
        var key = (item.Ship, item.Prefab, item.Frame, x, y, z);
        if (!cells.TryGetValue(key, out var bucket)) cells[key] = bucket = new List<Snapshot>();
        bucket.Add(item);
      }
    }
    Groups = keepersWithCopies.Count;
  }

  public string Summary => $"duplicateGroups={Groups} duplicateRecords={Removals.Count} skippedLocal={Skipped} tolerance={Tolerance.ToString(CultureInfo.InvariantCulture)}m";

  public string Describe(ZDOID id)
  {
    var snapshot = Records[id];
    return $"local={snapshot.PositionText} " + (KeepForRemoval.TryGetValue(id, out var keeper) ? $"duplicateOf={keeper}" : "keep");
  }

  private static bool Finite(Vector3 p) => !float.IsNaN(p.x) && !float.IsInfinity(p.x) &&
    !float.IsNaN(p.y) && !float.IsInfinity(p.y) && !float.IsNaN(p.z) && !float.IsInfinity(p.z);

  private static bool Cell(Vector3 p, out long x, out long y, out long z)
  {
    var a = Math.Floor((double)p.x / Tolerance);
    var b = Math.Floor((double)p.y / Tolerance);
    var c = Math.Floor((double)p.z / Tolerance);
    x = y = z = 0;
    if (Math.Abs(a) > 9e18 || Math.Abs(b) > 9e18 || Math.Abs(c) > 9e18) return false;
    x = (long)a; y = (long)b; z = (long)c;
    return true;
  }

  private static double DistanceSquared(Vector3 a, Vector3 b)
  {
    var x = (double)a.x - b.x; var y = (double)a.y - b.y; var z = (double)a.z - b.z;
    return x * x + y * y + z * z;
  }
}
