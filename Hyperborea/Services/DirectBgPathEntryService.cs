using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using System.Runtime.InteropServices;
using System.Text;

namespace Hyperborea.Services;

public unsafe sealed class DirectBgPathEntryService : IDisposable
{
    readonly object stateLock = new();
    nint targetPathPtr;
    bool requestLogged;
    bool hitLogged;
    bool completionLogged;
    bool hookCalledLogged;
    bool hookMismatchLogged;
    static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(20);

    public DirectBgPathState State { get; private set; } = DirectBgPathState.Idle;
    public string TargetPath { get; private set; } = "";
    public string NormalizedTargetPath { get; private set; } = "";
    public string SourceName { get; private set; } = "";
    public uint CarrierTerritoryId { get; private set; }
    public string CarrierBgPath { get; private set; } = "";
    public string LastOriginalBgPath { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public DateTime? StartedAt { get; private set; }
    public DateTime? LastLogTime { get; private set; }
    public int OverrideHits { get; private set; }
    public bool HasCarrier => GetCarrierTerritoryId() != 0;
    public bool IsHookReady => P.Memory?.LoadPrefetchLayoutHook?.IsEnabled == true;
    public bool HasPendingOverride => targetPathPtr != 0 && State is DirectBgPathState.Requested or DirectBgPathState.EnteringCarrier or DirectBgPathState.WaitingForOverrideHook or DirectBgPathState.OverrideHit or DirectBgPathState.Completed;

    public DirectBgPathEntryService()
    {
        TouchLog();
        PluginLog.Information("[DirectBgPath] Service initialized");
    }

    public uint GetCarrierTerritoryId()
    {
        if (C.DirectBgPathCarrierTerritoryId != 0) return C.DirectBgPathCarrierTerritoryId;
        return 0;
    }

    public void SetCarrier(uint territoryId)
    {
        C.DirectBgPathCarrierTerritoryId = territoryId;
        EzConfig.Save();
        TouchLog();
        PluginLog.Information($"[DirectBgPath] Carrier set: carrierTerritory = {territoryId}");
    }

    public void ButtonClicked(TerritoryBrowserEntry entry)
    {
        lock (stateLock)
        {
            State = DirectBgPathState.ButtonClicked;
            TargetPath = entry.Bg ?? "";
            NormalizedTargetPath = TargetPath.IsNullOrEmpty() ? "" : BgPathResolver.NormalizePath(TargetPath);
            CarrierTerritoryId = GetCarrierTerritoryId();
            LastError = "";
            TouchLog();
            PluginLog.Information($"[DirectBgPath] Button clicked: target = {TargetPath}; carrier = {CarrierTerritoryId}; hookReady = {IsHookReady}; canUse = {Utils.CanUse()}");
        }
    }

    public bool TryEnter(TerritoryBrowserEntry entry, bool setPosition, bool setPhase, int a3, int a4, int a5, int a6, int cfcOverride)
    {
        if (entry.Bg.IsNullOrEmpty())
        {
            Fail("entry has no bg path");
            return false;
        }

        var carrier = GetCarrierTerritoryId();
        if (carrier == 0)
        {
            Fail("carrier territory is not configured");
            return false;
        }

        if (!Utils.CanUse())
        {
            Fail("normal carrier enter logic is not available; enable Hyperborea and confirm packet hooks/inn check are ready");
            return false;
        }

        if (!IsHookReady)
        {
            Fail("LoadPrefetchLayout hook is not ready");
            return false;
        }

        if (!TryGetCarrierInfo(carrier, out var carrierBg, out var carrierError))
        {
            Fail(carrierError);
            return false;
        }

        Prepare(entry.Bg, entry.DisplayName, carrier, carrierBg);
        try
        {
            SetState(DirectBgPathState.EnteringCarrier);
            PluginLog.Information($"[DirectBgPath] Calling carrier enter logic: carrier = {carrier}; carrierBg = {CarrierBgPath}; target = {TargetPath}");
            Utils.LoadZone(carrier, setPosition, setPhase, a3, a4, a5, a6, cfcOverride);
            TouchLog();
            PluginLog.Information($"[DirectBgPath] Carrier enter logic returned: carrier = {carrier}; overrideHits = {OverrideHits}");
        }
        catch (Exception e)
        {
            Fail($"carrier zone load failed: {e.GetType().Name}: {e.Message}");
            return false;
        }

        lock (stateLock)
        {
            if (OverrideHits > 0)
            {
                State = DirectBgPathState.Completed;
                LogCompletionOnce();
                return true;
            }

            State = DirectBgPathState.WaitingForOverrideHook;
            TouchLog();
            PluginLog.Information($"[DirectBgPath] Carrier zone requested; waiting for LoadPrefetchLayout override hit. target = {TargetPath}; carrierTerritory = {CarrierTerritoryId}; carrierBg = {CarrierBgPath}");
            return true;
        }
    }

    public bool TryTestCarrier(bool setPosition, bool setPhase, int a3, int a4, int a5, int a6, int cfcOverride)
    {
        var carrier = GetCarrierTerritoryId();
        PluginLog.Information($"[DirectBgPath] Test carrier clicked: carrier = {carrier}; canUse = {Utils.CanUse()}");
        if (carrier == 0)
        {
            Fail("carrier territory is not configured");
            return false;
        }

        if (!Utils.CanUse())
        {
            Fail("normal carrier enter logic is not available; enable Hyperborea and confirm packet hooks/inn check are ready");
            return false;
        }

        if (!TryGetCarrierInfo(carrier, out var carrierBg, out var carrierError))
        {
            Fail(carrierError);
            return false;
        }

        try
        {
            SetState(DirectBgPathState.EnteringCarrier);
            PluginLog.Information($"[DirectBgPath] Calling normal carrier test enter logic: carrier = {carrier}; carrierBg = {carrierBg}");
            Utils.LoadZone(carrier, setPosition, setPhase, a3, a4, a5, a6, cfcOverride);
            SetState(DirectBgPathState.Completed);
            LastError = "";
            PluginLog.Information($"[DirectBgPath] Carrier test enter logic returned: carrier = {carrier}");
            return true;
        }
        catch (Exception e)
        {
            Fail($"carrier test enter failed: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    public void Update()
    {
        lock (stateLock)
        {
            if (State != DirectBgPathState.WaitingForOverrideHook || StartedAt == null) return;
            if (DateTime.Now - StartedAt.Value < PendingTimeout) return;
            State = DirectBgPathState.TimedOut;
            LastError = "timed out waiting for LoadPrefetchLayout override";
            TouchLog();
            PluginLog.Warning($"[DirectBgPath] Timed out waiting for bg path hook: reason = {LastError}; target = {TargetPath}; carrierTerritory = {CarrierTerritoryId}; carrierBg = {CarrierBgPath}");
            FreeTargetPath();
        }
    }

    void Prepare(string targetPath, string sourceName, uint carrierTerritoryId, string carrierBgPath)
    {
        lock (stateLock)
        {
            FreeTargetPath();
            TargetPath = NormalizeForLoad(targetPath);
            NormalizedTargetPath = BgPathResolver.NormalizePath(TargetPath);
            SourceName = sourceName;
            CarrierTerritoryId = carrierTerritoryId;
            CarrierBgPath = NormalizeForLoad(carrierBgPath);
            LastOriginalBgPath = "";
            LastError = "";
            StartedAt = DateTime.Now;
            OverrideHits = 0;
            State = DirectBgPathState.Requested;
            requestLogged = false;
            hitLogged = false;
            completionLogged = false;
            hookCalledLogged = false;
            hookMismatchLogged = false;
            targetPathPtr = AllocUtf8(TargetPath);
            LogRequestOnce();
        }
    }

    public bool TryGetOverridePath(string originalPath, out nint overridePathPtr)
    {
        lock (stateLock)
        {
            overridePathPtr = 0;
            if (!HasPendingOverride) return false;
            if (!hookCalledLogged)
            {
                hookCalledLogged = true;
                TouchLog();
                PluginLog.Information($"[DirectBgPath] Hook called while pending: originalBg = {originalPath}; expectedCarrierBg = {CarrierBgPath}; targetBg = {TargetPath}");
            }

            var normalizedOriginal = BgPathResolver.NormalizePath(originalPath);
            var normalizedCarrier = BgPathResolver.NormalizePath(CarrierBgPath);
            if (!normalizedOriginal.Equals(normalizedCarrier, StringComparison.OrdinalIgnoreCase))
            {
                if (!hookMismatchLogged)
                {
                    hookMismatchLogged = true;
                    TouchLog();
                    PluginLog.Information($"[DirectBgPath] Hook called but carrier bg did not match: carrierBg = {originalPath}; expectedCarrier = {CarrierBgPath}; normalizedCarrierBg = {normalizedOriginal}; normalizedExpectedCarrier = {normalizedCarrier}");
                }
                return false;
            }

            OverrideHits++;
            LastOriginalBgPath = originalPath;
            State = DirectBgPathState.OverrideHit;
            LastError = "";
            overridePathPtr = targetPathPtr;
            if (!hitLogged)
            {
                hitLogged = true;
                TouchLog();
                PluginLog.Information($"[DirectBgPath] Override hit: carrierBg = {originalPath}; targetBg = {TargetPath}");
            }
            State = DirectBgPathState.Completed;
            LogCompletionOnce();
            return true;
        }
    }

    public void Clear(string reason = "manual clear")
    {
        lock (stateLock)
        {
            if (State is DirectBgPathState.Requested or DirectBgPathState.EnteringCarrier or DirectBgPathState.WaitingForOverrideHook or DirectBgPathState.OverrideHit or DirectBgPathState.Completed)
            {
                PluginLog.Information($"[DirectBgPath] Cleared: reason = {reason}; target = {TargetPath}; hits = {OverrideHits}");
            }
            FreeTargetPath();
            State = DirectBgPathState.Idle;
            TargetPath = "";
            NormalizedTargetPath = "";
            SourceName = "";
            CarrierTerritoryId = 0;
            CarrierBgPath = "";
            LastOriginalBgPath = "";
            LastError = "";
            StartedAt = null;
            OverrideHits = 0;
            requestLogged = false;
            hitLogged = false;
            completionLogged = false;
            hookCalledLogged = false;
            hookMismatchLogged = false;
            TouchLog();
        }
    }

    public void Fail(string reason)
    {
        lock (stateLock)
        {
            LastError = reason;
            State = DirectBgPathState.Failed;
            TouchLog();
            PluginLog.Warning($"[DirectBgPath] Failed: reason = {reason}");
            FreeTargetPath();
        }
    }

    public bool TryGetCarrierInfo(uint carrier, out string carrierBg, out string error)
    {
        carrierBg = "";
        error = "";
        if (carrier == 0)
        {
            error = "carrier territory is not configured";
            return false;
        }

        var territory = ExcelTerritoryHelper.Get(carrier);
        if (territory == null)
        {
            error = $"carrier territory {carrier} does not exist";
            return false;
        }

        carrierBg = ExcelTerritoryHelper.GetBG(carrier) ?? "";
        if (carrierBg.IsNullOrEmpty())
        {
            error = $"carrier territory {carrier} has no bg path";
            return false;
        }

        return true;
    }

    public void LogDiagnostic(TerritoryBrowserEntry? entry = null)
    {
        var carrier = GetCarrierTerritoryId();
        var carrierExists = TryGetCarrierInfo(carrier, out var carrierBg, out var carrierError);
        var snapshot = S.TerritoryDiscovery.GetSnapshot();
        TouchLog();
        PluginLog.Information(
            "[DirectBgPath Diagnostic]\n" +
            $"serviceInitialized = true\n" +
            $"hookInstalled = {IsHookReady}\n" +
            $"hookName = LoadPrefetchLayout\n" +
            $"carrierTerritory = {carrier}\n" +
            $"carrierExists = {carrierExists}\n" +
            $"carrierBg = {carrierBg}\n" +
            $"carrierError = {carrierError}\n" +
            $"targetBg = {entry?.Bg ?? TargetPath}\n" +
            $"normalizedTarget = {(entry?.Bg.IsNullOrEmpty() == false ? BgPathResolver.NormalizePath(entry.Bg) : NormalizedTargetPath)}\n" +
            $"pending = {HasPendingOverride}\n" +
            $"state = {State}\n" +
            $"lastError = {(LastError.IsNullOrEmpty() ? "none" : LastError)}\n" +
            $"canCallNormalEnter = {Utils.CanUse()}\n" +
            $"includeCutsceneCacheReady = {snapshot?.IncludeCutsceneTerritories == true}\n" +
            $"cacheEntries = {snapshot?.Entries.Count ?? 0}");
    }

    void LogRequestOnce()
    {
        if (requestLogged) return;
        requestLogged = true;
        TouchLog();
        PluginLog.Information($"[DirectBgPath] Requested: target = {TargetPath}; carrierTerritory = {CarrierTerritoryId}; normalizedTarget = {NormalizedTargetPath}");
    }

    void LogCompletionOnce()
    {
        if (completionLogged) return;
        completionLogged = true;
        TouchLog();
        PluginLog.Information($"[DirectBgPath] Completed: target = {TargetPath}; carrierTerritory = {CarrierTerritoryId}; hits = {OverrideHits}");
    }

    void SetState(DirectBgPathState state)
    {
        lock (stateLock)
        {
            State = state;
            TouchLog();
        }
    }

    void TouchLog() => LastLogTime = DateTime.Now;

    static string NormalizeForLoad(string path)
    {
        var ret = BgPathResolver.NormalizeBasicPath(path);
        if (ret.StartsWith("bg/", StringComparison.Ordinal)) ret = ret[3..];
        foreach (var extension in new[] { ".lvb", ".lgb" })
        {
            if (ret.EndsWith(extension, StringComparison.Ordinal))
            {
                ret = ret[..^extension.Length];
                break;
            }
        }
        return ret;
    }

    static nint AllocUtf8(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\0");
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    void FreeTargetPath()
    {
        if (targetPathPtr == 0) return;
        Marshal.FreeHGlobal(targetPathPtr);
        targetPathPtr = 0;
    }

    public void Dispose() => Clear("service disposed");
}

public enum DirectBgPathState
{
    Idle,
    ButtonClicked,
    Requested,
    EnteringCarrier,
    WaitingForOverrideHook,
    OverrideHit,
    Completed,
    Failed,
    TimedOut,
}
