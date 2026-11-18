using System.Security.Cryptography;
using System.Text;

namespace BatteryBar;

/// <summary>
/// Мыши на платформе Compx (Pulsar X2 V2 / Xlite V3, ATK / VXE, Kysona и другие бренды на тех же чипах).
/// Протокол — по python-pulsar-mouse-tool (github.com/andrewrabert/python-pulsar-mouse-tool, MIT)
/// и драйверу Linux hid-kysona: 17-байтовые пакеты [0x08][команда][данные...][контрольная сумма].
/// Ответ принимается только с правильным заголовком и контрольной суммой, поэтому чужие устройства
/// с тем же VID не дадут ложных значений.
/// </summary>
internal static class CompxReader
{
    private static readonly ushort[] Vids = { 0x3554, 0x373B };
    private const int PacketLen = 17;
    private const byte ReportId = 0x08, CmdStatus = 0x03, CmdPower = 0x04;

    public static List<BatteryDevice> Read(StringBuilder? log)
    {
        var result = new List<BatteryDevice>();
        var done = new HashSet<string>();
        foreach (var vid in Vids)
        foreach (var info in Hid.Enumerate(vid))
        {
            if (info.OutLen != PacketLen || info.InLen != PacketLen) continue;
            if (done.Contains(info.DeviceKey)) continue;

            using var ch = HidChannel.Open(info);
            if (ch == null) { log?.AppendLine($"  {info.Product} {info.Vid:X4}:{info.Pid:X4}: не удалось открыть"); continue; }

            // STATUS: [6] = 1 — мышь на связи с донглом, 0 — спит/выключена.
            var status = Request(ch, info, CmdStatus);
            if (status == null) { log?.AppendLine($"  {info.Product} {info.Vid:X4}:{info.Pid:X4}: нет ответа"); continue; }
            if (status[6] != 1) { log?.AppendLine($"  {info.Product}: мышь не на связи"); continue; }

            // POWER: [6] заряд %, [7] подключено питание, [8..9] напряжение мВ (big-endian).
            var power = Request(ch, info, CmdPower);
            log?.AppendLine($"  {info.Product} {info.Vid:X4}:{info.Pid:X4}: {(power != null ? $"{power[6]}% питание={power[7]}" : "нет ответа")}");
            if (power == null || power[6] > 100) continue;

            done.Add(info.DeviceKey);
            string name = string.IsNullOrWhiteSpace(info.Product) ? "Беспроводная мышь" : info.Product!;
            result.Add(new BatteryDevice(new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"compx:{info.DeviceKey}"))),
                name, power[6], DeviceKind.Mouse));
        }
        return result;
    }

    private static byte[]? Request(HidChannel ch, HidInfo info, byte cmd)
    {
        var req = new byte[PacketLen];
        req[0] = ReportId;
        req[1] = cmd;
        req[16] = Checksum(req);

        // Устройство слушает только control-канал (SET_REPORT), поэтому HidD_SetOutputReport, а не WriteFile.
        ch.BeginRead();
        if (!Hid.SetOutputReport(info.Path, req)) return null;
        return Hid.ReadMatching(new[] { ch },
            r => r.Length >= PacketLen && r[0] == ReportId && r[1] == cmd && r[16] == Checksum(r),
            500);
    }

    private static byte Checksum(byte[] p)
    {
        int sum = 0;
        for (int i = 0; i < 16; i++) sum += p[i];
        return (byte)(0x55 - sum);
    }
}
