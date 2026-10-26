using System;
using System.Linq;
using HarmonyLib;

namespace ValheimVehicles.Fork;

[HarmonyPatch]
public static class RefundPatches
{
  private static string Prefab(Piece piece) => Utils.GetPrefabName(piece.gameObject);

  [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
  [HarmonyPrefix]
  private static void BeforeCreator(Piece __instance, out bool __state) => __state = __instance.GetCreator() == 0;

  [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
  [HarmonyPostfix]
  private static void StampCost(Piece __instance, bool __state)
  {
    if (!__state || __instance.GetCreator() == 0 || !RebalanceData.Legacy.ContainsKey(Prefab(__instance))) return;
    var view = __instance.GetComponent<ZNetView>();
    if (view == null || !view.IsValid() || !view.IsOwner()) return;
    var cost = string.Join("|", __instance.m_resources.Where(r => r?.m_resItem != null)
      .Select(r => $"{r.m_resItem.gameObject.name},{r.m_amount},{r.m_recover.ToString().ToLowerInvariant()},{r.m_amountPerLevel}"));
    view.GetZDO().Set(RebalanceData.PaidCostKey, cost);
  }

  [HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
  [HarmonyPrefix]
  private static void RefundCost(Piece __instance, out Piece.Requirement[]? __state)
  {
    __state = null;
    if (!RebalanceData.Legacy.TryGetValue(Prefab(__instance), out var legacy)) return;
    var view = __instance.GetComponent<ZNetView>();
    var paid = view != null && view.IsValid() ? view.GetZDO().GetString(RebalanceData.PaidCostKey) : "";
    if (string.IsNullOrEmpty(paid) && RebalancePolicy.LegacyRefunds.Value) paid = legacy;
    if (string.IsNullOrEmpty(paid)) return;
    var cost = RebalancePolicy.ParseCost(paid);
    if (cost == null) throw new InvalidOperationException("Cannot safely refund the saved build cost for " + Prefab(__instance));
    __state = __instance.m_resources;
    __instance.m_resources = cost;
  }

  [HarmonyPatch(typeof(Piece), nameof(Piece.DropResources))]
  [HarmonyFinalizer]
  private static Exception? Restore(Piece __instance, Piece.Requirement[]? __state, Exception? __exception)
  {
    if (__state != null) __instance.m_resources = __state;
    return __exception;
  }
}
