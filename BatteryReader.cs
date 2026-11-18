using System.Runtime.InteropServices;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BatteryBar;

internal enum DeviceKind { Generic, Headphones, Mouse, Keyboard, Gamepad, Phone }

internal sealed record BatteryDevice(Guid ContainerId, string Name, int Level, DeviceKind Kind);

/// <summary>
/// Читает уровень заряда беспроводных устройств.
/// Источник заряда — PnP-свойство DEVPKEY_Bluetooth_Battery, которое Windows заполняет
/// для Bluetooth-гарнитур (HFP) и BLE-устройств с GATT Battery Service (мыши, клавиатуры и т.п.).
/// Подключено ли устройство — берём из Bluetooth association endpoints (WinRT).
/// </summary>
internal static class BatteryReader
{
    private const string AepIsConnected = "System.Devices.Aep.IsConnected";
    private const string AepContainerId = "System.Devices.Aep.ContainerId";
    private const string AepCodMajor = "System.Devices.Aep.Bluetooth.Cod.Major";

    private sealed class PnpNode
    {
        public string InstanceId = "";
        public Guid Container;
        public string? Class;
        public string? Name;
        public int? Battery;
    }

    private sealed record AepInfo(Guid Container, string Name, bool Connected, uint? CodMajor);

    /// <summary>Тестовые данные для проверки внешнего вида (BatteryBar.exe --demo).</summary>
    public static bool Demo { get; set; }

    public static async Task<List<BatteryDevice>> ReadAsync()
    {
        if (Demo)
            return new()
            {
                new(Guid.NewGuid(), "Demo Headphones", 30, DeviceKind.Headphones),
                new(Guid.NewGuid(), "Demo Mouse", 93, DeviceKind.Mouse),
            };

        var nodesTask = Task.Run(EnumeratePnp);
        Dictionary<Guid, AepInfo>? aep = null;
        try { aep = await GetBluetoothEndpointsAsync(); }
        catch { /* WinRT недоступен — покажем всё, у чего есть заряд */ }
        var nodes = await nodesTask;
        var list = Merge(nodes, aep);

        list.AddRange(await Task.Run(() => ReadDongles()));

        return list.OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Устройства с собственными донглами (каждый производитель — свой протокол).</summary>
    private static readonly (string Name, Func<StringBuilder?, List<BatteryDevice>> Read)[] DongleReaders =
    {
        ("Razer", RazerReader.Read),
        ("Logitech", LogitechReader.Read),
        ("SteelSeries", SteelSeriesReader.Read),
        ("Corsair", CorsairReader.Read),
    };

    private static List<BatteryDevice> ReadDongles(StringBuilder? log = null)
    {
        var list = new List<BatteryDevice>();
        foreach (var (name, read) in DongleReaders)
        {
            log?.AppendLine().AppendLine($"=== {name} ===");
            try { list.AddRange(read(log)); }
            catch (Exception ex) { log?.AppendLine("Ошибка: " + ex.Message); }
        }
        return list;
    }

    private static List<BatteryDevice> Merge(List<PnpNode> nodes, Dictionary<Guid, AepInfo>? aep)
    {
        var result = new List<BatteryDevice>();
        foreach (var group in nodes.Where(n => n.Container != Guid.Empty).GroupBy(n => n.Container))
        {
            var withBattery = group.Where(n => n.Battery is >= 0 and <= 100).ToList();
            if (withBattery.Count == 0) continue;

            AepInfo? info = null;
            if (aep != null)
            {
                // Устройство не из Bluetooth или не подключено — пропускаем.
                if (!aep.TryGetValue(group.Key, out info) || !info.Connected) continue;
            }

            // Предпочитаем значение с корневого узла устройства (BTHLE\DEV_ / BTHENUM\DEV_).
            var best = withBattery.FirstOrDefault(n => IsRootBtNode(n.InstanceId)) ?? withBattery[0];
            string name = info?.Name
                          ?? group.FirstOrDefault(n => IsRootBtNode(n.InstanceId))?.Name
                          ?? best.Name ?? "Устройство";

            result.Add(new BatteryDevice(group.Key, name.Trim(), best.Battery!.Value, DetectKind(group, info)));
        }
        return result.OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool IsRootBtNode(string id) =>
        id.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase);

    private static DeviceKind DetectKind(IEnumerable<PnpNode> group, AepInfo? info)
    {
        var classes = new HashSet<string>(group.Select(n => n.Class ?? ""), StringComparer.OrdinalIgnoreCase);
        var ids = group.Select(n => n.InstanceId).ToList();

        if (classes.Contains("XnaComposite") || classes.Contains("XboxComposite")) return DeviceKind.Gamepad;
        if (classes.Contains("Mouse")) return DeviceKind.Mouse;
        if (classes.Contains("Keyboard")) return DeviceKind.Keyboard;
        if (classes.Contains("AudioEndpoint") || classes.Contains("MEDIA")) return DeviceKind.Headphones;
        // Сервисы гарнитуры: Hands-Free (111E), A2DP Sink (110B), Headset (1108)
        if (ids.Any(i => i.Contains("0000111E", StringComparison.OrdinalIgnoreCase) ||
                         i.Contains("0000110B", StringComparison.OrdinalIgnoreCase) ||
                         i.Contains("00001108", StringComparison.OrdinalIgnoreCase)))
            return DeviceKind.Headphones;

        return info?.CodMajor switch
        {
            2 => DeviceKind.Phone,
            4 => DeviceKind.Headphones,
            _ => DeviceKind.Generic,
        };
    }

    // ---------------- WinRT: Bluetooth association endpoints ----------------

    private static async Task<Dictionary<Guid, AepInfo>> GetBluetoothEndpointsAsync()
    {
        var props = new[] { AepIsConnected, AepContainerId, AepCodMajor };
        var selectors = new[]
        {
            BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
        };

        var map = new Dictionary<Guid, AepInfo>();
        foreach (var selector in selectors)
        {
            var list = await DeviceInformation.FindAllAsync(selector, props, DeviceInformationKind.AssociationEndpoint);
            foreach (var d in list)
            {
                if (!d.Properties.TryGetValue(AepContainerId, out var c) || c is not Guid container) continue;
                bool connected = d.Properties.TryGetValue(AepIsConnected, out var v) && v is bool b && b;
                uint? cod = d.Properties.TryGetValue(AepCodMajor, out var m) && m is uint u ? u : null;

                // Одно устройство может иметь и классический, и LE-endpoint — объединяем.
                if (map.TryGetValue(container, out var prev))
                    map[container] = prev with { Connected = prev.Connected || connected, CodMajor = prev.CodMajor ?? cod };
                else
                    map[container] = new AepInfo(container, d.Name, connected, cod);
            }
        }
        return map;
    }

    // ---------------- PnP (cfgmgr32) ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
        public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; }
    }

    private static readonly DEVPROPKEY KeyBattery = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    private static readonly DEVPROPKEY KeyContainer = new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);
    private static readonly DEVPROPKEY KeyClass = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 9);
    private static readonly DEVPROPKEY KeyFriendlyName = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
    private static readonly DEVPROPKEY KeyDeviceDesc = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);

    private const int CR_SUCCESS = 0;
    private const int CR_BUFFER_SMALL = 0x1A;
    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    private const uint DEVPROP_TYPE_BYTE = 0x3;
    private const uint DEVPROP_TYPE_GUID = 0xD;
    private const uint DEVPROP_TYPE_STRING = 0x12;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint len, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string? filter, [Out] char[] buffer, uint len, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type,
        [Out] byte[]? buffer, ref uint size, uint flags);

    private static List<PnpNode> EnumeratePnp()
    {
        var result = new List<PnpNode>();
        foreach (var id in GetPresentDeviceIds())
        {
            // Заряд Bluetooth-устройств лежит только на узлах BT-шины и их HID-детях.
            if (!(id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ||
                  id.StartsWith("HID", StringComparison.OrdinalIgnoreCase) ||
                  id.StartsWith("SWD", StringComparison.OrdinalIgnoreCase) ||
                  id.StartsWith("{", StringComparison.Ordinal)))
                continue;

            if (CM_Locate_DevNodeW(out uint inst, id, 0) != CR_SUCCESS) continue;
            var node = new PnpNode { InstanceId = id };

            if (GetProp(inst, KeyContainer, out uint t, out var buf) && t == DEVPROP_TYPE_GUID && buf.Length >= 16)
                node.Container = new Guid(buf.AsSpan(0, 16));
            if (GetProp(inst, KeyBattery, out t, out buf) && t == DEVPROP_TYPE_BYTE && buf.Length >= 1)
                node.Battery = buf[0];
            if (GetProp(inst, KeyClass, out t, out buf) && t == DEVPROP_TYPE_STRING)
                node.Class = DecodeString(buf);
            if (GetProp(inst, KeyFriendlyName, out t, out buf) && t == DEVPROP_TYPE_STRING)
                node.Name = DecodeString(buf);
            else if (GetProp(inst, KeyDeviceDesc, out t, out buf) && t == DEVPROP_TYPE_STRING)
                node.Name = DecodeString(buf);

            result.Add(node);
        }
        return result;
    }

    private static IEnumerable<string> GetPresentDeviceIds()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint len, null, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS) break;
            var buffer = new char[len];
            int cr = CM_Get_Device_ID_ListW(null, buffer, len, CM_GETIDLIST_FILTER_PRESENT);
            if (cr == CR_BUFFER_SMALL) continue;
            if (cr != CR_SUCCESS) break;
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        return Array.Empty<string>();
    }

    private static bool GetProp(uint inst, DEVPROPKEY key, out uint type, out byte[] data)
    {
        data = Array.Empty<byte>();
        uint size = 0;
        int cr = CM_Get_DevNode_PropertyW(inst, ref key, out type, null, ref size, 0);
        if (cr != CR_BUFFER_SMALL || size == 0) return false;
        data = new byte[size];
        return CM_Get_DevNode_PropertyW(inst, ref key, out type, data, ref size, 0) == CR_SUCCESS;
    }

    private static string DecodeString(byte[] buf) => Encoding.Unicode.GetString(buf).TrimEnd('\0');

    // ---------------- Диагностика ----------------

    public static async Task<string> Diagnose()
    {
        var sb = new StringBuilder();
        var nodes = EnumeratePnp();
        sb.AppendLine("=== PnP-узлы с зарядом ===");
        foreach (var n in nodes.Where(n => n.Battery != null))
            sb.AppendLine($"{n.Battery,3}%  {n.Name}  [{n.Class}]  {n.Container}  {n.InstanceId}");

        sb.AppendLine().AppendLine("=== Bluetooth endpoints ===");
        Dictionary<Guid, AepInfo>? aep = null;
        try
        {
            aep = await GetBluetoothEndpointsAsync();
            foreach (var a in aep.Values)
                sb.AppendLine($"{(a.Connected ? "подключено " : "отключено  ")} {a.Name}  cod={a.CodMajor}  {a.Container}");
        }
        catch (Exception ex) { sb.AppendLine("Ошибка: " + ex.Message); }

        var dongles = ReadDongles(sb);

        sb.AppendLine().AppendLine("=== Итог (что покажет панель) ===");
        foreach (var d in Merge(nodes, aep).Concat(dongles))
            sb.AppendLine($"{d.Kind,-10} {d.Level,3}%  {d.Name}");
        return sb.ToString();
    }
}
