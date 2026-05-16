using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Hyperborea.Services;

public unsafe sealed class DirectBgPathEntryService : IDisposable
{
    static readonly TimeSpan CarrierChangeWarningTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(20);
    static readonly string[] LayoutExtensions = [".lvb", ".lgb", ".sgb", ".pcb"];
    static readonly string[] ProbeKeywords = ["/level/", ".lvb", ".lgb", ".sgb", "bg/", "manfst00510", "s1ti"];

    readonly object stateLock = new();
    readonly Dictionary<string, nint> replacementPathPtrs = new(StringComparer.OrdinalIgnoreCase);
    nint targetPathPtr;
    int nextAttemptId;
    bool requestLogged;
    bool hitLogged;
    bool completionLogged;
    bool hookCalledLogged;
    bool hookMismatchLogged;
    bool carrierChangeWarningLogged;
    bool probeLimitLogged;
    bool overrideLimitLogged;
    int probeLogCount;

    public DirectBgPathState State { get; private set; } = DirectBgPathState.Idle;
    public int AttemptId { get; private set; }
    public string TargetPath { get; private set; } = "";
    public string NormalizedTargetPath { get; private set; } = "";
    public string SourceName { get; private set; } = "";
    public uint CarrierTerritoryId { get; private set; }
    public string CarrierBgPath { get; private set; } = "";
    public string LastOriginalBgPath { get; private set; } = "";
    public string LastReplacementPath { get; private set; } = "";
    public string LastResourceProbePath { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public DateTime? StartedAt { get; private set; }
    public DateTime? LastLogTime { get; private set; }
    public DateTime? CarrierEnterRequestedAt { get; private set; }
    public DateTime? CarrierObservedAt { get; private set; }
    public uint TerritoryBefore { get; private set; }
    public uint LastObservedTerritory { get; private set; }
    public int OverrideHits { get; private set; }
    public int ProbeLogCount { get; private set; }
    public bool HasCarrier => GetCarrierTerritoryId() != 0;
    public bool LoadPrefetchLayoutHookReady => P.Memory?.LoadPrefetchLayoutHook?.IsEnabled == true;
    public bool ResourceManagerSyncHookReady => P.Memory?.ResourceManagerGetResourceSyncHook?.IsEnabled == true;
    public bool ResourceManagerAsyncHookReady => P.Memory?.ResourceManagerGetResourceAsyncHook?.IsEnabled == true;
    public bool IsHookReady => LoadPrefetchLayoutHookReady || ResourceManagerSyncHookReady || ResourceManagerAsyncHookReady;
    public bool IsBusy => State is DirectBgPathState.Requested or DirectBgPathState.EnteringCarrier or DirectBgPathState.WaitingForOverrideHook or DirectBgPathState.OverrideHit;
    public bool HasPendingOverride => targetPathPtr != 0 && IsBusy;

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

    public bool ButtonClicked(TerritoryBrowserEntry entry)
    {
        lock (stateLock)
        {
            if (IsBusy)
            {
                LastError = "DirectBgPath attempt already pending; cancel or wait before trying again";
                TouchLog();
                PluginLog.Warning($"{LogPrefix()} Button click ignored: state = {State}; target = {TargetPath}; carrier = {CarrierTerritoryId}; hits = {OverrideHits}");
                return false;
            }

            AttemptId = Interlocked.Increment(ref nextAttemptId);
            State = DirectBgPathState.ButtonClicked;
            TargetPath = entry.Bg ?? "";
            NormalizedTargetPath = TargetPath.IsNullOrEmpty() ? "" : BgPathResolver.NormalizePath(TargetPath);
            CarrierTerritoryId = GetCarrierTerritoryId();
            LastError = "";
            TouchLog();
            PluginLog.Information($"{LogPrefix()} Button clicked: target = {TargetPath}; carrier = {CarrierTerritoryId}; hookReady = {IsHookReady}; canUse = {Utils.CanUse()}");
            return true;
        }
    }

    public bool TryEnter(TerritoryBrowserEntry entry, bool setPosition, bool setPhase, int a3, int a4, int a5, int a6, int cfcOverride)
    {
        if (entry.Bg.IsNullOrEmpty())
        {
            Fail("entry has no bg path");
            return false;
        }

        if (IsBusy)
        {
            Fail("DirectBgPath attempt already pending; cancel it before starting a new attempt");
            return false;
        }

        EnsureAttempt(entry);

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
            Fail("DirectBgPath hooks are not ready");
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
            PluginLog.Information($"{LogPrefix()} Calling carrier enter logic: carrier = {carrier}; carrierBg = {CarrierBgPath}; target = {TargetPath}");
            Utils.LoadZone(carrier, setPosition, setPhase, a3, a4, a5, a6, cfcOverride);
            TouchLog();
            PluginLog.Information($"{LogPrefix()} Carrier enter logic returned: carrier = {carrier}; overrideHits = {OverrideHits}");
        }
        catch (Exception e)
        {
            Fail($"carrier zone load failed: {e.GetType().Name}: {e.Message}");
            return false;
        }

        lock (stateLock)
        {
            State = DirectBgPathState.WaitingForOverrideHook;
            TouchLog();
            PluginLog.Information($"{LogPrefix()} Carrier zone requested; waiting for ResourceManager/LoadPrefetchLayout override hit. target = {TargetPath}; carrierTerritory = {CarrierTerritoryId}; carrierBg = {CarrierBgPath}");
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
            var before = Svc.ClientState.TerritoryType;
            PluginLog.Information($"[DirectBgPath] Calling normal carrier test enter logic: carrier = {carrier}; carrierBg = {carrierBg}; territoryBefore = {before}");
            Utils.LoadZone(carrier, setPosition, setPhase, a3, a4, a5, a6, cfcOverride);
            LastError = "";
            TouchLog();
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
            if (State is not (DirectBgPathState.WaitingForOverrideHook or DirectBgPathState.OverrideHit) || StartedAt == null) return;
            var elapsed = DateTime.Now - StartedAt.Value;

            if (!carrierChangeWarningLogged && CarrierObservedAt == null && CarrierEnterRequestedAt != null && DateTime.Now - CarrierEnterRequestedAt.Value >= CarrierChangeWarningTimeout)
            {
                carrierChangeWarningLogged = true;
                LastError = $"carrier territory did not change to {CarrierTerritoryId} within 5 seconds";
                TouchLog();
                PluginLog.Warning($"{LogPrefix()} Carrier territory did not change within timeout: before = {TerritoryBefore}; requested = {CarrierTerritoryId}; current = {Svc.ClientState.TerritoryType}");
            }

            if (elapsed < PendingTimeout) return;

            if (OverrideHits > 0)
            {
                Complete("override window completed");
                return;
            }

            State = DirectBgPathState.TimedOut;
            LastError = CarrierObservedAt == null
                ? "carrier territory did not change before DirectBgPath timeout"
                : "carrier loaded but bg override hook did not fire";
            TouchLog();

            if (CarrierObservedAt == null)
            {
                PluginLog.Warning($"{LogPrefix()} Timed out waiting for bg path hook: reason = {LastError}; before = {TerritoryBefore}; requested = {CarrierTerritoryId}; current = {Svc.ClientState.TerritoryType}; target = {TargetPath}; carrierBg = {CarrierBgPath}");
            }
            else
            {
                PluginLog.Warning($"{LogPrefix()} Carrier loaded but bg override hook did not fire: target = {TargetPath}; carrierTerritory = {CarrierTerritoryId}; carrierBg = {CarrierBgPath}");
            }

            FreeTargetPath();
            FreeReplacementPaths();
        }
    }

    public void OnTerritoryChanged(uint newTerritory)
    {
        lock (stateLock)
        {
            var oldTerritory = LastObservedTerritory != 0 ? LastObservedTerritory : TerritoryBefore;
            LastObservedTerritory = newTerritory;
            if (!IsBusy) return;

            TouchLog();
            PluginLog.Information($"{LogPrefix()} Territory changed: old = {oldTerritory}; new = {newTerritory}; requestedCarrier = {CarrierTerritoryId}");
            if (newTerritory == CarrierTerritoryId)
            {
                CarrierObservedAt = DateTime.Now;
                LastError = "";
            }
        }
    }

    void Prepare(string targetPath, string sourceName, uint carrierTerritoryId, string carrierBgPath)
    {
        lock (stateLock)
        {
            FreeTargetPath();
            FreeReplacementPaths();
            TargetPath = NormalizeForLoad(targetPath);
            NormalizedTargetPath = BgPathResolver.NormalizePath(TargetPath);
            SourceName = sourceName;
            CarrierTerritoryId = carrierTerritoryId;
            CarrierBgPath = NormalizeForLoad(carrierBgPath);
            LastOriginalBgPath = "";
            LastReplacementPath = "";
            LastResourceProbePath = "";
            LastError = "";
            StartedAt = DateTime.Now;
            CarrierEnterRequestedAt = StartedAt;
            CarrierObservedAt = null;
            TerritoryBefore = Svc.ClientState.TerritoryType;
            LastObservedTerritory = TerritoryBefore;
            if (TerritoryBefore == CarrierTerritoryId) CarrierObservedAt = StartedAt;
            OverrideHits = 0;
            ProbeLogCount = 0;
            probeLogCount = 0;
            State = DirectBgPathState.Requested;
            requestLogged = false;
            hitLogged = false;
            completionLogged = false;
            hookCalledLogged = false;
            hookMismatchLogged = false;
            carrierChangeWarningLogged = false;
            probeLimitLogged = false;
            overrideLimitLogged = false;
            targetPathPtr = AllocUtf8(TargetPath);
            LogRequestOnce();
            PluginLog.Information($"{LogPrefix()} Territory before = {TerritoryBefore}");
            PluginLog.Information($"{LogPrefix()} Carrier requested = {CarrierTerritoryId}");
        }
    }

    public bool TryGetOverridePath(string originalPath, out nint overridePathPtr)
    {
        lock (stateLock)
        {
            overridePathPtr = 0;
            if (!HasPendingOverride) return false;
            LogHookCalledOnce(originalPath);

            var normalizedOriginal = BgPathResolver.NormalizePath(originalPath);
            var normalizedCarrier = BgPathResolver.NormalizePath(CarrierBgPath);
            if (!normalizedOriginal.Equals(normalizedCarrier, StringComparison.OrdinalIgnoreCase))
            {
                LogHookMismatchOnce(originalPath, normalizedOriginal, normalizedCarrier);
                return false;
            }

            OverrideHits++;
            LastOriginalBgPath = originalPath;
            LastReplacementPath = TargetPath;
            State = DirectBgPathState.OverrideHit;
            LastError = "";
            overridePathPtr = targetPathPtr;
            LogOverrideHit("LoadPrefetchLayout", originalPath, TargetPath);
            return true;
        }
    }

    public bool TryGetResourceOverridePath(string originalPath, string hookName, out nint overridePathPtr)
    {
        lock (stateLock)
        {
            overridePathPtr = 0;
            if (!HasPendingOverride || originalPath.IsNullOrEmpty()) return false;
            LogProbeIfNeeded(originalPath, hookName);

            if (!TryBuildResourceReplacementPath(originalPath, out var replacementPath))
            {
                return false;
            }

            overridePathPtr = GetOrAllocateReplacementPath(replacementPath);
            OverrideHits++;
            LastOriginalBgPath = originalPath;
            LastReplacementPath = replacementPath;
            LastError = "";
            State = DirectBgPathState.OverrideHit;
            LogOverrideHit(hookName, originalPath, replacementPath);
            return true;
        }
    }

    public void Clear(string reason = "manual clear")
    {
        lock (stateLock)
        {
            if (State is DirectBgPathState.Requested or DirectBgPathState.EnteringCarrier or DirectBgPathState.WaitingForOverrideHook or DirectBgPathState.OverrideHit or DirectBgPathState.Completed or DirectBgPathState.TimedOut)
            {
                PluginLog.Information($"{LogPrefix()} Cleared: reason = {reason}; target = {TargetPath}; hits = {OverrideHits}");
            }
            FreeTargetPath();
            FreeReplacementPaths();
            State = DirectBgPathState.Idle;
            AttemptId = 0;
            TargetPath = "";
            NormalizedTargetPath = "";
            SourceName = "";
            CarrierTerritoryId = 0;
            CarrierBgPath = "";
            LastOriginalBgPath = "";
            LastReplacementPath = "";
            LastResourceProbePath = "";
            LastError = "";
            StartedAt = null;
            CarrierEnterRequestedAt = null;
            CarrierObservedAt = null;
            TerritoryBefore = 0;
            LastObservedTerritory = 0;
            OverrideHits = 0;
            ProbeLogCount = 0;
            probeLogCount = 0;
            requestLogged = false;
            hitLogged = false;
            completionLogged = false;
            hookCalledLogged = false;
            hookMismatchLogged = false;
            carrierChangeWarningLogged = false;
            probeLimitLogged = false;
            overrideLimitLogged = false;
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
            PluginLog.Warning($"{LogPrefix()} Failed: reason = {reason}");
            FreeTargetPath();
            FreeReplacementPaths();
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
        var target = entry?.Bg ?? TargetPath;
        var snapshot = S.TerritoryDiscovery.GetSnapshot();
        TouchLog();
        PluginLog.Information(
            "[DirectBgPath Diagnostic]\n" +
            $"serviceInitialized = true\n" +
            $"hookInstalled = {IsHookReady}\n" +
            $"loadPrefetchLayoutHookInstalled = {LoadPrefetchLayoutHookReady}\n" +
            $"resourceManagerGetResourceSyncHookInstalled = {ResourceManagerSyncHookReady}\n" +
            $"resourceManagerGetResourceAsyncHookInstalled = {ResourceManagerAsyncHookReady}\n" +
            $"hookName = ResourceManager.GetResourceSync/GetResourceAsync + LoadPrefetchLayout\n" +
            $"resourceProbeEnabled = {C.DirectBgPathResourceProbe}\n" +
            $"carrierTerritory = {carrier}\n" +
            $"carrierExists = {carrierExists}\n" +
            $"carrierBg = {carrierBg}\n" +
            $"carrierError = {carrierError}\n" +
            $"targetBg = {target}\n" +
            $"normalizedTarget = {(target.IsNullOrEmpty() ? NormalizedTargetPath : BgPathResolver.NormalizePath(target))}\n" +
            $"pending = {HasPendingOverride}\n" +
            $"attemptId = {AttemptId}\n" +
            $"state = {State}\n" +
            $"territoryBefore = {TerritoryBefore}\n" +
            $"lastObservedTerritory = {LastObservedTerritory}\n" +
            $"overrideHits = {OverrideHits}\n" +
            $"lastOriginalPath = {LastOriginalBgPath}\n" +
            $"lastReplacementPath = {LastReplacementPath}\n" +
            $"lastProbePath = {LastResourceProbePath}\n" +
            $"lastError = {(LastError.IsNullOrEmpty() ? "none" : LastError)}\n" +
            $"canCallNormalEnter = {Utils.CanUse()}\n" +
            $"includeCutsceneCacheReady = {snapshot?.IncludeCutsceneTerritories == true}\n" +
            $"cacheEntries = {snapshot?.Entries.Count ?? 0}\n" +
            $"testPath1 = ffxiv/manfst/manfst00510/manfst00510; normalized = {BgPathResolver.NormalizePath("ffxiv/manfst/manfst00510/manfst00510")}; canStartDirect = {carrierExists && IsHookReady}\n" +
            $"testPath2 = ex3/luckyw/luckyw00210/luckyw00210; normalized = {BgPathResolver.NormalizePath("ex3/luckyw/luckyw00210/luckyw00210")}; canStartDirect = {carrierExists && IsHookReady}");
    }

    void EnsureAttempt(TerritoryBrowserEntry entry)
    {
        lock (stateLock)
        {
            if (AttemptId != 0 && State == DirectBgPathState.ButtonClicked) return;
            AttemptId = Interlocked.Increment(ref nextAttemptId);
            State = DirectBgPathState.ButtonClicked;
            TargetPath = entry.Bg ?? "";
            NormalizedTargetPath = TargetPath.IsNullOrEmpty() ? "" : BgPathResolver.NormalizePath(TargetPath);
            CarrierTerritoryId = GetCarrierTerritoryId();
            LastError = "";
            TouchLog();
            PluginLog.Information($"{LogPrefix()} Button clicked: target = {TargetPath}; carrier = {CarrierTerritoryId}; hookReady = {IsHookReady}; canUse = {Utils.CanUse()}");
        }
    }

    void LogRequestOnce()
    {
        if (requestLogged) return;
        requestLogged = true;
        TouchLog();
        PluginLog.Information($"{LogPrefix()} Requested: target = {TargetPath}; carrierTerritory = {CarrierTerritoryId}; normalizedTarget = {NormalizedTargetPath}");
    }

    void LogCompletionOnce(string reason)
    {
        if (completionLogged) return;
        completionLogged = true;
        TouchLog();
        PluginLog.Information($"{LogPrefix()} Completed: reason = {reason}; target = {TargetPath}; carrierTerritory = {CarrierTerritoryId}; hits = {OverrideHits}; lastOriginal = {LastOriginalBgPath}; lastReplacement = {LastReplacementPath}");
    }

    void Complete(string reason)
    {
        State = DirectBgPathState.Completed;
        LogCompletionOnce(reason);
        FreeTargetPath();
        FreeReplacementPaths();
    }

    void SetState(DirectBgPathState state)
    {
        lock (stateLock)
        {
            State = state;
            TouchLog();
        }
    }

    void LogHookCalledOnce(string originalPath)
    {
        if (hookCalledLogged) return;
        hookCalledLogged = true;
        TouchLog();
        PluginLog.Information($"{LogPrefix()} Hook called while pending: originalBg = {originalPath}; expectedCarrierBg = {CarrierBgPath}; targetBg = {TargetPath}");
    }

    void LogHookMismatchOnce(string originalPath, string normalizedOriginal, string normalizedCarrier)
    {
        if (hookMismatchLogged) return;
        hookMismatchLogged = true;
        TouchLog();
        PluginLog.Information($"{LogPrefix()} Hook called but carrier bg did not match: carrierBg = {originalPath}; expectedCarrier = {CarrierBgPath}; normalizedCarrierBg = {normalizedOriginal}; normalizedExpectedCarrier = {normalizedCarrier}");
    }

    void LogOverrideHit(string hookName, string originalPath, string replacementPath)
    {
        if (!hitLogged)
        {
            hitLogged = true;
        }

        TouchLog();
        if (OverrideHits <= 20)
        {
            PluginLog.Information($"{LogPrefix()} Resource override hit: original = {originalPath}; replacement = {replacementPath}; type = {hookName}; overrideHits = {OverrideHits}");
        }
        else if (!overrideLimitLogged)
        {
            overrideLimitLogged = true;
            PluginLog.Information($"{LogPrefix()} Resource override hit log limit reached; further override hits will update counters only.");
        }
    }

    void LogProbeIfNeeded(string originalPath, string hookName)
    {
        if (!C.DirectBgPathResourceProbe) return;
        if (!LooksInterestingForProbe(originalPath)) return;
        LastResourceProbePath = originalPath;
        if (probeLogCount < 50)
        {
            probeLogCount++;
            ProbeLogCount = probeLogCount;
            TouchLog();
            var canOverride = TryBuildResourceReplacementPath(originalPath, out var replacementPath);
            PluginLog.Information($"{LogPrefix()}[Probe] Resource requested: path = {originalPath}; type = {hookName}; canOverride = {canOverride}; replacement = {replacementPath}");
            return;
        }

        ProbeLogCount = probeLogCount;
        if (!probeLimitLogged)
        {
            probeLimitLogged = true;
            TouchLog();
            PluginLog.Information($"{LogPrefix()}[Probe] Resource probe log limit reached; further matching requests will update lastProbePath only.");
        }
    }

    bool TryBuildResourceReplacementPath(string originalPath, out string replacementPath)
    {
        replacementPath = "";
        if (TargetPath.IsNullOrEmpty() || CarrierBgPath.IsNullOrEmpty()) return false;

        var originalBasic = BgPathResolver.NormalizeBasicPath(originalPath);
        var originalBase = StripBgPrefixAndExtension(originalBasic, out var hadBgPrefix, out var extension);
        var carrierBase = NormalizeForLoad(CarrierBgPath);
        if (!IsLayoutResourceCandidate(originalBasic, extension)) return false;
        if (!TryGetCarrierRelativeSuffix(originalBase, carrierBase, out var suffix)) return false;

        replacementPath = $"{(hadBgPrefix ? "bg/" : "")}{TargetPath}{suffix}{extension}";
        return true;
    }

    bool LooksInterestingForProbe(string originalPath)
    {
        var originalBasic = BgPathResolver.NormalizeBasicPath(originalPath);
        var normalized = BgPathResolver.NormalizePath(originalPath);
        var carrier = BgPathResolver.NormalizePath(CarrierBgPath);
        var target = BgPathResolver.NormalizePath(TargetPath);
        if (!carrier.IsNullOrEmpty() && (normalized.Contains(carrier, StringComparison.OrdinalIgnoreCase) || carrier.Contains(normalized, StringComparison.OrdinalIgnoreCase))) return true;
        if (!target.IsNullOrEmpty() && normalized.Contains(target, StringComparison.OrdinalIgnoreCase)) return true;
        return ProbeKeywords.Any(x => originalBasic.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    static bool IsLayoutResourceCandidate(string originalBasic, string extension)
    {
        if (LayoutExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return true;
        return extension.IsNullOrEmpty() && originalBasic.Contains("/level/", StringComparison.OrdinalIgnoreCase);
    }

    static bool TryGetCarrierRelativeSuffix(string originalBase, string carrierBase, out string suffix)
    {
        suffix = "";
        if (originalBase.IsNullOrEmpty() || carrierBase.IsNullOrEmpty()) return false;
        if (originalBase.Equals(carrierBase, StringComparison.OrdinalIgnoreCase)) return true;
        if (originalBase.StartsWith(carrierBase, StringComparison.OrdinalIgnoreCase))
        {
            suffix = originalBase[carrierBase.Length..];
            return true;
        }
        if (originalBase.Contains(carrierBase, StringComparison.OrdinalIgnoreCase)) return true;
        return carrierBase.Contains(originalBase, StringComparison.OrdinalIgnoreCase);
    }

    static string StripBgPrefixAndExtension(string path, out bool hadBgPrefix, out string extension)
    {
        var ret = path;
        hadBgPrefix = ret.StartsWith("bg/", StringComparison.OrdinalIgnoreCase);
        if (hadBgPrefix) ret = ret[3..];
        extension = "";
        foreach (var candidate in LayoutExtensions)
        {
            if (!ret.EndsWith(candidate, StringComparison.OrdinalIgnoreCase)) continue;
            extension = candidate;
            ret = ret[..^candidate.Length];
            break;
        }
        return ret.Trim('/');
    }

    nint GetOrAllocateReplacementPath(string replacementPath)
    {
        if (replacementPathPtrs.TryGetValue(replacementPath, out var ptr)) return ptr;
        ptr = AllocUtf8(replacementPath);
        replacementPathPtrs[replacementPath] = ptr;
        return ptr;
    }

    string LogPrefix() => AttemptId == 0 ? "[DirectBgPath]" : $"[DirectBgPath#{AttemptId}]";

    void TouchLog() => LastLogTime = DateTime.Now;

    static string NormalizeForLoad(string path)
    {
        var ret = BgPathResolver.NormalizeBasicPath(path);
        if (ret.StartsWith("bg/", StringComparison.Ordinal)) ret = ret[3..];
        foreach (var extension in new[] { ".lvb", ".lgb", ".sgb", ".pcb" })
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

    void FreeReplacementPaths()
    {
        foreach (var ptr in replacementPathPtrs.Values)
        {
            if (ptr != 0) Marshal.FreeHGlobal(ptr);
        }
        replacementPathPtrs.Clear();
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
