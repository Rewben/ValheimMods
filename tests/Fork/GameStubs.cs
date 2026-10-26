// Test doubles implement only the inspected game contracts used by the production portal code.
// No Unity/game assemblies or replacement implementation of the registry are used by these tests.
using System.Collections;
namespace UnityEngine
{
  public struct Vector3 { public float x,y,z; public Vector3(float x,float y=0,float z=0) { this.x=x;this.y=y;this.z=z; } }
  public struct Quaternion { }
  public static class Time { public static float realtimeSinceStartup; }
  public sealed class GameObject { public bool Destroyed; }
  public sealed class WaitForSeconds { public WaitForSeconds(float seconds) { } }
  public sealed class Transform { public Vector3 position; public void SetParent(object? parent, bool keepPosition) { } }
  public static class Object { public static void Destroy(GameObject value) => value.Destroyed=true; }
  public static class Resources
  {
    public static readonly List<object> Objects = new();
    public static T[] FindObjectsOfTypeAll<T>() => Objects.OfType<T>().ToArray();
  }
}
public readonly record struct ZDOID(long UserID, uint ID)
{
  public static ZDOID None => default;
  public override string ToString() => $"{UserID}:{ID}";
}
public static class ZoneSystem
{
  public readonly record struct SectorIndex(int Sector);
  public static SectorIndex GetSectorIndex(UnityEngine.Vector3 p) => new((int)Math.Floor(p.x/64));
}
public sealed class ZDO : IEquatable<ZDO>
{
  public ZDOID m_uid;
  public override int GetHashCode() => m_uid.GetHashCode();
  public bool Equals(ZDO? other) => other != null && m_uid == other.m_uid;
  public bool Valid = true, SaveClone;
  public string Tag = "Test portal";
  public int TagReads;
  public int Prefab = "portal_wood".GetStableHashCode(), Parent = 7;
  public UnityEngine.Vector3 Position;
  public UnityEngine.Vector3? Local;
  public int Swivel;
  public bool IsValid() => Valid;
  public int GetPrefab() => Prefab;
  public int GetInt(int key) => Parent;
  public int GetInt(string key) => Swivel;
  public bool GetVec3(int key, out UnityEngine.Vector3 value) { value=Local.GetValueOrDefault(); return Local.HasValue; }
  public string GetString(int key) { TagReads++; return Tag; }
  public UnityEngine.Vector3 GetPosition() => Position;
  public void SetOwner(long owner) { }
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
  public static long GetSessionID() => 17;
  private readonly List<ZDOID> destroyed = new();
  public void DestroyZDO(ZDO record) => destroyed.Add(record.m_uid);
  public void FlushDestroyed()
  {
    foreach(var id in destroyed)
      if(m_objectsByID.Remove(id,out var record)) { ValheimVehicles.Fork.PortalRegistry.BeforeReset(record); record.Valid=false; record.m_uid=ZDOID.None; }
    destroyed.Clear();
  }
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
  public readonly List<string> m_adminList = new();
  public ZNetPeer Peer = new();
  public bool MissingPeer;
  public ZNetPeer? GetPeer(long sender) => MissingPeer?null:Peer;
  public ZNetPeer GetServerPeer() => Peer;
}
public sealed class ZNetPeer { public long m_uid=99; public FakeSocket m_socket=new(); }
public sealed class FakeSocket { public string Host="Steam_123"; public string GetHostName() => Host; }
public sealed class ZRoutedRpc
{
  public static ZRoutedRpc instance = new();
  public const long Everybody = 0;
  public readonly Dictionary<string,Delegate> Handlers = new();
  public readonly List<(long Peer,string Name,string Value)> Sent = new();
  public void Register<T>(string name, Action<long,T> callback) => Handlers[name]=callback;
  public void InvokeRoutedRPC(long peer,string name,string value) => Sent.Add((peer,name,value));
}
public static class ZDOVars { public const int s_tag=2; }
public sealed class TeleportWorld { public ZNetView m_nview=null!; }
public sealed class Player { public static Player? m_localPlayer; }
public sealed class Console
{
  public static Console? instance = new();
  public readonly List<string> Messages = new();
  public void Print(string message) => Messages.Add(message);
}
public sealed class ZNetView
{
  public UnityEngine.GameObject gameObject = new();
  public ZDO Record = null!;
  public ZDO? m_zdo { get=>Record; set=>Record=value!; }
  public UnityEngine.Transform transform = new();
  public object? Parent;
  public ZDO GetZDO() => Record;
  public bool IsValid() => Record != null && Record.Valid;
  public int GetInstanceID() => GetHashCode();
  public T? GetComponentInParent<T>() where T:class => Parent as T;
}
public sealed class Game
{
  public void Start() { }
  public void ConnectPortals() { }
  public static Game instance = new();
  public readonly HashSet<int> PortalPrefabHash = new() { "portal_wood".GetStableHashCode(), "portal_stone".GetStableHashCode() };
  public readonly List<IEnumerator> Coroutines = new();
  public void StartCoroutine(IEnumerator routine) { if(routine.MoveNext()) Coroutines.Add(routine); }
  public void AdvanceFrame()
  {
    ZDOMan.instance.FlushDestroyed();
    foreach(var routine in Coroutines.ToArray()) if(!routine.MoveNext()) Coroutines.Remove(routine);
  }
  public void CompleteCoroutines()
  {
    for(var frame=0; Coroutines.Count>0 && frame<10000; frame++) AdvanceFrame();
    if(Coroutines.Count>0) throw new InvalidOperationException("Coroutine did not complete");
  }
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
  public sealed class VehiclePiecesController
  {
    public static readonly Dictionary<int,VehiclePiecesController> ActiveInstances = new();
    public readonly List<ZNetView> m_portals = new();
    public void RemovePiece(ZNetView view) => m_portals.Remove(view);
  }
}
namespace ValheimVehicles.Shared.Constants { public static class VehicleZdoVars { public const int MBParentId=1, MBPositionHash=3; public const string SwivelParentId="SwivelParentId"; } }
namespace ValheimVehicles.Fork
{
  public static class RebalancePolicy { public static FakeEntry PortalDiagnostics=new(); }
  public sealed class FakeEntry { public bool Value; }
}
namespace BepInEx { public static class Paths { public static string ConfigPath = ""; } }
namespace Jotunn
{
  public static class Logger
  {
    public static readonly List<string> Messages = new();
    public static void LogInfo(string message) => Messages.Add(message);
    public static void LogWarning(string message) => Messages.Add(message);
  }
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
