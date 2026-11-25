using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using SnugWin.Windows.Display;

namespace SnugWin.Core.Tests;

// Models v2's full-list Notify and legacy's ignored duplicate Add. No calls to
// the real driver, display settings or cursor APIs.
internal sealed class DisplayOwnershipFixture : PhoneDisplayTopology, IVirtualDisplayIpc, IVirtualDisplayState
{
    public bool IsLegacyProtocol { get; set; } = true;
    public bool PipeUp { get; set; } = true;
    public List<VddMonitor> Config { get; } = [];
    public List<uint> Profile { get; } = [];
    public List<uint> Adds { get; } = [];
    public List<uint> Removes { get; } = [];
    public List<CherryMonitor> Attachments { get; } = [];
    public List<string> Writes { get; } = [];
    public List<CherryMonitor> Present { get; } = [];
    public HashSet<Guid> History { get; } = [];
    public List<MonitorSnapshot> Desktop { get; } = [];
    public Func<CherryMonitor, string?>? AttachAs { get; set; }
    public Action<uint>? Arrive { get; set; }
    public Action<int>? BeforeStateRead { get; set; }
    private int _reads;
    public bool AcceptResolution { get; set; } = true;
    public bool ApplyResolution { get; set; } = true;
    public bool AcceptScale { get; set; } = true;
    public bool ApplyScale { get; set; } = true;
    public List<int> ScaleRequests { get; } = [];
    public PhoneDisplayTopology? TopologyOverride { get; set; }

    public PhoneDisplayService Service(SnugPaths? paths = null) =>
        new(this, paths, () => PipeUp, TopologyOverride ?? this, TimeSpan.FromMilliseconds(35));

    public CherryMonitor Plug(uint id)
    {
        var target = new CherryMonitor(id, Guid.NewGuid());
        History.Add(target.ContainerId);
        Present.Add(target);
        return target;
    }

    public void Unplug(uint id)
    {
        Present.RemoveAll(m => m.DriverId == id);
        Desktop.RemoveAll(m => m.Cherry?.DriverId == id);
    }

    public static MonitorSnapshot Monitor(string name, CherryMonitor? target = null, string adapter = "PCI", bool primary = false) =>
        new(10, name, "Virtual Display", "Display Adapter", new RectD(4096, 0, 1080, 1900),
            new RectD(4096, 0, 1080, 1860), primary, new DisplayModeInfo(1080, 1900, 60, 100), default, 0)
        {
            Cherry = target, HasDedicatedTarget = true, DpiRange = DpiScaleRange.FromRelative(0, 4),
            AdapterHardwareId = target is null ? adapter : CherryMonitorIdentity.AdapterHardwareId
        };

    public Action<CherryInventory>? DuringInspection { get; set; }
    public override CherryInventory Inspect()
    {
        var scan = new CherryInventory();
        scan.KnownContainers.UnionWith(History);
        foreach (var target in Present) scan.ObservePresent(target.DriverId, target.ContainerId);
        DuringInspection?.Invoke(scan);
        return scan;
    }
    public override IReadOnlyList<MonitorSnapshot> Active() => Desktop.ToArray();
    public override IReadOnlyList<uint> LegacyProfileIds() => Profile;
    public override void RestoreCursorIfStranded() { }

    public override string? Attach(CherryMonitor target, int width, int height)
    {
        Attachments.Add(target);
        if (AttachAs is not null) return AttachAs(target);
        string name = $@"\\.\DISPLAY{80 + target.DriverId}";
        Desktop.Add(Monitor(name, target));
        return name;
    }

    public override bool SetResolution(MonitorSnapshot current, int width, int height, double hz)
    {
        Writes.Add(current.DeviceName);
        if (!AcceptResolution) return false;
        if (ApplyResolution)
            Desktop[Desktop.IndexOf(current)] = current with { Mode = current.Mode with { Width = width, Height = height, RefreshRate = hz } };
        return true;
    }

    public override bool SetScale(MonitorSnapshot current, int percent)
    {
        Writes.Add(current.DeviceName);
        ScaleRequests.Add(percent);
        if (!AcceptScale || current.ResolveDpiScalePercent(percent) != percent) return false;
        if (ApplyScale) Desktop[Desktop.IndexOf(current)] = current with { Mode = current.Mode with { DpiScalePercent = percent } };
        return true;
    }

    public Task<IReadOnlyList<VddMonitor>?> ReadStateAsync(CancellationToken ct)
    {
        BeforeStateRead?.Invoke(++_reads);
        if (!PipeUp) throw new IOException("driver temporarily unavailable");
        return Task.FromResult<IReadOnlyList<VddMonitor>?>(IsLegacyProtocol ? null : Config.ToArray());
    }

    public Task NotifyAsync(IReadOnlyList<VddMonitor> monitors, CancellationToken ct = default)
    {
        var requested = monitors[^1];
        Adds.Add(requested.Id);
        if (IsLegacyProtocol && Config.Any(m => m.Id == requested.Id)) return Task.CompletedTask;
        if (!IsLegacyProtocol)
        {
            foreach (var missing in Config.Where(c => monitors.All(m => m.Id != c.Id))) Unplug(missing.Id);
            Config.Clear();
        }
        Config.AddRange(monitors);
        Unplug(requested.Id);
        if (Arrive is null) Plug(requested.Id); else Arrive(requested.Id);
        return Task.CompletedTask;
    }

    public Task ReplaceLegacyAsync(IReadOnlyList<uint> removeIds, IReadOnlyList<VddMonitor> monitors, CancellationToken ct = default)
    {
        foreach (var id in removeIds)
        {
            Config.RemoveAll(m => m.Id == id);
            Unplug(id);
        }
        return NotifyAsync(monitors, ct);
    }

    public Task RemoveAsync(IReadOnlyList<uint> ids, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Removes.AddRange(ids);
        foreach (uint id in ids)
        {
            Config.RemoveAll(m => m.Id == id);
            Unplug(id);
        }
        return Task.CompletedTask;
    }

    public Task RemoveAllAsync(CancellationToken ct = default) => throw new InvalidOperationException("RemoveAll is never safe");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TestTopologyWithTransientGlitch : PhoneDisplayTopology
{
    private readonly DisplayOwnershipFixture _rig;
    private readonly Func<List<MonitorSnapshot>> _activeProvider;

    public TestTopologyWithTransientGlitch(DisplayOwnershipFixture rig, Func<List<MonitorSnapshot>> activeProvider)
    {
        _rig = rig;
        _activeProvider = activeProvider;
    }

    public override CherryInventory Inspect() => _rig.Inspect();
    public override IReadOnlyList<MonitorSnapshot> Active() => _activeProvider();
    public override IReadOnlyList<uint> LegacyProfileIds() => _rig.LegacyProfileIds();
    public override void RestoreCursorIfStranded() => _rig.RestoreCursorIfStranded();
    public override string? Attach(CherryMonitor target, int width, int height) => _rig.Attach(target, width, height);
    public override bool SetResolution(MonitorSnapshot current, int width, int height, double hz) => _rig.SetResolution(current, width, height, hz);
    public override bool SetScale(MonitorSnapshot current, int percent) => _rig.SetScale(current, percent);
}

internal sealed class TestTopologyWithPermanentLoss : PhoneDisplayTopology
{
    public override CherryInventory Inspect() => new CherryInventory();
    public override IReadOnlyList<MonitorSnapshot> Active() => Array.Empty<MonitorSnapshot>();
    public override IReadOnlyList<uint> LegacyProfileIds() => Array.Empty<uint>();
    public override void RestoreCursorIfStranded() { }
    public override string? Attach(CherryMonitor target, int width, int height) => null;
    public override bool SetResolution(MonitorSnapshot current, int width, int height, double hz) => false;
    public override bool SetScale(MonitorSnapshot current, int percent) => false;
}
