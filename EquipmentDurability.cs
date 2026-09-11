using FFXIVClientStructs.FFXIV.Client.Game;

namespace FisherDutyScheduler;

internal static class EquipmentDurability
{
    public static unsafe bool TryGetLowestEquippedPercent(out float percent)
    {
        percent = 0;
        var manager = InventoryManager.Instance();
        if (manager is null)
            return false;

        var container = manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (container is null || !container->IsLoaded)
            return false;

        var found = false;
        var lowest = 100f;
        for (var index = 0; index < container->Size; index++)
        {
            var item = container->GetInventorySlot(index);
            if (item is null || item->ItemId == 0)
                continue;

            found = true;
            lowest = Math.Min(lowest, item->Condition / 300f);
        }

        percent = lowest;
        return found;
    }
}
