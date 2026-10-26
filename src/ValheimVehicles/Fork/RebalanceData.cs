using System.Collections.Generic;

namespace ValheimVehicles.Fork;

public static class RebalanceData
{
  public static bool IsCompanion(string path)
  {
    if (string.Equals(System.IO.Path.GetFileName(path), "RaftRebalance.dll", System.StringComparison.OrdinalIgnoreCase)) return true;
    try { return string.Equals(System.Reflection.AssemblyName.GetAssemblyName(path).Name, "RaftRebalance", System.StringComparison.OrdinalIgnoreCase); }
    catch (System.BadImageFormatException) { return false; } // Native libraries are not plugins.
  }
  public sealed class Cost
  {
    public string Item = "";
    public int Amount;
    public bool Recover = true;
    public int PerLevel;
  }
  public static List<Cost> ParseCosts(string text)
  {
    var result = new List<Cost>();
    foreach (var term in text.Split('|'))
    {
      var fields = term.Split(',');
      if (fields.Length < 2 || fields.Length > 4 || string.IsNullOrWhiteSpace(fields[0]) ||
          !int.TryParse(fields[1], out var amount) || amount < 0 || amount > 999)
        throw new System.FormatException("Invalid recipe requirement: " + term);
      var recover = true;
      if (fields.Length > 2 && !bool.TryParse(fields[2], out recover)) throw new System.FormatException("Invalid recover flag");
      var level = 0;
      if (fields.Length > 3 && (!int.TryParse(fields[3], out level) || level < 0 || level > 999)) throw new System.FormatException("Invalid per-level amount");
      result.Add(new Cost { Item = fields[0].Trim(), Amount = amount, Recover = recover, PerLevel = level });
    }
    return result;
  }
  public static string ResolveRecipe(bool migrated, string? integrated, string? legacy, string? existing, string fallback)
    => ForkRules.MigratedValue(integrated, migrated ? null : legacy, existing, fallback);
  public const string PaidCostKey = "vhrf_paid_v1";
  public static readonly Dictionary<string, string> Recipes = new()
  {
    ["ValheimVehicles_ShipRudderAdvanced_Wood"] = "FineWood,10|BronzeNails,10",
    ["ValheimVehicles_ShipRudderAdvanced_Tail_Wood"] = "FineWood,20|BronzeNails,20",
    ["ValheimVehicles_ShipRudderAdvanced_Iron"] = "Iron,4|YggdrasilWood,6",
    ["ValheimVehicles_ShipRudderAdvanced_Tail_Iron"] = "Iron,8|YggdrasilWood,12",
    ["ValheimVehicles_ram_stake_tier1_1x2"] = "RoundLog,2|Bronze,1",
    ["ValheimVehicles_ram_stake_tier1_2x4"] = "RoundLog,4|Bronze,2",
    ["ValheimVehicles_Cannon_Fixed_Tier1"] = "Iron,4|FineWood,6",
    ["ValheimVehicles_Cannon_Turret_Tier1"] = "Iron,6|Chain,1",
    ["ValheimVehicles_Cannonball_Solid"] = "Iron,1|Coal,4",
    ["ValheimVehicles_Cannon_Handheld_Item"] = "Iron,4,true,1|Chain,1"
  };
  public static readonly Dictionary<string, string> Legacy = new()
  {
    ["ValheimVehicles_ShipRudderAdvanced_Wood"] = "FineWood,20",
    ["ValheimVehicles_ShipRudderAdvanced_Tail_Wood"] = "FineWood,20",
    ["ValheimVehicles_ShipRudderAdvanced_Iron"] = "FineWood,20",
    ["ValheimVehicles_ShipRudderAdvanced_Tail_Iron"] = "FineWood,20",
    ["ValheimVehicles_ram_stake_tier1_1x2"] = "Iron,3|RoundLog,2",
    ["ValheimVehicles_ram_stake_tier1_2x4"] = "Iron,6|RoundLog,4",
    ["ValheimVehicles_Cannon_Fixed_Tier1"] = "Bronze,4|Wood,6",
    ["ValheimVehicles_Cannon_Turret_Tier1"] = "Bronze,4|Chain,1|Iron,2"
  };

  public static Dictionary<string, string> ReadIni(IEnumerable<string> lines)
  {
    var result = new Dictionary<string, string>();
    var section = "";
    foreach (var raw in lines)
    {
      var line = raw.Trim();
      if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
      if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
      var equals = line.IndexOf('=');
      if (equals > 0) result[section + "/" + line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
    }
    return result;
  }
}
