using System.Runtime.InteropServices;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;
using SnugWin.Windows.Native;

namespace SnugWin.Windows.Display;

public readonly record struct AdapterLuid(uint LowPart, int HighPart);

public sealed record MonitorSnapshot(
    uint StableId,
    string DeviceName,
    string FriendlyName,
    string DeviceString,
    RectD Bounds,
    RectD WorkArea,
    bool IsPrimary,
    DisplayModeInfo Mode,
    AdapterLuid AdapterId,
    uint SourceId)
{
    internal CherryMonitor? Cherry { get; init; }
    internal string AdapterHardwareId { get; init; } = string.Empty;
    internal bool HasDedicatedTarget { get; init; }
    internal DpiScaleRange? DpiRange { get; init; }

    public int? ResolveDpiScalePercent(int requestedPercent) => DpiRange?.NearestPercent(requestedPercent);
}

internal sealed class DpiScaleRange
{
    private static readonly int[] Levels = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];
    private readonly int _minimumRelative;
    private readonly int _maximumIndex;

    private DpiScaleRange(int minimumRelative, int maximumIndex) => (_minimumRelative, _maximumIndex) = (minimumRelative, maximumIndex);

    public static DpiScaleRange? FromRelative(int min, int max) =>
        min <= 0 && max >= 0 && (long)max - min < Levels.Length ? new(min, max - min) : null;

    public int? PercentAt(int relative)
    {
        long index = (long)relative - _minimumRelative;
        return index >= 0 && index <= _maximumIndex ? Levels[(int)index] : null;
    }

    public int? RelativeFor(int percent)
    {
        int index = Array.IndexOf(Levels, percent);
        return index >= 0 && index <= _maximumIndex ? _minimumRelative + index : null;
    }

    public int NearestPercent(int requested) => Levels.Take(_maximumIndex + 1).MinBy(p => Math.Abs((long)p - requested));
}

public static unsafe class DisplayConfigService
{
    public static IReadOnlyList<MonitorSnapshot> GetActiveMonitors()
    {
        var sources = CcdTopology.Query(Win32.QDC_ONLY_ACTIVE_PATHS)?.ActiveSources() ?? [];
        var adapters = QueryAdapters();
        var results = new List<MonitorSnapshot>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Win32.RECT _, IntPtr _) =>
        {
            var mi = new Win32.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (!Win32.GetMonitorInfoW(h, ref mi)) return true;
            string name = new string(mi.szDevice);
            var source = sources.GetValueOrDefault(name) ?? new CcdSource(default, 0, name, string.Empty);
            if (!adapters.TryGetValue(name, out var adapter)) adapter = (string.Empty, string.Empty);
            results.Add(Snapshot(mi, name, source, adapter));
            return true;
        }, IntPtr.Zero);
        return results;
    }

    private static MonitorSnapshot Snapshot(Win32.MONITORINFOEX mi, string name, CcdSource source, (string Desc, string Id) adapter)
    {
        var bounds = new RectD(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Width, mi.rcMonitor.Height);
        var work = new RectD(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Width, mi.rcWork.Height);
        var luid = new AdapterLuid(source.Adapter.LowPart, source.Adapter.HighPart);
        bool dedicated = source.TargetPath.Length != 0;
        var dpi = dedicated ? ReadDpiScale(luid, source.Id) : null;
        var mode = CurrentMode(name, dpi?.Current);
        uint stableId = dedicated ? (uint)HashCode.Combine(luid, source.Id, StringComparer.OrdinalIgnoreCase.GetHashCode(source.TargetPath))
            : (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(name);
        return new(stableId, name, string.IsNullOrEmpty(source.FriendlyName) ? name : source.FriendlyName,
            adapter.Desc, bounds, work, (mi.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0,
            mode ?? new((int)bounds.Width, (int)bounds.Height, 60, dpi?.Current), luid, source.Id)
        {
            AdapterHardwareId = adapter.Id,
            DpiRange = dpi?.Range,
            HasDedicatedTarget = dedicated && mode is not null,
            Cherry = dpi.HasValue && adapter.Id.Equals(CherryMonitorIdentity.AdapterHardwareId, StringComparison.OrdinalIgnoreCase)
                ? CherryMonitorIdentity.FromInterface(source.TargetPath) : null
        };
    }

    private static DisplayModeInfo? CurrentMode(string name, int? dpi)
    {
        var dm = new Win32.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Win32.DEVMODE>() };
        return Win32.EnumDisplaySettingsExW(name, Win32.ENUM_CURRENT_SETTINGS, ref dm, 0)
            ? new((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, dm.dmDisplayFrequency > 0 ? dm.dmDisplayFrequency : 60, dpi)
            : null;
    }

    public static bool SetResolution(string deviceName, int pixelWidth, int pixelHeight, double refreshRate = 60)
    {
        var dm = new Win32.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Win32.DEVMODE>() };
        if (!Win32.EnumDisplaySettingsExW(deviceName, Win32.ENUM_CURRENT_SETTINGS, ref dm, 0)) return false;
        dm.dmPelsWidth = (uint)pixelWidth;
        dm.dmPelsHeight = (uint)pixelHeight;
        dm.dmFields = Win32.DM_PELSWIDTH | Win32.DM_PELSHEIGHT;
        if (refreshRate > 0)
        {
            dm.dmDisplayFrequency = (uint)Math.Round(refreshRate);
            dm.dmFields |= Win32.DM_DISPLAYFREQUENCY;
        }
        // Prefer dynamic application (0) so we do not pollute Windows persistent boot registry with virtual screen modes
        int res = Win32.ChangeDisplaySettingsExW(deviceName, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
        if (res != Win32.DISP_CHANGE_SUCCESSFUL)
        {
            res = Win32.ChangeDisplaySettingsExW(deviceName, ref dm, IntPtr.Zero, Win32.CDS_UPDATEREGISTRY, IntPtr.Zero);
        }
        if (res == Win32.DISP_CHANGE_SUCCESSFUL) return true;
        Log.Warn($"ChangeDisplaySettingsEx({deviceName}, {pixelWidth}x{pixelHeight}) 返回 {res}");
        return false;
    }

    internal static string? TryAttachVirtualDisplay(CherryMonitor target, int width, int height) =>
        CcdTopology.Query(Win32.QDC_ALL_PATHS)?.Attach(target, width, height);

    public static bool HasDisplayMode(string deviceName, int pixelWidth, int pixelHeight)
    {
        var dm = new Win32.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Win32.DEVMODE>() };
        int idx = 0;
        while (Win32.EnumDisplaySettingsExW(deviceName, idx++, ref dm, 0))
        {
            if (dm.dmPelsWidth == (uint)pixelWidth && dm.dmPelsHeight == (uint)pixelHeight)
                return true;
        }
        return false;
    }

    public static int? GetDpiScalePercent(AdapterLuid adapterLuid, uint sourceId) => ReadDpiScale(adapterLuid, sourceId)?.Current;

    private static (DpiScaleRange Range, int Current)? ReadDpiScale(AdapterLuid adapterLuid, uint sourceId)
    {
        var pkt = DpiQuery(adapterLuid, sourceId);
        if (Win32.DisplayConfigGetDeviceInfo(ref pkt) != 0) return null;
        var range = DpiScaleRange.FromRelative(pkt.minScaleRel, pkt.maxScaleRel);
        return range?.PercentAt(pkt.curScaleRel) is { } current ? (range, current) : null;
    }

    public static bool SetDpiScalePercent(AdapterLuid adapterLuid, uint sourceId, int targetPercent)
    {
        if (ReadDpiScale(adapterLuid, sourceId) is not { } scale) return false;
        if (scale.Range.RelativeFor(targetPercent) is not { } rel) return false;
        if (scale.Current == targetPercent) return true;
        var setPkt = new Win32.DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
        {
            header = new() { type = Win32.DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE,
                size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_SOURCE_DPI_SCALE_SET>(),
                adapterId = new() { LowPart = adapterLuid.LowPart, HighPart = adapterLuid.HighPart }, id = sourceId },
            scaleRel = rel
        };
        int status = Win32.DisplayConfigSetDeviceInfo(ref setPkt);
        if (status != 0) Log.Warn($"设置 DPI 缩放 {targetPercent}% 失败（status={status}）");
        return status == 0;
    }

    private static Win32.DISPLAYCONFIG_SOURCE_DPI_SCALE_GET DpiQuery(AdapterLuid adapter, uint id) => new()
    {
        header = new()
        {
            type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE,
            size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_SOURCE_DPI_SCALE_GET>(),
            adapterId = new() { LowPart = adapter.LowPart, HighPart = adapter.HighPart }, id = id
        }
    };

    private static Dictionary<string, (string Desc, string Id)> QueryAdapters()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var dd = new Win32.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<Win32.DISPLAY_DEVICE>() };
        uint i = 0;
        while (Win32.EnumDisplayDevicesW(null, i++, ref dd, 0))
            map[new string(dd.DeviceName)] = (new string(dd.DeviceString), new string(dd.DeviceID));
        return map;
    }
}
