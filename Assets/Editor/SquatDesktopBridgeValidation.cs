using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using MotionStudio.SquatAnalysis;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class SquatDesktopBridgeValidation
{
    public static void InstallAndValidateBatch()
    {
        SquatAnalysisSetup.InstallAndSaveForBatch();
        Validate();
    }

    // Local synthetic protocol check. No camera, MediaPipe or real pose socket is opened.
    public static void Validate()
    {
        string reportPath = null;
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-bridgeValidationReport") reportPath = args[i + 1];
        if (reportPath == null) throw new ArgumentException("A -bridgeValidationReport path is required.");

        var host = new GameObject("SquatDesktopBridgeValidation");
        try
        {
            var analysis = host.AddComponent<SquatAnalysisController>();
            analysis.SendMessage("Awake", SendMessageOptions.RequireReceiver);
            var bridge = host.AddComponent<SquatDesktopBridge>();
            bridge.analysis = analysis;
            bridge.commandPort = 0;
            bridge.StartListening();
            Require(bridge.IsListening, "Loopback control listener did not start.");
            var sender = host.AddComponent<FootIkUdpSender>();
            sender.analysis = analysis;
            sender.controller = null;
            using (var client = new UdpClient())
            {
                Send(client, bridge, analysis.SessionId, "start-check", "start");
                Require(analysis.IsRunning && bridge.LastCommandSucceeded, "Start was not applied.");
                var counter = (SquatCycleCounter)typeof(SquatAnalysisController)
                    .GetField("counter", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(analysis);
                double now = 1;
                Hold(counter, ref now, 5, 9);
                for (int i = 0; i < 5; i++)
                {
                    Hold(counter, ref now, 35, 5);
                    Hold(counter, ref now, 80, 7);
                    Hold(counter, ref now, 45, 5);
                    Hold(counter, ref now, 5, 9);
                }
                Require(analysis.CompletedRepetitions == 5, "Synthetic cycles did not reach five.");
                JObject before = Snapshot(sender);

                Send(client, bridge, analysis.SessionId, "pause-check", "pause");
                Require(!analysis.IsRunning && analysis.CompletedRepetitions == 5, "Pause lost completed counts.");
                Send(client, bridge, analysis.SessionId, "start-check", "start");
                Require(!analysis.IsRunning, "A repeated start restarted analysis.");
                Send(client, bridge, analysis.SessionId, "reset-check", "reset");
                Require(analysis.CompletedRepetitions == 0 && !analysis.IsRunning, "Reset did not clear counts.");
                JObject after = Snapshot(sender);
                var report = new JObject
                {
                    ["synthetic"] = true, ["udp_loopback"] = true,
                    ["start"] = true, ["pause"] = true, ["duplicate_ignored"] = true, ["reset"] = true,
                    ["before_reset"] = before, ["after_reset"] = after
                };
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));
                File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
                Debug.Log("[SquatDesktopValidation] Loopback start/pause/reset and duplicate handling passed.");
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void Hold(SquatCycleCounter counter, ref double now, float angle, int frames)
    {
        for (int i = 0; i < frames; i++) { counter.ProcessSample(now, angle); now += 0.05; }
    }

    private static void Send(UdpClient client, SquatDesktopBridge bridge, string session, string id, string command)
    {
        var packet = new JObject
        {
            ["type"] = "squat_analysis_command", ["version"] = 1,
            ["session"] = session, ["id"] = id, ["command"] = command
        };
        byte[] bytes = Encoding.UTF8.GetBytes(packet.ToString());
        client.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, bridge.BoundPort));
        for (int i = 0; i < 50; i++)
        {
            bridge.ProcessPendingCommands();
            if (bridge.LastCommandId == id) return;
            Thread.Sleep(5);
        }
        throw new InvalidOperationException("No command was processed: " + command);
    }

    private static JObject Snapshot(FootIkUdpSender sender)
    {
        var bytes = (byte[])typeof(FootIkUdpSender).GetMethod("BuildPayload",
            BindingFlags.NonPublic | BindingFlags.Instance).Invoke(sender, null);
        return JObject.Parse(Encoding.UTF8.GetString(bytes));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
