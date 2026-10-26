// Test doubles implement only the inspected game contracts used by production PortalRegistry.
// No Unity/game assemblies or replacement implementation of the registry are used by these tests.
using System.Collections;
namespace UnityEngine
{
  public struct Vector3 { public float x,y,z; public Vector3(float x,float y=0,float z=0) { this.x=x;this.y=y;this.z=z; } }
  public struct Quaternion { }
  public static class Time { public static float realtimeSinceStartup; }
  public sealed class GameObject { }
}
public readonly record struct ZDOID(long UserID, uint ID)
{
  public static ZDOID None => default;
}
public static class ZoneSystem
{
  public readonly record struct SectorIndex(int Sector);
  public static SectorIndex GetSectorIndex(UnityEngine.Vector3 p) => new((int)Math.Floor(p.x/64));
}
public sealed class ZDO
{
  public ZDOID m_uid;
  public bool Valid = true, SaveClone;
  public int Prefab = "portal_wood".GetStableHashCode(), Parent = 7;
  public UnityEngine.Vector3 Position;
  public bool IsValid() => Valid;
  public int GetPrefab() => Prefab;
  public int GetInt(int key) => Parent;
  public ZoneSystem.SectorIndex GetSectorIndex() => ZoneSystem.GetSectorIndex(Position);
  public void SetPosition(UnityEngine.Vector3 value) => Position=value;
  public void SetRotation(UnityEngine.Quaternion rotation) { }
  public void Reset() { }
}
public sealed class ZDOMan
{
  public static ZDOMan instance = null!;
  public readonly Dictionary<ZDOID,ZDO> m_objectsByID = new();
  public readonly List<ZDO>?[] m_objectsBySector = new List<ZDO>?[16];
  public readonly Dictionary<ZoneSystem.SectorIndex,List<ZDO>> m_portalObjects = new();
  public int Invalidations, DirtyCalls;
  public ZDO? GetZDO(ZDOID id) => m_objectsByID.GetValueOrDefault(id);
  public List<ZDO> GetPortalList() => m_portalObjects.Values.SelectMany(x=>x).ToList();
  public Dictionary<ZoneSystem.SectorIndex,List<ZDO>> GetPortals() => m_portalObjects;
  public void SetDirtyPortals() => DirtyCalls++;
  public void ZDOSectorInvalidated(ZDO zdo) => Invalidations++;
}
public sealed class ZNet
{
  public static ZNet? instance;
  public bool Server, LocalAdmin;
  public bool IsServer() => Server;
}
public sealed class ZNetView
{
  public UnityEngine.GameObject gameObject = new();
  public ZDO Record = null!;
  public ZDO GetZDO() => Record;
  public bool IsValid() => Record != null && Record.Valid;
}
public sealed class Game
{
  public void Start() { }
  public void ConnectPortals() { }
  public static Game instance = new();
  public readonly HashSet<int> PortalPrefabHash = new() { "portal_wood".GetStableHashCode(), "portal_stone".GetStableHashCode() };
}
public sealed class ZNetScene
{
  public static ZNetScene instance=new();
  public readonly Dictionary<ZDOID,ZNetView> Instances=new();
  public ZNetView? FindInstance(ZDO record) => Instances.GetValueOrDefault(record.m_uid);
  public UnityEngine.GameObject CreateObject(ZDO zdo) => new();
}
public static class HashExtensions
{
  public static int GetStableHashCode(this string s) { var hash=0;foreach(var c in s) hash=unchecked(hash*31+c); return hash; }
}
namespace ValheimVehicles.Controllers
{
  public sealed class VehiclePiecesController { public readonly List<ZNetView> m_portals = new(); }
}
namespace ValheimVehicles.Shared.Constants { public static class VehicleZdoVars { public const int MBParentId=1; } }
namespace ValheimVehicles.Fork
{
  public static class PortalCommands { public static void Reset() { } public static void Register() { } public static void LogCounters() { } }
  public static class RebalancePolicy { public static FakeEntry PortalDiagnostics=new(); }
  public sealed class FakeEntry { public bool Value; }
}
namespace BepInEx.Bootstrap
{
  public static class Chainloader { public static readonly Dictionary<string,PluginInfo> PluginInfos=new(); }
  public sealed class PluginInfo { public FakePlugin Instance=new(); }
  public sealed class FakePlugin { public FakeConfig Config=new(); }
  public sealed class FakeConfig : List<KeyValuePair<FakeKey,FakeValue>> { }
  public sealed class FakeKey { public string Section="1 - General",Key="Limit to vanilla portals"; }
  public sealed class FakeValue { public object BoxedValue="Off"; }
}
namespace Jotunn.Managers
{
  public sealed class SynchronizationManager { public static SynchronizationManager? Instance; public bool PlayerIsAdmin; }
}
namespace Zolantris.Shared
{
  public static class ZNetExtensions { public static bool LocalPlayerIsAdminOrHost(this ZNet net) => net.LocalAdmin; }
}
