using System;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// 把 FootPlantingController 的脚锁诊断快照经 UDP (127.0.0.1:8890) 回传给 Python 桌面监看。
/// - 自动关联场景中的 FootPlantingController，无需手工拖引用。
/// - 在脚锁求解完成后（本组件执行顺序晚于 FootPlantingController）以 10 Hz 发送本帧诊断。
/// - 非阻塞 UDP；发送失败不阻塞姿态驱动；禁用/启用可重建 socket；退出时关闭。
/// - 控制器缺失/禁用/IK 关闭时仍回传对应状态；序号由本组件每次发送递增（读取快照不改变序号）。
/// </summary>
[DefaultExecutionOrder(1200)] // 晚于脚锁与深蹲计数，发送本帧状态
public class FootIkUdpSender : MonoBehaviour
{
    [Header("Target")]
    public string host = "127.0.0.1";
    public int port = 8890;
    [Range(1, 60)] public float sendHz = 10f;

    [Header("Auto-link")]
    public FootPlantingController controller;   // 可指定；为空则自动 FindObjectOfType
    public SquatAnalysisController analysis;

    private UdpClient _udp;
    private float _sendAccum;
    private int _seq;

    void OnEnable() { InitSocket(); }
    void OnDisable() { CloseSocket(); }
    void OnDestroy() { CloseSocket(); }
    void OnApplicationQuit() { CloseSocket(); }

    void InitSocket()
    {
        if (_udp != null) return;
        if (controller == null) controller = FindObjectOfType<FootPlantingController>();
        if (analysis == null) analysis = FindObjectOfType<SquatAnalysisController>();
        try
        {
            _udp = new UdpClient();
            _udp.Connect(host, port);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[FootIkUdp] UDP 发送器初始化失败: {ex.Message}");
            _udp = null;
        }
    }

    void CloseSocket()
    {
        if (_udp != null) { try { _udp.Close(); } catch { } try { _udp.Dispose(); } catch { } _udp = null; }
    }

    // 在脚锁解算完成后的 LateUpdate 发送，保证是本帧数据
    void LateUpdate()
    {
        if (_udp == null) InitSocket();
        if (_udp == null) return;

        float interval = 1f / Mathf.Max(1f, sendHz);
        _sendAccum += Time.deltaTime;
        if (_sendAccum < interval) return;
        _sendAccum = 0f;

        byte[] payload = BuildPayload();
        if (payload == null) return;
        try { _udp.Send(payload, payload.Length); }
        catch (Exception) { /* 发送失败不阻塞 */ }
    }

    byte[] BuildPayload()
    {
        var msg = new Dictionary<string, object>
        {
            ["type"] = "foot_ik_diagnostics",
            ["version"] = 1,
            ["controller"] = "missing",
            ["ik_enabled"] = false,
            ["published"] = false,
            ["session"] = controller != null ? controller.GetDiagnostics().session : "-",
            ["seq"] = ++_seq,
            ["front_age_ms"] = Null(),
            ["squat"] = null,
            ["squat_analysis"] = AnalysisToDict(),
        };

        if (controller == null)
        {
            return Encode(msg);
        }

        var snap = controller.GetDiagnostics();
        msg["session"] = snap.session;
        msg["seq"] = ++_seq; // 每次发送递增
        msg["ik_enabled"] = snap.ikEnabled;
        msg["controller"] = snap.controllerEnabled ? (snap.ikEnabled ? "active" : "disabled") : "disabled";
        msg["published"] = snap.published;
        msg["front_age_ms"] = Fnum(snap.frontAgeMs, 0); // 已是毫秒，不再乘 1000

        msg["left"] = FootToDict(snap.left, snap.ikEnabled);
        msg["right"] = FootToDict(snap.right, snap.ikEnabled);
        msg["squat"] = SquatToDict();
        return Encode(msg);
    }

    Dictionary<string, object> AnalysisToDict()
    {
        if (analysis == null) return null;
        var bridge = analysis.GetComponent<SquatDesktopBridge>();
        return new Dictionary<string, object>
        {
            ["session"] = analysis.SessionId,
            ["enabled"] = analysis.isActiveAndEnabled,
            ["completed_repetitions"] = analysis.CompletedRepetitions,
            ["phase"] = analysis.Phase.ToString(),
            ["is_running"] = analysis.IsRunning,
            ["valid_measurements"] = analysis.HasValidMeasurements,
            ["left_flexion_deg"] = analysis.HasValidMeasurements ? Deg(analysis.EstimatedLeftFlexion) : null,
            ["right_flexion_deg"] = analysis.HasValidMeasurements ? Deg(analysis.EstimatedRightFlexion) : null,
            ["message"] = analysis.TrackingMessage,
            ["baseline"] = analysis.driver != null && analysis.driver.BaselineEstablished,
            ["control_ready"] = analysis.isActiveAndEnabled && bridge != null &&
                bridge.isActiveAndEnabled && bridge.IsListening,
            ["control_port"] = bridge != null ? bridge.BoundPort : 0,
            ["control_error"] = bridge != null ? bridge.ConnectionError : "桌面控制组件缺失",
            ["last_command_id"] = bridge != null ? bridge.LastCommandId : null,
            ["last_command_success"] = bridge != null && bridge.LastCommandSucceeded,
            ["last_command_message"] = bridge != null ? bridge.LastCommandMessage : null,
        };
    }

    Dictionary<string, object> SquatToDict()
    {
        var pd = controller != null ? controller.poseDriver : null;
        var d = new Dictionary<string, object>
        {
            ["baseline"] = false,
            ["fail_reason"] = "",
            ["left_knee"] = null,
            ["right_knee"] = null,
            ["left_conf"] = null,
            ["right_conf"] = null,
            ["target_cm"] = null,
            ["actual_cm"] = null,
            ["limit_reason"] = "",
            ["paused"] = false,
            ["valid"] = false,
        };
        if (pd == null) return d;
        d["baseline"] = pd.BaselineEstablished;
        d["fail_reason"] = pd.SquatFailReason;
        d["left_knee"] = Deg(pd.LeftKneeAngleDeg);
        d["right_knee"] = Deg(pd.RightKneeAngleDeg);
        d["left_conf"] = R2(pd.LeftLegConfidence);
        d["right_conf"] = R2(pd.RightLegConfidence);
        d["target_cm"] = Cm(pd.SquatTargetDescent);
        d["actual_cm"] = Cm(pd.DescentMeters);
        d["limit_reason"] = (controller != null && controller.reachLimited)
            ? controller.reachReason
            : pd.SquatLimitReason;
        d["paused"] = pd.SquatPaused;
        d["valid"] = pd.SquatDataValid;
        return d;
    }

    Dictionary<string, object> FootToDict(FootPlantingController.FootDiag d, bool ikEnabled)
    {
        var dict = new Dictionary<string, object>
        {
            ["status"] = ikEnabled ? d.status : "—",
            ["reason"] = ikEnabled ? d.releaseReason : "IK 已关闭",
            ["has_anchor"] = d.hasAnchor && ikEnabled,
            ["anchor_id"] = d.hasAnchor && ikEnabled ? d.anchorId : (object)null,
            ["planted"] = ikEnabled ? R3(d.planted) : (object)null,
            ["lift_rise_cm"] = ikEnabled && d.hasAnchor ? Cm(d.liftRise) : Null(),
            ["anchor_dev_cm"] = ikEnabled && d.hasAnchor ? Cm(d.anchorDev) : Null(),
            ["hip_to_anchor_cm"] = ikEnabled && d.hasAnchor ? Cm(d.hipToAnchor) : Null(),
            ["ankle_to_anchor_cm"] = ikEnabled && d.hasAnchor ? Cm(d.ankleToAnchor) : Null(),
            ["v_speed_cms"] = ikEnabled ? Cms(d.vSpeed) : Null(),
            ["unreach"] = d.unreach && ikEnabled,
            ["unreach_ms"] = ikEnabled ? Ms(d.unreachTimeAcc) : (object)null,
            ["contact"] = d.contact && ikEnabled,
        };
        return dict;
    }

    static object Cm(float m) => float.IsFinite(m) ? Math.Round(m * 100f, 1) : Null();
    static object Cms(float ms) => float.IsFinite(ms) ? Math.Round(ms * 100f, 1) : Null();
    static object Ms(float s) => float.IsFinite(s) ? Math.Round(s * 1000f, 0) : Null();
    static object Deg(float v) => float.IsFinite(v) ? Math.Round(v, 0) : Null();
    static object R2(float v) => float.IsFinite(v) ? Math.Round(v, 2) : Null();
    static object R3(float v) => float.IsFinite(v) ? Math.Round(v, 3) : Null();
    static object Fnum(float v, int digits) => float.IsFinite(v) ? Math.Round(v, digits) : Null();
    static object Null() => null;

    byte[] Encode(Dictionary<string, object> msg)
    {
        try
        {
            string json = JsonConvert.SerializeObject(msg);
            return System.Text.Encoding.UTF8.GetBytes(json);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
