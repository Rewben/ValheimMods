using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using UnityEngine;
using ValheimVehicles.Controllers;
using ValheimVehicles.Shared.Constants;

namespace ValheimVehicles.Fork;

/// <summary>One canonical portal record, one portal-sector membership, no ordinary-sector membership.</summary>
public static class PortalRegistry
{
  private static ZDOMan? world;
  private static readonly HashSet<ZDOID> seen = new();
  private static float nextDiagnostics;
  private static readonly Dictionary<ZDOID, ZoneSystem.SectorIndex> registeredSectors = new();
  public static int PreventedCreations { get; internal set; }
  public static int CorrectedMemberships { get; private set; }

  public static bool IsPortal(ZDO? zdo) => zdo != null && zdo.IsValid() && zdo.m_uid != ZDOID.None &&
    Game.instance != null && Game.instance.PortalPrefabHash.Contains(zdo.GetPrefab());
  public static bool IsShipPortal(ZDO? zdo) => IsPortal(zdo) && zdo!.GetInt(VehicleZdoVars.MBParentId) != 0;

  public static void Reset()
  {
    world = null;
    registeredSectors.Clear();
    PreventedCreations = CorrectedMemberships = 0;
    nextDiagnostics = 0;
    PortalCommands.Reset();
  }

  private static void EnsureWorld()
  {
    var manager = ZDOMan.instance;
    if (manager == null || ReferenceEquals(world, manager)) return;
    world = manager;
    registeredSectors.Clear();
    // A bounded once-per-world pass also repairs sessions loaded with old registry corruption.
    foreach (var bucket in manager.m_objectsBySector)
      if (bucket != null) CorrectedMemberships += bucket.RemoveAll(z => IsPortal(z));
    foreach (var key in manager.m_portalObjects.Keys.ToList())
    {
      var bucket = manager.m_portalObjects[key];
      CorrectedMemberships += bucket.RemoveAll(z => !IsPortal(z) ||
        !ReferenceEquals(manager.GetZDO(z.m_uid), z) || z.GetSectorIndex() != key);
      var unique = ForkRules.UniqueValid(bucket, IsPortal, z => z.m_uid);
      CorrectedMemberships += bucket.Count - unique.Count;
      if (unique.Count == 0) manager.m_portalObjects.Remove(key);
      else manager.m_portalObjects[key] = unique;
    }
    foreach (var zdo in manager.m_objectsByID.Values.Where(IsPortal).ToList()) Canonicalize(zdo);
    manager.SetDirtyPortals();
  }

  public static void Update(ZDO zdo, Vector3 position, Quaternion? rotation = null)
  {
    if (!IsPortal(zdo) || ZDOMan.instance == null || !ReferenceEquals(ZDOMan.instance.GetZDO(zdo.m_uid), zdo)) return;
    EnsureWorld();
    var oldPositionSector = zdo.GetSectorIndex();
    zdo.SetPosition(position);
    if (rotation.HasValue) zdo.SetRotation(rotation.Value);
    // Portals are intentionally excluded from vanilla ZDO.SetSector. Never call AddToSector here.
    if (oldPositionSector != zdo.GetSectorIndex())
      ZDOMan.instance.m_objectsBySector[oldPositionSector.Sector]?.RemoveAll(z => ReferenceEquals(z, zdo));
    Canonicalize(zdo);
    if (RebalancePolicy.PortalDiagnostics != null && RebalancePolicy.PortalDiagnostics.Value && Time.realtimeSinceStartup >= nextDiagnostics)
    {
      nextDiagnostics = Time.realtimeSinceStartup + 30;
      PortalCommands.LogCounters();
    }
  }

  private static void Canonicalize(ZDO zdo)
  {
    var manager = ZDOMan.instance;
    var sector = zdo.GetSectorIndex();
    var unchanged = registeredSectors.TryGetValue(zdo.m_uid, out var registered) && registered == sector;
    manager.m_objectsBySector[sector.Sector]?.RemoveAll(z => ReferenceEquals(z, zdo));
    if (unchanged && manager.m_portalObjects.TryGetValue(sector, out var existing) &&
        existing.Count(z => ReferenceEquals(z, zdo)) == 1) return;
    foreach (var key in manager.m_portalObjects.Keys.ToList())
    {
      var bucket = manager.m_portalObjects[key];
      CorrectedMemberships += bucket.RemoveAll(z => ReferenceEquals(z, zdo) ||
        (!IsPortal(z)) || z.m_uid == zdo.m_uid);
      if (bucket.Count == 0) manager.m_portalObjects.Remove(key);
    }
    if (!manager.m_portalObjects.TryGetValue(sector, out var target))
      manager.m_portalObjects[sector] = target = new List<ZDO>();
    target.Add(zdo);
    registeredSectors[zdo.m_uid] = sector;
    manager.SetDirtyPortals();
    if (ZNet.instance != null && ZNet.instance.IsServer() && !unchanged) manager.ZDOSectorInvalidated(zdo);
  }

  public static void BeforeReset(ZDO zdo)
  {
    if (zdo.SaveClone || world == null || !ReferenceEquals(world, ZDOMan.instance)) return;
    var id = zdo.m_uid;
    if (!IsPortal(zdo) && !registeredSectors.ContainsKey(id)) return;
    foreach (var key in world.m_portalObjects.Keys.ToList())
    {
      var list = world.m_portalObjects[key];
      list.RemoveAll(z => ReferenceEquals(z, zdo));
      if (list.Count == 0) world.m_portalObjects.Remove(key);
    }
    world.m_objectsBySector[zdo.GetSectorIndex().Sector]?.RemoveAll(z => ReferenceEquals(z, zdo));
    if (registeredSectors.TryGetValue(id, out var old))
      world.m_objectsBySector[old.Sector]?.RemoveAll(z => ReferenceEquals(z, zdo));
    registeredSectors.Remove(id);
    world.SetDirtyPortals();
  }

  public static void Normalize() { world = null; EnsureWorld(); }

  public static void Track(VehiclePiecesController controller, ZNetView view)
  {
    if (view == null || !IsPortal(view.GetZDO())) return;
    if (!controller.m_portals.Any(v => v != null && v.IsValid() && v.GetZDO().m_uid == view.GetZDO().m_uid))
      controller.m_portals.Add(view);
  }

  public static void Prune(VehiclePiecesController controller)
  {
    seen.Clear();
    var write = 0;
    for (var read = 0; read < controller.m_portals.Count; read++)
    {
      var view = controller.m_portals[read];
      if (view != null && view.IsValid() && IsPortal(view.GetZDO()) && seen.Add(view.GetZDO().m_uid))
        controller.m_portals[write++] = view;
    }
    if (write < controller.m_portals.Count) controller.m_portals.RemoveRange(write, controller.m_portals.Count - write);
  }

  public static bool TargetPortalManages(ZDO zdo)
  {
    if (!Chainloader.PluginInfos.TryGetValue("org.bepinex.plugins.targetportal", out var plugin)) return false;
    var config = plugin.Instance.Config;
    // TargetPortal's setting is an enum, not a string ConfigEntry.
    foreach (var entry in config)
      if (entry.Key.Section == "1 - General" && entry.Key.Key == "Limit to vanilla portals")
        return entry.Value.BoxedValue.ToString() != "On" || IsVanilla(zdo);
    return true;
  }
  private static bool IsVanilla(ZDO zdo) => zdo.GetPrefab() == "portal_wood".GetStableHashCode() || zdo.GetPrefab() == "portal_stone".GetStableHashCode();
  public static List<ZDO> VanillaPairingCandidates(ZDOMan manager) => manager.GetPortalList()
    .Where(z => IsPortal(z) && !TargetPortalManages(z)).GroupBy(z => z.m_uid).Select(g => g.First()).ToList();
}
