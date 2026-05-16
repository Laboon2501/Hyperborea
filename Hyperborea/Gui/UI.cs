using Dalamud.Game;
using Dalamud.Interface.Components;
using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.GameHelpers;
using ECommons.ImGuiMethods.TerritorySelection;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Hyperborea.Services;
using ECommons.ChatMethods;
using ECommons.Throttlers;

namespace Hyperborea.Gui;

public unsafe static class UI
{
    public static SavedZoneState SavedZoneState = null;
    public static Vector3? SavedPos = null;
    public static string MountFilter = "";
    static int a2 = 0;
    static int a3 = 0;
    static int a4 = 0;
    static int a5 = 1;
    internal static int a6 = 1;
    static Point3 Position = new(0,0,0);
    static bool SpawnOverride;
    static int CFCOverride = 0;
    static string TerritorySearch = "";
    const string TerritoryBrowserPopup = "浏览区域##HyperboreaTerritoryBrowser";
    const string UnsafeTerritoryPopup = "确认加载风险区域##HyperboreaUnsafeTerritory";
    static readonly ClientLanguage[] TerritorySearchLanguageCandidates =
    [
        ClientLanguage.Japanese,
        ClientLanguage.English,
        ClientLanguage.German,
        ClientLanguage.French,
        ClientLanguage.ChineseSimplified,
        ClientLanguage.ChineseTraditional,
        ClientLanguage.Korean,
    ];
    static readonly Dictionary<ClientLanguage, bool> TerritoryLanguageSupport = [];
    static readonly HashSet<string> TerritoryBrowserLoggedWarnings = [];
    static bool TerritoryBrowserUsedFallbackNames;
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

    public static void DrawNeo()
    {
        /*if(!Svc.Condition[ConditionFlag.OnFreeTrial])
        {
            ImGuiEx.TextWrapped(EColor.RedBright, "You can currently use Hyperborea only with free trial accounts. Please register free trial account and try again or wait for an update.");
            return;
        }*/
        if(!P.AllowedOperation)
        {
            ImGuiEx.TextWrapped(EColor.RedBright, $"本版本暂未找到可用的 opcode，请耐心等待更新");
            if(ImGuiEx.Button("尝试更新 opcodes", EzThrottler.Check("Opcode")))
            {
                EzThrottler.Throttle("Opcode", 60000, true);
                S.ThreadPool.Run(S.OpcodeUpdater.RunForCurrentVersion, (x) =>
                {
                    if(x != null)
                    {
                        ChatPrinter.Red($"更新 opcodes 失败: \n{x.Message}");
                    }
                });
            }
            if(ImGuiEx.Button("手动输入opcodes", ImGuiEx.Ctrl))
            {
                P.DebugWindow.IsOpen = true;
            }
            ImGuiEx.Tooltip("长按 CTRL 并点击。请谨慎操作，错误的设置可能会导致账号面临严重风险");
            return;
        }
        var l = LayoutWorld.Instance()->ActiveLayout;
        var disableCheckbox = !Utils.CanEnablePlugin(out var DisableReasons) || Svc.Condition[ConditionFlag.Mounted];
        if (disableCheckbox) ImGui.BeginDisabled();
        if (ImGui.Checkbox("启用 Hyperborea", ref P.Enabled))
        {
            if (P.Enabled)
            {
                SavedPos = Player.Object.Position;
                P.Memory.EnableFirewall();
                P.Memory.TargetSystem_InteractWithObjectHook.Enable();
            }
            else
            {
                Utils.Revert();
                SavedPos = null;
                SavedZoneState = null;
                P.Memory.DisableFirewall();
                P.Memory.TargetSystem_InteractWithObjectHook.Pause();
            }
        }
        if (disableCheckbox)
        {
            ImGui.EndDisabled();
            if (!P.Enabled)
            {
                ImGuiEx.HelpMarker($"由于你当前处于以下受限状态，无法启用 Hyperborea:\n{DisableReasons.Print("\n")}", ImGuiColors.DalamudOrange);
            }
            else
            {
                ImGuiEx.HelpMarker("在禁用 Hyperborea 之前, 请先下坐骑或恢复正常状态", ImGuiColors.DalamudOrange);
            }
        }
        ImGuiEx.Text("数据包过滤:");
        ImGui.SameLine();
        if (P.Memory.PacketDispatcher_OnSendPacketHook.IsEnabled && P.Memory.PacketDispatcher_OnReceivePacketHook.IsEnabled)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGuiEx.Text(EColor.GreenBright, FontAwesomeIcon.Check.ToIconString());
            ImGui.PopFont();
        }
        else
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGuiEx.Text(EColor.RedBright, "\uf00d");
            ImGui.PopFont();
        }
        ImGuiEx.Tooltip("当启用 Hyperborea 的数据包过滤时, 客户端与游戏服务器之间的数据包都会被过滤, 防止被踢回标题界面");
        ImGui.SameLine();

        ImGuiEx.Text("交互 Hook:");
        ImGui.SameLine();
        if (P.Memory.TargetSystem_InteractWithObjectHook.IsEnabled)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGuiEx.Text(EColor.GreenBright, FontAwesomeIcon.Check.ToIconString());
            ImGui.PopFont();
        }
        else
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGuiEx.Text(EColor.RedBright, "\uf00d");
            ImGui.PopFont();
        }
        ImGuiEx.Tooltip("启用 Hyperborea 的交互 Hook时, 你将无法与 游戏物体 或 NPC 进行交互");

        ImGuiEx.Text("免费试玩:");
        ImGui.SameLine();
        if (Svc.Condition[ConditionFlag.OnFreeTrial])
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGuiEx.Text(EColor.GreenBright, FontAwesomeIcon.Check.ToIconString());
            ImGui.PopFont();
        }
        else
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGuiEx.Text(EColor.RedBright, "\uf00d");
            ImGui.PopFont();
        }
        ImGuiEx.Tooltip("Hyperborea 已尽量通过阻止数据上传来确保安全，但仍无法完全保证，强烈建议在免费试玩账号或者小号上使用");

        if (ImGuiGroup.BeginGroupBox())
        {
            try
            {
                ZoneInfo info = null;
                var layout = Utils.GetLayout();
                Utils.TryGetZoneInfo(layout, out info);

                var cur = ImGui.GetCursorPos();
                ImGui.SetCursorPosX(ImGuiEx.GetWindowContentRegionWidth() - ImGuiHelpers.GetButtonSize("浏览").X - ImGuiHelpers.GetButtonSize("区域编辑器").X - 50f);
                if (ImGuiComponents.IconButtonWithText((FontAwesomeIcon)0xf002, "浏览"))
                {
                    ImGui.OpenPopup(TerritoryBrowserPopup);
                }
                ImGui.SameLine();
                if (ImGuiComponents.IconButtonWithText((FontAwesomeIcon)0xf303, "区域编辑器"))
                {
                    P.EditorWindow.IsOpen = true;
                    P.EditorWindow.SelectedTerritory = (uint)a2;
                }

                ImGui.SetCursorPos(cur);
                ImGuiEx.TextV("区域数据:");
                ImGui.SetNextItemWidth(150);
                ImGui.InputInt("区域 ID", ref a2);
                var selectedTerritoryEntry = CreateTerritoryEntry((uint)Math.Max(a2, 0));
                if (selectedTerritoryEntry != null)
                {
                    ImGuiEx.Text(selectedTerritoryEntry.DisplayName);
                }
                if (ImGui.Checkbox("显示过场动画/事件专用地图", ref C.IncludeCutsceneEventTerritories))
                {
                    EzConfig.Save();
                }
                ImGuiEx.Tooltip("默认隐藏旧版区域浏览器不会列出的 TerritoryType；开启后会显示 cutscene/event-only 和缺少部分资料的区域。");
                DrawSelectedTerritoryWarning((uint)Math.Max(a2, 0));
                ImGuiEx.Text($"额外数据:");
                ImGui.SetNextItemWidth(150);
                var StoryValues = Utils.GetStoryValues((uint)a2);
                var disableda3 = !StoryValues.Any(x => x != 0);
                if (disableda3) ImGui.BeginDisabled();
                if (ImGui.BeginCombo("故事进度", $"{a3}"))
                {
                    foreach (var x in StoryValues.Order())
                    {
                        if (ImGui.Selectable($"{x}", a3 == x)) a3 = (int)x;
                        if (a3 == x && ImGui.IsWindowAppearing()) ImGui.SetScrollHereY();
                    }
                    ImGui.EndCombo();
                }
                if (disableda3) ImGui.EndDisabled();
                if (!StoryValues.Contains((uint)a3)) a3 = (int)StoryValues.FirstOrDefault();
                ImGui.SetNextItemWidth(150);
                ImGui.InputInt("参数 4", ref a4);
                ImGui.SetNextItemWidth(150);
                ImGui.InputInt("参数 5", ref a5);
                ImGui.SetNextItemWidth(150);
                ImGui.InputInt("CFC 重载", ref CFCOverride);

                ImGui.Checkbox($"出生点重定向:", ref SpawnOverride);
                if (!SpawnOverride) ImGui.BeginDisabled();
                CoordBlock("X:", ref Position.X);
                ImGui.SameLine();
                CoordBlock("Y:", ref Position.Y);
                ImGui.SameLine();
                CoordBlock("Z:", ref Position.Z);
                if (!SpawnOverride) ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGuiComponents.IconButton(FontAwesomeIcon.MapMarkerAlt))
                {
                    if (Player.Available)
                    {
                        Position = Player.Object.Position.ToPoint3();
                    }
                }
                ImGuiEx.Tooltip("设置为玩家当前所在位置");

                ImGuiHelpers.ScaledDummy(3f);
                ImGui.Separator();
                ImGuiHelpers.ScaledDummy(3f);

                {
                    var size = ImGuiEx.CalcIconSize("\uf3c5", true);
                    size += ImGuiEx.CalcIconSize("\uf15c", true);
                    size += ImGuiEx.CalcIconSize(FontAwesomeIcon.Cog, true);
                    size.X += ImGui.GetStyle().ItemSpacing.X * 3;

                    var cur2 = ImGui.GetCursorPos();
                    ImGui.SetCursorPosX(ImGuiEx.GetWindowContentRegionWidth() - size.X);
                    var disabled = !Utils.CanUse();
                    if (disabled) ImGui.BeginDisabled();
                    if (ImGuiEx.IconButton(FontAwesomeIcon.Compass))
                    {
                        P.CompassWindow.IsOpen = !P.CompassWindow.IsOpen;
                    }
                    ImGuiEx.Tooltip("Hyperborea 指南针");
                    if (disabled) ImGui.EndDisabled();
                    ImGui.SameLine();
                    if (ImGuiEx.IconButton("\uf15c"))
                    {
                        P.LogWindow.IsOpen = true;
                    }
                    ImGuiEx.Tooltip("Hyperborea 日志");
                    ImGui.SameLine();
                    if (ImGuiEx.IconButton(FontAwesomeIcon.Cog))
                    {
                        P.SettingsWindow.IsOpen = true;
                    }
                    ImGuiEx.Tooltip("Hyperborea 设置");
                    ImGui.SetCursorPos(cur2);
                }

                {
                    var disabled = !Utils.CanUse();
                    if (disabled) ImGui.BeginDisabled();
                    if (ImGui.Button("加载区域"))
                    {
                        if (ShouldConfirmTerritoryLoad((uint)Math.Max(a2, 0)))
                        {
                            ImGui.OpenPopup(UnsafeTerritoryPopup);
                        }
                        else
                        {
                            LoadSelectedTerritory();
                        }
                    }
                    if (disabled) ImGui.EndDisabled();
                }
                ImGui.SameLine();
                {
                    var disabled = !P.Enabled;
                    if (disabled) ImGui.BeginDisabled();
                    if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Undo, "还原"))
                    {
                        Utils.Revert();
                    }
                    if (disabled) ImGui.EndDisabled();
                }
            }
            catch(Exception e)
            {
                LogTerritoryBrowserWarningOnce("ZoneDataDraw", e, "Failed to draw zone data.");
                ImGuiEx.TextWrapped(EColor.RedBright, "区域数据读取失败，已写入插件日志。");
            }
            ImGuiGroup.EndGroupBox();
        }
        DrawTerritoryBrowserPopup();
        DrawUnsafeTerritoryPopup();
    }

    static void DrawTerritoryBrowserPopup()
    {
        if (!ImGui.BeginPopup(TerritoryBrowserPopup)) return;

        ImGui.SetNextItemWidth(360f);
        ImGui.InputTextWithHint("##territorySearch", "搜索 ID / 名称 / PlaceName / Map / Bg", ref TerritorySearch, 128);
        ImGui.SameLine();
        if (ImGui.Checkbox("显示过场动画/事件专用地图", ref C.IncludeCutsceneEventTerritories))
        {
            EzConfig.Save();
        }

        var entries = GetTerritoryEntries();
        var regularCount = entries.Count(x => x.WasVisibleInOldSelector);
        var extraCount = entries.Count - regularCount;
        ImGuiEx.Text($"常规: {regularCount}  额外/风险: {extraCount}");
        if (TerritoryBrowserUsedFallbackNames)
        {
            ImGuiEx.TextWrapped(EColor.YellowBright, "部分区域名称读取失败，已使用 Territory ID 代替。");
        }

        if (ImGui.BeginChild("##HyperboreaTerritoryBrowserChild", new Vector2(900f, 420f), true))
        {
            if (ImGui.BeginTable("##HyperboreaTerritoryBrowserTable", 7, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("选择", ImGuiTableColumnFlags.WidthFixed, 52f);
                ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 56f);
                ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("标签", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Bg", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("类型", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();

                foreach (var entry in entries)
                {
                    if (!MatchesTerritorySearch(entry)) continue;

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"选择##territory{entry.RowId}", a2 == entry.RowId))
                    {
                        a2 = (int)entry.RowId;
                        ImGui.CloseCurrentPopup();
                    }

                    ImGui.TableNextColumn();
                    ImGuiEx.Text($"{entry.RowId}");
                    ImGui.TableNextColumn();
                    ImGuiEx.Text(entry.DisplayName);
                    ImGui.TableNextColumn();
                    ImGuiEx.Text(entry.Tags.Print(" "));
                    ImGui.TableNextColumn();
                    ImGuiEx.Text(entry.MapText);
                    ImGui.TableNextColumn();
                    ImGuiEx.Text(entry.Bg);
                    ImGui.TableNextColumn();
                    ImGuiEx.Text(entry.IntendedUse);
                }

                ImGui.EndTable();
            }
        }
        ImGui.EndChild();

        if (ImGui.Button("关闭"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    static void DrawUnsafeTerritoryPopup()
    {
        if (!ImGui.BeginPopup(UnsafeTerritoryPopup)) return;

        var entry = CreateTerritoryEntry((uint)Math.Max(a2, 0));
        ImGuiEx.TextWrapped(EColor.RedBright, "该区域可能是过场动画/事件专用或缺少 Map、PlaceName、出生点等资料。请确认已启用 Hyperborea，并准备使用出生点重定向、指南针或快速移动功能脱离异常位置。");
        if (entry != null)
        {
            ImGuiEx.Text($"#{entry.RowId} {entry.DisplayName}");
            ImGuiEx.Text(entry.Tags.Print(" "));
        }

        if (ImGui.Button("确认加载"))
        {
            LoadSelectedTerritory();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("取消"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    static void LoadSelectedTerritory()
    {
        if (a2 <= 0 || !Player.Available) return;
        var l = LayoutWorld.Instance()->ActiveLayout;
        if (l == null) return;

        Utils.TryGetZoneInfo(Utils.GetLayout((uint)a2), out var info2);
        SavedZoneState ??= new SavedZoneState(l->TerritoryTypeId, Player.Object.Position);
        Utils.LoadZone((uint)a2, !SpawnOverride, true, a3, a4, a5, a6, CFCOverride);
        if (SpawnOverride)
        {
            Player.GameObject->SetPosition(Position.X, Position.Y, Position.Z);
        }
        else if (info2 != null && info2.Spawn != null)
        {
            Player.GameObject->SetPosition(info2.Spawn.X, info2.Spawn.Y, info2.Spawn.Z);
        }
    }

    static void DrawSelectedTerritoryWarning(uint territory)
    {
        var entry = CreateTerritoryEntry(territory);
        if (entry == null || !entry.IsPotentiallyUnsafe) return;
        ImGuiEx.TextWrapped(EColor.RedBright, $"风险标签: {entry.Tags.Print(" ")}");
    }

    static bool IsTerritoryPotentiallyUnsafe(uint territory)
    {
        var entry = CreateTerritoryEntry(territory);
        return entry?.IsPotentiallyUnsafe == true;
    }

    static bool ShouldConfirmTerritoryLoad(uint territory)
    {
        var entry = CreateTerritoryEntry(territory);
        return entry?.RequiresConfirmation == true;
    }

    static List<TerritoryBrowserEntry> GetTerritoryEntries()
    {
        var entries = new List<TerritoryBrowserEntry>();
        var sheet = GetSafeTerritorySheet();
        if (sheet == null) return entries;

        List<TerritoryType> territories;
        try
        {
            territories = sheet.ToList();
        }
        catch (Exception e)
        {
            LogTerritoryBrowserWarningOnce("TerritorySheetEnumeration", e, "Failed to enumerate territory sheet.");
            return entries;
        }

        foreach (var territory in territories)
        {
            var entry = CreateTerritoryEntry(territory);
            if (entry == null) continue;
            if (!C.IncludeCutsceneEventTerritories && !entry.WasVisibleInOldSelector) continue;
            entries.Add(entry);
        }
        return entries.OrderBy(x => x.RowId).ToList();
    }

    static TerritoryBrowserEntry? CreateTerritoryEntry(uint territoryId)
    {
        if (territoryId == 0) return null;
        return TryGetTerritoryRow(territoryId, out var territory)
            ? CreateTerritoryEntry(territory)
            : CreateFallbackTerritoryEntry(territoryId);
    }

    static TerritoryBrowserEntry? CreateTerritoryEntry(TerritoryType territory)
    {
        if (territory.RowId == 0) return null;

        try
        {
            return CreateTerritoryEntryCore(territory);
        }
        catch (Exception e)
        {
            return CreateFallbackTerritoryEntry(territory.RowId, e);
        }
    }

    static TerritoryBrowserEntry CreateTerritoryEntryCore(TerritoryType territory)
    {
        var bg = ReadTerritoryText(() => territory.Bg.GetText() ?? "", "Territory.Bg");
        var territoryName = ReadTerritoryText(() => territory.Name.GetText() ?? "", "Territory.Name");
        var placeName = ReadTerritoryText(() => territory.PlaceName.ValueNullable?.Name.GetText() ?? "", "Territory.PlaceName");
        var zoneName = ReadTerritoryText(() => territory.PlaceNameZone.ValueNullable?.Name.GetText() ?? "", "Territory.PlaceNameZone");
        var regionName = ReadTerritoryText(() => territory.PlaceNameRegion.ValueNullable?.Name.GetText() ?? "", "Territory.PlaceNameRegion");
        var cfcName = ReadTerritoryText(() => territory.ContentFinderCondition.ValueNullable?.Name.GetText() ?? "", "Territory.ContentFinderCondition");
        var questBattleName = ReadTerritoryText(() => territory.QuestBattle.ValueNullable?.Quest.GetValueOrDefault<Quest>()?.Name.GetText() ?? "", "Territory.QuestBattle");
        var map = ReadTerritoryValue(() => territory.Map.ValueNullable, "Territory.Map");
        var mapName = ReadTerritoryText(() => map?.PlaceName.ValueNullable?.Name.GetText() ?? "", "Map.PlaceName");
        var mapSubName = ReadTerritoryText(() => map?.PlaceNameSub.ValueNullable?.Name.GetText() ?? "", "Map.PlaceNameSub");
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

        var tags = new List<string>();
        if (!wasVisibleInOldSelector && hasBg && !hasContentFinder) tags.Add("[Cutscene]");
        if (!questBattleName.IsNullOrEmpty()) tags.Add("[Event]");
        if (!hasMap) tags.Add("[No Map]");
        if (placeName.IsNullOrEmpty()) tags.Add("[No PlaceName]");
        if (!wasVisibleInOldSelector || !isRegularUse) tags.Add("[Inaccessible]");
        if (!hasAnyName) tags.Add("[MissingName]");
        if (!hasBg || !hasAnyName || !hasKnownSpawn) tags.Add("[Unsafe]");

        return new TerritoryBrowserEntry
        {
            RowId = territory.RowId,
            DisplayName = cfcName.NullWhenEmpty()
                ?? placeName.NullWhenEmpty()
                ?? questBattleName.NullWhenEmpty()
                ?? territoryName.NullWhenEmpty()
                ?? localizedNames.FirstOrDefault()
                ?? mapText.NullWhenEmpty()
                ?? zoneName.NullWhenEmpty()
                ?? bg.NullWhenEmpty()
                ?? $"Territory {territory.RowId}",
            SearchText = string.Join("\n", localizedNames
                .Append($"{territory.RowId}")
                .Append($"Territory {territory.RowId}")
                .Append(territoryName)
                .Append(placeName)
                .Append(zoneName)
                .Append(regionName)
                .Append(cfcName)
                .Append(questBattleName)
                .Append(mapText)
                .Append($"{territory.Map.RowId}")
                .Append(bg)
                .Append(intendedUse)
                .Append(tags.Print(" "))),
            MapText = mapText,
            Bg = bg,
            IntendedUse = intendedUse,
            Tags = tags,
            WasVisibleInOldSelector = wasVisibleInOldSelector,
            IsPotentiallyUnsafe = tags.Count > 0,
            RequiresConfirmation = !wasVisibleInOldSelector || !hasBg || !hasAnyName || !hasMap,
        };
    }

    static TerritoryBrowserEntry CreateFallbackTerritoryEntry(uint territoryId, Exception? e = null)
    {
        TerritoryBrowserUsedFallbackNames = true;
        if (e != null)
        {
            LogTerritoryBrowserWarningOnce($"TerritoryEntry:{territoryId}", e, $"Failed to create territory entry {territoryId}.");
        }

        return new TerritoryBrowserEntry
        {
            RowId = territoryId,
            DisplayName = $"Territory {territoryId}",
            SearchText = $"{territoryId}\nTerritory {territoryId}",
            MapText = "",
            Bg = "",
            IntendedUse = "",
            Tags = ["[No Map]", "[No PlaceName]", "[MissingName]", "[Unsafe]"],
            WasVisibleInOldSelector = false,
            IsPotentiallyUnsafe = true,
            RequiresConfirmation = true,
        };
    }

    static ExcelSheet<TerritoryType>? GetSafeTerritorySheet()
    {
        foreach (var language in GetSafeTerritorySearchLanguages())
        {
            try
            {
                var sheet = Svc.Data.GetExcelSheet<TerritoryType>(language);
                if (sheet != null) return sheet;
            }
            catch (Exception e)
            {
                LogTerritoryBrowserWarningOnce($"TerritorySheet:{language}", e, $"Failed to load TerritoryType sheet for {language}.");
            }
        }

        return null;
    }

    static bool TryGetTerritoryRow(uint territoryId, out TerritoryType territory)
    {
        territory = default;
        var sheet = GetSafeTerritorySheet();
        if (sheet == null) return false;

        try
        {
            return sheet.TryGetRow(territoryId, out territory);
        }
        catch (Exception e)
        {
            LogTerritoryBrowserWarningOnce($"TerritoryRow:{territoryId}", e, $"Failed to read territory row {territoryId}.");
            return false;
        }
    }

    static IEnumerable<ClientLanguage> GetSafeTerritorySearchLanguages()
    {
        var seen = new HashSet<ClientLanguage>();
        foreach (var language in GetPreferredTerritoryLanguageCandidates())
        {
            if (!seen.Add(language)) continue;
            if (IsSupportedTerritoryLanguage(language)) yield return language;
        }
    }

    static IEnumerable<ClientLanguage> GetPreferredTerritoryLanguageCandidates()
    {
        yield return Svc.ClientState.ClientLanguage;
        yield return ClientLanguage.English;
        foreach (var language in TerritorySearchLanguageCandidates)
        {
            yield return language;
        }
    }

    static bool IsSupportedTerritoryLanguage(ClientLanguage language)
    {
        if (IsInvalidExcelLanguage(language)) return false;
        if (TerritoryLanguageSupport.TryGetValue(language, out var supported)) return supported;

        try
        {
            var sheet = Svc.Data.GetExcelSheet<TerritoryType>(language);
            if (sheet != null)
            {
                _ = sheet.Count;
                supported = true;
            }
            else
            {
                supported = false;
            }
        }
        catch (Exception e)
        {
            supported = false;
            TerritoryBrowserUsedFallbackNames = true;
            LogTerritoryBrowserWarningOnce($"UnsupportedLanguage:{language}", e, $"Skipping unsupported territory language {language}.");
        }

        TerritoryLanguageSupport[language] = supported;
        return supported;
    }

    static bool IsInvalidExcelLanguage(ClientLanguage language)
    {
        var name = language.ToString();
        return name.Equals("None", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    static string[] GetLocalizedTerritoryNames(uint territoryId)
    {
        var names = new List<string>();
        foreach (var language in GetSafeTerritorySearchLanguages())
        {
            var name = GetLocalizedTerritoryName(territoryId, language);
            if (name.IsNullOrEmpty() || name.StartsWith("#")) continue;
            if (!names.Contains(name)) names.Add(name);
        }
        return names.ToArray();
    }

    static string GetLocalizedTerritoryName(uint territoryId, ClientLanguage language)
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<TerritoryType>(language);
            if (sheet == null) return "";

            var territory = sheet.GetRowOrDefault(territoryId);
            if (territory == null) return "";

            var row = territory.Value;
            var cfcName = ReadTerritoryText(() => row.ContentFinderCondition.ValueNullable?.Name.GetText() ?? "", $"LocalizedName.{language}.ContentFinderCondition");
            if (!cfcName.IsNullOrEmpty()) return cfcName;

            var placeName = ReadTerritoryText(() => row.PlaceName.ValueNullable?.Name.GetText() ?? "", $"LocalizedName.{language}.PlaceName");
            if (!placeName.IsNullOrEmpty()) return placeName;

            var territoryName = ReadTerritoryText(() => row.Name.GetText() ?? "", $"LocalizedName.{language}.TerritoryName");
            return territoryName;
        }
        catch (Exception e)
        {
            TerritoryBrowserUsedFallbackNames = true;
            LogTerritoryBrowserWarningOnce($"LocalizedName:{language}", e, $"Failed to read localized territory names for {language}.");
            return "";
        }
    }

    static string ReadTerritoryText(Func<string?> read, string context)
    {
        try
        {
            return read() ?? "";
        }
        catch (Exception e)
        {
            TerritoryBrowserUsedFallbackNames = true;
            LogTerritoryBrowserWarningOnce($"TerritoryText:{context}", e, $"Failed to read {context}.");
            return "";
        }
    }

    static T? ReadTerritoryValue<T>(Func<T?> read, string context) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception e)
        {
            LogTerritoryBrowserWarningOnce($"TerritoryValue:{context}", e, $"Failed to read {context}.");
            return null;
        }
    }

    static void LogTerritoryBrowserWarningOnce(string key, Exception e, string message)
    {
        var warningKey = $"{key}:{e.GetType().FullName}:{e.Message}";
        if (!TerritoryBrowserLoggedWarnings.Add(warningKey)) return;
        PluginLog.Warning($"[TerritoryBrowser] {message} {e.GetType().Name}: {e.Message}");
    }

    static bool MatchesTerritorySearch(TerritoryBrowserEntry entry)
    {
        if (TerritorySearch.IsNullOrEmpty()) return true;
        return entry.SearchText.Contains(TerritorySearch, StringComparison.OrdinalIgnoreCase);
    }

    internal static void CoordBlock(string t, ref float p)
    {
        ImGuiEx.TextV(t);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60f);
        ImGui.DragFloat("##" + t, ref p, 0.1f);
    }

    class TerritoryBrowserEntry
    {
        public uint RowId;
        public string DisplayName = "";
        public string SearchText = "";
        public string MapText = "";
        public string Bg = "";
        public string IntendedUse = "";
        public List<string> Tags = [];
        public bool WasVisibleInOldSelector;
        public bool IsPotentiallyUnsafe;
        public bool RequiresConfirmation;
    }
}
