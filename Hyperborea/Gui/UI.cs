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
    static string SelectedTerritoryBrowserKey = "";
    static TerritoryBrowserFilter TerritoryFilter = TerritoryBrowserFilter.All;
    static string LastSeenTerritorySearch = "";
    static DateTime TerritorySearchChangedAt = DateTime.MinValue;
    static string AppliedTerritorySearch = "";
    static TerritoryBrowserFilter AppliedTerritoryFilter = TerritoryBrowserFilter.All;
    static DateTime AppliedSnapshotBuiltAt = DateTime.MinValue;
    static TerritoryBrowserEntry[] CachedVisibleTerritoryEntries = [];
    const string DirectBgPathPopup = "Direct BgPath 风险确认##HyperboreaDirectBgPath";
    const string TerritoryBrowserPopup = "浏览区域##HyperboreaTerritoryBrowser";
    const string UnsafeTerritoryPopup = "确认加载风险区域##HyperboreaUnsafeTerritory";

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
                    S.TerritoryDiscovery.RequestBuild(C.IncludeCutsceneEventTerritories, false);
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
                var selectedTerritoryEntry = S.TerritoryDiscovery.GetTerritoryEntry((uint)Math.Max(a2, 0));
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
                PluginLog.Warning($"[TerritoryBrowser] Failed to draw zone data. {e.GetType().Name}: {e.Message}");
                ImGuiEx.TextWrapped(EColor.RedBright, "区域数据读取失败，已写入插件日志。");
            }
            ImGuiGroup.EndGroupBox();
        }
        DrawTerritoryBrowserPopup();
        DrawUnsafeTerritoryPopup();
        DrawDirectBgPathPopup();
    }

    static void DrawTerritoryBrowserPopup()
    {
        if (!ImGui.BeginPopup(TerritoryBrowserPopup)) return;

        S.TerritoryDiscovery.RequestBuild(C.IncludeCutsceneEventTerritories, false);
        var snapshot = S.TerritoryDiscovery.GetSnapshot();
        ImGui.SetNextItemWidth(360f);
        ImGui.InputTextWithHint("##territorySearch", "搜索 Territory / Map / Quest / Cutscene / Event / tag", ref TerritorySearch, 160);
        ImGui.SameLine();
        if (ImGui.Checkbox("显示过场动画/事件专用地图", ref C.IncludeCutsceneEventTerritories))
        {
            EzConfig.Save();
            S.TerritoryDiscovery.Invalidate();
        }
        ImGui.SameLine();
        if (ImGui.Button("刷新区域缓存"))
        {
            S.TerritoryDiscovery.Invalidate();
        }

        var entries = S.TerritoryDiscovery.GetEntries(C.IncludeCutsceneEventTerritories);
        var stats = S.TerritoryDiscovery.Stats;
        if (S.TerritoryDiscovery.IsBuilding)
        {
            ImGuiEx.Text($"后台构建: {S.TerritoryDiscovery.CurrentStage} {S.TerritoryDiscovery.Progress:P0}");
            ImGui.SameLine();
            if (ImGui.Button("取消构建")) S.TerritoryDiscovery.CancelBuild();
        }
        if (!S.TerritoryDiscovery.LastError.IsNullOrEmpty())
        {
            ImGuiEx.TextWrapped(EColor.RedBright, $"区域缓存构建失败: {S.TerritoryDiscovery.LastError}");
        }
        ImGuiEx.Text($"状态: {S.TerritoryDiscovery.Status}  总数: {stats.TotalEntries}  普通: {stats.NormalEntries}  Quest: {stats.QuestEntries}  Cutscene: {stats.CutsceneEntries}  Event: {stats.EventSceneEntries}  Instance: {stats.InstanceEntries}  Unsafe: {stats.UnsafeEntries}");
        DrawDirectBgPathStatus();
        if (S.TerritoryDiscovery.UsedFallbackNames)
        {
            ImGuiEx.TextWrapped(EColor.YellowBright, "部分区域名称读取失败，已使用 Territory ID 代替。");
        }

        ImGui.SetNextItemWidth(180f);
        if (ImGui.BeginCombo("过滤", TerritoryFilter.ToString()))
        {
            foreach (var filter in Enum.GetValues<TerritoryBrowserFilter>())
            {
                if (ImGui.Selectable(filter.ToString(), TerritoryFilter == filter))
                {
                    TerritoryFilter = filter;
                }
            }
            ImGui.EndCombo();
        }

        var visibleEntries = snapshot == null ? GetVisibleTerritoryEntries(entries) : GetVisibleTerritoryEntries(snapshot);
        ImGui.SameLine();
        ImGuiEx.Text($"显示: {visibleEntries.Length}");

        if (ImGui.BeginChild("##HyperboreaTerritoryBrowserChild", new Vector2(980f, 430f), true))
        {
            if (ImGui.BeginTable("##HyperboreaTerritoryBrowserTable", 8, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("选择", ImGuiTableColumnFlags.WidthFixed, 52f);
                ImGui.TableSetupColumn("Territory", ImGuiTableColumnFlags.WidthFixed, 76f);
                ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("来源", ImGuiTableColumnFlags.WidthFixed, 72f);
                ImGui.TableSetupColumn("标签", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthFixed, 64f);
                ImGui.TableSetupColumn("PlaceName/Bg", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("类型", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();

                foreach (var entry in visibleEntries)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var disabled = !entry.HasTerritory;
                    if (disabled) ImGui.BeginDisabled();
                    if (ImGui.Selectable($"选择##{entry.Key}", entry.HasTerritory && a2 == entry.RowId))
                    {
                        a2 = (int)entry.RowId;
                        SelectedTerritoryBrowserKey = entry.Key;
                        ImGui.CloseCurrentPopup();
                    }
                    if (disabled)
                    {
                        ImGui.EndDisabled();
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        {
                            ImGuiEx.Tooltip("该条目缺少 TerritoryType，请在详情中使用 Direct BgPath 模式尝试进入。");
                        }
                    }

                    ImGui.TableNextColumn();
                    ImGuiEx.Text(entry.HasTerritory ? $"{entry.RowId}" : "-");
                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{entry.DisplayName}##detail{entry.Key}", SelectedTerritoryBrowserKey == entry.Key))
                    {
                        SelectedTerritoryBrowserKey = entry.Key;
                    }
                    ImGui.TableNextColumn();
                    ImGuiEx.Text($"{entry.SourceCount}");
                    ImGui.TableNextColumn();
                    var tags = entry.Tags.Print(" ");
                    if (!entry.HasTerritory && !entry.Bg.IsNullOrEmpty() && C.DirectBgPathCarrierTerritoryId != 0)
                    {
                        tags += " [DirectBgPathAvailable]";
                    }
                    ImGuiEx.Text(tags);
                    ImGui.TableNextColumn();
                    ImGuiEx.Text(entry.MapId == 0 ? entry.MapText : $"{entry.MapId}");
                    ImGui.TableNextColumn();
                    ImGuiEx.Text(entry.PlaceName.NullWhenEmpty() ?? entry.Bg);
                    ImGui.TableNextColumn();
                    ImGuiEx.Text(entry.IntendedUse);
                }

                ImGui.EndTable();
            }
        }
        ImGui.EndChild();

        DrawTerritoryBrowserDetails();

        if (ImGui.Button("关闭"))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    static void DrawUnsafeTerritoryPopup()
    {
        if (!ImGui.BeginPopup(UnsafeTerritoryPopup)) return;

        var entry = S.TerritoryDiscovery.GetTerritoryEntry((uint)Math.Max(a2, 0));
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
        var entry = S.TerritoryDiscovery.GetTerritoryEntry(territory);
        if (entry == null || !entry.IsPotentiallyUnsafe) return;
        ImGuiEx.TextWrapped(EColor.RedBright, $"风险标签: {entry.Tags.Print(" ")}");
    }

    static bool IsTerritoryPotentiallyUnsafe(uint territory)
    {
        var entry = S.TerritoryDiscovery.GetTerritoryEntry(territory);
        return entry?.IsPotentiallyUnsafe == true;
    }

    static bool ShouldConfirmTerritoryLoad(uint territory)
    {
        var entry = S.TerritoryDiscovery.GetTerritoryEntry(territory);
        return entry?.RequiresConfirmation == true;
    }

    static TerritoryBrowserEntry[] GetVisibleTerritoryEntries(TerritoryDiscoverySnapshot snapshot)
        => GetVisibleTerritoryEntries(snapshot.Entries, snapshot.BuiltAt);

    static TerritoryBrowserEntry[] GetVisibleTerritoryEntries(IReadOnlyList<TerritoryBrowserEntry> entries)
        => GetVisibleTerritoryEntries(entries, DateTime.MinValue);

    static TerritoryBrowserEntry[] GetVisibleTerritoryEntries(IReadOnlyList<TerritoryBrowserEntry> entries, DateTime snapshotBuiltAt)
    {
        if (!LastSeenTerritorySearch.Equals(TerritorySearch, StringComparison.Ordinal))
        {
            LastSeenTerritorySearch = TerritorySearch;
            TerritorySearchChangedAt = DateTime.UtcNow;
        }

        var queryChanged = !AppliedTerritorySearch.Equals(TerritorySearch, StringComparison.Ordinal);
        var debounceReady = !queryChanged || (DateTime.UtcNow - TerritorySearchChangedAt).TotalMilliseconds >= 250;
        var mustRefresh = snapshotBuiltAt != AppliedSnapshotBuiltAt
            || AppliedTerritoryFilter != TerritoryFilter
            || CachedVisibleTerritoryEntries.Length == 0
            || (queryChanged && debounceReady);

        if (!mustRefresh) return CachedVisibleTerritoryEntries;

        AppliedTerritorySearch = TerritorySearch;
        AppliedTerritoryFilter = TerritoryFilter;
        AppliedSnapshotBuiltAt = snapshotBuiltAt;
        var query = TerritorySearch.Trim().ToLowerInvariant();
        CachedVisibleTerritoryEntries = entries
            .Where(MatchesTerritoryFilter)
            .Where(entry => query.IsNullOrEmpty() || entry.SearchText.Contains(query, StringComparison.Ordinal))
            .Take(5000)
            .ToArray();
        return CachedVisibleTerritoryEntries;
    }

    static bool MatchesTerritorySearch(TerritoryBrowserEntry entry)
    {
        if (TerritorySearch.IsNullOrEmpty()) return true;
        return entry.SearchText.Contains(TerritorySearch.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }

    static bool MatchesTerritoryFilter(TerritoryBrowserEntry entry)
    {
        return TerritoryFilter switch
        {
            TerritoryBrowserFilter.Normal => entry.HasTag("[Normal]"),
            TerritoryBrowserFilter.Quest => entry.HasTag("[Quest]") || entry.HasTag("[MSQ]") || entry.HasTag("[SideQuest]"),
            TerritoryBrowserFilter.Cutscene => entry.HasTag("[Cutscene]"),
            TerritoryBrowserFilter.EventScene => entry.HasTag("[EventScene]"),
            TerritoryBrowserFilter.Instance => entry.HasTag("[Instance]"),
            TerritoryBrowserFilter.Unsafe => entry.HasTag("[Unsafe]"),
            _ => true,
        };
    }

    static void DrawTerritoryBrowserDetails()
    {
        if (SelectedTerritoryBrowserKey.IsNullOrEmpty()) return;
        var entry = S.TerritoryDiscovery.GetEntryByKey(SelectedTerritoryBrowserKey);
        if (entry == null) return;

        if (!ImGui.BeginChild("##HyperboreaTerritoryBrowserDetails", new Vector2(980f, 180f), true))
        {
            ImGui.EndChild();
            return;
        }
        ImGuiEx.Text($"{entry.DisplayName}  Territory: {(entry.HasTerritory ? entry.RowId.ToString() : "-")}  Map: {(entry.MapId == 0 ? "-" : entry.MapId.ToString())}");
        ImGuiEx.TextWrapped($"Tags: {entry.Tags.Print(" ")}");
        if (!entry.Bg.IsNullOrEmpty()) ImGuiEx.TextWrapped($"Bg/Path: {entry.Bg}");
        if (!entry.PlaceName.IsNullOrEmpty()) ImGuiEx.TextWrapped($"PlaceName: {entry.PlaceName}");
        if (!entry.HasTerritory)
        {
            ImGuiEx.TextWrapped(EColor.RedBright, "该条目缺少 TerritoryType，只能作为 cutscene/scene 线索搜索，不能直接进入。");
        }

        if (!entry.HasTerritory && !entry.Bg.IsNullOrEmpty())
        {
            ImGuiEx.TextWrapped(EColor.RedBright, "No TerritoryType matched this Bg/Path. Direct BgPath Entry Mode / territory data injection is required.");
            DrawDirectBgPathControls(entry);
        }

        if (entry.BgPathMatches.Count > 0)
        {
            ImGuiEx.Text($"Also matched territory candidates: {entry.BgPathMatches.Count}");
            foreach (var match in entry.BgPathMatches.Take(12))
            {
                ImGuiEx.TextWrapped($"Territory {match.TerritoryId}  {match.MatchKind}  score {match.Score}  {match.TerritoryBgPath}");
            }
        }

        ImGui.Separator();
        ImGuiEx.Text($"Sources: {entry.SourceCount}");
        var shownSources = entry.Sources.Take(20).ToArray();
        foreach (var source in shownSources)
        {
            var ids = new List<string>();
            if (source.QuestId is { } questId) ids.Add($"Quest {questId}");
            if (source.CutsceneId is { } cutsceneId) ids.Add($"Cutscene {cutsceneId}");
            if (source.EventSceneId is { } eventId) ids.Add($"EventScene {eventId}");
            if (source.LevelId is { } levelId) ids.Add($"Level {levelId}");
            if (source.MapId is { } mapId) ids.Add($"Map {mapId}");
            if (source.InstanceContentId is { } instanceId) ids.Add($"Instance {instanceId}");
            ImGuiEx.TextWrapped($"{source.Sheet}#{source.RowId} {source.Field} {ids.Print(" ")} {source.Name}");
        }
        if (entry.Sources.Count > shownSources.Length)
        {
            ImGuiEx.Text($"... {entry.Sources.Count - shownSources.Length} more sources");
        }
        ImGui.EndChild();
    }

    static void DrawDirectBgPathStatus()
    {
        var service = S.DirectBgPathEntry;
        service.Update();
        ImGuiEx.Text($"DirectBgPath State: {service.State}  Carrier: {(C.DirectBgPathCarrierTerritoryId == 0 ? "未配置" : C.DirectBgPathCarrierTerritoryId.ToString())}  Hook: {(service.IsHookReady ? "OK" : "不可用")}");
        ImGuiEx.Text($"Attempt: {(service.AttemptId == 0 ? "-" : service.AttemptId.ToString())}  ResourceProbe: {(C.DirectBgPathResourceProbe ? "ON" : "OFF")}");
        if (!service.TargetPath.IsNullOrEmpty())
        {
            ImGuiEx.TextWrapped($"Last target: {service.TargetPath}  Hits: {service.OverrideHits}");
        }
        if (!service.LastOriginalBgPath.IsNullOrEmpty())
        {
            ImGuiEx.TextWrapped($"Last override: {service.LastOriginalBgPath} -> {service.LastReplacementPath}");
        }
        if (!service.LastResourceProbePath.IsNullOrEmpty())
        {
            ImGuiEx.TextWrapped($"Last probe: {service.LastResourceProbePath}");
        }
        ImGuiEx.Text($"Resource counters: probed {service.ProbedResources}  unique {service.UniqueProbedResources}  hits {service.OverrideHits}");
        ImGuiEx.Text($"Skipped: missing target {service.SkippedBecauseTargetMissing}  non-layout {service.SkippedBecauseNotLayout}  after clear {service.SkippedBecauseAfterClear}");
        if (!service.TargetResourceProbeSummary.IsNullOrEmpty())
        {
            ImGuiEx.TextWrapped($"Target probe: {service.TargetResourceProbeSummary}");
        }
        if (!service.LastSkipReason.IsNullOrEmpty())
        {
            ImGuiEx.TextWrapped($"Last skip: {service.LastSkipReason}");
        }
        if (service.TerritoryBefore != 0 || service.LastObservedTerritory != 0)
        {
            ImGuiEx.Text($"Territory: before {service.TerritoryBefore}  last {service.LastObservedTerritory}");
        }
        if (service.LastLogTime != null)
        {
            ImGuiEx.Text($"Last log time: {service.LastLogTime:yyyy-MM-dd HH:mm:ss}");
        }
        if (!service.LastError.IsNullOrEmpty())
        {
            ImGuiEx.TextWrapped(EColor.RedBright, $"Last error: {service.LastError}");
            if (service.State == DirectBgPathState.TimedOut)
            {
                ImGuiEx.TextWrapped(EColor.YellowBright, "hook 没命中，当前 hook 点可能不适合国服客户端，需要更换更底层的 bg/scene resolve hook。");
            }
        }
        if (service.IsBusy)
        {
            ImGuiEx.TextWrapped(EColor.YellowBright, "DirectBgPath 正在尝试进入，请等待或点击取消。");
        }
        if (service.State is DirectBgPathState.Requested or DirectBgPathState.EnteringCarrier or DirectBgPathState.WaitingForResourceOverride or DirectBgPathState.ActiveOverride or DirectBgPathState.Stable or DirectBgPathState.TimedOut)
        {
            if (ImGui.Button(service.IsBusy ? "取消 DirectBgPath" : "清除 DirectBgPath Override"))
            {
                service.Clear("user cleared override");
            }
            if (SavedZoneState != null)
            {
                ImGui.SameLine();
                if (ImGui.Button("Return to saved normal territory"))
                {
                    Utils.Revert();
                }
            }
        }
    }

    static void DrawDirectBgPathControls(TerritoryBrowserEntry entry)
    {
        var carrier = (int)C.DirectBgPathCarrierTerritoryId;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Direct BgPath Carrier Territory", ref carrier))
        {
            S.DirectBgPathEntry.SetCarrier((uint)Math.Max(carrier, 0));
        }
        ImGui.SameLine();
        if (ImGui.Button("使用当前区域作为载体"))
        {
            S.DirectBgPathEntry.SetCarrier(Svc.ClientState.TerritoryType);
        }
        ImGui.SameLine();
        if (entry.HasTerritory && ImGui.Button("使用该区域作为载体"))
        {
            S.DirectBgPathEntry.SetCarrier(entry.RowId);
        }

        var carrierOk = S.DirectBgPathEntry.TryGetCarrierInfo(C.DirectBgPathCarrierTerritoryId, out var carrierBg, out var carrierError);
        if (carrierOk)
        {
            ImGuiEx.TextWrapped($"Carrier Bg: {carrierBg}");
        }
        else if (C.DirectBgPathCarrierTerritoryId != 0)
        {
            ImGuiEx.TextWrapped(EColor.RedBright, $"Carrier 无效: {carrierError}");
        }

        var probe = C.DirectBgPathResourceProbe;
        if (ImGui.Checkbox("DirectBgPath Resource Probe", ref probe))
        {
            C.DirectBgPathResourceProbe = probe;
            EzConfig.Save();
        }
        var holdUntilManualClear = C.DirectBgPathHoldUntilManualClear;
        if (ImGui.Checkbox("Keep DirectBgPath override until manual clear", ref holdUntilManualClear))
        {
            C.DirectBgPathHoldUntilManualClear = holdUntilManualClear;
            EzConfig.Save();
        }

        if (C.DirectBgPathCarrierTerritoryId == 0)
        {
            ImGuiEx.TextWrapped(EColor.YellowBright, "该条目缺少 TerritoryType。可作为线索搜索；若要尝试进入，请先配置 Direct BgPath Carrier。");
        }
        else
        {
            ImGuiEx.TextWrapped(EColor.YellowBright, "该条目缺少 TerritoryType，将使用 Direct BgPath 模式通过载体区域尝试加载。");
        }

        if (ImGui.Button("复制 BgPath"))
        {
            ImGui.SetClipboardText(entry.Bg);
        }
        ImGui.SameLine();
        if (ImGui.Button("测试 Carrier"))
        {
            LoadCarrierTest();
        }
        ImGui.SameLine();
        if (ImGui.Button("DirectBgPath 诊断"))
        {
            S.DirectBgPathEntry.LogDiagnostic(entry);
        }
        ImGui.SameLine();
        if (ImGui.Button("清除 DirectBgPath 状态"))
        {
            S.DirectBgPathEntry.Clear("user cleared direct state");
        }

        var directBusy = S.DirectBgPathEntry.IsBusy;
        if (directBusy)
        {
            ImGui.BeginDisabled();
        }
        if (ImGui.Button("使用 Direct BgPath 尝试进入"))
        {
            SelectedTerritoryBrowserKey = entry.Key;
            if (S.DirectBgPathEntry.ButtonClicked(entry))
            {
                LoadDirectBgPath(entry);
            }
        }
        if (directBusy)
        {
            ImGui.EndDisabled();
        }
    }

    static void DrawDirectBgPathPopup()
    {
        if (!ImGui.BeginPopup(DirectBgPathPopup)) return;
        var entry = S.TerritoryDiscovery.GetEntryByKey(SelectedTerritoryBrowserKey);
        ImGuiEx.TextWrapped(EColor.RedBright, "该场景没有 TerritoryType，将使用 Direct BgPath 模式通过载体区域加载，可能黑屏、崩溃、卡死或无法移动。若失败，请清除 DirectBgPath Override 或返回普通区域。");
        if (entry != null)
        {
            ImGuiEx.TextWrapped($"Target: {entry.Bg}");
            ImGuiEx.Text($"Carrier Territory: {C.DirectBgPathCarrierTerritoryId}");
        }

        if (ImGui.Button("确认 Direct BgPath 进入") && entry != null)
        {
            if (S.DirectBgPathEntry.ButtonClicked(entry))
            {
                LoadDirectBgPath(entry);
            }
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("取消"))
        {
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    static void LoadDirectBgPath(TerritoryBrowserEntry entry)
    {
        if (entry.Bg.IsNullOrEmpty())
        {
            S.DirectBgPathEntry.Fail("entry has no bg path");
            return;
        }
        if (!Player.Available)
        {
            S.DirectBgPathEntry.Fail("player is not available");
            return;
        }
        var l = LayoutWorld.Instance()->ActiveLayout;
        if (l == null)
        {
            S.DirectBgPathEntry.Fail("active layout is not available");
            return;
        }
        SavedZoneState ??= new SavedZoneState(l->TerritoryTypeId, Player.Object.Position);
        var loaded = S.DirectBgPathEntry.TryEnter(entry, !SpawnOverride, true, a3, a4, a5, a6, CFCOverride);
        if (loaded && SpawnOverride)
        {
            Player.GameObject->SetPosition(Position.X, Position.Y, Position.Z);
        }
    }

    static void LoadCarrierTest()
    {
        if (!Player.Available)
        {
            S.DirectBgPathEntry.Fail("player is not available");
            return;
        }
        var l = LayoutWorld.Instance()->ActiveLayout;
        if (l == null)
        {
            S.DirectBgPathEntry.Fail("active layout is not available");
            return;
        }

        SavedZoneState ??= new SavedZoneState(l->TerritoryTypeId, Player.Object.Position);
        var loaded = S.DirectBgPathEntry.TryTestCarrier(!SpawnOverride, true, a3, a4, a5, a6, CFCOverride);
        if (loaded && SpawnOverride)
        {
            Player.GameObject->SetPosition(Position.X, Position.Y, Position.Z);
        }
    }

    internal static void CoordBlock(string t, ref float p)
    {
        ImGuiEx.TextV(t);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60f);
        ImGui.DragFloat("##" + t, ref p, 0.1f);
    }

}
