using Dalamud.Game;
using ECommons.ExcelServices;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System.Collections;

namespace Hyperborea.Services;

public sealed class TerritoryDiscoveryService
{
    static readonly ClientLanguage[] SearchLanguageCandidates =
    [
        ClientLanguage.Japanese,
        ClientLanguage.English,
        ClientLanguage.German,
        ClientLanguage.French,
        ClientLanguage.ChineseSimplified,
        ClientLanguage.ChineseTraditional,
        ClientLanguage.Korean,
    ];

    static readonly TerritoryIntendedUseEnum[] RegularTerritoryUses =
    [
        TerritoryIntendedUseEnum.City_Area,
        TerritoryIntendedUseEnum.Open_World,
        TerritoryIntendedUseEnum.Housing_Instances,
        TerritoryIntendedUseEnum.Residential_Area,
        TerritoryIntendedUseEnum.Inn,
        TerritoryIntendedUseEnum.Dungeon,
        TerritoryIntendedUseEnum.Variant_Dungeon,
        TerritoryIntendedUseEnum.Criterion_Duty,
        TerritoryIntendedUseEnum.Criterion_Savage_Duty,
        TerritoryIntendedUseEnum.Raid,
        TerritoryIntendedUseEnum.Raid_2,
        TerritoryIntendedUseEnum.Alliance_Raid,
        TerritoryIntendedUseEnum.Large_Scale_Raid,
        TerritoryIntendedUseEnum.Large_Scale_Savage_Raid,
        TerritoryIntendedUseEnum.Trial,
        TerritoryIntendedUseEnum.Deep_Dungeon,
    ];

    readonly Dictionary<ClientLanguage, bool> languageSupport = [];
    readonly HashSet<string> loggedWarnings = [];
    readonly object cacheLock = new();

    List<TerritoryBrowserEntry> cachedEntries = [];
    Dictionary<uint, TerritoryBrowserEntry> cachedTerritoryEntries = [];
    Dictionary<string, TerritoryBrowserEntry> cachedKeyEntries = [];
    bool? cachedIncludeCutscene;

    public TerritoryDiscoveryStats Stats { get; private set; } = new();
    public string Status { get; private set; } = "Not built";
    public bool UsedFallbackNames { get; private set; }

    public IReadOnlyList<TerritoryBrowserEntry> GetEntries(bool includeCutsceneTerritories)
    {
        lock (cacheLock)
        {
            if (cachedIncludeCutscene != includeCutsceneTerritories || cachedEntries.Count == 0)
            {
                Rebuild(includeCutsceneTerritories);
            }

            return cachedEntries;
        }
    }

    public TerritoryBrowserEntry? GetTerritoryEntry(uint territoryId)
    {
        if (territoryId == 0) return null;
        lock (cacheLock)
        {
            if (cachedEntries.Count == 0)
            {
                Rebuild(false);
            }

            if (cachedTerritoryEntries.TryGetValue(territoryId, out var entry)) return entry;
            return TryGetTerritoryRow(territoryId, out var territory)
                ? CreateTerritoryEntry(territory)
                : CreateFallbackTerritoryEntry(territoryId);
        }
    }

    public TerritoryBrowserEntry? GetEntryByKey(string key)
    {
        lock (cacheLock)
        {
            return cachedKeyEntries.TryGetValue(key, out var entry) ? entry : null;
        }
    }

    public void Invalidate()
    {
        lock (cacheLock)
        {
            cachedIncludeCutscene = null;
            cachedEntries = [];
            cachedTerritoryEntries = [];
            cachedKeyEntries = [];
            Status = "Cache invalidated";
        }
    }

    public void Rebuild(bool includeCutsceneTerritories)
    {
        UsedFallbackNames = false;
        Status = "Building territory cache...";
        var stats = new TerritoryDiscoveryStats();
        var entries = new Dictionary<string, TerritoryBrowserEntry>();
        var territoryEntries = new Dictionary<uint, TerritoryBrowserEntry>();
        var sceneEntries = new Dictionary<string, TerritoryBrowserEntry>();

        LoadNormalTerritories(entries, territoryEntries, stats);
        if (includeCutsceneTerritories)
        {
            LoadExtraTerritoriesFromTerritoryType(entries, territoryEntries, stats);
            LoadMapTerritories(entries, territoryEntries, stats);
            LoadCutsceneTerritoriesFromQuestData(entries, territoryEntries, sceneEntries, stats);
            LoadInstanceContentTerritories(entries, territoryEntries, sceneEntries, stats);
            LoadPublicContentTerritories(entries, territoryEntries, sceneEntries, stats);
            LoadWarpTerritories(entries, territoryEntries, sceneEntries, stats);
            LoadAetheryteTerritories(entries, territoryEntries, stats);
            LoadGatheringPointTerritories(entries, territoryEntries, stats);
            LoadQuestEventAreaTerritories(entries, territoryEntries, stats);
            LoadReflectiveEventTerritories(entries, territoryEntries, sceneEntries, stats);
            LoadCutsceneSceneOnly(entries, sceneEntries, stats);
        }

        foreach (var entry in entries.Values)
        {
            FinalizeEntry(entry);
        }

        cachedEntries = entries.Values
            .OrderBy(x => x.HasTerritory ? 0 : 1)
            .ThenBy(x => x.TerritoryId ?? uint.MaxValue)
            .ThenBy(x => x.DisplayName)
            .ToList();
        cachedTerritoryEntries = territoryEntries;
        cachedKeyEntries = cachedEntries.ToDictionary(x => x.Key, x => x);
        cachedIncludeCutscene = includeCutsceneTerritories;
        Stats = stats.WithEntryCounts(cachedEntries);
        Status = $"Built {cachedEntries.Count} entries";
    }

    void LoadNormalTerritories(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, TerritoryDiscoveryStats stats)
    {
        foreach (var territory in EnumerateSheet<TerritoryType>("TerritoryType", stats))
        {
            var entry = CreateTerritoryEntry(territory);
            if (!entry.WasVisibleInOldSelector) continue;
            entry.AddTag("[Normal]");
            entry.AddTag("[TerritoryType]");
            entry.AddSource(new("TerritoryType", territory.RowId, "Normal territory", "PlaceName/CFC/QuestBattle", "[Normal]"));
            AddEntry(entries, territoryEntries, entry);
            stats.AddCandidate("TerritoryType.Normal");
        }
    }

    void LoadExtraTerritoriesFromTerritoryType(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, TerritoryDiscoveryStats stats)
    {
        foreach (var territory in EnumerateSheet<TerritoryType>("TerritoryType", stats))
        {
            var entry = AddTerritory(entries, territoryEntries, territory.RowId);
            if (entry == null) continue;
            entry.AddTag("[TerritoryType]");
            entry.AddSource(new("TerritoryType", territory.RowId, "TerritoryType row", "Bg/Map/PlaceName", "[TerritoryType]"));
            stats.AddCandidate("TerritoryType.Extra");
        }
    }

    void LoadMapTerritories(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, TerritoryDiscoveryStats stats)
    {
        foreach (var map in EnumerateSheet<Map>("Map", stats))
        {
            var territoryId = SafeRowId(() => map.TerritoryType.RowId, "Map.TerritoryType");
            if (territoryId == 0) continue;
            var entry = AddTerritory(entries, territoryEntries, territoryId);
            if (entry == null) continue;
            entry.MapId = entry.MapId == 0 ? map.RowId : entry.MapId;
            if (map.IsEvent) entry.AddTag("[EventScene]");
            entry.AddSource(new("Map", map.RowId, SafeText(() => map.Id.GetText() ?? "", "Map.Id"), "TerritoryType", map.IsEvent ? "[EventScene]" : "[Map]"));
            stats.AddCandidate("Map");
        }
    }

    void LoadCutsceneTerritoriesFromQuestData(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, Dictionary<string, TerritoryBrowserEntry> sceneEntries, TerritoryDiscoveryStats stats)
    {
        foreach (var quest in EnumerateSheet<Quest>("Quest", stats))
        {
            if (quest.RowId == 0) continue;
            var questName = SafeText(() => quest.Name.GetText() ?? "", "Quest.Name").NullWhenEmpty() ?? $"Quest {quest.RowId}";
            var questTag = quest.HideInScenarioGuide ? "[SideQuest]" : "[MSQ]";
            var source = new TerritorySourceInfo("Quest", quest.RowId, questName, "Quest locations", "[Quest]")
            {
                QuestId = quest.RowId,
                QuestName = questName,
            };

            AddQuestLevel(entries, territoryEntries, quest.IssuerLocation.RowId, source with { Field = "IssuerLocation", Tag = questTag }, stats);

            foreach (var todo in SafeCollection(() => quest.TodoParams, "Quest.TodoParams"))
            {
                foreach (var location in SafeCollection(() => todo.ToDoLocation, "Quest.TodoLocation"))
                {
                    AddQuestLevel(entries, territoryEntries, location.RowId, source with { Field = "TodoLocation", Tag = questTag }, stats);
                }
            }

            AddInstanceContent(entries, territoryEntries, sceneEntries, quest.InstanceContentUnlock.RowId, source with { Field = "InstanceContentUnlock", Tag = "[Instance]" }, stats);
            foreach (var instance in SafeCollection(() => quest.InstanceContent, "Quest.InstanceContent"))
            {
                AddInstanceContent(entries, territoryEntries, sceneEntries, instance.RowId, source with { Field = "InstanceContent", Tag = "[Instance]" }, stats);
            }

            foreach (var questParam in SafeCollection(() => quest.QuestParams, "Quest.QuestParams"))
            {
                var instruction = SafeText(() => questParam.ScriptInstruction.GetText() ?? "", "Quest.ScriptInstruction");
                if (!LooksLikeSceneInstruction(instruction)) continue;

                var cutsceneId = questParam.ScriptArg;
                var sceneSource = source with
                {
                    Field = $"QuestParam:{instruction}",
                    Tag = "[Cutscene]",
                    CutsceneId = cutsceneId,
                };
                AddCutsceneSource(entries, sceneEntries, cutsceneId, sceneSource, stats);
            }

            stats.AddCandidate("Quest");
        }
    }

    void LoadInstanceContentTerritories(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, Dictionary<string, TerritoryBrowserEntry> sceneEntries, TerritoryDiscoveryStats stats)
    {
        foreach (var instance in EnumerateSheet<InstanceContent>("InstanceContent", stats))
        {
            AddInstanceContent(entries, territoryEntries, sceneEntries, instance.RowId, new("InstanceContent", instance.RowId, $"InstanceContent {instance.RowId}", "ContentFinderCondition/Cutscene", "[Instance]"), stats);
        }
    }

    void LoadPublicContentTerritories(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, Dictionary<string, TerritoryBrowserEntry> sceneEntries, TerritoryDiscoveryStats stats)
    {
        foreach (var content in EnumerateSheet<PublicContent>("PublicContent", stats))
        {
            var sourceName = SafeText(() => content.Name.GetText() ?? "", "PublicContent.Name").NullWhenEmpty() ?? $"PublicContent {content.RowId}";
            var entry = AddContentFinderCondition(entries, territoryEntries, content.ContentFinderCondition.RowId, new("PublicContent", content.RowId, sourceName, "ContentFinderCondition", "[Instance]"), stats);
            AddPublicContentCutsceneToTerritory(entry, content.StartCutscene.RowId, new("PublicContent", content.RowId, sourceName, "StartCutscene", "[Cutscene]"));
            AddPublicContentCutsceneToTerritory(entry, content.EndCutscene.RowId, new("PublicContent", content.RowId, sourceName, "EndCutscene", "[Cutscene]"));
            AddPublicContentCutscene(entries, sceneEntries, content.StartCutscene.RowId, new("PublicContent", content.RowId, sourceName, "StartCutscene", "[Cutscene]"), stats);
            AddPublicContentCutscene(entries, sceneEntries, content.EndCutscene.RowId, new("PublicContent", content.RowId, sourceName, "EndCutscene", "[Cutscene]"), stats);
            stats.AddCandidate("PublicContent");
        }
    }

    void LoadWarpTerritories(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, Dictionary<string, TerritoryBrowserEntry> sceneEntries, TerritoryDiscoveryStats stats)
    {
        foreach (var warp in EnumerateSheet<Warp>("Warp", stats))
        {
            var sourceName = SafeText(() => warp.Name.GetText() ?? "", "Warp.Name").NullWhenEmpty() ?? $"Warp {warp.RowId}";
            var source = new TerritorySourceInfo("Warp", warp.RowId, sourceName, "Territory/PopRange/Cutscene", "[EventScene]");
            var entry = AddTerritorySource(entries, territoryEntries, warp.TerritoryType.RowId, source, stats);
            AddLevelSource(entries, territoryEntries, warp.PopRange.RowId, source with { Field = "PopRange" }, stats);
            AddCutsceneToTerritory(entry, warp.StartCutscene.RowId, source with { Field = "StartCutscene", Tag = "[Cutscene]", CutsceneId = warp.StartCutscene.RowId });
            AddCutsceneToTerritory(entry, warp.EndCutscene.RowId, source with { Field = "EndCutscene", Tag = "[Cutscene]", CutsceneId = warp.EndCutscene.RowId });
            AddCutsceneSource(entries, sceneEntries, warp.StartCutscene.RowId, source with { Field = "StartCutscene", Tag = "[Cutscene]", CutsceneId = warp.StartCutscene.RowId }, stats);
            AddCutsceneSource(entries, sceneEntries, warp.EndCutscene.RowId, source with { Field = "EndCutscene", Tag = "[Cutscene]", CutsceneId = warp.EndCutscene.RowId }, stats);
            stats.AddCandidate("Warp");
        }
    }

    void LoadAetheryteTerritories(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, TerritoryDiscoveryStats stats)
    {
        foreach (var aetheryte in EnumerateSheet<Aetheryte>("Aetheryte", stats))
        {
            var sourceName = SafeText(() => aetheryte.Singular.GetText() ?? "", "Aetheryte.Singular").NullWhenEmpty() ?? $"Aetheryte {aetheryte.RowId}";
            AddTerritorySource(entries, territoryEntries, aetheryte.Territory.RowId, new("Aetheryte", aetheryte.RowId, sourceName, "Territory", "[TerritoryType]"), stats);
            foreach (var level in SafeCollection(() => aetheryte.Level, "Aetheryte.Level"))
            {
                AddLevelSource(entries, territoryEntries, level.RowId, new("Aetheryte", aetheryte.RowId, sourceName, "Level", "[TerritoryType]"), stats);
            }
            stats.AddCandidate("Aetheryte");
        }
    }

    void LoadGatheringPointTerritories(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, TerritoryDiscoveryStats stats)
    {
        foreach (var gatheringPoint in EnumerateSheet<GatheringPoint>("GatheringPoint", stats))
        {
            AddTerritorySource(entries, territoryEntries, gatheringPoint.TerritoryType.RowId, new("GatheringPoint", gatheringPoint.RowId, $"GatheringPoint {gatheringPoint.RowId}", "TerritoryType", "[TerritoryType]"), stats);
        }
    }

    void LoadQuestEventAreaTerritories(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, TerritoryDiscoveryStats stats)
    {
        foreach (var row in EnumerateSubrowSheet<QuestEventAreaEntranceInfo>("QuestEventAreaEntranceInfo", stats))
        {
            var questName = GetQuestName(row.Quest.RowId);
            AddLevelSource(entries, territoryEntries, row.Location.RowId, new("QuestEventAreaEntranceInfo", row.RowId, questName, $"Location/{row.SubrowId}", "[Quest]") { QuestId = row.Quest.RowId, QuestName = questName }, stats);
        }
    }

    void LoadReflectiveEventTerritories(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, Dictionary<string, TerritoryBrowserEntry> sceneEntries, TerritoryDiscoveryStats stats)
    {
        var sheetNames = new[]
        {
            "Quest",
            "CutScene",
            "CutSceneMotion",
            "CutsceneWorkIndex",
            "EventScene",
            "EventHandler",
            "EventItem",
            "EventIconType",
            "LayerSet",
            "ContentDirector",
            "QuestEventAreaEntranceInfo",
            "EventPathMove",
            "EventGimmickPathMove",
            "EventMountGimmickPathMove",
            "EventSetDefine",
            "QuestSetDefine",
            "QuestSceneAbortCondition",
            "CutSceneIncompQuest",
            "PartyContent",
            "PartyContentCutscene",
            "PublicContentCutscene",
            "TerritoryIntendedUse",
            "PopRange",
            "ContentFinderCondition",
            "Level",
        };

        foreach (var sheetName in sheetNames)
        {
            ScanSheetByReflection(sheetName, entries, territoryEntries, sceneEntries, stats);
        }
    }

    void LoadCutsceneSceneOnly(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<string, TerritoryBrowserEntry> sceneEntries, TerritoryDiscoveryStats stats)
    {
        foreach (var cutscene in EnumerateSheet<Cutscene>("Cutscene", stats))
        {
            AddCutsceneSource(entries, sceneEntries, cutscene.RowId, new("Cutscene", cutscene.RowId, SafeText(() => cutscene.Path.GetText() ?? "", "Cutscene.Path"), "Path", "[Cutscene]") { CutsceneId = cutscene.RowId }, stats);
        }
    }

    void AddQuestLevel(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, uint levelId, TerritorySourceInfo source, TerritoryDiscoveryStats stats)
    {
        AddLevelSource(entries, territoryEntries, levelId, source with { Tag = "[Quest]" }, stats);
        if (source.Tag is "[MSQ]" or "[SideQuest]")
        {
            AddLevelSource(entries, territoryEntries, levelId, source, stats);
        }
    }

    void AddLevelSource(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, uint levelId, TerritorySourceInfo source, TerritoryDiscoveryStats stats)
    {
        if (levelId == 0) return;
        if (!TryGetLevel(levelId, out var level)) return;
        var territoryId = level.Territory.RowId;
        if (territoryId == 0 && level.Map.RowId != 0 && TryGetMap(level.Map.RowId, out var map))
        {
            territoryId = map.TerritoryType.RowId;
        }
        AddTerritorySource(entries, territoryEntries, territoryId, source with { LevelId = levelId, MapId = level.Map.RowId }, stats);
    }

    void AddInstanceContent(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, Dictionary<string, TerritoryBrowserEntry> sceneEntries, uint instanceId, TerritorySourceInfo source, TerritoryDiscoveryStats stats)
    {
        if (instanceId == 0) return;
        var instance = GetSheetRow<InstanceContent>(instanceId);
        if (instance == null) return;
        var entry = AddContentFinderCondition(entries, territoryEntries, instance.Value.ContentFinderCondition.RowId, source with { InstanceContentId = instanceId }, stats);
        var cutsceneId = instance.Value.Cutscene.RowId;
        if (entry != null && cutsceneId != 0)
        {
            entry.AddTag("[Cutscene]");
            entry.AddSource(source with { Field = "Cutscene", Tag = "[Cutscene]", CutsceneId = cutsceneId, InstanceContentId = instanceId });
        }
        AddCutsceneSource(entries, sceneEntries, cutsceneId, source with { Tag = "[Cutscene]", CutsceneId = cutsceneId }, stats);
        stats.AddCandidate("InstanceContent");
    }

    TerritoryBrowserEntry? AddContentFinderCondition(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, uint cfcId, TerritorySourceInfo source, TerritoryDiscoveryStats stats)
    {
        if (cfcId == 0) return null;
        var cfc = GetSheetRow<ContentFinderCondition>(cfcId);
        if (cfc == null) return null;
        return AddTerritorySource(entries, territoryEntries, cfc.Value.TerritoryType.RowId, source with { ContentFinderConditionId = cfcId }, stats);
    }

    void AddPublicContentCutscene(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<string, TerritoryBrowserEntry> sceneEntries, uint publicCutsceneId, TerritorySourceInfo source, TerritoryDiscoveryStats stats)
    {
        if (publicCutsceneId == 0) return;
        var row = GetSheetRow<PublicContentCutscene>(publicCutsceneId);
        if (row == null) return;
        AddCutsceneSource(entries, sceneEntries, row.Value.Cutscene.RowId, source with { CutsceneId = row.Value.Cutscene.RowId }, stats);
        AddCutsceneSource(entries, sceneEntries, row.Value.Cutscene2.RowId, source with { Field = source.Field + "2", CutsceneId = row.Value.Cutscene2.RowId }, stats);
    }

    void AddPublicContentCutsceneToTerritory(TerritoryBrowserEntry? entry, uint publicCutsceneId, TerritorySourceInfo source)
    {
        if (entry == null || publicCutsceneId == 0) return;
        var row = GetSheetRow<PublicContentCutscene>(publicCutsceneId);
        if (row == null) return;
        AddCutsceneToTerritory(entry, row.Value.Cutscene.RowId, source with { CutsceneId = row.Value.Cutscene.RowId });
        AddCutsceneToTerritory(entry, row.Value.Cutscene2.RowId, source with { Field = source.Field + "2", CutsceneId = row.Value.Cutscene2.RowId });
    }

    static void AddCutsceneToTerritory(TerritoryBrowserEntry? entry, uint cutsceneId, TerritorySourceInfo source)
    {
        if (entry == null || cutsceneId == 0) return;
        entry.AddTag("[Cutscene]");
        entry.AddSource(source with { Tag = "[Cutscene]", CutsceneId = cutsceneId });
    }

    void AddCutsceneSource(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<string, TerritoryBrowserEntry> sceneEntries, uint cutsceneId, TerritorySourceInfo source, TerritoryDiscoveryStats stats)
    {
        if (cutsceneId == 0) return;
        var key = $"cutscene:{cutsceneId}";
        if (!sceneEntries.TryGetValue(key, out var entry))
        {
            var cutscene = GetSheetRow<Cutscene>(cutsceneId);
            var path = cutscene == null ? "" : SafeText(() => cutscene.Value.Path.GetText() ?? "", "Cutscene.Path");
            entry = new TerritoryBrowserEntry
            {
                Key = key,
                TerritoryId = null,
                DisplayName = path.NullWhenEmpty() ?? $"Cutscene {cutsceneId}",
                Bg = path,
                Tags = ["[Cutscene]", "[SceneOnly]", "[NoTerritory]", "[Unsafe]"],
                RequiresConfirmation = true,
                IsPotentiallyUnsafe = true,
            };
            sceneEntries[key] = entry;
            entries[key] = entry;
        }

        entry.AddSource(source with { Sheet = source.Sheet.IsNullOrEmpty() ? "Cutscene" : source.Sheet, CutsceneId = cutsceneId });
        stats.AddCandidate("Cutscene");
    }

    TerritoryBrowserEntry? AddTerritorySource(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, uint territoryId, TerritorySourceInfo source, TerritoryDiscoveryStats stats)
    {
        if (territoryId == 0) return null;
        var entry = AddTerritory(entries, territoryEntries, territoryId);
        if (entry == null) return null;
        entry.AddSource(source);
        if (!source.Tag.IsNullOrEmpty()) entry.AddTag(source.Tag);
        if (source.MapId is > 0 && entry.MapId == 0) entry.MapId = source.MapId.Value;
        stats.AddCandidate(source.Sheet);
        return entry;
    }

    TerritoryBrowserEntry? AddTerritory(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, uint territoryId)
    {
        if (territoryId == 0) return null;
        if (territoryEntries.TryGetValue(territoryId, out var existing)) return existing;

        var entry = TryGetTerritoryRow(territoryId, out var territory)
            ? CreateTerritoryEntry(territory)
            : CreateFallbackTerritoryEntry(territoryId);

        AddEntry(entries, territoryEntries, entry);
        return entry;
    }

    void AddEntry(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, TerritoryBrowserEntry entry)
    {
        entries[entry.Key] = entry;
        if (entry.TerritoryId is { } territoryId)
        {
            territoryEntries[territoryId] = entry;
        }
    }

    TerritoryBrowserEntry CreateTerritoryEntry(TerritoryType territory)
    {
        var bg = SafeText(() => territory.Bg.GetText() ?? "", "Territory.Bg");
        var territoryName = SafeText(() => territory.Name.GetText() ?? "", "Territory.Name");
        var placeName = SafeText(() => territory.PlaceName.ValueNullable?.Name.GetText() ?? "", "Territory.PlaceName");
        var zoneName = SafeText(() => territory.PlaceNameZone.ValueNullable?.Name.GetText() ?? "", "Territory.PlaceNameZone");
        var regionName = SafeText(() => territory.PlaceNameRegion.ValueNullable?.Name.GetText() ?? "", "Territory.PlaceNameRegion");
        var cfcName = SafeText(() => territory.ContentFinderCondition.ValueNullable?.Name.GetText() ?? "", "Territory.ContentFinderCondition");
        var questBattleName = SafeText(() => territory.QuestBattle.ValueNullable?.Quest.GetValueOrDefault<Quest>()?.Name.GetText() ?? "", "Territory.QuestBattle");
        var map = SafeValue(() => territory.Map.ValueNullable, "Territory.Map");
        var mapName = SafeText(() => map?.PlaceName.ValueNullable?.Name.GetText() ?? "", "Map.PlaceName");
        var mapSubName = SafeText(() => map?.PlaceNameSub.ValueNullable?.Name.GetText() ?? "", "Map.PlaceNameSub");
        var mapText = mapName.NullWhenEmpty() ?? mapSubName.NullWhenEmpty() ?? (territory.Map.RowId == 0 ? "" : $"#{territory.Map.RowId}");
        var intendedUse = ((TerritoryIntendedUseEnum)territory.TerritoryIntendedUse.RowId).ToString().Replace("_", " ");
        var localizedNames = GetLocalizedTerritoryNames(territory.RowId);
        var hasAnyName = localizedNames.Length > 0
            || !territoryName.IsNullOrEmpty()
            || !placeName.IsNullOrEmpty()
            || !zoneName.IsNullOrEmpty()
            || !regionName.IsNullOrEmpty()
            || !cfcName.IsNullOrEmpty()
            || !questBattleName.IsNullOrEmpty()
            || !mapText.IsNullOrEmpty();
        var hasBg = !bg.IsNullOrEmpty();
        var hasMap = territory.Map.RowId != 0 && map != null;
        var hasKnownSpawn = hasBg && Utils.TryGetZoneInfo(bg, out var zoneInfo) && zoneInfo.Spawn != null;
        var hasContentFinder = territory.ContentFinderCondition.RowId != 0;
        var isRegularUse = RegularTerritoryUses.Contains((TerritoryIntendedUseEnum)territory.TerritoryIntendedUse.RowId);
        var wasVisibleInOldSelector = !placeName.IsNullOrEmpty() || !cfcName.IsNullOrEmpty() || !questBattleName.IsNullOrEmpty();

        var entry = new TerritoryBrowserEntry
        {
            Key = $"territory:{territory.RowId}",
            TerritoryId = territory.RowId,
            MapId = territory.Map.RowId,
            PlaceName = placeName,
            DisplayName = cfcName.NullWhenEmpty()
                ?? placeName.NullWhenEmpty()
                ?? questBattleName.NullWhenEmpty()
                ?? territoryName.NullWhenEmpty()
                ?? localizedNames.FirstOrDefault()
                ?? mapText.NullWhenEmpty()
                ?? zoneName.NullWhenEmpty()
                ?? bg.NullWhenEmpty()
                ?? $"Territory {territory.RowId}",
            MapText = mapText,
            Bg = bg,
            IntendedUse = intendedUse,
            WasVisibleInOldSelector = wasVisibleInOldSelector,
            IsPotentiallyUnsafe = !wasVisibleInOldSelector || !hasBg || !hasAnyName || !hasMap || !hasKnownSpawn,
            RequiresConfirmation = !wasVisibleInOldSelector || !hasBg || !hasAnyName || !hasMap,
        };

        foreach (var name in localizedNames) entry.SearchParts.Add(name);
        entry.SearchParts.AddRange([territoryName, placeName, zoneName, regionName, cfcName, questBattleName, mapText, $"{territory.Map.RowId}", bg, intendedUse]);
        if (!wasVisibleInOldSelector && hasBg && !hasContentFinder) entry.AddTag("[Cutscene]");
        if (!questBattleName.IsNullOrEmpty()) entry.AddTag("[EventScene]");
        if (!hasMap) entry.AddTag("[NoMap]");
        if (placeName.IsNullOrEmpty()) entry.AddTag("[NoPlaceName]");
        if (!wasVisibleInOldSelector || !isRegularUse) entry.AddTag("[Inaccessible]");
        if (!hasAnyName) entry.AddTag("[MissingName]");
        if (entry.IsPotentiallyUnsafe) entry.AddTag("[Unsafe]");
        return entry;
    }

    TerritoryBrowserEntry CreateFallbackTerritoryEntry(uint territoryId)
    {
        UsedFallbackNames = true;
        return new TerritoryBrowserEntry
        {
            Key = $"territory:{territoryId}",
            TerritoryId = territoryId,
            DisplayName = $"Territory {territoryId}",
            SearchParts = [$"{territoryId}", $"Territory {territoryId}"],
            Tags = ["[NoMap]", "[NoPlaceName]", "[MissingName]", "[Unsafe]"],
            IsPotentiallyUnsafe = true,
            RequiresConfirmation = true,
        };
    }

    void FinalizeEntry(TerritoryBrowserEntry entry)
    {
        if (!entry.HasTerritory) entry.AddTag("[NoTerritory]");
        if (entry.MapId == 0 && entry.MapText.IsNullOrEmpty()) entry.AddTag("[NoMap]");
        if (entry.PlaceName.IsNullOrEmpty()) entry.AddTag("[NoPlaceName]");
        if (entry.Sources.Count > 1) entry.AddTag("[DuplicateSource]");
        if (entry.Tags.Any(x => x is "[NoTerritory]" or "[NoMap]" or "[NoPlaceName]" or "[SceneOnly]" or "[Inaccessible]"))
        {
            entry.AddTag("[Unsafe]");
            entry.IsPotentiallyUnsafe = true;
            entry.RequiresConfirmation = true;
        }
        entry.BuildSearchText();
    }

    void ScanSheetByReflection(string sheetName, Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, Dictionary<string, TerritoryBrowserEntry> sceneEntries, TerritoryDiscoveryStats stats)
    {
        var type = typeof(TerritoryType).Assembly.GetType($"Lumina.Excel.Sheets.{sheetName}");
        if (type == null)
        {
            stats.SkipSheet(sheetName, "sheet type not generated");
            return;
        }

        try
        {
            var method = typeof(TerritoryDiscoveryService).GetMethod(nameof(ScanTypedSheetByReflection), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.MakeGenericMethod(type).Invoke(this, [entries, territoryEntries, sceneEntries, stats, sheetName]);
        }
        catch (Exception e)
        {
            stats.SkipSheet(sheetName, e.InnerException?.Message ?? e.Message);
            LogWarningOnce($"ReflectiveSheet:{sheetName}", e.InnerException ?? e, $"Failed to scan {sheetName}.");
        }
    }

    void ScanTypedSheetByReflection<T>(Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, Dictionary<string, TerritoryBrowserEntry> sceneEntries, TerritoryDiscoveryStats stats, string sheetName)
        where T : struct, IExcelRow<T>
    {
        foreach (var row in EnumerateSheet<T>(sheetName, stats))
        {
            var rowId = GetRowId(row);
            var source = new TerritorySourceInfo(sheetName, rowId, $"{sheetName} {rowId}", "reflection", sheetName.Contains("Event", StringComparison.OrdinalIgnoreCase) ? "[EventScene]" : $"[{sheetName}]");
            ScanValueForReferences(row!, source, entries, territoryEntries, sceneEntries, stats, 0);
        }
    }

    void ScanValueForReferences(object value, TerritorySourceInfo source, Dictionary<string, TerritoryBrowserEntry> entries, Dictionary<uint, TerritoryBrowserEntry> territoryEntries, Dictionary<string, TerritoryBrowserEntry> sceneEntries, TerritoryDiscoveryStats stats, int depth)
    {
        if (depth > 2 || value is string) return;
        var type = value.GetType();

        if (TryReadRowRef(value, type, out var targetName, out var rowId))
        {
            switch (targetName)
            {
                case "TerritoryType":
                    AddTerritorySource(entries, territoryEntries, rowId, source, stats);
                    break;
                case "Map":
                    if (TryGetMap(rowId, out var map)) AddTerritorySource(entries, territoryEntries, map.TerritoryType.RowId, source with { MapId = rowId }, stats);
                    break;
                case "Level":
                    AddLevelSource(entries, territoryEntries, rowId, source, stats);
                    break;
                case "ContentFinderCondition":
                    AddContentFinderCondition(entries, territoryEntries, rowId, source, stats);
                    break;
                case "InstanceContent":
                    AddInstanceContent(entries, territoryEntries, sceneEntries, rowId, source with { Tag = "[Instance]" }, stats);
                    break;
                case "Cutscene":
                    AddCutsceneSource(entries, sceneEntries, rowId, source with { Tag = "[Cutscene]", CutsceneId = rowId }, stats);
                    break;
            }
            return;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item == null) continue;
                ScanValueForReferences(item, source, entries, territoryEntries, sceneEntries, stats, depth + 1);
            }
            return;
        }

        if (type.Namespace?.StartsWith("Lumina.Excel.Sheets") == true || type.FullName?.Contains("+") == true)
        {
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Where(x => x.GetIndexParameters().Length == 0))
            {
                object? propValue;
                try
                {
                    propValue = prop.GetValue(value);
                }
                catch
                {
                    continue;
                }
                if (propValue == null) continue;
                ScanValueForReferences(propValue, source with { Field = prop.Name }, entries, territoryEntries, sceneEntries, stats, depth + 1);
            }
        }
    }

    static bool TryReadRowRef(object value, Type type, out string targetName, out uint rowId)
    {
        targetName = "";
        rowId = 0;
        if (type.FullName?.StartsWith("Lumina.Excel.RowRef") != true) return false;
        rowId = (uint)(type.GetProperty("RowId")?.GetValue(value) ?? 0u);
        if (type.IsGenericType)
        {
            targetName = type.GetGenericArguments()[0].Name;
        }
        return rowId != 0;
    }

    bool TryGetTerritoryRow(uint territoryId, out TerritoryType territory)
    {
        territory = default;
        var sheet = GetSafeSheet<TerritoryType>();
        if (sheet == null) return false;
        try
        {
            return sheet.TryGetRow(territoryId, out territory);
        }
        catch (Exception e)
        {
            LogWarningOnce($"TerritoryRow:{territoryId}", e, $"Failed to read territory row {territoryId}.");
            return false;
        }
    }

    bool TryGetLevel(uint levelId, out Level level)
    {
        level = default;
        var row = GetSheetRow<Level>(levelId);
        if (row == null) return false;
        level = row.Value;
        return true;
    }

    bool TryGetMap(uint mapId, out Map map)
    {
        map = default;
        var row = GetSheetRow<Map>(mapId);
        if (row == null) return false;
        map = row.Value;
        return true;
    }

    T? GetSheetRow<T>(uint rowId) where T : struct, IExcelRow<T>
    {
        if (rowId == 0) return null;
        var sheet = GetSafeSheet<T>();
        if (sheet == null) return null;
        try
        {
            return sheet.GetRowOrDefault(rowId);
        }
        catch (Exception e)
        {
            LogWarningOnce($"Row:{typeof(T).Name}:{rowId}", e, $"Failed to read {typeof(T).Name} row {rowId}.");
            return null;
        }
    }

    IEnumerable<T> EnumerateSheet<T>(string sheetName, TerritoryDiscoveryStats stats) where T : struct, IExcelRow<T>
    {
        var sheet = GetSafeSheet<T>();
        if (sheet == null)
        {
            stats.SkipSheet(sheetName, "sheet not available");
            yield break;
        }

        stats.ScanSheet(sheetName);
        IEnumerator<T> enumerator;
        try
        {
            enumerator = sheet.GetEnumerator();
        }
        catch (Exception e)
        {
            stats.SkipSheet(sheetName, e.Message);
            LogWarningOnce($"Enumerate:{sheetName}", e, $"Failed to enumerate {sheetName}.");
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                T row;
                try
                {
                    if (!enumerator.MoveNext()) break;
                    row = enumerator.Current;
                }
                catch (Exception e)
                {
                    LogWarningOnce($"EnumerateRow:{sheetName}", e, $"Failed to enumerate a {sheetName} row.");
                    continue;
                }
                yield return row;
            }
        }
    }

    IEnumerable<T> EnumerateSubrowSheet<T>(string sheetName, TerritoryDiscoveryStats stats) where T : struct, IExcelSubrow<T>
    {
        var sheet = GetSafeSubrowSheet<T>();
        if (sheet == null)
        {
            stats.SkipSheet(sheetName, "subrow sheet not available");
            yield break;
        }

        stats.ScanSheet(sheetName);
        foreach (var collection in sheet)
        {
            foreach (var row in collection)
            {
                yield return row;
            }
        }
    }

    ExcelSheet<T>? GetSafeSheet<T>() where T : struct, IExcelRow<T>
    {
        foreach (var language in GetSafeSearchLanguages())
        {
            try
            {
                var sheet = Svc.Data.GetExcelSheet<T>(language);
                if (sheet != null) return sheet;
            }
            catch (Exception e)
            {
                LogWarningOnce($"Sheet:{typeof(T).Name}:{language}", e, $"Failed to load {typeof(T).Name} sheet for {language}.");
            }
        }
        return null;
    }

    SubrowExcelSheet<T>? GetSafeSubrowSheet<T>() where T : struct, IExcelSubrow<T>
    {
        foreach (var language in GetSafeSearchLanguages())
        {
            try
            {
                var sheet = Svc.Data.GetSubrowExcelSheet<T>(language);
                if (sheet != null) return sheet;
            }
            catch (Exception e)
            {
                LogWarningOnce($"SubrowSheet:{typeof(T).Name}:{language}", e, $"Failed to load {typeof(T).Name} subrow sheet for {language}.");
            }
        }
        return null;
    }

    IEnumerable<ClientLanguage> GetSafeSearchLanguages()
    {
        var seen = new HashSet<ClientLanguage>();
        foreach (var language in GetPreferredLanguages())
        {
            if (!seen.Add(language)) continue;
            if (IsSupportedLanguage(language)) yield return language;
        }
    }

    IEnumerable<ClientLanguage> GetPreferredLanguages()
    {
        yield return Svc.ClientState.ClientLanguage;
        yield return ClientLanguage.English;
        foreach (var language in SearchLanguageCandidates) yield return language;
    }

    bool IsSupportedLanguage(ClientLanguage language)
    {
        var name = language.ToString();
        if (name.Equals("None", StringComparison.OrdinalIgnoreCase) || name.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return false;
        if (languageSupport.TryGetValue(language, out var supported)) return supported;
        try
        {
            var sheet = Svc.Data.GetExcelSheet<TerritoryType>(language);
            supported = sheet != null;
            if (supported) _ = sheet!.Count;
        }
        catch (Exception e)
        {
            supported = false;
            UsedFallbackNames = true;
            LogWarningOnce($"UnsupportedLanguage:{language}", e, $"Skipping unsupported territory language {language}.");
        }
        languageSupport[language] = supported;
        return supported;
    }

    string[] GetLocalizedTerritoryNames(uint territoryId)
    {
        var names = new List<string>();
        foreach (var language in GetSafeSearchLanguages())
        {
            try
            {
                var sheet = Svc.Data.GetExcelSheet<TerritoryType>(language);
                var row = sheet?.GetRowOrDefault(territoryId);
                if (row == null) continue;
                var cfcName = SafeText(() => row.Value.ContentFinderCondition.ValueNullable?.Name.GetText() ?? "", $"Localized.{language}.CFC");
                var placeName = SafeText(() => row.Value.PlaceName.ValueNullable?.Name.GetText() ?? "", $"Localized.{language}.PlaceName");
                var territoryName = SafeText(() => row.Value.Name.GetText() ?? "", $"Localized.{language}.Name");
                foreach (var name in new[] { cfcName, placeName, territoryName })
                {
                    if (!name.IsNullOrEmpty() && !name.StartsWith("#") && !names.Contains(name)) names.Add(name);
                }
            }
            catch (Exception e)
            {
                UsedFallbackNames = true;
                LogWarningOnce($"Localized:{language}", e, $"Failed to read localized territory names for {language}.");
            }
        }
        return names.ToArray();
    }

    string GetQuestName(uint questId)
    {
        var quest = GetSheetRow<Quest>(questId);
        return quest == null ? $"Quest {questId}" : SafeText(() => quest.Value.Name.GetText() ?? "", "Quest.Name").NullWhenEmpty() ?? $"Quest {questId}";
    }

    static bool LooksLikeSceneInstruction(string instruction)
    {
        return instruction.Contains("cut", StringComparison.OrdinalIgnoreCase)
            || instruction.Contains("scene", StringComparison.OrdinalIgnoreCase)
            || instruction.Contains("event", StringComparison.OrdinalIgnoreCase);
    }

    string SafeText(Func<string?> read, string context)
    {
        try
        {
            return read() ?? "";
        }
        catch (Exception e)
        {
            UsedFallbackNames = true;
            LogWarningOnce($"Text:{context}", e, $"Failed to read {context}.");
            return "";
        }
    }

    static T? SafeValue<T>(Func<T?> read, string context) where T : struct
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }

    static uint SafeRowId(Func<uint> read, string context)
    {
        try
        {
            return read();
        }
        catch
        {
            return 0;
        }
    }

    IEnumerable<T> SafeCollection<T>(Func<IEnumerable<T>> read, string context)
    {
        try
        {
            return read() ?? [];
        }
        catch (Exception e)
        {
            LogWarningOnce($"Collection:{context}", e, $"Failed to read {context}.");
            return [];
        }
    }

    static uint GetRowId<T>(T row)
    {
        return (uint)(typeof(T).GetProperty("RowId")?.GetValue(row!) ?? 0u);
    }

    void LogWarningOnce(string key, Exception e, string message)
    {
        var warningKey = $"{key}:{e.GetType().FullName}:{e.Message}";
        if (!loggedWarnings.Add(warningKey)) return;
        PluginLog.Warning($"[TerritoryDiscovery] {message} {e.GetType().Name}: {e.Message}");
    }
}

public enum TerritoryBrowserFilter
{
    All,
    Normal,
    Quest,
    Cutscene,
    EventScene,
    Instance,
    Unsafe,
}

public sealed class TerritoryBrowserEntry
{
    public string Key = "";
    public uint? TerritoryId;
    public uint RowId => TerritoryId ?? 0;
    public bool HasTerritory => TerritoryId is > 0;
    public uint MapId;
    public string DisplayName = "";
    public string SearchText = "";
    public string MapText = "";
    public string PlaceName = "";
    public string Bg = "";
    public string IntendedUse = "";
    public List<string> Tags = [];
    public List<string> SearchParts = [];
    public List<TerritorySourceInfo> Sources = [];
    public bool WasVisibleInOldSelector;
    public bool IsPotentiallyUnsafe;
    public bool RequiresConfirmation;
    public int SourceCount => Sources.Count;

    public void AddTag(string tag)
    {
        if (!tag.IsNullOrEmpty() && !Tags.Contains(tag)) Tags.Add(tag);
    }

    public void AddSource(TerritorySourceInfo source)
    {
        if (source.RowId == 0 && source.CutsceneId is null && source.QuestId is null) return;
        if (Sources.Any(x => x.DedupeKey == source.DedupeKey)) return;
        Sources.Add(source);
        AddTag(source.Tag);
        SearchParts.AddRange(source.SearchParts);
    }

    public void BuildSearchText()
    {
        SearchText = string.Join("\n",
            SearchParts
                .Append(Key)
                .Append(TerritoryId?.ToString() ?? "")
                .Append(MapId == 0 ? "" : MapId.ToString())
                .Append(DisplayName)
                .Append(PlaceName)
                .Append(MapText)
                .Append(Bg)
                .Append(IntendedUse)
                .Concat(Tags)
                .Concat(Sources.SelectMany(x => x.SearchParts))
                .Where(x => !x.IsNullOrEmpty())
                .Distinct());
    }

    public bool HasTag(string tag) => Tags.Any(x => x.Equals(tag, StringComparison.OrdinalIgnoreCase));
}

public record TerritorySourceInfo(string Sheet, uint RowId, string Name, string Field, string Tag)
{
    public uint? QuestId { get; init; }
    public string QuestName { get; init; } = "";
    public uint? CutsceneId { get; init; }
    public uint? EventSceneId { get; init; }
    public uint? MapId { get; init; }
    public uint? LevelId { get; init; }
    public uint? InstanceContentId { get; init; }
    public uint? ContentFinderConditionId { get; init; }
    public string DedupeKey => $"{Sheet}:{RowId}:{Field}:{QuestId}:{CutsceneId}:{LevelId}:{MapId}:{InstanceContentId}:{ContentFinderConditionId}";
    public IEnumerable<string> SearchParts
    {
        get
        {
            yield return Sheet;
            yield return RowId.ToString();
            yield return Name;
            yield return Field;
            yield return Tag;
            if (QuestId is { } questId) yield return questId.ToString();
            if (!QuestName.IsNullOrEmpty()) yield return QuestName;
            if (CutsceneId is { } cutsceneId)
            {
                yield return cutsceneId.ToString();
                yield return $"Cutscene {cutsceneId}";
            }
            if (EventSceneId is { } eventSceneId)
            {
                yield return eventSceneId.ToString();
                yield return $"EventScene {eventSceneId}";
            }
            if (MapId is { } mapId) yield return mapId.ToString();
            if (LevelId is { } levelId) yield return levelId.ToString();
            if (InstanceContentId is { } instanceId)
            {
                yield return instanceId.ToString();
                yield return $"InstanceContent {instanceId}";
            }
            if (ContentFinderConditionId is { } cfcId) yield return cfcId.ToString();
        }
    }
}

public sealed class TerritoryDiscoveryStats
{
    public Dictionary<string, int> SheetsScanned = [];
    public Dictionary<string, int> CandidatesBySheet = [];
    public Dictionary<string, string> SkippedSheets = [];
    public int TotalEntries;
    public int NormalEntries;
    public int QuestEntries;
    public int CutsceneEntries;
    public int EventSceneEntries;
    public int InstanceEntries;
    public int UnsafeEntries;
    public int NoMapEntries;
    public int NoPlaceNameEntries;
    public int NoTerritoryEntries;

    public void ScanSheet(string sheetName) => SheetsScanned[sheetName] = SheetsScanned.GetValueOrDefault(sheetName) + 1;
    public void AddCandidate(string sheetName) => CandidatesBySheet[sheetName] = CandidatesBySheet.GetValueOrDefault(sheetName) + 1;
    public void SkipSheet(string sheetName, string reason) => SkippedSheets.TryAdd(sheetName, reason);

    public TerritoryDiscoveryStats WithEntryCounts(IReadOnlyList<TerritoryBrowserEntry> entries)
    {
        TotalEntries = entries.Count;
        NormalEntries = entries.Count(x => x.HasTag("[Normal]"));
        QuestEntries = entries.Count(x => x.HasTag("[Quest]") || x.HasTag("[MSQ]") || x.HasTag("[SideQuest]"));
        CutsceneEntries = entries.Count(x => x.HasTag("[Cutscene]"));
        EventSceneEntries = entries.Count(x => x.HasTag("[EventScene]"));
        InstanceEntries = entries.Count(x => x.HasTag("[Instance]"));
        UnsafeEntries = entries.Count(x => x.HasTag("[Unsafe]"));
        NoMapEntries = entries.Count(x => x.HasTag("[NoMap]"));
        NoPlaceNameEntries = entries.Count(x => x.HasTag("[NoPlaceName]"));
        NoTerritoryEntries = entries.Count(x => x.HasTag("[NoTerritory]"));
        return this;
    }
}
