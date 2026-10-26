using System;
using System.Collections.Generic;
using System.Linq;

namespace ValheimVehicles.Fork;

/// <summary>Keep full diagnostics on disk and a small, readable preview in the game console.</summary>
public sealed class PortalAuditReport
{
  public const int PreviewLimit = 10;
  public string Summary { get; }
  public IReadOnlyList<string> Records { get; }

  public PortalAuditReport(string summary, IReadOnlyList<string> records)
  {
    Summary = summary;
    Records = records;
  }

  public string FullText => Summary + "\n" + string.Join("\n", Records);

  public string Preview(string id, int duplicates = 0)
  {
    var count = Math.Min(PreviewLimit, Records.Count);
    return $"SERVER audit={id} | {Summary}\nShowing {count} of {Records.Count} portal records. Full report: PortalAudits/{id}.txt\n" +
      string.Join("\n", Records.Take(PreviewLimit)) +
      (duplicates > 0 ? $"\nCLEANUP {duplicates} extra saved portals: vehicle portals repair {id} --dedupe --confirm" : "") +
      $"\nAUDIT ID: {id} | {Records.Count} total portals | expires in 5 minutes; current connection only.";
  }

  public static string NewId(Func<string, bool> exists)
  {
    string id;
    do { id = Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant(); }
    while (exists(id));
    return id;
  }
}
