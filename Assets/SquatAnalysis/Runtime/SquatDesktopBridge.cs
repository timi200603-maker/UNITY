using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MotionStudio.SquatAnalysis;
using Newtonsoft.Json.Linq;
using UnityEngine;

[DefaultExecutionOrder(1100)]
[DisallowMultipleComponent]
public sealed class SquatDesktopBridge : MonoBehaviour
{
    public SquatAnalysisController analysis;
    public int commandPort = 8891;
    private UdpClient socket;
    private SquatCommandGate commandGate;
    public bool IsListening { get { return socket != null; } }
    public int BoundPort { get { return socket == null ? 0 : ((IPEndPoint)socket.Client.LocalEndPoint).Port; } }
    public string ConnectionError { get; private set; }
    public string LastCommandId { get; private set; }
    public bool LastCommandSucceeded { get; private set; }
    public string LastCommandMessage { get; private set; }

    private void OnEnable() { if (Application.isPlaying) StartListening(); }
    private void OnDisable() { CloseSocket(); }
    private void OnDestroy() { CloseSocket(); }
    private void OnApplicationQuit() { CloseSocket(); }
    private void Update() { ProcessPendingCommands(); }

    public void StartListening()
    {
        if (socket != null) return;
        if (analysis == null) analysis = GetComponent<SquatAnalysisController>();
        if (analysis == null) { ConnectionError = "深蹲组件缺失"; return; }
        commandGate = new SquatCommandGate(analysis.SessionId);
        try
        {
            socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, commandPort));
            socket.Client.Blocking = false;
            ConnectionError = null;
        }
        catch (Exception exception)
        {
            CloseSocket();
            ConnectionError = "深蹲控制连接失败：" + exception.Message;
            Debug.LogWarning("[SquatDesktop] " + ConnectionError, this);
        }
    }

    public void ProcessPendingCommands()
    {
        if (socket == null || analysis == null) return;
        // Bounded main-thread work; commands never touch camera or pose worker threads.
        for (int i = 0; i < 8 && socket.Available > 0; i++)
        {
            try
            {
                IPEndPoint peer = new IPEndPoint(IPAddress.Any, 0);
                byte[] payload = socket.Receive(ref peer);
                if (!IPAddress.IsLoopback(peer.Address) || payload.Length > 1024) continue;
                var request = JObject.Parse(Encoding.UTF8.GetString(payload));
                if ((string)request["type"] != "squat_analysis_command" ||
                    request["version"]?.Type != JTokenType.Integer || (int)request["version"] != 1 ||
                    request["session"]?.Type != JTokenType.String ||
                    request["id"]?.Type != JTokenType.String ||
                    request["command"]?.Type != JTokenType.String) continue;
                string id = (string)request["id"];
                string command = (string)request["command"];
                var decision = commandGate.Evaluate((string)request["session"], id, command);
                if (decision == SquatCommandDecision.WrongSession ||
                    decision == SquatCommandDecision.Invalid) continue;
                LastCommandId = id;
                if (decision == SquatCommandDecision.Duplicate) continue;
                LastCommandSucceeded = analysis.isActiveAndEnabled;
                if (!LastCommandSucceeded)
                {
                    LastCommandMessage = "深蹲组件已停用";
                    continue;
                }
                if (command == "start") analysis.StartSession();
                else if (command == "pause") analysis.PauseSession();
                else analysis.ResetSession();
                LastCommandMessage = command == "start" ? "已开始深蹲分析"
                    : command == "pause" ? "已暂停深蹲分析" : "次数已清零";
            }
            catch (SocketException) { break; }
            catch (Exception) { /* Malformed datagrams are ignored. */ }
        }
    }

    private void CloseSocket()
    {
        if (socket == null) return;
        socket.Close();
        socket = null;
    }
}
