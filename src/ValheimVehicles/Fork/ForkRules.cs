using System;
using System.Collections.Generic;

namespace ValheimVehicles.Fork;

// Kept independent of Unity so the production rules also run in headless tests.
public static class ForkRules
{
  public static List<T> UniqueValid<T, TId>(IEnumerable<T> values, Func<T, bool> valid, Func<T, TId> identity)
  {
    var ids = new HashSet<TId>();
    var result = new List<T>();
    foreach (var value in values)
      if (valid(value) && ids.Add(identity(value))) result.Add(value);
    return result;
  }

  public static double RemainingSeconds(double interval, double elapsedSeconds) => Math.Max(0, interval - elapsedSeconds);
  public static float CreativeLift(float requested, bool admin) => admin ? requested : Math.Max(0, Math.Min(5, requested));
  public static float HaulCost(float distance, float rate) => Math.Max(0, distance) * Math.Max(0, rate);
  public static bool HaulExhausted(float stamina, float cost) => stamina <= cost;
  public static float FlotationFloor(float lowestHullPoint, float clearance) => (float.IsInfinity(lowestHullPoint) ? 0 : lowestHullPoint) - Math.Max(0, clearance);
  public static bool CannonTargetAllowed(bool enabled, bool creatureOrSpawner, bool harvestable, bool playerBuilding)
    => !enabled || creatureOrSpawner || (!harvestable || playerBuilding);
  public static string MigratedValue(string? integrated, string? legacy, string? existing, string fallback)
    => integrated ?? legacy ?? existing ?? fallback;
}
