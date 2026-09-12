using FFXIVClientStructs.FFXIV.Client.Game;

namespace FisherDutyScheduler;

internal static class InventoryCapacity
{
    public static unsafe bool TryGetEmptyBagSlots(out int emptySlots)
    {
        emptySlots = 0;
        var manager = InventoryManager.Instance();
        if (manager is null)
            return false;

        try
        {
            emptySlots = (int)manager->GetEmptySlotsInBag();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
