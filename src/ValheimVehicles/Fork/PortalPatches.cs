using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimVehicles.Fork;

[HarmonyPatch]
public static class PortalPatches
{
  [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
  [HarmonyPrefix]
  private static bool GuardDuplicate(ZDO zdo, ref GameObject __result)
  {
    if (!PortalRegistry.IsShipPortal(zdo)) return true;
    var existing = ZNetScene.instance.FindInstance(zdo);
    if (existing == null) return true;
    __result = existing.gameObject;
    PortalRegistry.PreventedCreations++;
    return false;
  }

  [HarmonyPatch(typeof(ZDO), nameof(ZDO.Reset))]
  [HarmonyPrefix]
  private static void ResetRecord(ZDO __instance) => PortalRegistry.BeforeReset(__instance);

  [HarmonyPatch(typeof(Game), nameof(Game.Start))]
  [HarmonyPostfix]
  private static void RegisterCommands() => PortalCommands.Register();

  [HarmonyPatch(typeof(Game), nameof(Game.ConnectPortals))]
  [HarmonyTranspiler]
  private static IEnumerable<CodeInstruction> FilterPairing(IEnumerable<CodeInstruction> instructions)
  {
    var getter = AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.GetPortalList));
    var replaced = 0;
    foreach (var instruction in instructions)
    {
      if (instruction.Calls(getter))
      {
        instruction.opcode = OpCodes.Call;
        instruction.operand = AccessTools.Method(typeof(PortalRegistry), nameof(PortalRegistry.VanillaPairingCandidates));
        replaced++;
      }
      yield return instruction;
    }
    if (replaced != 1) throw new System.InvalidOperationException("Rewben RAFT pairing hook expected one GetPortalList call; found " + replaced + ". Recheck game/mod compatibility.");
  }
}
