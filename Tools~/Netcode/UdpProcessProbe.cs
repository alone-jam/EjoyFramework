using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using EjoyFramework.GamePlay.Netcode.Transport;

internal static class UdpProcessProbe
{
    private const int Messages = 10, Bytes = 65000;
    private static byte[] Payload(int role, int index)
    {
        var data = new byte[Bytes]; data[0] = (byte)role; data[1] = (byte)index;
        for (int i = 2; i < data.Length; i++) data[i] = (byte)(role * 31 + index * 17 + i);
        return data;
    }
    private static bool Valid(ArraySegment<byte> data, int role, int index)
    {
        if (data.Count != Bytes || data.Array[data.Offset] != role || data.Array[data.Offset + 1] != index) return false;
        for (int i = 2; i < data.Count; i++) if (data.Array[data.Offset + i] != (byte)(role * 31 + index * 17 + i)) return false;
        return true;
    }
    public static int Main(string[] args)
    {
        if (args[0].StartsWith("authority-", StringComparison.Ordinal)) return UdpAuthorityProbe.Run(args);
        string mode = args[0], report = args[2]; int port = int.Parse(args[1]);
        using var transport = new UdpNetTransport(maxPeers: 2, retrySeconds: 0.04, timeoutSeconds: 4);
        int received = 0, connected = 0, disconnected = 0;
        bool correct = true, sent = false;
        var counts = new Dictionary<int, int>();
        int role = mode == "server" ? 0 : int.Parse(args[3]), interval = role == 1 ? 5 : 17;
        transport.OnClientConnected += id =>
        {
            connected++; counts[id] = 0;
            if (role != 0)
            {
                for (int i = 0; i < Messages; i++) transport.Send(id, new ArraySegment<byte>(Payload(role, i)));
                sent = true;
            }
        };
        transport.OnClientDisconnected += id => disconnected++;
        transport.OnData += (id, data) =>
        {
            int expected = counts[id];
            int sourceRole = role == 0 ? data.Array[data.Offset] : role;
            correct &= sourceRole >= 1 && sourceRole <= 2 && Valid(data, sourceRole, expected);
            counts[id]++; received++;
            if (role == 0) transport.Send(id, data);
        };
        if (role == 0)
        {
            transport.StartServer(port);
            File.WriteAllText(report + ".ready", JsonSerializer.Serialize(new { port = transport.LocalPort, pid = Environment.ProcessId, address = "127.0.0.1" }));
        }
        else transport.Connect("127.0.0.1", port);
        var watch = Stopwatch.StartNew();
        bool finished = false;
        while (watch.Elapsed.TotalSeconds < 25)
        {
            transport.Poll();
            if (role != 0 && sent && received == Messages) { transport.Disconnect(); transport.Poll(); finished = true; break; }
            if (role == 0 && received == Messages * 2 && disconnected == 2) { finished = true; break; }
            Thread.Sleep(role == 0 ? 2 : interval);
        }
        bool passed = finished && correct && transport.CallbackFailures == 0 && connected == (role == 0 ? 2 : 1);
        File.WriteAllText(report, JsonSerializer.Serialize(new { mode, role, pid = Environment.ProcessId, connected, disconnected, received, passed,
            elapsedSeconds = watch.Elapsed.TotalSeconds, pollIntervalMs = role == 0 ? 2 : interval, retransmissions = transport.Retransmissions,
            rejected = transport.RejectedDatagrams, callbackFailures = transport.CallbackFailures }));
        return passed ? 0 : 1;
    }
}
