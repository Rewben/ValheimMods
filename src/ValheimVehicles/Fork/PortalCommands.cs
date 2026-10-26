using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
    internal PortalDuplicatePlan Plan = null!;
    internal ZDOMan World = null!;
    internal int? Ship;
  }
  public const int RepairBatchLimit = 32;
  private sealed class RepairJob
  {
    internal string Id = "";
    internal Audit Audit = null!;
    internal List<PortalDuplicatePlan.Snapshot> Removals = new();
    internal List<PortalDuplicatePlan.Snapshot> Pending = new();
    internal int Next, Removed, Waits, Generation;
    internal bool Initialized, Dedupe;
    internal DateTime NextProgress = DateTime.UtcNow.AddSeconds(5);
  }
  private static RepairJob? running;
  private static int generation;
  private static readonly Dictionary<string, Audit> audits = new(StringComparer.OrdinalIgnoreCase);
  public static void Reset() { audits.Clear(); running = null; generation++; }
  public static void LogCounters() => Print("Rewben portal counters " + Describe(SelectRecords(null), false).Summary);

  private static bool RemoteAdmin(long sender)
  {
    var host = ZNet.instance?.GetPeer(sender)?.m_socket?.GetHostName();
    if (host != null && host.StartsWith("Steam_")) host = host.Substring(6);
    return host != null && (ZNet.instance!.m_adminList.Contains(host) || ZNet.instance.m_adminList.Contains("Steam_" + host));
  }

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
      if (!RemoteAdmin(sender))
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
      var allowed = ids == "*" ? null : new HashSet<string>(ids.Split(','));
      RepairInstances(allowed);
      PortalRegistry.Normalize();
      PortalMapRefresh.AfterRepair();
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
    if (args.Length > 0 && args[0] == "audit")
    {
      // The full client dump can contain tens of thousands of lines. Only inspect
      // loaded views here; the server creates the authoritative, complete report.
      var views = Resources.FindObjectsOfTypeAll<TeleportWorld>().Select(p => p.m_nview)
        .Where(v => v != null && v.IsValid() && PortalRegistry.IsShipPortal(v.GetZDO())).ToList();
      Print($"CLIENT ship portal instances={views.Count} uniqueRecords={views.Select(v => v.GetZDO().m_uid).Distinct().Count()}. Requesting server audit...");
    }
    ZRoutedRpc.instance.InvokeRoutedRPC(ZNet.instance.GetServerPeer().m_uid, Request, string.Join(" ", args));
  }

  private static Dictionary<ZDOID, ZDO> SelectRecords(int? ship) => ZDOMan.instance.m_objectsByID.Values
    .Where(z => PortalRegistry.IsShipPortal(z) && (!ship.HasValue || z.GetInt(VehicleZdoVars.MBParentId) == ship.Value))
    .ToDictionary(z => z.m_uid);

  private static PortalAuditReport Describe(Dictionary<ZDOID, ZDO> records, bool includeRecords = true, PortalDuplicatePlan? plan = null)
  {
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
    if (includeRecords) foreach (var record in records.Values)
    {
      registered.TryGetValue(record, out var membership);
      ordinaryEntries.TryGetValue(record, out var ordinary);
      wrongSectors.TryGetValue(record, out var wrongSector);
      live.TryGetValue(record, out var instances);
      var instanceText = instances == null ? "" : string.Join(",", instances.Select(v => v.GetInstanceID() + "@" + v.transform.position));
      lines.Add($"id={record.m_uid} ship={record.GetInt(VehicleZdoVars.MBParentId)} tag='{record.GetString(ZDOVars.s_tag)}' pos={record.GetPosition()} sector={record.GetSectorIndex()} registry={membership} wrongSector={wrongSector} ordinary={ordinary} instances=[{instanceText}]" + (plan == null ? "" : " " + plan.Describe(record.m_uid)));
    }
    var invalid = ZDOMan.instance.m_portalObjects.Values.Sum(l => l.Count(z => !PortalRegistry.IsPortal(z) || !ReferenceEquals(ZDOMan.instance.GetZDO(z.m_uid), z)));
    var tracked = Controllers.VehiclePiecesController.ActiveInstances.Values.Where(c => c != null).Sum(c => c.m_portals.Count);
    var instanceCount = live.Where(p => selected.ContainsKey(p.Key.m_uid)).Sum(p => p.Value.Count);
    var summary = $"portals={records.Count} instances={instanceCount} registry={records.Values.Sum(z => registered.TryGetValue(z, out var count) ? count : 0)} ordinary={ordinaryEntries.Values.Sum()} invalidRegistry={invalid} tracked={tracked} preventedCreates={PortalRegistry.PreventedCreations}";
    return new PortalAuditReport(summary + (plan == null ? "" : " " + plan.Summary), lines);
  }

  private static string Execute(string[] args, long owner)
  {
    if (args.Length == 0) return "vehicle portals audit [ship-id] | repair <audit-id> [--dedupe --confirm | --remove ID,ID --confirm]";
    if (running != null) return $"Portal cleanup {running.Id} in progress: {running.Removed}/{running.Removals.Count} saved records removed. Wait for completion before another audit/repair.";
    foreach (var key in audits.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToList()) audits.Remove(key);
    if (args[0] == "audit")
    {
      int? ship = args.Length > 1 ? int.Parse(args[1]) : null;
      var records = SelectRecords(ship);
      var plan = new PortalDuplicatePlan(records.Values);
      var report = Describe(records, true, plan);
      var directory = Path.Combine(Paths.ConfigPath, "PortalAudits");
      Directory.CreateDirectory(directory);
      // Do not overwrite earlier reports, including after reconnects/world changes.
      var token = PortalAuditReport.NewId(id => audits.ContainsKey(id) || File.Exists(Path.Combine(directory, id + ".txt")));
      File.WriteAllText(Path.Combine(directory, token + ".txt"), report.FullText);
      audits[token] = new Audit { Owner = owner, Expires = DateTime.UtcNow.AddMinutes(5), Records = records, Plan = plan, World = ZDOMan.instance, Ship = ship };
      return report.Preview(token, plan.Removals.Count);
    }
    if (args[0] != "repair" || args.Length < 2 || !audits.TryGetValue(args[1], out var audit) || audit.Owner != owner)
      throw new InvalidOperationException("A fresh audit belonging to this admin is required.");
    if (!ReferenceEquals(audit.World, ZDOMan.instance)) throw new InvalidOperationException("World changed; run a new audit.");
    var current = SelectRecords(audit.Ship);
    if (current.Count != audit.Records.Count) throw new InvalidOperationException("Portal records changed; run a new audit.");
    foreach (var snapshot in audit.Plan.Records.Values)
      if (!current.ContainsKey(snapshot.Id) || !snapshot.Matches())
        throw new InvalidOperationException("World records changed; run a new audit.");
    var removeIndex = Array.IndexOf(args, "--remove");
    var dedupe = args.Contains("--dedupe");
    if (dedupe && removeIndex >= 0) throw new InvalidOperationException("Choose --dedupe or --remove, not both.");
    if (removeIndex == args.Length - 1) throw new InvalidOperationException("--remove requires saved record IDs.");
    var remove = removeIndex < 0 ? Array.Empty<string>() : args[removeIndex + 1].Split(',').Distinct().ToArray();
    if ((dedupe || remove.Length > 0) && !args.Contains("--confirm")) throw new InvalidOperationException("Saved record deletion requires --confirm.");
    var byId = audit.Plan.Records.Values.ToDictionary(s => s.Id.ToString());
    if (remove.Any(id => !byId.ContainsKey(id))) throw new InvalidOperationException("Removal ID was not included in this audit.");
    var removals = dedupe ? audit.Plan.Removals : remove.Select(id => byId[id]).ToList();
    if (removals.Count > 0)
    {
      var job = new RepairJob { Id = args[1].ToUpperInvariant(), Audit = audit, Removals = removals, Generation = generation, Dedupe = dedupe };
      AppendRepair(job, $"REPAIR START mode={(dedupe ? "dedupe" : "explicit-ids")} confirmed=true planned={removals.Count} selected={string.Join(",", removals.Select(s => s.Id))}");
      running = job;
      audits.Remove(args[1]);
      Game.instance.StartCoroutine(RunRepair(job));
      return $"CLEANUP {job.Id} started: {removals.Count} selected extra saved records, at most {RepairBatchLimit} per batch. Keep connected until COMPLETE. No resources refunded.";
    }
    PortalRegistry.Normalize();
    var allowed = new HashSet<string>(audit.Records.Keys.Select(id => id.ToString()));
    var repaired = RepairInstances(allowed);
    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, LocalRepair, "*");
    PortalMapRefresh.AfterRepair();
    audits.Remove(args[1]);
    return $"Registry repaired; removed {repaired} extra local instances and 0 explicitly selected saved records. No resources refunded." +
      (audit.Plan.Removals.Count > 0 ? $" Saved position duplicates remain: {audit.Plan.Removals.Count}. Run a new audit, then its printed --dedupe --confirm command." : "");
  }

  private static void AppendRepair(RepairJob job, string message) => File.AppendAllText(
    Path.Combine(Paths.ConfigPath, "PortalAudits", job.Id + ".txt"), "\n" + DateTime.UtcNow.ToString("O") + " " + message + "\n");

  private static void Reply(RepairJob job, string message)
  {
    Print(message);
    if (job.Audit.Owner != ZDOMan.GetSessionID() && RemoteAdmin(job.Audit.Owner))
      ZRoutedRpc.instance.InvokeRoutedRPC(job.Audit.Owner, Response, message);
  }

  private static bool RepairStep(RepairJob job)
  {
    if (!ReferenceEquals(ZDOMan.instance, job.Audit.World)) throw new InvalidOperationException("World changed.");
    if (job.Audit.Owner != ZDOMan.GetSessionID() && !RemoteAdmin(job.Audit.Owner)) throw new InvalidOperationException("Requesting admin disconnected or lost admin access.");
    if (!job.Initialized) { PortalRegistry.Normalize(); job.Initialized = true; }
    if (job.Pending.Count > 0)
    {
      if (job.Pending.Any(s => ZDOMan.instance.GetZDO(s.Id) != null))
      {
        if (++job.Waits > 200) throw new InvalidOperationException("Timed out waiting for saved-record removal acknowledgement.");
        return false;
      }
      job.Removed += job.Pending.Count;
      job.Pending.Clear();
      job.Waits = 0;
    }
    if (job.Next == job.Removals.Count) return true;
    var watch = Stopwatch.StartNew();
    // Validate each selected record and its surviving keeper again immediately
    // before a small batch; movement of the ship in world space is harmless.
    var count = Math.Min(RepairBatchLimit, job.Removals.Count - job.Next);
    var batch = job.Removals.GetRange(job.Next, count);
    foreach (var item in batch)
    {
      if (!item.Matches()) throw new InvalidOperationException("Selected portal changed; run a new audit for the remaining records.");
      if (job.Dedupe && job.Audit.Plan.KeepForRemoval.TryGetValue(item.Id, out var keeper) && !job.Audit.Plan.Records[keeper].Matches())
        throw new InvalidOperationException("Surviving portal changed; run a new audit.");
    }
    PortalRegistry.DetachRepairBatch(batch.Select(s => s.Record));
    foreach (var item in batch)
    {
      item.Record.SetOwner(ZDOMan.GetSessionID());
      ZDOMan.instance.DestroyZDO(item.Record); // Network deletion only: no hammer damage or resource drops.
      job.Pending.Add(item);
      job.Next++;
    }
    if (DateTime.UtcNow >= job.NextProgress)
    {
      job.NextProgress = DateTime.UtcNow.AddSeconds(5);
      Reply(job, $"CLEANUP {job.Id}: removed {job.Removed}/{job.Removals.Count}; batch queued={batch.Count} ({watch.ElapsedMilliseconds}ms).");
    }
    return false;
  }

  private static IEnumerator RunRepair(RepairJob job)
  {
    yield return null;
    while (job.Generation == generation)
    {
      if (!ReferenceEquals(ZDOMan.instance, job.Audit.World) || ZNet.instance == null || !ZNet.instance.IsServer())
      {
        if (ReferenceEquals(running, job)) running = null;
        yield break;
      }
      string? failure = null;
      var complete = false;
      try { complete = RepairStep(job); }
      catch (Exception e) { failure = e.Message; }
      if (complete || failure != null)
      {
        try
        {
          PortalRegistry.Normalize();
          RepairInstances(null);
          PortalMapRefresh.AfterRepair();
          ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, LocalRepair, "*");
        }
        catch (Exception e) { complete = false; failure = "Final registry refresh failed: " + e.Message; }
        finally { running = null; }
        var message = complete ? $"CLEANUP {job.Id} COMPLETE: removed {job.Removed} saved records; {SelectRecords(job.Audit.Ship).Count} ship portals remain. No resources refunded. Reopen the portal map."
          : $"CLEANUP {job.Id} STOPPED: {job.Removed} removals confirmed, {job.Pending.Count} pending. {failure} Run a fresh audit before continuing.";
        try { AppendRepair(job, message); } catch (Exception e) { Logger.LogWarning("Could not append portal repair result: " + e.Message); }
        Reply(job, message);
        yield break;
      }
      yield return new WaitForSeconds(0.05f);
    }
  }

  private static int RepairInstances(HashSet<string>? allowed)
  {
    var repaired = 0;
    var groups = Resources.FindObjectsOfTypeAll<TeleportWorld>().Select(p => p.m_nview)
      .Where(v => v != null && v.IsValid() && PortalRegistry.IsShipPortal(v.GetZDO()) && (allowed == null || allowed.Contains(v.GetZDO().m_uid.ToString())))
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
