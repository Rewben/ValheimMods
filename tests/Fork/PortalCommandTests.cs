using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using NUnit.Framework;
using UnityEngine;
using ValheimVehicles.Controllers;
using ValheimVehicles.Fork;

[TestFixture]
public sealed class PortalCommandTests
{
  private string directory = "";

  [SetUp] public void Setup()
  {
    directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "portal-audit-tests", Guid.NewGuid().ToString("N"));
    Paths.ConfigPath = directory;
    ZDOMan.instance = new();
    ZNet.instance = new() { Server=true, LocalAdmin=true };
    ZNet.instance.m_adminList.Add("123");
    ZRoutedRpc.instance = new();
    ZNetScene.instance = new();
    Game.instance = new();
    Console.instance = new();
    Player.m_localPlayer = null;
    Resources.Objects.Clear();
    VehiclePiecesController.ActiveInstances.Clear();
    Jotunn.Logger.Messages.Clear();
    BepInEx.Bootstrap.Chainloader.PluginInfos.Clear();
    PortalRegistry.Reset();
    PortalCommands.Register();
  }

  [TearDown] public void Cleanup()
  {
    // Remove only this test's unique directory under NUnit's working directory.
    var root = Path.GetFullPath(TestContext.CurrentContext.WorkDirectory) + Path.DirectorySeparatorChar;
    var target = Path.GetFullPath(directory);
    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path");
    if (Directory.Exists(target)) Directory.Delete(target, true);
    Resources.Objects.Clear();
    VehiclePiecesController.ActiveInstances.Clear();
    Console.instance = null;
  }

  private ZDO Add(uint id, int ship=7)
  {
    var record = new ZDO { m_uid=new(17,id), Parent=ship, Position=new(1) };
    ZDOMan.instance.m_objectsByID.Add(record.m_uid,record);
    var sector = record.GetSectorIndex();
    if (!ZDOMan.instance.m_portalObjects.TryGetValue(sector,out var bucket))
      ZDOMan.instance.m_portalObjects[sector] = bucket = new();
    bucket.Add(record);
    return record;
  }

  private string Audit(params string[] extra)
  {
    PortalCommands.Run(new[] { "audit" }.Concat(extra).ToArray());
    return Regex.Match(Console.instance!.Messages.Last(), @"SERVER audit=([0-9A-F]{6})\b").Groups[1].Value;
  }

  [Test] public void SixteenThousandRecordsPrintOnlyTenAndKeepTheCompleteFile()
  {
    for (uint id=1; id<=16248; id++) Add(id);
    var token=Audit();
    var output=Console.instance!.Messages.Single();
    Assert.That(token,Has.Length.EqualTo(6));
    Assert.That(Regex.Matches(output,@"(?m)^id=").Count,Is.EqualTo(10));
    Assert.That(output,Does.Contain("portals=16248").And.Contain("Showing 10 of 16248"));
    Assert.That(output.Split('\n').Last(),Does.Contain("AUDIT ID: " + token).And.Contain("16248 total portals"));
    Assert.That(output.Length,Is.LessThan(5000));
    var full=File.ReadAllText(Path.Combine(directory,"PortalAudits",token + ".txt"));
    Assert.That(Regex.Matches(full,@"(?m)^id=").Count,Is.EqualTo(16248));
    Assert.That(ZDOMan.instance.m_objectsByID,Has.Count.EqualTo(16248));
  }

  [Test] public void ClientAuditSendsOneRequestWithoutDumpingSavedRecords()
  {
    for (uint id=1; id<=16248; id++) Add(id);
    var record=ZDOMan.instance.m_objectsByID.Values.First();
    Resources.Objects.Add(new TeleportWorld { m_nview=new() { Record=record } });
    Resources.Objects.Add(new TeleportWorld { m_nview=new() { Record=record } });
    ZNet.instance!.Server=false;
    PortalCommands.Run(new[] { "audit" });
    Assert.That(Console.instance!.Messages.Single(),Does.Contain("instances=2 uniqueRecords=1"));
    Assert.That(Console.instance.Messages.Single(),Does.Not.Contain("\nid="));
    Assert.That(ZRoutedRpc.instance.Sent.Single().Value,Is.EqualTo("audit"));
    Assert.That(ZDOMan.instance.m_objectsByID.Values.Sum(z=>z.TagReads),Is.Zero);
    Assert.That(Directory.Exists(directory),Is.False);
  }

  [Test] public void PeriodicCountersDoNotFormatThousandsOfRecordDetails()
  {
    for (uint id=1; id<=16248; id++) Add(id);
    PortalCommands.LogCounters();
    Assert.That(Console.instance!.Messages.Single(),Does.Contain("portals=16248").And.Not.Contain("\n"));
    Assert.That(ZDOMan.instance.m_objectsByID.Values.Sum(z=>z.TagReads),Is.Zero);
  }

  [Test] public void ShipFilterAndSmallAuditShowOnlyMatchingRecords()
  {
    for (uint id=1; id<=15; id++) Add(id,id<=4?7:8);
    var token=Audit("7");
    var output=Console.instance!.Messages.Last();
    Assert.That(output,Does.Contain("Showing 4 of 4").And.Contain("portals=4"));
    Assert.That(Regex.Matches(output,@"(?m)^id=").Count,Is.EqualTo(4));
    Assert.That(File.ReadAllText(Path.Combine(directory,"PortalAudits",token+".txt")),Does.Not.Contain("ship=8"));
  }

  [Test] public void EmptyAuditStillHasAReadableIdAndZeroTotal()
  {
    Assert.That(Audit(),Has.Length.EqualTo(6));
    Assert.That(Console.instance!.Messages.Last(),Does.Contain("Showing 0 of 0").And.Contain("0 total portals"));
  }

  [Test] public void ShortIdAcceptsLowercaseAndIsConsumedAfterRepair()
  {
    Add(1);
    var token=Audit();
    PortalCommands.Run(new[] { "repair",token.ToLowerInvariant() });
    Assert.That(Console.instance!.Messages.Last(),Does.StartWith("Registry repaired;"));
    Assert.That(ZDOMan.instance.m_objectsByID,Has.Count.EqualTo(1));
    PortalCommands.Run(new[] { "repair",token });
    Assert.That(Console.instance.Messages.Last(),Does.Contain("fresh audit"));
  }

  [Test] public void AnotherConnectionCannotUseTheShortId()
  {
    Add(1);
    var handler=(Action<long,string>)ZRoutedRpc.instance.Handlers["RewbenRAFT.PortalRequest"];
    handler(100,"audit");
    var token=Regex.Match(ZRoutedRpc.instance.Sent.Last().Value,@"SERVER audit=(\w+)").Groups[1].Value;
    handler(200,"repair " + token);
    Assert.That(ZRoutedRpc.instance.Sent.Last().Value,Does.Contain("fresh audit"));
    Assert.That(ZDOMan.instance.m_objectsByID,Has.Count.EqualTo(1));
  }

  [Test] public void ExpiredShortIdCannotRepair()
  {
    Add(1);
    var token=Audit();
    var cache=(System.Collections.IDictionary)typeof(PortalCommands).GetField("audits",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
    var audit=cache[token]!;
    audit.GetType().GetField("Expires",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(audit,DateTime.UtcNow.AddMinutes(-1));
    PortalCommands.Run(new[] { "repair",token });
    Assert.That(Console.instance!.Messages.Last(),Does.Contain("fresh audit"));
  }

  [Test] public void NonAdminCannotRequestOrRepairAnAudit()
  {
    ZNet.instance!.m_adminList.Clear();
    var handler=(Action<long,string>)ZRoutedRpc.instance.Handlers["RewbenRAFT.PortalRequest"];
    handler(100,"audit");
    Assert.That(ZRoutedRpc.instance.Sent.Last().Value,Does.Contain("require server admin"));
    Assert.That(Directory.Exists(directory),Is.False);
  }

  [Test] public void GeneratedShortIdsStayUniqueAmongExistingReports()
  {
    var existing=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    for(var i=0;i<10000;i++)
    {
      var id=PortalAuditReport.NewId(existing.Contains);
      Assert.That(id,Does.Match("^[0-9A-F]{6}$"));
      Assert.That(existing.Add(id),Is.True);
    }
  }
}
