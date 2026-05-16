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
    static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(20);

    public DirectBgPathState State { get; private set; } = DirectBgPathState.Disabled;
    public string TargetPath { get; private set; } = "";
    public string NormalizedTargetPath { get; private set; } = "";
    public string SourceName { get; private set; } = "";
    public uint CarrierTerritoryId { get; private set; }
    public string CarrierBgPath { get; private set; } = "";
    public string LastOriginalBgPath { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public DateTime? StartedAt { get; private set; }
    public int OverrideHits { get; private set; }
    public bool HasCarrier => GetCarrierTerritoryId() != 0;
    public bool IsHookReady => P.Memory?.LoadPrefetchLayoutHook?.IsEnabled == true;
    public bool HasPendingOverride => targetPathPtr != 0 && State is DirectBgPathState.Preparing or DirectBgPathState.Active;

    public uint GetCarrierTerritoryId()
    {
        if (C.DirectBgPathCarrierTerritoryId != 0) return C.DirectBgPathCarrierTerritoryId;
        return 0;
    }

    public void SetCarrier(uint territoryId)
    {
        C.DirectBgPathCarrierTerritoryId = territoryId;
        EzConfig.Save();
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

        if (!IsHookReady)
        {
            Fail("LoadPrefetchLayout hook is not ready");
            return false;
        }

        var carrierBg = ExcelTerritoryHelper.GetBG(carrier) ?? "";
        if (carrierBg.IsNullOrEmpty())
        {
            Fail($"carrier territory {carrier} has no bg path");
            return false;
        }

        Prepare(entry.Bg, entry.DisplayName, carrier, carrierBg);
        try
        {
            Utils.LoadZone(carrier, setPosition, setPhase, a3, a4, a5, a6, cfcOverride);
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
                State = DirectBgPathState.Active;
                LogCompletionOnce();
                return true;
            }

            PluginLog.Information($"[DirectBgPath] Carrier zone requested; waiting for LoadPrefetchLayout override hit. target = {TargetPath}; carrierTerritory = {CarrierTerritoryId}; carrierBg = {CarrierBgPath}");
            return true;
        }
    }

    public void Update()
    {
        lock (stateLock)
        {
            if (State != DirectBgPathState.Preparing || StartedAt == null) return;
            if (DateTime.Now - StartedAt.Value < PendingTimeout) return;
            State = DirectBgPathState.Failed;
            LastError = "timed out waiting for LoadPrefetchLayout override";
            PluginLog.Warning($"[DirectBgPath] Timed out: reason = {LastError}; target = {TargetPath}; carrierTerritory = {CarrierTerritoryId}; carrierBg = {CarrierBgPath}");
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
            State = DirectBgPathState.Preparing;
            requestLogged = false;
            hitLogged = false;
            completionLogged = false;
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
            var normalizedOriginal = BgPathResolver.NormalizePath(originalPath);
            var normalizedCarrier = BgPathResolver.NormalizePath(CarrierBgPath);
            if (!normalizedOriginal.Equals(normalizedCarrier, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            OverrideHits++;
            LastOriginalBgPath = originalPath;
            State = DirectBgPathState.Active;
            LastError = "";
            overridePathPtr = targetPathPtr;
            if (!hitLogged)
            {
                hitLogged = true;
                PluginLog.Information($"[DirectBgPath] Override hit: carrierBg = {originalPath}; targetBg = {TargetPath}");
            }
            LogCompletionOnce();
            return true;
        }
    }

    public void Clear(string reason = "manual clear")
    {
        lock (stateLock)
        {
            if (State is DirectBgPathState.Preparing or DirectBgPathState.Active)
            {
                PluginLog.Information($"[DirectBgPath] Cleared: reason = {reason}; target = {TargetPath}; hits = {OverrideHits}");
            }
            FreeTargetPath();
            State = DirectBgPathState.Disabled;
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
        }
    }

    void Fail(string reason)
    {
        lock (stateLock)
        {
            LastError = reason;
            State = DirectBgPathState.Failed;
            PluginLog.Warning($"[DirectBgPath] Failed: reason = {reason}");
            FreeTargetPath();
        }
    }

    void LogRequestOnce()
    {
        if (requestLogged) return;
        requestLogged = true;
        PluginLog.Information($"[DirectBgPath] Requested: target = {TargetPath}; carrierTerritory = {CarrierTerritoryId}; normalizedTarget = {NormalizedTargetPath}");
    }

    void LogCompletionOnce()
    {
        if (completionLogged) return;
        completionLogged = true;
        PluginLog.Information($"[DirectBgPath] Completed: target = {TargetPath}; carrierTerritory = {CarrierTerritoryId}; hits = {OverrideHits}");
    }

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
    Disabled,
    Preparing,
    Active,
    Failed,
}
