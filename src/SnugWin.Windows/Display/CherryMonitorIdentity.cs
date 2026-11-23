using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Text;
using Microsoft.Win32;
using SnugWin.Windows.Native;

namespace SnugWin.Windows.Display;

// Both Cherry protocols put Monitor.id in the EDID serial and generate a fresh
// MonitorContainerId on each arrival. Neither the GDI name nor the UID is a lease.
internal sealed record CherryMonitor(uint DriverId, Guid ContainerId)
{
    public static CherryMonitor? From(uint? id, Guid container) =>
        id is { } driverId && container != Guid.Empty ? new(driverId, container) : null;
}

internal sealed class CherryInventory
{
    private readonly List<(uint? Id, Guid Container)> _pending = [];
    private readonly List<CherryMonitor> _present = [];
    public IReadOnlyList<CherryMonitor> Present => _present;
    public HashSet<Guid> KnownContainers { get; } = [];
    public HashSet<uint> OccupiedIds { get; } = [];
    public bool HasUnknownId => _pending.Any(p => p.Id is null);

    public void ObservePresent(uint? id, Guid container)
    {
        if (container != Guid.Empty) KnownContainers.Add(container);
        if (id is { } slot) OccupiedIds.Add(slot);
        if (CherryMonitor.From(id, container) is { } ready) _present.Add(ready);
        else _pending.Add((id, container));
    }

    public CherryMonitor? Unique(uint id)
    {
        if (_pending.Any(p => p.Id is null || p.Id == id)) return null;
        var matches = _present.Where(m => m.DriverId == id).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public bool MayContain(CherryMonitor target) => _present.Contains(target) || _pending.Any(p =>
        (p.Id is null || p.Id == target.DriverId) && (p.Container == Guid.Empty || p.Container == target.ContainerId));
}

internal static class CherryMonitorIdentity
{
    internal const string AdapterHardwareId = @"Root\VirtualDisplayDriver";
    private const string EnumKey = @"SYSTEM\CurrentControlSet\Enum\";
    private const string Model = @"DISPLAY\CHY0000\";
    private const string InterfacePrefix = @"\\?\DISPLAY#CHY0000#";
    private const string MonitorClass = "#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    internal static CherryInventory Inspect()
    {
        var inventory = new CherryInventory();
        try
        {
            using var model = Registry.LocalMachine.OpenSubKey(EnumKey + Model.TrimEnd('\\'));
            if (model is not null)
                foreach (string instance in model.GetSubKeyNames()) InspectNode(model, instance, inventory);
        }
        catch (Exception ex) when (IsUnavailable(ex)) { inventory.ObservePresent(null, Guid.Empty); }
        return inventory;
    }

    private static void InspectNode(RegistryKey model, string instance, CherryInventory inventory)
    {
        Guid container = Guid.Empty;
        try
        {
            using var key = model.OpenSubKey(instance);
            if (key is not null) container = ContainerOf(key);
            if (IsPresentOnCherryAdapter(Model + instance))
                inventory.ObservePresent(key is null ? null : DriverIdOf(key), container);
        }
        catch (Exception ex) when (IsUnavailable(ex)) { inventory.ObservePresent(null, container); }
    }

    internal static CherryMonitor? FromInterface(string path)
    {
        if (InstanceFromInterface(path) is not { } instance) return null;
        try
        {
            if (!IsPresentOnCherryAdapter(instance)) return null;
            using var key = Registry.LocalMachine.OpenSubKey(EnumKey + instance);
            return key is null ? null : CherryMonitor.From(DriverIdOf(key), ContainerOf(key));
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            return null;
        }
    }

    internal static string? InstanceFromInterface(string path)
    {
        if (!path.StartsWith(InterfacePrefix, StringComparison.OrdinalIgnoreCase)
            || !path.EndsWith(MonitorClass, StringComparison.OrdinalIgnoreCase)) return null;
        string instance = path[InterfacePrefix.Length..^MonitorClass.Length];
        return instance.Length > 0 && instance.IndexOfAny(['#', '\\', '/']) < 0 ? Model + instance : null;
    }

    internal static uint? DriverIdFromEdid(ReadOnlySpan<byte> edid)
    {
        ReadOnlySpan<byte> prefix = [0, 255, 255, 255, 255, 255, 255, 0, 0x0d, 0x19, 0, 0];
        if (edid.Length < 128 || !edid.StartsWith(prefix)) return null;
        int sum = 0;
        foreach (byte value in edid[..128]) sum += value;
        return (sum & 255) == 0 ? BinaryPrimitives.ReadUInt32LittleEndian(edid[12..]) : null;
    }

    private static Guid ContainerOf(RegistryKey key) =>
        Guid.TryParse(key.GetValue("ContainerID") as string, out var container) ? container : Guid.Empty;

    private static uint? DriverIdOf(RegistryKey key)
    {
        using var parameters = key.OpenSubKey("Device Parameters");
        return DriverIdFromEdid(parameters?.GetValue("EDID") as byte[]);
    }

    private static bool IsUnavailable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception;

    private static bool IsPresentOnCherryAdapter(string instance)
    {
        uint status = Win32.CM_Locate_DevNodeW(out uint node, instance, 0);
        if (status == Win32.CR_NO_SUCH_DEVNODE) return false;
        if (status != 0 || Win32.CM_Get_Parent(out uint parent, node, 0) != 0)
            throw new IOException("无法确认 Cherry 显示器是否存在");
        var id = new StringBuilder(200); // MAX_DEVICE_ID_LEN
        if (Win32.CM_Get_Device_IDW(parent, id, (uint)id.Capacity, 0) != 0)
            throw new IOException("无法读取显示适配器实例 ID");
        using var adapter = Registry.LocalMachine.OpenSubKey(EnumKey + id);
        var rawHardwareId = adapter?.GetValue("HardwareID");
        string[] hardwareIds = rawHardwareId switch
        {
            string[] array => array,
            string single => [single],
            _ => throw new IOException("无法读取显示适配器硬件 ID")
        };
        return hardwareIds.Contains(AdapterHardwareId, StringComparer.OrdinalIgnoreCase);
    }
}
