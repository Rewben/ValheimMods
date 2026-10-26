using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Jotunn;
using Logger = Jotunn.Logger;
using UnityEngine;
using ValheimVehicles.Shared.Constants;

namespace ValheimVehicles.Fork;

public static class PortalCommands
{
  private const string Request = "RewbenRAFT.PortalRequest";
  private const string Response = "RewbenRAFT.PortalResponse";
  private const string LocalRepair = "RewbenRAFT.PortalLocalRepair";
  private sealed class Audit
  {
    internal long Owner;
    internal DateTime Expires;
    internal Dictionary<ZDOID, ZDO> Records = new();
  }
  private static readonly Dictionary<string, Audit> audits = new();
  public static void Reset() => audits.Clear();
  public static void LogCounters() => Print("Rewben portal counters " + Describe(null, out _).Split('\n')[0]);

  private static void Print(string message)
  {
    Console.instance?.Print(message);
    Logger.LogInfo(message);
  }

  public static void Register()
  {
    Reset();
    ZRoutedRpc.instance.Register<string>(Request, (sender, command) =>
    {
      if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
      var peer = ZNet.instance.GetPeer(sender);
      var host = peer?.m_socket?.GetHostName();
      if (host != null && host.StartsWith("Steam_")) host = host.Substring(6);
      if (host == null || !(ZNet.instance.m_adminList.Contains(host) ||
          ZNet.instance.m_adminList.Contains("Steam_" + host)))
      {
        ZRoutedRpc.instance.InvokeRoutedRPC(sender, Response, "Portal tools require server admin access.");
        return;
      }
      try { ZRoutedRpc.instance.InvokeRoutedRPC(sender, Response, Execute(command.Split(' '), sender)); }
      catch (Exception e) { ZRoutedRpc.instance.InvokeRoutedRPC(sender, Response, "Portal operation failed: " + e.Message); }
    });
    ZRoutedRpc.instance.Register<string>(Response, (sender, text) =>
    {
      if (sender == ZNet.instance.GetServerPeer()?.m_uid) Print(text);
    });
    ZRoutedRpc.instance.Register<string>(LocalRepair, (sender, ids) =>
    {
      if (sender != ZNet.instance.GetServerPeer()?.m_uid) return;
      var allowed = new HashSet<string>(ids.Split(','));
      RepairInstances(allowed);
      PortalRegistry.Normalize();
    });
  }

  public static void Run(string[] args)
  {
    if (ZNet.instance == null) return;
    if (ZNet.instance.IsServer())
    {
      if (Player.m_localPlayer != null && !ForkAccess.IsAdmin()) { Print("Admin access required."); return; }
      try { Print(Execute(args, ZDOMan.GetSessionID())); }
      catch (Exception e) { Print("Portal operation failed: " + e.Message); }
      return;
    }
    if (!ForkAccess.IsAdmin()) { Print("Admin access required."); return; }
    if (args.Length > 0 && args[0] == "audit") Print("CLIENT " + Describe(null, out _));
    ZRoutedRpc.instance.InvokeRoutedRPC(ZNet.instance.GetServerPeer().m_uid, Request, string.Join(" ", args));
  }

  private static string Describe(int? ship, out Dictionary<ZDOID, ZDO> records)
  {
    records = ZDOMan.instance.m_objectsByID.Values.Where(z => PortalRegistry.IsShipPortal(z) &&
      (!ship.HasValue || z.GetInt(VehicleZdoVars.MBParentId) == ship.Value)).ToDictionary(z => z.m_uid);
    var selected = records;
    var views = Resources.FindObjectsOfTypeAll<TeleportWorld>().Select(p => p.m_nview)
      .Where(v => v != null && v.IsValid()).ToList();
    var live = views.GroupBy(v => v.GetZDO()).ToDictionary(g => g.Key, g => g.ToList());
    var registered = ZDOMan.instance.m_portalObjects.Values.SelectMany(l => l).Where(z => z != null)
      .GroupBy(z => z).ToDictionary(g => g.Key, g => g.Count());
    var wrongSectors = ZDOMan.instance.m_portalObjects.SelectMany(p => p.Value.Where(z => z != null && z.GetSectorIndex() != p.Key))
      .GroupBy(z => z).ToDictionary(g => g.Key, g => g.Count());
    var ordinaryEntries = ZDOMan.instance.m_objectsBySector.Where(l => l != null).SelectMany(l => l)
      .Where(z => z != null && selected.ContainsKey(z.m_uid)).GroupBy(z => z).ToDictionary(g => g.Key, g => g.Count());
    var lines = new List<string>();
    foreach (var record in records.Values)
    {
      registered.TryGetValue(record, out var membership);
      ordinaryEntries.TryGetValue(record, out var ordinary);
      wrongSectors.TryGetValue(record, out var wrongSector);
      live.TryGetValue(record, out var instances);
      var instanceText = instances == null ? "" : string.Join(",", instances.Select(v => v.GetInstanceID() + "@" + v.transform.position));
      lines.Add($"id={record.m_uid} ship={record.GetInt(VehicleZdoVars.MBParentId)} tag='{record.GetString(ZDOVars.s_tag)}' pos={record.GetPosition()} sector={record.GetSectorIndex()} registry={membership} wrongSector={wrongSector} ordinary={ordinary} instances=[{instanceText}]");
    }
    var invalid = ZDOMan.instance.m_portalObjects.Values.Sum(l => l.Count(z => !PortalRegistry.IsPortal(z) || !ReferenceEquals(ZDOMan.instance.GetZDO(z.m_uid), z)));
    var tracked = Controllers.VehiclePiecesController.ActiveInstances.Values.Where(c => c != null).Sum(c => c.m_portals.Count);
    var instanceCount = live.Where(p => selected.ContainsKey(p.Key.m_uid)).Sum(p => p.Value.Count);
    return $"portals={records.Count} instances={instanceCount} registry={records.Values.Sum(z => registered.TryGetValue(z, out var count) ? count : 0)} ordinary={ordinaryEntries.Values.Sum()} invalidRegistry={invalid} tracked={tracked} preventedCreates={PortalRegistry.PreventedCreations}\n" + string.Join("\n", lines);
  }

  private static string Execute(string[] args, long owner)
  {
    if (args.Length == 0) return "vehicle portals audit [ship-id] | repair <audit-id> [--remove ID,ID --confirm]";
    foreach (var key in audits.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToList()) audits.Remove(key);
    if (args[0] == "audit")
    {
      int? ship = args.Length > 1 ? int.Parse(args[1]) : null;
      var report = Describe(ship, out var records);
      var token = Guid.NewGuid().ToString("N");
      audits[token] = new Audit { Owner = owner, Expires = DateTime.UtcNow.AddMinutes(5), Records = records };
      Directory.CreateDirectory(Path.Combine(Paths.ConfigPath, "PortalAudits"));
      File.WriteAllText(Path.Combine(Paths.ConfigPath, "PortalAudits", token + ".txt"), report);
      var preview = string.Join("\n", report.Split('\n').Take(65));
      return $"SERVER audit={token} expires in 5 minutes. Overlap is not proof of duplication. Full report: PortalAudits/{token}.txt\n{preview}";
    }
    if (args[0] != "repair" || args.Length < 2 || !audits.TryGetValue(args[1], out var audit) || audit.Owner != owner)
      throw new InvalidOperationException("A fresh audit belonging to this admin is required.");
    foreach (var pair in audit.Records)
      if (!ReferenceEquals(ZDOMan.instance.GetZDO(pair.Key), pair.Value) || !PortalRegistry.IsShipPortal(pair.Value))
        throw new InvalidOperationException("World records changed; run a new audit.");
    var removeIndex = Array.IndexOf(args, "--remove");
    var remove = removeIndex < 0 ? Array.Empty<string>() : args[removeIndex + 1].Split(',');
    if (remove.Length > 0 && !args.Contains("--confirm")) throw new InvalidOperationException("Saved record deletion requires --confirm.");
    var removals = remove.Select(id => audit.Records.FirstOrDefault(p => p.Key.ToString() == id)) .ToList();
    if (removals.Any(p => p.Value == null)) throw new InvalidOperationException("Removal ID was not included in this audit.");
    // All selections have been validated before the first mutation. No positional auto-deletion and no refunds.
    PortalRegistry.Normalize();
    var allowed = new HashSet<string>(audit.Records.Keys.Select(id => id.ToString()));
    var repaired = RepairInstances(allowed);
    foreach (var pair in removals)
    {
      pair.Value.SetOwner(ZDOMan.GetSessionID());
      ZDOMan.instance.DestroyZDO(pair.Value);
    }
    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, LocalRepair, string.Join(",", allowed));
    audits.Remove(args[1]);
    return $"Registry repaired; removed {repaired} extra local instances and {removals.Count} explicitly selected saved records. No resources refunded.";
  }

  private static int RepairInstances(HashSet<string> allowed)
  {
    var repaired = 0;
    var groups = Resources.FindObjectsOfTypeAll<TeleportWorld>().Select(p => p.m_nview)
      .Where(v => v != null && v.IsValid() && PortalRegistry.IsShipPortal(v.GetZDO()) && allowed.Contains(v.GetZDO().m_uid.ToString()))
      .GroupBy(v => v.GetZDO().m_uid).ToList();
    foreach (var group in groups)
    {
      var canonical = ZNetScene.instance.FindInstance(group.First().GetZDO()) ?? group.First();
      foreach (var view in group.Where(v => v != canonical))
      {
        // ResetZDO/DestroyZDO would reset or delete the record shared by the canonical instance.
        var controller = view.GetComponentInParent<Controllers.VehiclePiecesController>();
        controller?.RemovePiece(view);
        controller?.m_portals.Remove(view);
        // Prevent the normal parent-controller destruction hook from running a second time.
        view.transform.SetParent(null, true);
        view.m_zdo = null;
        UnityEngine.Object.Destroy(view.gameObject);
        repaired++;
      }
    }
    return repaired;
  }
}
