using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using Logger = Jotunn.Logger;

namespace ValheimVehicles.Fork;

/// <summary>Clear only TargetPortal's temporary portal pins after an explicit repair.</summary>
public static class PortalMapRefresh
{
  public static void AfterRepair()
  {
    if (!Chainloader.PluginInfos.TryGetValue("org.bepinex.plugins.targetportal", out var plugin)) return;
    try
    {
      var assembly = plugin.Instance.GetType().Assembly;
      var known = assembly.GetType("TargetPortal.TargetPortal")?.GetField("knownPortals", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as ICollection<ZDO>;
      if (known != null)
        foreach (var record in known.Where(z => !PortalRegistry.IsPortal(z) || !ReferenceEquals(ZDOMan.instance.GetZDO(z.m_uid), z)).ToList())
          known.Remove(record);
      var map = assembly.GetType("TargetPortal.Map");
      var pins = map?.GetField("activePins", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IDictionary;
      if (pins != null && pins.Count > 0)
        map!.GetMethod("RemovePortalPins", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null);
    }
    catch (Exception e) { Logger.LogWarning("Portal records repaired; TargetPortal pin refresh failed: " + e.GetBaseException().Message + ". Reconnect to refresh the map."); }
  }
}
