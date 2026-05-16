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
    static readonly TimeSpan AfterClearWatchWindow = TimeSpan.FromSeconds(10);
    static readonly string[] MainLayoutExtensions = [".lvb", ".lgb", ".sgb", ".svb", ".lcb", ".pcb"];
    static readonly string[] ProbeKeywords = ["/level/", ".lvb", ".lgb", ".sgb", ".svb", ".lcb", "bg/", "manfst00510", "s1ti"];
    static readonly string[] LevelLgbFiles = ["bg.lgb", "vfx.lgb", "planmap.lgb", "planevent.lgb", "sound.lgb"];

    readonly object stateLock = new();
    readonly Dictionary<string, nint> replacementPathPtrs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, bool> targetResourceExists = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> uniqueProbedResources = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> loggedProbePaths = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> loggedOverridePaths = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> loggedLateOverridePaths = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> loggedMissingTargets = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> loggedAfterClearPaths = new(StringComparer.OrdinalIgnoreCase);
    nint targetPathPtr;
    int nextAttemptId;
    bool requestLogged;
    bool activeLogged;
    bool completionLogged;
    bool hookCalledLogged;
    bool hookMismatchLogged;
    bool carrierChangeWarningLogged;
    bool probeLimitLogged;
    bool overrideLimitLogged;
    bool afterClearLimitLogged;
    int probeLogCount;
    int overrideLogCount;
    int afterClearLogCount;
    DateTime? clearedAt;
    string clearedCarrierBgPath = "";
    string clearedTargetPath = "";

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
    public string LastSkipReason { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public string TargetResourceProbeSummary { get; private set; } = "";
    public DateTime? StartedAt { get; private set; }
    public DateTime? LastLogTime { get; private set; }
    public DateTime? CarrierEnterRequestedAt { get; private set; }
    public DateTime? CarrierObservedAt { get; private set; }
    public uint TerritoryBefore { get; private set; }
    public uint LastObservedTerritory { get; private set; }
    public int OverrideHits { get; private set; }
    public int ProbedResources { get; private set; }
    public int UniqueProbedResources => uniqueProbedResources.Count;
    public int SkippedBecauseTargetMissing { get; private set; }
    public int SkippedBecauseNotLayout { get; private set; }
    public int SkippedBecauseAfterClear { get; private set; }
    public bool HasCarrier => GetCarrierTerritoryId() != 0;
    public bool LoadPrefetchLayoutHookReady => P.Memory?.LoadPrefetchLayoutHook?.IsEnabled == true;
    public bool ResourceManagerSyncHookReady => P.Memory?.ResourceManagerGetResourceSyncHook?.IsEnabled == true;
    public bool ResourceManagerAsyncHookReady => P.Memory?.ResourceManagerGetResourceAsyncHook?.IsEnabled == true;
    public bool IsHookReady => LoadPrefetchLayoutHookReady || ResourceManagerSyncHookReady || ResourceManagerAsyncHookReady;
    public bool IsBusy => State is DirectBgPathState.Requested or DirectBgPathState.EnteringCarrier or DirectBgPathState.WaitingForResourceOverride or DirectBgPathState.ActiveOverride;
    public bool HasPendingOverride => targetPathPtr != 0 && IsBusy;
    public bool ShouldObserveAfterClear => clearedAt != null && DateTime.Now - clearedAt.Value <= AfterClearWatchWindow && !clearedCarrierBgPath.IsNullOrEmpty();

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
                LastError = "DirectBgPath attempt already active; cancel or clear it before trying again";
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
            Fail("DirectBgPath attempt already active; clear it before starting a new attempt");
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
            State = DirectBgPathState.WaitingForResourceOverride;
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
            if (State is not (DirectBgPathState.WaitingForResourceOverride or DirectBgPathState.ActiveOverride) || StartedAt == null) return;
            var elapsed = DateTime.Now - StartedAt.Value;

            if (!carrierChangeWarningLogged && CarrierObservedAt == null && CarrierEnterRequestedAt != null && DateTime.Now - CarrierEnterRequestedAt.Value >= CarrierChangeWarningTimeout)
            {
                carrierChangeWarningLogged = true;
                LastError = $"carrier territory did not change to {CarrierTerritoryId} within 5 seconds";
                TouchLog();
                PluginLog.Warning($"{LogPrefix()} Carrier territory did not change within timeout: before = {TerritoryBefore}; requested = {CarrierTerritoryId}; current = {Svc.ClientState.TerritoryType}");
            }

            if (State == DirectBgPathState.ActiveOverride)
            {
                if (C.DirectBgPathHoldUntilManualClear) return;
                if (elapsed < PendingTimeout) return;
                State = DirectBgPathState.Stable;
                LogCompletionOnce("timed active override window completed");
                FreeTargetPath();
                FreeReplacementPaths();
                return;
            }

            if (elapsed < PendingTimeout) return;

            State = DirectBgPathState.TimedOut;
            LastError = CarrierObservedAt == null
                ? "carrier territory did not change before DirectBgPath timeout"
                : "carrier loaded but no bg resource override hit";
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
                return;
            }

            if (CarrierObservedAt != null && State == DirectBgPathState.ActiveOverride)
            {
                ClearLocked($"territory changed away from carrier: old={oldTerritory}; new={newTerritory}");
            }
        }
    }

    void Prepare(string targetPath, string sourceName, uint carrierTerritoryId, string carrierBgPath)
    {
        lock (stateLock)
        {
            FreeTargetPath();
            FreeReplacementPaths();
            ResetAttemptCounters();
            clearedAt = null;
            clearedCarrierBgPath = "";
            clearedTargetPath = "";
            TargetPath = NormalizeForLoad(targetPath);
            NormalizedTargetPath = BgPathResolver.NormalizePath(TargetPath);
            SourceName = sourceName;
            CarrierTerritoryId = carrierTerritoryId;
            CarrierBgPath = NormalizeForLoad(carrierBgPath);
            LastOriginalBgPath = "";
            LastReplacementPath = "";
            LastResourceProbePath = "";
            LastSkipReason = "";
            LastError = "";
            StartedAt = DateTime.Now;
            CarrierEnterRequestedAt = StartedAt;
            CarrierObservedAt = null;
            TerritoryBefore = Svc.ClientState.TerritoryType;
            LastObservedTerritory = TerritoryBefore;
            if (TerritoryBefore == CarrierTerritoryId) CarrierObservedAt = StartedAt;
            State = DirectBgPathState.Requested;
            requestLogged = false;
            activeLogged = false;
            completionLogged = false;
            hookCalledLogged = false;
            hookMismatchLogged = false;
            carrierChangeWarningLogged = false;
            probeLimitLogged = false;
            overrideLimitLogged = false;
            afterClearLimitLogged = false;
            targetPathPtr = AllocUtf8(TargetPath);
            ProbeTargetResourcesLocked();
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

            overridePathPtr = targetPathPtr;
            RecordOverrideHit("LoadPrefetchLayout", originalPath, TargetPath);
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

            if (!TryBuildResourceReplacementPath(originalPath, out var replacementPath, out var skipReason))
            {
                CountSkip(skipReason, originalPath);
                return false;
            }

            overridePathPtr = GetOrAllocateReplacementPath(replacementPath);
            RecordOverrideHit(hookName, originalPath, replacementPath);
            return true;
        }
    }

    public void ObserveResourceAfterClear(string originalPath, string hookName)
    {
        lock (stateLock)
        {
            if (!ShouldObserveAfterClear || originalPath.IsNullOrEmpty()) return;
            if (!IsClearedCarrierResource(originalPath)) return;
            SkippedBecauseAfterClear++;
            LastSkipReason = "after clear";
            if (!loggedAfterClearPaths.Add(BgPathResolver.NormalizeBasicPath(originalPath))) return;
            if (afterClearLogCount++ < 10)
            {
                TouchLog();
                PluginLog.Warning($"{LogPrefix()} Carrier resource requested after clear: path = {originalPath}; type = {hookName}");
                PluginLog.Warning($"{LogPrefix()} WARNING: carrier resource requested after override completed; this may cause visual revert");
            }
            else if (!afterClearLimitLogged)
            {
                afterClearLimitLogged = true;
                PluginLog.Warning($"{LogPrefix()} Carrier resource after-clear log limit reached.");
            }
        }
    }

    public void Clear(string reason = "manual clear")
    {
        lock (stateLock)
        {
            ClearLocked(reason);
        }
    }

    void ClearLocked(string reason)
    {
        if (State is DirectBgPathState.Requested or DirectBgPathState.EnteringCarrier or DirectBgPathState.WaitingForResourceOverride or DirectBgPathState.ActiveOverride or DirectBgPathState.Stable or DirectBgPathState.TimedOut)
        {
            PluginLog.Information($"{LogPrefix()} DirectBgPath cleared: reason = {reason}; target = {TargetPath}; hits = {OverrideHits}; lastOriginal = {LastOriginalBgPath}; lastReplacement = {LastReplacementPath}");
        }

        clearedAt = DateTime.Now;
        clearedCarrierBgPath = CarrierBgPath;
        clearedTargetPath = TargetPath;
        FreeTargetPath();
        FreeReplacementPaths();
        State = reason.Contains("user", StringComparison.OrdinalIgnoreCase) || reason.Contains("manual", StringComparison.OrdinalIgnoreCase)
            ? DirectBgPathState.UserCancelled
            : DirectBgPathState.Cleared;
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
        requestLogged = false;
        activeLogged = false;
        completionLogged = false;
        hookCalledLogged = false;
        hookMismatchLogged = false;
        carrierChangeWarningLogged = false;
        TouchLog();
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
            $"holdUntilManualClear = {C.DirectBgPathHoldUntilManualClear}\n" +
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
            $"probedResources = {ProbedResources}\n" +
            $"uniqueProbedResources = {UniqueProbedResources}\n" +
            $"skippedBecauseTargetMissing = {SkippedBecauseTargetMissing}\n" +
            $"skippedBecauseNotLayout = {SkippedBecauseNotLayout}\n" +
            $"skippedBecauseAfterClear = {SkippedBecauseAfterClear}\n" +
            $"targetResourceProbe = {TargetResourceProbeSummary}\n" +
            $"lastOriginalPath = {LastOriginalBgPath}\n" +
            $"lastReplacementPath = {LastReplacementPath}\n" +
            $"lastProbePath = {LastResourceProbePath}\n" +
            $"lastSkipReason = {LastSkipReason}\n" +
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

    void RecordOverrideHit(string hookName, string originalPath, string replacementPath)
    {
        OverrideHits++;
        LastOriginalBgPath = originalPath;
        LastReplacementPath = replacementPath;
        LastError = "";
        StartActiveOverrideIfNeeded();
        LogOverrideHit(hookName, originalPath, replacementPath);
        LogLateOverrideIfNeeded(originalPath);
    }

    void StartActiveOverrideIfNeeded()
    {
        if (State == DirectBgPathState.ActiveOverride) return;
        State = DirectBgPathState.ActiveOverride;
        if (activeLogged) return;
        activeLogged = true;
        TouchLog();
        PluginLog.Information($"{LogPrefix()} Active override session started");
        if (C.DirectBgPathHoldUntilManualClear)
        {
            PluginLog.Information($"{LogPrefix()} Keeping override active until manual clear");
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
        var key = $"{BgPathResolver.NormalizeBasicPath(originalPath)}->{BgPathResolver.NormalizeBasicPath(replacementPath)}";
        if (loggedOverridePaths.Add(key) && overrideLogCount < 30)
        {
            overrideLogCount++;
            TouchLog();
            PluginLog.Information($"{LogPrefix()} Resource override hit: original = {originalPath}; replacement = {replacementPath}; type = {hookName}; overrideHits = {OverrideHits}");
            return;
        }

        if (!overrideLimitLogged && overrideLogCount >= 30)
        {
            overrideLimitLogged = true;
            PluginLog.Information($"{LogPrefix()} Resource override hit log limit reached; further override hits will update counters only.");
        }
    }

    void LogLateOverrideIfNeeded(string originalPath)
    {
        if (State != DirectBgPathState.ActiveOverride || OverrideHits <= 4) return;
        var key = BgPathResolver.NormalizeBasicPath(originalPath);
        if (!loggedLateOverridePaths.Add(key) || loggedLateOverridePaths.Count > 10) return;
        TouchLog();
        PluginLog.Information($"{LogPrefix()} Late carrier resource request overridden: path = {originalPath}; hits = {OverrideHits}");
    }

    void LogProbeIfNeeded(string originalPath, string hookName)
    {
        if (!C.DirectBgPathResourceProbe) return;
        if (!LooksInterestingForProbe(originalPath)) return;
        ProbedResources++;
        var normalized = BgPathResolver.NormalizeBasicPath(originalPath);
        uniqueProbedResources.Add(normalized);
        LastResourceProbePath = originalPath;
        if (loggedProbePaths.Add(normalized) && probeLogCount < 100)
        {
            probeLogCount++;
            TouchLog();
            var canOverride = TryBuildResourceReplacementPath(originalPath, out var replacementPath, out var skipReason);
            PluginLog.Information($"{LogPrefix()}[Probe] Resource requested: path = {originalPath}; type = {hookName}; canOverride = {canOverride}; replacement = {replacementPath}; skipReason = {skipReason}");
            return;
        }

        if (!probeLimitLogged && probeLogCount >= 100)
        {
            probeLimitLogged = true;
            TouchLog();
            PluginLog.Information($"{LogPrefix()}[Probe] Resource probe log limit reached; further matching requests will update counters only.");
        }
    }

    bool TryBuildResourceReplacementPath(string originalPath, out string replacementPath, out DirectBgPathSkipReason skipReason)
    {
        replacementPath = "";
        skipReason = DirectBgPathSkipReason.None;
        if (TargetPath.IsNullOrEmpty() || CarrierBgPath.IsNullOrEmpty())
        {
            skipReason = DirectBgPathSkipReason.NotCarrierFamily;
            return false;
        }

        var originalBasic = BgPathResolver.NormalizeBasicPath(originalPath);
        var originalBase = StripBgPrefixAndExtension(originalBasic, out var hadBgPrefix, out var extension);
        var carrierBase = NormalizeForLoad(CarrierBgPath);
        var carrierRoot = GetSceneRoot(carrierBase);
        var carrierLevelRoot = $"{carrierRoot}/level";

        if (!IsCarrierFamilyPath(originalBase, carrierBase, carrierRoot, carrierLevelRoot))
        {
            skipReason = DirectBgPathSkipReason.NotCarrierFamily;
            return false;
        }

        if (!IsMainLayoutResource(originalBase, extension, carrierBase, carrierLevelRoot))
        {
            skipReason = DirectBgPathSkipReason.NotLayout;
            return false;
        }

        var candidates = BuildReplacementCandidates(originalBase, extension, carrierBase, carrierLevelRoot);
        foreach (var candidate in candidates)
        {
            if (!TargetResourceExists(candidate)) continue;
            replacementPath = $"{(hadBgPrefix ? "bg/" : "")}{candidate}";
            return true;
        }

        skipReason = DirectBgPathSkipReason.TargetMissing;
        LogMissingTargetOnce(originalPath, candidates);
        return false;
    }

    IEnumerable<string> BuildReplacementCandidates(string originalBase, string extension, string carrierBase, string carrierLevelRoot)
    {
        var targetRoot = GetSceneRoot(TargetPath);
        var targetName = GetLastSegment(TargetPath);
        if (originalBase.Equals(carrierBase, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in BuildBaseReplacementCandidates(extension, targetRoot, targetName))
            {
                yield return candidate;
            }
            yield break;
        }

        if (originalBase.StartsWith($"{carrierLevelRoot}/", StringComparison.OrdinalIgnoreCase) && extension.Equals(".lgb", StringComparison.OrdinalIgnoreCase))
        {
            var levelFileName = $"{GetLastSegment(originalBase)}{extension}";
            foreach (var candidate in BuildLevelLgbReplacementCandidates(levelFileName, targetRoot))
            {
                yield return candidate;
            }
        }
    }

    IEnumerable<string> BuildBaseReplacementCandidates(string extension, string targetRoot, string targetName)
    {
        if (extension.Equals(".lvb", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{TargetPath}.lvb";
            yield return $"{TargetPath}.sgb";
            yield return $"{targetRoot}/level/{targetName}.lvb";
            yield return $"{targetRoot}/level/bg.lgb";
            yield break;
        }

        if (extension.Equals(".sgb", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{TargetPath}.sgb";
            yield return $"{TargetPath}.lvb";
            yield return $"{targetRoot}/level/{targetName}.sgb";
            yield return $"{targetRoot}/level/bg.lgb";
            yield break;
        }

        if (extension.Equals(".lgb", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{TargetPath}.lgb";
            yield return $"{targetRoot}/level/bg.lgb";
            yield return $"{targetRoot}/bg.lgb";
            yield return $"{TargetPath}.sgb";
            yield break;
        }

        yield return $"{TargetPath}{extension}";
        yield return $"{targetRoot}/level/{targetName}{extension}";
    }

    IEnumerable<string> BuildLevelLgbReplacementCandidates(string levelFileName, string targetRoot)
    {
        yield return $"{targetRoot}/level/{levelFileName}";
        yield return $"{targetRoot}/{levelFileName}";
        if (levelFileName.Equals("bg.lgb", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"{TargetPath}.lgb";
            yield return $"{TargetPath}.sgb";
        }
    }

    bool TargetResourceExists(string noBgPath)
    {
        var fullPath = WithBgPrefix(noBgPath);
        if (targetResourceExists.TryGetValue(fullPath, out var exists)) return exists;
        exists = SafeFileExists(fullPath);
        targetResourceExists[fullPath] = exists;
        return exists;
    }

    void ProbeTargetResourcesLocked()
    {
        targetResourceExists.Clear();
        var candidates = BuildTargetProbeCandidates(TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var lines = new StringBuilder();
        var found = 0;
        foreach (var candidate in candidates)
        {
            var fullPath = WithBgPrefix(candidate);
            var exists = SafeFileExists(fullPath);
            targetResourceExists[fullPath] = exists;
            if (exists) found++;
            lines.AppendLine($"exists {fullPath} = {exists}");
        }

        TargetResourceProbeSummary = $"{found}/{candidates.Length} target resource candidates exist";
        PluginLog.Information($"{LogPrefix()} Target resource probe:\n{lines.ToString().TrimEnd()}");
    }

    IEnumerable<string> BuildTargetProbeCandidates(string targetPath)
    {
        var targetRoot = GetSceneRoot(targetPath);
        var targetName = GetLastSegment(targetPath);
        foreach (var extension in new[] { ".lvb", ".sgb", ".svb", ".lcb", ".lgb" })
        {
            yield return $"{targetPath}{extension}";
        }

        yield return $"{targetRoot}/level/{targetName}.lvb";
        yield return $"{targetRoot}/level/{targetName}.sgb";
        foreach (var fileName in LevelLgbFiles)
        {
            yield return $"{targetRoot}/level/{fileName}";
        }

        foreach (var fileName in LevelLgbFiles)
        {
            yield return $"{targetRoot}/{fileName}";
        }
    }

    bool SafeFileExists(string path)
    {
        try
        {
            return Svc.Data.FileExists(path);
        }
        catch (Exception e)
        {
            PluginLog.Warning($"{LogPrefix()} Target resource FileExists failed: path = {path}; {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    void CountSkip(DirectBgPathSkipReason skipReason, string originalPath)
    {
        if (skipReason is DirectBgPathSkipReason.None or DirectBgPathSkipReason.NotCarrierFamily) return;
        LastSkipReason = $"{skipReason}: {originalPath}";
        switch (skipReason)
        {
            case DirectBgPathSkipReason.TargetMissing:
                SkippedBecauseTargetMissing++;
                break;
            case DirectBgPathSkipReason.NotLayout:
                SkippedBecauseNotLayout++;
                break;
        }
    }

    void LogMissingTargetOnce(string originalPath, IEnumerable<string> candidates)
    {
        var key = BgPathResolver.NormalizeBasicPath(originalPath);
        if (!loggedMissingTargets.Add(key)) return;
        TouchLog();
        PluginLog.Warning($"{LogPrefix()} Resource override skipped because target resource is missing: original = {originalPath}; candidates = {string.Join(", ", candidates.Select(WithBgPrefix))}");
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

    bool IsClearedCarrierResource(string originalPath)
    {
        var originalBase = StripBgPrefixAndExtension(BgPathResolver.NormalizeBasicPath(originalPath), out _, out _);
        var carrierBase = NormalizeForLoad(clearedCarrierBgPath);
        var carrierRoot = GetSceneRoot(carrierBase);
        return IsCarrierFamilyPath(originalBase, carrierBase, carrierRoot, $"{carrierRoot}/level");
    }

    static bool IsCarrierFamilyPath(string originalBase, string carrierBase, string carrierRoot, string carrierLevelRoot)
    {
        return originalBase.Equals(carrierBase, StringComparison.OrdinalIgnoreCase)
            || originalBase.StartsWith($"{carrierLevelRoot}/", StringComparison.OrdinalIgnoreCase)
            || originalBase.StartsWith($"{carrierRoot}/", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsMainLayoutResource(string originalBase, string extension, string carrierBase, string carrierLevelRoot)
    {
        if (originalBase.Equals(carrierBase, StringComparison.OrdinalIgnoreCase) && MainLayoutExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return true;
        if (!originalBase.StartsWith($"{carrierLevelRoot}/", StringComparison.OrdinalIgnoreCase) || !extension.Equals(".lgb", StringComparison.OrdinalIgnoreCase)) return false;
        var fileName = $"{GetLastSegment(originalBase)}{extension}";
        return LevelLgbFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase);
    }

    static string StripBgPrefixAndExtension(string path, out bool hadBgPrefix, out string extension)
    {
        var ret = path;
        hadBgPrefix = ret.StartsWith("bg/", StringComparison.OrdinalIgnoreCase);
        if (hadBgPrefix) ret = ret[3..];
        extension = "";
        foreach (var candidate in MainLayoutExtensions)
        {
            if (!ret.EndsWith(candidate, StringComparison.OrdinalIgnoreCase)) continue;
            extension = candidate;
            ret = ret[..^candidate.Length];
            break;
        }
        return ret.Trim('/');
    }

    static string GetSceneRoot(string path)
    {
        var normalized = NormalizeForLoad(path);
        var levelIndex = normalized.IndexOf("/level/", StringComparison.OrdinalIgnoreCase);
        if (levelIndex >= 0) return normalized[..levelIndex].Trim('/');
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash <= 0 ? normalized : normalized[..lastSlash];
    }

    static string GetLastSegment(string path)
    {
        var normalized = NormalizeForLoad(path);
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? normalized : normalized[(lastSlash + 1)..];
    }

    static string WithBgPrefix(string noBgPath)
    {
        var normalized = BgPathResolver.NormalizeBasicPath(noBgPath);
        return normalized.StartsWith("bg/", StringComparison.OrdinalIgnoreCase) ? normalized : $"bg/{normalized}";
    }

    nint GetOrAllocateReplacementPath(string replacementPath)
    {
        if (replacementPathPtrs.TryGetValue(replacementPath, out var ptr)) return ptr;
        ptr = AllocUtf8(replacementPath);
        replacementPathPtrs[replacementPath] = ptr;
        return ptr;
    }

    void ResetAttemptCounters()
    {
        OverrideHits = 0;
        ProbedResources = 0;
        SkippedBecauseTargetMissing = 0;
        SkippedBecauseNotLayout = 0;
        SkippedBecauseAfterClear = 0;
        probeLogCount = 0;
        overrideLogCount = 0;
        afterClearLogCount = 0;
        uniqueProbedResources.Clear();
        loggedProbePaths.Clear();
        loggedOverridePaths.Clear();
        loggedLateOverridePaths.Clear();
        loggedMissingTargets.Clear();
        loggedAfterClearPaths.Clear();
        targetResourceExists.Clear();
        TargetResourceProbeSummary = "";
    }

    string LogPrefix() => AttemptId == 0 ? "[DirectBgPath]" : $"[DirectBgPath#{AttemptId}]";

    void TouchLog() => LastLogTime = DateTime.Now;

    static string NormalizeForLoad(string path)
    {
        var ret = BgPathResolver.NormalizeBasicPath(path);
        if (ret.StartsWith("bg/", StringComparison.Ordinal)) ret = ret[3..];
        foreach (var extension in MainLayoutExtensions)
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
    WaitingForResourceOverride,
    ActiveOverride,
    Stable,
    Failed,
    TimedOut,
    UserCancelled,
    Cleared,
}

public enum DirectBgPathSkipReason
{
    None,
    NotCarrierFamily,
    NotLayout,
    TargetMissing,
}
