namespace FisherDutyScheduler;

internal static class DutyCatalog
{
    public static readonly DutyOption[] SupportLevelingDuties =
    [
        new(1036, 15, "Sastasha"),
        new(1037, 16, "The Tam-Tara Deepcroft"),
        new(1039, 24, "The Thousand Maws of Toto-Rak"),
        new(1041, 32, "Brayflox's Longstop"),
        new(1303, 38, "Cutter's Cry"),
        new(1042, 41, "The Stone Vigil"),
        new(1330, 44, "Dzemael Darkhold"),
        new(1331, 47, "The Aurum Vale"),
        new(1048, 50, "The Porta Decumana"),
        new(1043, 50, "Castrum Meridianum"),
        new(1366, 51, "The Dusk Vigil"),
        new(1064, 53, "Sohm Al"),
        new(1065, 55, "The Aery"),
        new(1066, 57, "The Vault"),
        new(1109, 59, "The Great Gubal Library"),
        new(1142, 61, "The Sirensong Sea"),
        new(1144, 67, "Doma Castle"),
        new(1145, 69, "Castrum Abania"),
        new(837, 71, "Holminster Switch"),
        new(821, 73, "Dohn Mheg"),
        new(823, 75, "The Qitana Ravel"),
        new(836, 77, "Malikah's Well"),
        new(822, 79, "Mt. Gulg"),
        new(952, 81, "The Tower of Zot"),
        new(969, 83, "The Tower of Babil"),
        new(970, 85, "Vanaspati"),
        new(974, 87, "Ktisis Hyperboreia / 创造环境极北造物院"),
        new(978, 89, "The Aitiascope / 星海深幽寻因星晶镜"),
        new(1167, 91, "Ihuykatumu"),
        new(1193, 93, "Worqor Zormor"),
        new(1194, 95, "The Skydeep Cenote"),
        new(1198, 97, "Vanguard"),
        new(1208, 99, "Origenics"),
    ];

    public static DutyOption? Find(uint territoryId)
    {
        foreach (var duty in SupportLevelingDuties)
            if (duty.TerritoryId == territoryId)
                return duty;
        return null;
    }
}

internal readonly record struct DutyOption(uint TerritoryId, int MinimumLevel, string Name);
