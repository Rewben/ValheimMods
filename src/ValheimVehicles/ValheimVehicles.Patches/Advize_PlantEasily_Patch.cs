using HarmonyLib;

namespace ValheimVehicles.Patches;

/// <summary>
/// Compatibility Patches for the mod PlantEasy
/// </summary>
public class Advize_PlantEasily_Patch
{
  [HarmonyPatch(typeof(Advize_PlantEasily.PlantEasily), "CheckPlacementStatus")]
  [HarmonyPrefix]
  private static bool CheckPlacementStatus(
    Advize_PlantEasily.PlantEasily __instance,
    ref Player.PlacementStatus __result)
  {
    __result = Player.PlacementStatus.Valid;
    return false;
  }
}