using HarmonyLib;
using NUnit.Framework;
using UnityEngine;
using ValheimVehicles.Fork;
using ValheimVehicles.Controllers;
using BepInEx.Bootstrap;

[TestFixture]
public sealed class PortalRegressionTests
{
  private ZDO NewPortal(uint id=1, float x=1)
  {
    var z=new ZDO { m_uid=new(17,id),Position=new(x) };
    ZDOMan.instance.m_objectsByID.Add(z.m_uid,z);
    return z;
  }
  private int Membership(ZDO z) => ZDOMan.instance.GetPortalList().Count(p=>ReferenceEquals(p,z));
  [SetUp] public void Setup()
  {
    ZDOMan.instance=new(); ZNet.instance=new() { Server=true }; Game.instance=new();
    ZNetScene.instance=new();
    PortalRegistry.Reset(); Chainloader.PluginInfos.Clear();
  }
  [Test] public void SceneGuardKeepsCanonicalInstanceAndAllowsDistinctOverlappingRecord()
  {
    var a=NewPortal();var b=NewPortal(2);var live=new ZNetView { Record=a };
    ZNetScene.instance.Instances.Add(a.m_uid,live);
    var guard=HarmonyLib.AccessTools.Method(typeof(PortalPatches),"GuardDuplicate");
    object?[] args={a,null};
    Assert.That(guard.Invoke(null,args),Is.False);Assert.That(args[1],Is.SameAs(live.gameObject));
    Assert.That(guard.Invoke(null,new object?[] {b,null}),Is.True);
    Assert.That(a.IsValid(),Is.True);Assert.That(ZDOMan.instance.m_objectsByID.Count,Is.EqualTo(2));
  }
  [Test] public void PairingHookTargetsTheCurrentGameCallAndPreservesLabels()
  {
    var hook=HarmonyLib.AccessTools.Method(typeof(PortalPatches),"FilterPairing");
    var call=new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Callvirt,HarmonyLib.AccessTools.Method(typeof(ZDOMan),nameof(ZDOMan.GetPortalList)));
    var result=((IEnumerable<HarmonyLib.CodeInstruction>)hook.Invoke(null,new object[] {new[] {call}})!).ToList();
    Assert.That(result[0],Is.SameAs(call));
    Assert.That(result[0].Calls(HarmonyLib.AccessTools.Method(typeof(PortalRegistry),nameof(PortalRegistry.VanillaPairingCandidates))),Is.True);
    var missing=(IEnumerable<HarmonyLib.CodeInstruction>)hook.Invoke(null,new object[] {Array.Empty<HarmonyLib.CodeInstruction>()})!;
    Assert.Throws<InvalidOperationException>(()=>missing.ToList());
  }
  [Test] public void MigrationRemovesBothDuplicateSourcesAndKeepsIdentity()
  {
    var z=NewPortal();var id=z.m_uid;
    ZDOMan.instance.m_objectsBySector[0]=new() { z,z };
    ZDOMan.instance.m_portalObjects[new(0)]=new() { z,z };
    PortalRegistry.Update(z,new(70));
    Assert.That(z.m_uid,Is.EqualTo(id)); Assert.That(Membership(z),Is.EqualTo(1));
    Assert.That(ZDOMan.instance.m_portalObjects[new(1)],Does.Contain(z));
    Assert.That(ZDOMan.instance.m_objectsBySector.Where(l=>l!=null).SelectMany(l=>l!),Does.Not.Contain(z));
  }
  [Test] public void ThousandsOfUpdatesAndOwnershipSwitchesDoNotAccumulateEntries()
  {
    var z=NewPortal();
    for(var i=0;i<2000;i++) { ZNet.instance!.Server=i%2==0;PortalRegistry.Update(z,new(i%2==0?1:70)); }
    Assert.That(Membership(z),Is.EqualTo(1)); Assert.That(ZDOMan.instance.m_objectsByID.Count,Is.EqualTo(1));
  }
  [Test] public void AlreadyMovedCoordinatesDoNotHideOldRegistrySector()
  {
    var z=NewPortal();PortalRegistry.Update(z,new(1));z.SetPosition(new(70));
    PortalRegistry.Update(z,new(70)); Assert.That(Membership(z),Is.EqualTo(1));
    Assert.That(ZDOMan.instance.m_portalObjects.ContainsKey(new(0)),Is.False);
  }
  [Test] public void ResetRemovesReferencesBeforePooledRecordIdentityChanges()
  {
    var z=NewPortal();PortalRegistry.Update(z,new(1));PortalRegistry.BeforeReset(z);
    z.m_uid=ZDOID.None;z.Valid=false;
    Assert.That(Membership(z),Is.Zero);Assert.That(ZDOMan.instance.GetPortalList(),Is.Empty);
  }
  [Test] public void SaveCloneResetDoesNotRemoveLiveRecord()
  {
    var z=NewPortal();PortalRegistry.Update(z,new(1));
    var clone=new ZDO { m_uid=z.m_uid, SaveClone=true };PortalRegistry.BeforeReset(clone);
    Assert.That(Membership(z),Is.EqualTo(1));
  }
  [Test] public void StaleResetRecordsArePrunedWithoutDeletingDistinctOverlappingPortals()
  {
    var a=NewPortal(1);var b=NewPortal(2);var stale=new ZDO { m_uid=ZDOID.None,Valid=false };
    ZDOMan.instance.m_portalObjects[new(0)]=new() { a,a,b,stale };
    PortalRegistry.Normalize(); Assert.That(Membership(a),Is.EqualTo(1));Assert.That(Membership(b),Is.EqualTo(1));
    Assert.That(ZDOMan.instance.m_objectsByID.Count,Is.EqualTo(2));
  }
  [Test] public void TrackingIsUniqueAndInvalidViewsArePruned()
  {
    var z=NewPortal();var c=new VehiclePiecesController();var a=new ZNetView { Record=z };var b=new ZNetView { Record=z };
    for(var i=0;i<1000;i++) PortalRegistry.Track(c,a);
    c.m_portals.Add(b);PortalRegistry.Prune(c);Assert.That(c.m_portals.Count,Is.EqualTo(1));
    z.Valid=false;PortalRegistry.Prune(c);Assert.That(c.m_portals,Is.Empty);
  }
  [Test] public void LaterOrdinarySectorContaminationIsRemoved()
  {
    var z=NewPortal();PortalRegistry.Update(z,new(1));
    ZDOMan.instance.m_objectsBySector[0]=new() { z };
    PortalRegistry.Update(z,new(1));
    Assert.That(ZDOMan.instance.m_objectsBySector[0],Is.Empty);
    Assert.That(Membership(z),Is.EqualTo(1));
  }
  [Test] public void NewWorldDoesNotReuseCachedRegistrations()
  {
    var old=NewPortal();PortalRegistry.Update(old,new(1));
    ZDOMan.instance=new();var next=NewPortal();PortalRegistry.Update(next,new(70));
    Assert.That(ZDOMan.instance.GetPortalList(),Is.EqualTo(new[] { next }));
  }
  [Test] public void TargetPortalFilteringFollowsItsConfiguredScope()
  {
    var wood=NewPortal();var custom=NewPortal(2);custom.Prefab=123;Game.instance.PortalPrefabHash.Add(123);
    PortalRegistry.Normalize();Assert.That(PortalRegistry.VanillaPairingCandidates(ZDOMan.instance).Count,Is.EqualTo(2));
    var plugin=new PluginInfo();plugin.Instance.Config.Add(new(new FakeKey(),new FakeValue { BoxedValue="Off" }));
    Chainloader.PluginInfos.Add("org.bepinex.plugins.targetportal",plugin);
    Assert.That(PortalRegistry.VanillaPairingCandidates(ZDOMan.instance),Is.Empty);
    plugin.Instance.Config[0].Value.BoxedValue="On";
    Assert.That(PortalRegistry.VanillaPairingCandidates(ZDOMan.instance),Is.EqualTo(new[] { custom }));
  }
}

[TestFixture]
public sealed class RebalanceRegressionTests
{
  [TestCase(0,1.5f,-1.5f)] [TestCase(-4,1.5f,-5.5f)] [TestCase(3,1.5f,1.5f)]
  public void FlotationTracksHullBottom(float hull,float clearance,float floor) => Assert.That(ForkRules.FlotationFloor(hull,clearance),Is.EqualTo(floor));
  [Test] public void MissingHullUsesSafeClearance() => Assert.That(ForkRules.FlotationFloor(float.PositiveInfinity,1.5f),Is.EqualTo(-1.5f));
  [TestCase(0,0,true)] [TestCase(10,10,true)] [TestCase(9,10,true)] [TestCase(11,10,false)]
  public void HaulingStopsAtExhaustion(float stamina,float cost,bool stopped) => Assert.That(ForkRules.HaulExhausted(stamina,cost),Is.EqualTo(stopped));
  [TestCase(true,false,true,false,false)] [TestCase(true,true,true,false,true)] [TestCase(true,false,true,true,true)]
  [TestCase(false,false,true,false,true)] [TestCase(true,false,false,false,true)]
  public void CannonPolicyAllowsCreaturesSpawnersAndPlayerBuildings(bool enabled,bool creature,bool harvestable,bool playerBuilding,bool allowed)
    => Assert.That(ForkRules.CannonTargetAllowed(enabled,creature,harvestable,playerBuilding),Is.EqualTo(allowed));
  [Test] public void CompanionDetectionPrecedesAnyConfigMigration()
  {
    Assert.That(RebalanceData.IsCompanion("RaftRebalance.dll"),Is.True);
    Assert.That(RebalanceData.IsCompanion(typeof(RebalanceRegressionTests).Assembly.Location),Is.False);
  }
  [TestCase(5,0.2,4.8)] [TestCase(5,7,0)] [TestCase(5,0,5)]
  public void SchedulerUsesSeconds(double interval,double elapsed,double remaining) => Assert.That(ForkRules.RemainingSeconds(interval,elapsed),Is.EqualTo(remaining));
  [TestCase(20,false,5)] [TestCase(-3,false,0)] [TestCase(2,false,2)] [TestCase(20,true,20)]
  public void CreativeLiftIsCappedOnlyForNonAdmins(float value,bool admin,float expected) => Assert.That(ForkRules.CreativeLift(value,admin),Is.EqualTo(expected));
  [TestCase(0.25f,10f,2.5f)] [TestCase(2.7f,10f,27f)] [TestCase(-1f,10f,0f)]
  public void HaulingChargesActualDistance(float distance,float rate,float expected) => Assert.That(ForkRules.HaulCost(distance,rate),Is.EqualTo(expected));
  [Test] public void AllTenRecipesAndEightLegacyRefundCostsParse()
  {
    Assert.That(RebalanceData.Recipes.Count,Is.EqualTo(10));Assert.That(RebalanceData.Legacy.Count,Is.EqualTo(8));
    foreach(var value in RebalanceData.Recipes.Values.Concat(RebalanceData.Legacy.Values)) Assert.That(RebalanceData.ParseCosts(value),Is.Not.Empty);
    Assert.That(RebalanceData.PaidCostKey,Is.EqualTo("vhrf_paid_v1"));
  }
  [Test] public void RefundCostKeepsRecoverFlagsAndUpgradeCost()
  {
    var costs=RebalanceData.ParseCosts("Iron,4,false,1|Chain,1,true");
    Assert.That(costs[0].Recover,Is.False);Assert.That(costs[0].PerLevel,Is.EqualTo(1));Assert.That(costs[1].Amount,Is.EqualTo(1));
  }
  [TestCase("Iron,-1")] [TestCase("Iron,1000")] [TestCase("Iron,2.2")] [TestCase("Iron,4,wrong")] [TestCase("Iron,4,true,-2")] [TestCase(",3")]
  public void InvalidSavedCostsAreRejected(string value) => Assert.Throws<FormatException>(()=>RebalanceData.ParseCosts(value));
  [Test] public void MigrationOnlyImportsLegacyOverridesOnce()
  {
    Assert.That(RebalanceData.ResolveRecipe(false,null,"legacy","old-native","default"),Is.EqualTo("legacy"));
    Assert.That(RebalanceData.ResolveRecipe(true,"integrated","stale-legacy","raft","default"),Is.EqualTo("integrated"));
    Assert.That(RebalanceData.ResolveRecipe(true,null,"stale-legacy",null,"default"),Is.EqualTo("default"));
    Assert.That(RebalanceData.ResolveRecipe(false,null,null,"custom-native","default"),Is.EqualTo("custom-native"));
  }
  [Test] public void IntegratedSettingsWinBeforeAndAfterMigration()
  {
    Assert.That(ForkRules.MigratedValue("integrated","legacy","raft","default"),Is.EqualTo("integrated"));
    Assert.That(ForkRules.MigratedValue(null,"legacy","raft","default"),Is.EqualTo("legacy"));
    Assert.That(ForkRules.MigratedValue(null,null,"raft","default"),Is.EqualTo("raft"));
    Assert.That(ForkRules.MigratedValue(null,null,null,"default"),Is.EqualTo("default"));
  }
  [Test] public void LegacyIniRetainsSectionSpecificKeysAndIgnoresComments()
  {
    var data=RebalanceData.ReadIni(new[] { "# comment", "[Recipes]", "A = Iron,4", "[Floatation]", "MaxHullClearance = 1.5" });
    Assert.That(data["Recipes/A"],Is.EqualTo("Iron,4"));Assert.That(data["Floatation/MaxHullClearance"],Is.EqualTo("1.5"));
  }
  [Test] public void LocalAndSynchronizedAdminChecksAreExplicit()
  {
    ZNet.instance=null;Assert.That(ForkAccess.IsAdmin(),Is.False);
    ZNet.instance=new();Jotunn.Managers.SynchronizationManager.Instance=new();Assert.That(ForkAccess.IsAdmin(),Is.False);
    Jotunn.Managers.SynchronizationManager.Instance.PlayerIsAdmin=true;Assert.That(ForkAccess.IsAdmin(),Is.True);
    Jotunn.Managers.SynchronizationManager.Instance.PlayerIsAdmin=false;ZNet.instance.LocalAdmin=true;Assert.That(ForkAccess.IsAdmin(),Is.True);
  }
}

