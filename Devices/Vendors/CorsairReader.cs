using System.Security.Cryptography;
using System.Text;

namespace BatteryBar;

/// <summary>
/// Беспроводные мыши Corsair через донгл Slipstream (протокол Bragi).
/// Формат команд — по открытым проектам ckb-next и OpenLinkHub.
/// Устройство не переводится в «программный режим», чтобы не трогать подсветку и iCUE:
/// если прошивка без этого не отдаёт заряд, устройство просто не показывается.
/// </summary>
internal static class CorsairReader
{
    private const ushort Vid = 0x1B1C;
    private const byte BragiMagic = 0x08, BragiGet = 0x02;
    private const byte PropBatteryLevel = 0x0F, PropPid = 0x12, PropSubdevices = 0x36;

    private static readonly HashSet<ushort> DonglePids = new()
    {
        0x1BA6, 0x1B65, 0x1B66, 0x1BDC, 0x2B00, 0x1B81, 0x1B7F,
    };

    private static readonly Dictionary<ushort, string> KnownChildren = new()
    {
        [0x1B5E] = "Corsair Harpoon RGB Wireless",
        [0x1BB5] = "Corsair M65 RGB Ultra Wireless",
        [0x1BF1] = "Corsair M75 Wireless",
    };

    public static List<BatteryDevice> Read(StringBuilder? log)
    {
        var result = new List<BatteryDevice>();
        var done = new HashSet<string>();
        foreach (var info in Hid.Enumerate(Vid))
        {
            if (!DonglePids.Contains(info.Pid) || info.UsagePage != 0xFF42 || info.OutLen <= 4) continue;
            if (!done.Add(info.DeviceKey)) continue;

            using var ch = HidChannel.Open(info);
            if (ch == null) { log?.AppendLine($"  pid={info.Pid:X4}: не удалось открыть"); continue; }

            // Какие беспроводные устройства сейчас на связи с донглом (биты 1..7).
            uint? bits = GetProperty(ch, BragiMagic, PropSubdevices);
            log?.AppendLine($"  донгл pid={info.Pid:X4}: подустройства={(bits?.ToString("X2") ?? "нет ответа")}");
            var children = bits is uint b
                ? Enumerable.Range(1, 7).Where(i => (b & (1u << i)) != 0).Select(i => (byte)(BragiMagic | i)).ToList()
                : new List<byte> { BragiMagic | 1 }; // по умолчанию первый слот

            foreach (var child in children)
            {
                uint? raw = GetProperty(ch, child, PropBatteryLevel);
                log?.AppendLine($"    слот 0x{child:X2}: заряд(raw)={raw?.ToString() ?? "нет ответа"}");
                if (raw is not uint r || r > 1000) continue;

                uint? pid = GetProperty(ch, child, PropPid);
                string name = pid is uint p && KnownChildren.TryGetValue((ushort)p, out var n)
                    ? n
                    : "Corsair (беспроводная)";
                result.Add(new BatteryDevice(
                    new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"corsair:{info.DeviceKey}:{child}"))),
                    name, (int)Math.Round(r / 10.0), DeviceKind.Mouse));
            }
        }
        return result;
    }

    /// <summary>Bragi GET: [report id 0][адрес][0x02][свойство]. Ответ: [адрес][0x02][ошибка][значение LE...].</summary>
    private static uint? GetProperty(HidChannel ch, byte address, byte property)
    {
        if (!ch.Write(new byte[] { 0x00, address, BragiGet, property, 0x00 })) return null;
        uint? value = null;
        Hid.ReadMatching(new[] { ch }, d =>
        {
            var p = d.Length > 0 && d[0] == 0 ? d[1..] : d;
            if (p.Length < 6 || p[1] != BragiGet) return false;
            if (p[2] != 0) return true; // ошибка (например, свойство не поддерживается)
            value = (uint)(p[3] | (p[4] << 8) | (p[5] << 16));
            return true;
        }, 400);
        return value;
    }
}
