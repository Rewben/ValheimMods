using Jotunn.Managers;
using Zolantris.Shared;

namespace ValheimVehicles.Fork;

public static class ForkAccess
{
  public static bool IsAdmin()
  {
    if (ZNet.instance == null) return false;
    return (SynchronizationManager.Instance != null && SynchronizationManager.Instance.PlayerIsAdmin) ||
      ZNet.instance.LocalPlayerIsAdminOrHost();
  }
}
