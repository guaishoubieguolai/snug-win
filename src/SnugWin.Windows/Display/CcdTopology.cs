using System.Runtime.InteropServices;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;
using SnugWin.Windows.Native;

namespace SnugWin.Windows.Display;

internal sealed record CcdSource(Win32.LUID Adapter, uint Id, string FriendlyName, string TargetPath);

internal sealed unsafe record CcdTopology(Win32.DISPLAYCONFIG_PATH_INFO[] Paths, Win32.DISPLAYCONFIG_MODE_INFO[] Modes)
{
    public static CcdTopology? Query(uint flags)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (Win32.GetDisplayConfigBufferSizes(flags, out uint pc, out uint mc) != 0) break;
            var paths = new Win32.DISPLAYCONFIG_PATH_INFO[pc];
            var modes = new Win32.DISPLAYCONFIG_MODE_INFO[mc];
            int error = Win32.QueryDisplayConfig(flags, ref pc, paths, ref mc, modes, IntPtr.Zero);
            if (error == 122) continue; // topology changed, resize both buffers
            if (error != 0) break;
            return new(paths[..(int)pc], modes[..(int)mc]);
        }
        return null;
    }

    public Dictionary<string, CcdSource> ActiveSources()
    {
        var result = new Dictionary<string, CcdSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Paths.Where(IsActive))
        {
            string source = SourceName(path.sourceInfo);
            var (friendly, target) = TargetName(path.targetInfo);
            // A cloned source is not a dedicated screen, even when one target is Cherry.
            if (result.ContainsKey(source)) target = string.Empty;
            result[source] = new(path.sourceInfo.adapterId, path.sourceInfo.id, friendly, target);
        }
        return result;
    }

    public string? Attach(CherryMonitor monitor, int width, int height)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var active = Paths.Where(IsActive).ToList();
        if (active.Count == 0 || active.Any(p => !HasSourceMode(p))) return null;
        var desired = FindDetachedPath(monitor, active);
        if (desired is not { } path)
        {
            Log.Warn($"[诊断-错误87] FindDetachedPath未找到Cherry #{monitor.DriverId} detached path (ContainerId={monitor.ContainerId})");
            return null;
        }
        string name = SourceName(path.sourceInfo);
        if (name.Length == 0) return null;

        // [诊断-错误87] 记录找到的detached path详情
        Log.Info($"[诊断-错误87] Cherry #{monitor.DriverId} detached path找到: " +
            $"Src(AdapterId={path.sourceInfo.adapterId.LowPart:X8}-{path.sourceInfo.adapterId.HighPart:X8}, " +
            $"Id={path.sourceInfo.id}, ModeIdx={path.sourceInfo.modeInfoIdx}), " +
            $"Tgt(AdapterId={path.targetInfo.adapterId.LowPart:X8}-{path.targetInfo.adapterId.HighPart:X8}, " +
            $"Id={path.targetInfo.id}, Available={path.targetInfo.targetAvailable}), " +
            $"Flags=0x{path.flags:X}");

        // Re-check active topology right before submitting; a Win32 full-topology SetDisplayConfig
        // still retains an irreducible kernel TOCTOU window after this read.
        var latestActive = Query(Win32.QDC_ONLY_ACTIVE_PATHS)?.Paths.Where(IsActive).ToList();
        if (latestActive is null || !SameActivePaths(active, latestActive))
        {
            Log.Warn($"[诊断-错误87] TOCTOU检测到拓扑变化，放弃本次Attach (耗时={sw.ElapsedMilliseconds}ms)");
            return null;
        }

        var rightmost = active.Select(p => Modes[p.sourceInfo.modeInfoIdx].info.sourceMode)
            .MaxBy(s => (long)s.position.X + s.width);
        var position = new Win32.POINT { X = checked(rightmost.position.X + (int)rightmost.width), Y = rightmost.position.Y };
        var mode = new Win32.DISPLAYCONFIG_MODE_INFO
        {
            infoType = 1, // DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE
            adapterId = path.sourceInfo.adapterId, id = path.sourceInfo.id,
            info = new() { sourceMode = new() { width = (uint)width, height = (uint)height, pixelFormat = 4, position = position } }
        };
        path.sourceInfo.modeInfoIdx = (uint)Modes.Length;
        path.targetInfo.modeInfoIdx = uint.MaxValue;
        path.flags |= Win32.DISPLAYCONFIG_PATH_ACTIVE;
        active.Add(path);
        
        // [诊断-错误87] 记录SetDisplayConfig参数
        Log.Info($"[诊断-错误87] 调用SetDisplayConfig: pathCount={active.Count}, modeCount={(uint)Modes.Length + 1}, " +
            $"新mode(width={width}, height={height}, pos=({position.X},{position.Y}), pixelFormat=4, modeIdx={path.sourceInfo.modeInfoIdx}), " +
            $"flags=SDC_USE_SUPPLIED_DISPLAY_CONFIG|SDC_APPLY");
        
        // Keep every existing active path, mode and priority; do not allow Windows
        // to change other displays to make this request succeed.
        sw.Restart();
        int error = Win32.SetDisplayConfig((uint)active.Count, active.ToArray(), (uint)Modes.Length + 1,
            [.. Modes, mode], Win32.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Win32.SDC_APPLY);
        sw.Stop();
        
        if (error != 0)
        {
            // [诊断-错误87] 记录失败详情
            Log.Error($"[诊断-错误87] SetDisplayConfig失败: 错误码={error}, 耗时={sw.ElapsedMilliseconds}ms");
            Log.Error($"[诊断-错误87] Cherry path详情: Src.adapterId={path.sourceInfo.adapterId.LowPart:X8}-{path.sourceInfo.adapterId.HighPart:X8}, " +
                $"Src.id={path.sourceInfo.id}, Src.modeIdx={path.sourceInfo.modeInfoIdx}, " +
                $"Tgt.adapterId={path.targetInfo.adapterId.LowPart:X8}-{path.targetInfo.adapterId.HighPart:X8}, " +
                $"Tgt.id={path.targetInfo.id}, Tgt.modeIdx={path.targetInfo.modeInfoIdx}, flags=0x{path.flags:X}");
            Log.Error($"[诊断-错误87] 新mode详情: width={width}, height={height}, position=({position.X},{position.Y}), " +
                $"adapterId={mode.adapterId.LowPart:X8}-{mode.adapterId.HighPart:X8}, id={mode.id}, modeIdx={(uint)Modes.Length}");
            Log.Error($"[诊断-错误87] active paths count={active.Count}, total modes count={(uint)Modes.Length + 1}");
            
            throw new PhoneScreenException(PhoneScreenErrorKind.CreateFailed, $"挂载 Cherry 显示器 #{monitor.DriverId} 失败（{error}）");
        }
        
        Log.Info($"[诊断-错误87] SetDisplayConfig成功, 耗时={sw.ElapsedMilliseconds}ms, DeviceName={name}");
        return name;
    }

    private static bool SameActivePaths(List<Win32.DISPLAYCONFIG_PATH_INFO> baseline, List<Win32.DISPLAYCONFIG_PATH_INFO> current)
    {
        if (baseline.Count != current.Count) return false;
        foreach (var a in baseline)
        {
            if (!current.Any(b => SameSource(a.sourceInfo, b.sourceInfo) && SameTarget(a.targetInfo, b.targetInfo)))
                return false;
        }
        return true;
    }

    private Win32.DISPLAYCONFIG_PATH_INFO? FindDetachedPath(CherryMonitor monitor, List<Win32.DISPLAYCONFIG_PATH_INFO> active)
    {
        // Inactive GDI sources can each enumerate *all* of an adapter's monitors.
        // Only an explicit CCD target path says which connector would be enabled.
        var inspected = new HashSet<(Win32.LUID, uint)>();
        foreach (var path in Paths)
        {
            if (IsActive(path) || path.targetInfo.targetAvailable == 0) continue;
            if (!path.sourceInfo.adapterId.Equals(path.targetInfo.adapterId)) continue;
            if (active.Any(p => SameSource(p.sourceInfo, path.sourceInfo) || SameTarget(p.targetInfo, path.targetInfo))) continue;
            if (!inspected.Add((path.targetInfo.adapterId, path.targetInfo.id))) continue;
            if (CherryMonitorIdentity.FromInterface(TargetName(path.targetInfo).Path) == monitor)
                return path;
        }
        return null;
    }

    private bool HasSourceMode(Win32.DISPLAYCONFIG_PATH_INFO path) =>
        path.sourceInfo.modeInfoIdx < Modes.Length && Modes[path.sourceInfo.modeInfoIdx].infoType == 1;

    private static bool IsActive(Win32.DISPLAYCONFIG_PATH_INFO p) => (p.flags & Win32.DISPLAYCONFIG_PATH_ACTIVE) != 0;

    private static bool SameSource(Win32.DISPLAYCONFIG_PATH_SOURCE_INFO a, Win32.DISPLAYCONFIG_PATH_SOURCE_INFO b) =>
        a.id == b.id && a.adapterId.Equals(b.adapterId);

    private static bool SameTarget(Win32.DISPLAYCONFIG_PATH_TARGET_INFO a, Win32.DISPLAYCONFIG_PATH_TARGET_INFO b) =>
        a.id == b.id && a.adapterId.Equals(b.adapterId);

    private static string SourceName(Win32.DISPLAYCONFIG_PATH_SOURCE_INFO source)
    {
        var packet = new Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new() { type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), adapterId = source.adapterId, id = source.id }
        };
        return Win32.DisplayConfigGetDeviceInfo(ref packet) == 0 ? new string(packet.viewGdiDeviceName) : string.Empty;
    }

    private static (string Friendly, string Path) TargetName(Win32.DISPLAYCONFIG_PATH_TARGET_INFO target)
    {
        var packet = new Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new() { type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME>(), adapterId = target.adapterId, id = target.id }
        };
        return Win32.DisplayConfigGetDeviceInfo(ref packet) == 0
            ? (new string(packet.monitorFriendlyDeviceName), new string(packet.monitorDevicePath)) : (string.Empty, string.Empty);
    }
}
