using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using Jotunn.Managers;
using UnityEngine;
using ValheimVehicles.BepInExConfig;
using ValheimVehicles.Components;
using ValheimVehicles.Controllers;
using Zolantris.Shared;

namespace ValheimVehicles.Fork;

public static class RebalancePolicy
{
  public static ConfigEntry<float> MaxHullClearance = null!;
  public static ConfigEntry<bool> BlockCannonHarvesting = null!;
  public static ConfigEntry<bool> EnforceRaftLocks = null!;
  public static ConfigEntry<bool> LegacyRefunds = null!;
  public static ConfigEntry<bool> PortalDiagnostics = null!;
  public static ConfigEntry<float> HaulStaminaPerMeter = null!;
  private static ConfigFile config = null!;
  private static bool applying;
  private static bool ready;
  private static readonly Dictionary<string, ConfigEntry<string>> recipes = new();
  private static readonly List<Vector3> vertices = new();

  public static void Bind(ConfigFile target)
  {
    var companion = Directory.EnumerateFiles(Paths.PluginPath, "*.dll", SearchOption.AllDirectories)
      .FirstOrDefault(RebalanceData.IsCompanion);
    if (companion != null)
    {
      Debug.LogError("Rewben RAFT cannot start with RaftRebalance.dll installed. Remove the companion before loading a world.");
      Application.Quit(2);
      throw new InvalidOperationException("RaftRebalance is now integrated; duplicate companion detected: " + companion);
    }
    config = target;
    var legacyPath = Path.Combine(Paths.ConfigPath, "vhrf.RaftRebalance.cfg");
    var legacy = File.Exists(legacyPath) ? RebalanceData.ReadIni(File.ReadAllLines(legacyPath)) : new Dictionary<string, string>();
    var existing = File.Exists(target.ConfigFilePath) ? RebalanceData.ReadIni(File.ReadAllLines(target.ConfigFilePath)) : new Dictionary<string, string>();
    var migrated = existing.TryGetValue("Rebalance Migration/Schema", out var schema) && schema == "1";
    if (!migrated && File.Exists(target.ConfigFilePath) && !File.Exists(target.ConfigFilePath + ".before-rewben")) File.Copy(target.ConfigFilePath, target.ConfigFilePath + ".before-rewben", false);
    string Legacy(string key, string fallback) => !migrated && legacy.TryGetValue(key, out var value) ? value : fallback;
    ConfigDescription Description(string text) => new(text, null, new global::ConfigurationManagerAttributes { IsAdminOnly = true });
    MaxHullClearance = target.Bind("Rebalance", "MaxHullClearance", float.Parse(Legacy("Floatation/MaxHullClearance", "1.5"), System.Globalization.CultureInfo.InvariantCulture), new ConfigDescription("Maximum hull clearance above water in meters.", new AcceptableValueRange<float>(0, 5), new global::ConfigurationManagerAttributes { IsAdminOnly = true }));
    BlockCannonHarvesting = target.Bind("Rebalance", "BlockCannonHarvesting", bool.Parse(Legacy("Cannons/BlockCannonHarvesting", "true")), Description("Block cannon harvesting while allowing creatures, spawners and player buildings."));
    EnforceRaftLocks = target.Bind("Rebalance", "EnforceRaftLocks", bool.Parse(Legacy("Locks/EnforceRaftLocks", "true")), Description("Enforce no flight/ballast/terrain ramming and balanced hauling settings."));
    LegacyRefunds = target.Bind("Rebalance", "LegacyRefunds", bool.Parse(Legacy("Recipes/LegacyRefunds", "true")), Description("Use legacy costs for unmarked pieces built before the rebalance."));
    PortalDiagnostics = target.Bind("Rebalance", "PortalDiagnostics", false, Description("Enable periodic portal counters for validation."));
    existing.TryGetValue("PrefabConfig/VehicleStaminaHaulingCost", out var haulCost);
    HaulStaminaPerMeter = target.Bind("Rebalance", "HaulStaminaPerMeter", float.Parse(haulCost ?? "10", System.Globalization.CultureInfo.InvariantCulture), new ConfigDescription("Stamina per meter under the rebalance policy.", new AcceptableValueRange<float>(0, 100), new global::ConfigurationManagerAttributes { IsAdminOnly = true }));
    foreach (var pair in RebalanceData.Recipes)
    {
      existing.TryGetValue("RecipeConfig/" + pair.Key, out var saved);
      existing.TryGetValue("Rebalance Recipes/" + pair.Key, out var integrated);
      legacy.TryGetValue("Recipes/" + pair.Key, out var prior);
      var selected = RebalanceData.ResolveRecipe(migrated, integrated, prior, saved, pair.Value);
      var entry = target.Bind("Rebalance Recipes", pair.Key, selected, Description("Build/craft requirements: Item,Amount[,Recover][,AmountPerLevel]|..."));
      if (!migrated) entry.Value = selected;
      recipes[pair.Key] = entry;
      entry.SettingChanged += (_, _) => ApplyRecipes();
    }
    target.Bind("Rebalance Migration", "Schema", 0, Description("One-time legacy migration version.")).Value = 1;
    target.SettingChanged += (_, _) => ApplyLocks();
    PieceManager.OnPiecesRegistered += ApplyRecipes;
    ItemManager.OnItemsRegistered += ApplyRecipes;
    SynchronizationManager.OnConfigurationSynchronized += (_, _) => { ApplyLocks(); ApplyRecipes(); };
    target.Save();
    Jotunn.Logger.LogInfo("Rewben RAFT migration schema=1; legacy=" + (!migrated && File.Exists(legacyPath)) + "; companion absent; integrated settings bound");
  }

  public static void CompleteInitialization() { ready = true; ApplyLocks(); }

  public static void ApplyLocks()
  {
    if (!ready || applying || config == null || PrefabConfig.VehicleStaminaHaulingCost == null) return;
    applying = true;
    try
    {
      if (EnforceRaftLocks.Value)
      {
        PropulsionConfig.AllowFlight.Value = false;
        WaterConfig.WaterBallastEnabled.Value = false;
        RamConfig.CanHitEnvironmentOrTerrain.Value = false;
        RamConfig.VehicleRamCanHitEnvironmentOrTerrain.Value = false;
        PrefabConfig.EnableLandVehicles.Value = false;
        PrefabConfig.VehicleStaminaHaulingCost.Value = HaulStaminaPerMeter.Value;
        PrefabConfig.VehicleHaulingSnapsOnStaminaZero.Value = true;
        VehicleGuiMenuConfig.AllowDebugCommandsForNonAdmins.Value = false;
      }
      VehicleMovementController.staminaHaulCost = PrefabConfig.VehicleStaminaHaulingCost.Value;
      VehicleMovementController.ShouldHaulingLineSnapOnZeroStamina = PrefabConfig.VehicleHaulingSnapsOnStaminaZero.Value;
    }
    finally { applying = false; }
  }

  public static Piece.Requirement[]? ParseCost(string raw)
  {
    if (ObjectDB.instance == null) return null;
    var requirements = new List<Piece.Requirement>();
    List<RebalanceData.Cost> parsed;
    try { parsed = RebalanceData.ParseCosts(raw); } catch (FormatException) { return null; }
    foreach (var requirement in parsed)
    {
      var item = ObjectDB.instance.GetItemPrefab(requirement.Item)?.GetComponent<ItemDrop>();
      if (item == null) return null;
      requirements.Add(new Piece.Requirement { m_resItem = item, m_amount = requirement.Amount,
        m_recover = requirement.Recover, m_amountPerLevel = requirement.PerLevel });
    }
    return requirements.Count > 0 ? requirements.ToArray() : null;
  }

  public static void ApplyRecipes()
  {
    if (ObjectDB.instance == null || ObjectDB.instance.m_items.Count == 0) return;
    foreach (var pair in recipes)
    {
      var cost = ParseCost(pair.Value.Value);
      if (cost == null) { Jotunn.Logger.LogWarning("Invalid rebalance recipe: " + pair.Key); continue; }
      var prefab = PrefabManager.Instance.GetPrefab(pair.Key);
      var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
      if (piece != null) piece.m_resources = cost;
      foreach (var recipe in ObjectDB.instance.m_recipes.Where(r => r != null && r.m_item != null && r.m_item.gameObject.name == pair.Key)) recipe.m_resources = cost;
    }
  }

  public static void ClampFlotation(VehiclePiecesController controller)
  {
    if (controller == null || controller.Manager == null || controller.Manager.Config == null || controller.convexHullComponent == null || controller.FloatCollider == null || MaxHullClearance == null) return;
    var collider = controller.FloatCollider;
    var frame = collider.transform.parent;
    var lowest = float.PositiveInfinity;
    foreach (var hull in controller.convexHullComponent.convexHullMeshColliders)
    {
      if (hull == null || !hull.enabled || hull.sharedMesh == null || !hull.sharedMesh.isReadable) continue;
      vertices.Clear(); hull.sharedMesh.GetVertices(vertices);
      foreach (var vertex in vertices) lowest = Mathf.Min(lowest, frame.InverseTransformPoint(hull.transform.TransformPoint(vertex)).y);
    }
    vertices.Clear();
    var floor = ForkRules.FlotationFloor(lowest, MaxHullClearance.Value);
    var position = collider.transform.localPosition;
    if (position.y < floor) { position.y = floor; collider.transform.localPosition = position; Physics.SyncTransforms(); }
    if (controller.Manager.Config.HasCustomFloatationHeight && controller.Manager.Config.CustomFloatationHeight < floor)
      controller.Manager.Config.CustomFloatationHeight = floor;
  }

  public static bool CanCannonHit(Collider collider)
  {
    if (BlockCannonHarvesting == null || !BlockCannonHarvesting.Value) return true;
    var creature = collider.GetComponentInParent<Character>() != null || collider.GetComponentInParent<SpawnArea>() != null || collider.GetComponentInParent<CreatureSpawner>() != null;
    var piece = collider.GetComponentInParent<Piece>();
    var destructible = collider.GetComponentInParent<Destructible>();
    var spawn = destructible != null ? destructible.m_spawnWhenDestroyed : null;
    var mineSpawn = spawn != null && (spawn.GetComponent<MineRock>() != null || spawn.GetComponent<MineRock5>() != null);
    var harvestable = collider.GetComponentInParent<TreeBase>() != null || collider.GetComponentInParent<TreeLog>() != null ||
        collider.GetComponentInParent<MineRock>() != null || collider.GetComponentInParent<MineRock5>() != null ||
      (destructible != null && (destructible.m_destructibleType == DestructibleType.Tree || destructible.m_minToolTier > 0 ||
      destructible.GetComponent<DropOnDestroyed>() != null || mineSpawn));
    var wear = collider.GetComponentInParent<WearNTear>();
    harvestable |= wear != null && wear.m_minToolTier > 0;
    return ForkRules.CannonTargetAllowed(true, creature, harvestable, piece != null && piece.IsPlacedByPlayer());
  }
}
