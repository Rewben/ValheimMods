using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;
using NUnit.Framework;
using UnityEngine;
using ValheimVehicles.Controllers;
using ValheimVehicles.Fork;

[TestFixture]
public sealed class PortalDuplicateTests
{
  private string directory = "";
  [SetUp] public void Setup()
  {
    directory=Path.Combine(TestContext.CurrentContext.WorkDirectory,"portal-dedupe-tests",Guid.NewGuid().ToString("N"));
    Paths.ConfigPath=directory;
    ZDOMan.instance=new(); ZNet.instance=new() { Server=true,LocalAdmin=true };
    ZNet.instance.m_adminList.Add("123");
    ZNetScene.instance=new(); ZRoutedRpc.instance=new(); Game.instance=new(); Console.instance=new();
    Player.m_localPlayer=null; Resources.Objects.Clear(); VehiclePiecesController.ActiveInstances.Clear();
    Chainloader.PluginInfos.Clear(); TargetPortal.Map.Reset(); TargetPortal.TargetPortal.knownPortals.Clear();
    PortalRegistry.Reset(); PortalCommands.Register();
  }
  [TearDown] public void Cleanup()
  {
    var root=Path.GetFullPath(TestContext.CurrentContext.WorkDirectory)+Path.DirectorySeparatorChar;
    if(!Path.GetFullPath(directory).StartsWith(root,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe cleanup");
    if(Directory.Exists(directory)) Directory.Delete(directory,true);
    Resources.Objects.Clear(); VehiclePiecesController.ActiveInstances.Clear(); Chainloader.PluginInfos.Clear();
    PortalRegistry.Reset();
  }
  private ZDO Add(uint id, Vector3? local=null, int ship=7, int? prefab=null)
  {
    var z=new ZDO {m_uid=new(17,id),Parent=ship,Local=local,Position=new(1)};
    if(prefab.HasValue) { z.Prefab=prefab.Value;Game.instance.PortalPrefabHash.Add(z.Prefab); }
    ZDOMan.instance.m_objectsByID.Add(z.m_uid,z);
    return z;
  }
  private string Audit()
  {
    PortalCommands.Run(new[] {"audit"});
    return Regex.Match(Console.instance!.Messages.Last(),@"SERVER audit=(\w+)").Groups[1].Value;
  }
  private void Repair(string id, params string[] flags) => PortalCommands.Run(new[] {"repair",id}.Concat(flags).ToArray());

  [Test] public void SameLocationDifferentIdsHaveOneKeeperAndTagDoesNotHideCopies()
  {
    var a=Add(1,new(4,2,7));var b=Add(2,new(4,2,7));a.Tag="";b.Tag="SKIPPER";
    var plan=new PortalDuplicatePlan(new[] {a,b});
    Assert.That(plan.Removals.Single().Id,Is.EqualTo(a.m_uid));
    Assert.That(plan.KeepForRemoval[a.m_uid],Is.EqualTo(b.m_uid));
    Assert.That(ZDOMan.instance.m_objectsByID,Has.Count.EqualTo(2));
  }
  [Test] public void ShipPrefabLocalPositionAndCoordinateFrameSeparateLegitimatePortals()
  {
    Add(1,new(1,2,3));Add(2,new(2,2,3));Add(3,new(1,2,3),ship:8);
    Add(4,new(1,2,3),prefab:1234);Add(5,new(1,2,3)).Swivel=77;
    Assert.That(new PortalDuplicatePlan(ZDOMan.instance.m_objectsByID.Values).Removals,Is.Empty);
  }
  [Test] public void ToleranceFindsNeighborsAcrossSpatialCellsWithoutChainMerging()
  {
    Add(1,new(0));Add(2,new(0.0009f));Add(3,new(0.0018f));
    var plan=new PortalDuplicatePlan(ZDOMan.instance.m_objectsByID.Values);
    Assert.That(plan.Removals,Has.Count.EqualTo(1));
    Assert.That(plan.KeepForRemoval[new(17,2)],Is.EqualTo(new ZDOID(17,1)));
  }
  [Test] public void MissingAndNonfiniteCoordinatesAreNotGroupedAtAnInventedOrigin()
  {
    Add(1);Add(2);Add(3,new(float.NaN));Add(4,new(float.PositiveInfinity));
    var plan=new PortalDuplicatePlan(ZDOMan.instance.m_objectsByID.Values);
    Assert.That(plan.Removals,Is.Empty);Assert.That(plan.Skipped,Is.EqualTo(4));
  }
  [Test] public void AuditPreviewsExactSelectionAndDoesNotDeleteAnything()
  {
    Add(1,new(4));Add(2,new(4));Add(3,new(4));
    var id=Audit();var output=Console.instance!.Messages.Last();
    Assert.That(output,Does.Contain("duplicateGroups=1 duplicateRecords=2").And.Contain($"repair {id} --dedupe --confirm"));
    var file=File.ReadAllText(Path.Combine(directory,"PortalAudits",id+".txt"));
    Assert.That(file,Does.Contain("duplicateOf=17:1").And.Contain("local=(4,0,0)"));
    Assert.That(ZDOMan.instance.m_objectsByID,Has.Count.EqualTo(3));Assert.That(Game.instance.Coroutines,Is.Empty);
  }
  [Test] public void ConfirmationIsRequiredAndBareRepairNeverDeletesSavedCopies()
  {
    Add(1,new(4));Add(2,new(4));var id=Audit();
    Repair(id,"--dedupe");Assert.That(Console.instance!.Messages.Last(),Does.Contain("requires --confirm"));
    Repair(id);Game.instance.CompleteCoroutines();
    Assert.That(ZDOMan.instance.m_objectsByID,Has.Count.EqualTo(2));
    Assert.That(Console.instance.Messages.Last(),Does.Contain("Saved position duplicates remain: 1"));
  }
  [Test] public void LargeCleanupYieldsBetweenBatchesAndWaitsForNetworkDeletionAcknowledgement()
  {
    for(uint id=1;id<=16247;id++) Add(id,new(4,2,3));
    var separate=Add(16248,new(8,2,3));var keeper=ZDOMan.instance.GetZDO(new(17,1));
    var auditToken=Audit();Repair(auditToken,"--dedupe","--confirm");
    Assert.That(ZDOMan.instance.m_objectsByID,Has.Count.EqualTo(16248));
    Game.instance.AdvanceFrame();Assert.That(ZDOMan.instance.m_objectsByID,Has.Count.EqualTo(16248));
    Game.instance.AdvanceFrame();Assert.That(ZDOMan.instance.m_objectsByID.Count,Is.EqualTo(16248-PortalCommands.RepairBatchLimit));
    Game.instance.CompleteCoroutines();
    Assert.That(ZDOMan.instance.m_objectsByID.Values,Is.EquivalentTo(new[] {keeper,separate}));
    Assert.That(ZDOMan.instance.GetPortalList(),Is.EquivalentTo(new[] {keeper,separate}));
    Assert.That(Console.instance!.Messages.Last(),Does.Contain("COMPLETE: removed 16246 saved records; 2 ship portals remain"));
    Assert.That(ZRoutedRpc.instance.Sent.Where(s=>s.Name=="RewbenRAFT.PortalLocalRepair").All(s=>s.Value=="*"),Is.True);
  }
  [Test] public void ChangedCoordinatesAndNewRecordsInvalidateTheAuditBeforeMutation()
  {
    Add(1,new(4));var b=Add(2,new(4));var id=Audit();b.Local=new(5);
    Repair(id,"--dedupe","--confirm");Assert.That(Console.instance!.Messages.Last(),Does.Contain("run a new audit"));
    Assert.That(Game.instance.Coroutines,Is.Empty);
    id=Audit();Add(3,new(4));Repair(id,"--dedupe","--confirm");
    Assert.That(Console.instance.Messages.Last(),Does.Contain("records changed"));
  }
  [Test] public void WorldMovementDoesNotInvalidateShipRelativeSelection()
  {
    Add(1,new(4));var b=Add(2,new(4));var id=Audit();b.SetPosition(new(90));
    Repair(id,"--dedupe","--confirm");Game.instance.CompleteCoroutines();
    Assert.That(ZDOMan.instance.m_objectsByID,Has.Count.EqualTo(1));
  }
  [Test] public void MovingTheKeeperDuringCleanupStopsFurtherBatches()
  {
    var keeper=Add(1,new(4));for(uint id=2;id<=100;id++) Add(id,new(4));
    var token=Audit();Repair(token,"--dedupe","--confirm");Game.instance.AdvanceFrame();keeper.Local=new(7);
    Game.instance.CompleteCoroutines();
    Assert.That(ZDOMan.instance.m_objectsByID.Count,Is.EqualTo(100-PortalCommands.RepairBatchLimit));
    Assert.That(Console.instance!.Messages.Last(),Does.Contain("STOPPED").And.Contain("Surviving portal changed"));
    Assert.That(ZDOMan.instance.GetZDO(keeper.m_uid),Is.SameAs(keeper));
  }
  [Test] public void AnotherWorldCancelsTheOldJobWithoutTouchingNewRecords()
  {
    Add(1,new(4));Add(2,new(4));Repair(Audit(),"--dedupe","--confirm");
    ZDOMan.instance=new();var newRecord=Add(1,new(4));PortalRegistry.Reset();Game.instance.CompleteCoroutines();
    Assert.That(ZDOMan.instance.m_objectsByID.Values,Is.EqualTo(new[] {newRecord}));
  }
  [Test] public void AdminDisconnectStopsAQueuedCleanup()
  {
    Add(1,new(4));Add(2,new(4));
    var handler=(Action<long,string>)ZRoutedRpc.instance.Handlers["RewbenRAFT.PortalRequest"];
    handler(100,"audit");var token=Regex.Match(ZRoutedRpc.instance.Sent.Last().Value,@"SERVER audit=(\w+)").Groups[1].Value;
    handler(100,"repair "+token+" --dedupe --confirm");ZNet.instance!.MissingPeer=true;
    Game.instance.CompleteCoroutines();Assert.That(ZDOMan.instance.m_objectsByID,Has.Count.EqualTo(2));
    Assert.That(Console.instance!.Messages.Last(),Does.Contain("disconnected"));
  }
  [Test] public void WorldReplacementWithoutAnExplicitResetDoesNotRepairTheNewWorld()
  {
    Add(1,new(4));Add(2,new(4));Repair(Audit(),"--dedupe","--confirm");
    ZDOMan.instance=new();var replacement=Add(1,new(4));Game.instance.CompleteCoroutines();
    Assert.That(ZDOMan.instance.m_objectsByID.Values,Is.EqualTo(new[] {replacement}));
    Assert.That(ZDOMan.instance.GetPortalList(),Is.Empty);
  }
  [Test] public void ExplicitIdsRemainSupportedAndCannotBeMixedWithDedupe()
  {
    var keep=Add(1);Add(2);var token=Audit();
    Repair(token,"--dedupe","--remove","17:2","--confirm");Assert.That(Console.instance!.Messages.Last(),Does.Contain("not both"));
    Repair(token,"--remove","17:2","--confirm");Game.instance.CompleteCoroutines();
    Assert.That(ZDOMan.instance.m_objectsByID.Values,Is.EqualTo(new[] {keep}));
  }
  [Test] public void TargetPortalRefreshClearsTemporaryPinsAndPrunesDeadCachedRecords()
  {
    var keep=Add(1,new(4));var dead=Add(2,new(4));
    Chainloader.PluginInfos.Add("org.bepinex.plugins.targetportal",new PluginInfo());
    TargetPortal.TargetPortal.knownPortals.UnionWith(new[] {keep,dead});TargetPortal.Map.Add(keep);TargetPortal.Map.Add(dead);
    ZDOMan.instance.m_objectsByID.Remove(dead.m_uid);dead.Valid=false;dead.m_uid=ZDOID.None;
    PortalMapRefresh.AfterRepair();
    Assert.That(TargetPortal.TargetPortal.knownPortals,Is.EquivalentTo(new[] {keep}));
    Assert.That(TargetPortal.Map.Count,Is.Zero);Assert.That(TargetPortal.Map.Clears,Is.EqualTo(1));
    Chainloader.PluginInfos.Clear();PortalMapRefresh.AfterRepair();Assert.That(TargetPortal.Map.Clears,Is.EqualTo(1));
  }
  [Test] public void CleanupPreservesTheCanonicalLivePortalAndRemovesExtraViewsSharingItsRecord()
  {
    var keeper=Add(1,new(4));Add(2,new(4));
    var canonical=new ZNetView {Record=keeper};var extra=new ZNetView {Record=keeper};
    ZNetScene.instance.Instances.Add(keeper.m_uid,canonical);
    Resources.Objects.Add(new TeleportWorld {m_nview=canonical});Resources.Objects.Add(new TeleportWorld {m_nview=extra});
    Repair(Audit(),"--dedupe","--confirm");Game.instance.CompleteCoroutines();
    Assert.That(canonical.IsValid(),Is.True);Assert.That(canonical.gameObject.Destroyed,Is.False);
    Assert.That(extra.m_zdo,Is.Null);Assert.That(extra.gameObject.Destroyed,Is.True);
    Assert.That(ZDOMan.instance.GetZDO(keeper.m_uid),Is.SameAs(keeper));
  }
}

namespace TargetPortal
{
  public static class TargetPortal { public static readonly HashSet<ZDO> knownPortals=new(); }
  public static class Map
  {
    private static readonly Dictionary<object,ZDO> activePins=new();
    public static int Clears;
    public static int Count=>activePins.Count;
    public static void Add(ZDO record)=>activePins.Add(new object(),record);
    private static void RemovePortalPins() { activePins.Clear();Clears++; }
    public static void Reset() { activePins.Clear();Clears=0; }
  }
}
