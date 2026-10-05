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
[DefaultExecutionOrder(200)] // 晚于 FootPlantingController(150)，确保拿到本帧解算后的快照
public class FootIkUdpSender : MonoBehaviour
{
    [Header("Target")]
    public string host = "127.0.0.1";
    public int port = 8890;
    [Range(1, 60)] public float sendHz = 10f;

    [Header("Auto-link")]
    public FootPlantingController controller;   // 可指定；为空则自动 FindObjectOfType

    [Header("Capture to file (排查用，默认关闭)")]
    [Tooltip("把同一份诊断 JSON 逐包追加到 captureDir/footlog.jsonl。"
           + "8890 端口可能已被桌面监看占用，落盘不依赖任何监听器先启动，排查更稳。")]
    public bool enableCapture = false;
    [Tooltip("落盘目录。留空则写到 Application.persistentDataPath/footlog。"
           + "只用 ASCII 路径——cv2/文件 API 对非 ASCII 路径会静默失败")]
    public string captureDir = "";

    private UdpClient _udp;
    private float _sendAccum;
    private int _seq;
    private System.IO.StreamWriter _cap;
    private string _capPath;
    private int _capLines;

    void OnEnable()
    {
        // 10Hz 对步态排查太粗：vEvidenceTime/stepDevTime 只有 0.06s，一个采样间隔就跨过去了，
        // 证据计时器怎么涨起来的完全看不见。提到 50Hz（≈每 20ms 一包）。
        if (sendHz < 40f)
        {
            Debug.LogWarning($"[FootIkUdp] sendHz={sendHz:F0} 过粗（0.06s 级的证据计时器看不见）→ 提升为 50Hz。");
            sendHz = 50f;
        }
        InitSocket();
        InitCapture();
    }
    void OnDisable() { CloseSocket(); CloseCapture(); }
    void OnDestroy() { CloseSocket(); CloseCapture(); }
    void OnApplicationQuit() { CloseSocket(); CloseCapture(); }

    void InitCapture()
    {
        if (!enableCapture || _cap != null) return;
        // captureDir 留空时落到 Unity 的持久化目录，避免把任何人的本机路径写进仓库
        string dir = string.IsNullOrEmpty(captureDir)
            ? System.IO.Path.Combine(Application.persistentDataPath, "footlog")
            : captureDir;
        try
        {
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            // 每次进 Play 新建一个文件，避免和上一轮的记录混在一起
            _capPath = System.IO.Path.Combine(dir,
                $"footlog_{System.DateTime.Now:MMdd_HHmmss}.jsonl");
            _cap = new System.IO.StreamWriter(_capPath, false, System.Text.Encoding.UTF8);
            _capLines = 0;
            Debug.Log($"[FootIkUdp] 诊断落盘 → {_capPath}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[FootIkUdp] 落盘初始化失败（{dir}）: {ex.Message}");
            _cap = null;
        }
    }

    void CloseCapture()
    {
        if (_cap == null) return;
        try { _cap.Flush(); _cap.Close(); _cap.Dispose(); } catch { }
        _cap = null;
        if (_capPath != null) Debug.Log($"[FootIkUdp] 落盘结束：{_capLines} 行 → {_capPath}");
    }

    void InitSocket()
    {
        if (_udp != null) return;
        if (controller == null) controller = FindObjectOfType<FootPlantingController>();
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

        string json = BuildJson();
        if (json == null) return;
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(json);
        try { _udp.Send(payload, payload.Length); }
        catch (Exception) { /* 发送失败不阻塞 */ }

        if (_cap != null)
        {
            try
            {
                _cap.WriteLine(json);
                _capLines++;
                if ((_capLines & 15) == 0) _cap.Flush();   // 每 16 行刷一次，20ms 一行 ≈ 0.32s
            }
            catch (Exception) { CloseCapture(); }
        }
    }

    string BuildJson()
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
            ["t"] = System.Math.Round(Time.realtimeSinceStartupAsDouble, 4),   // 落盘/分析用的时间轴
            ["fps"] = System.Math.Round(1.0 / Mathf.Max(Time.deltaTime, 1e-4f), 1),
            ["front_age_ms"] = Null(),
            ["squat"] = null,
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
        // ★ 上面那行在 controller.reachLimited 时会被子控制器的 reachReason 整个顶掉，
        //   下蹲自己的 SquatLimitReason 就看不见了（实测本采集 620 帧被吞，占 19.5%，
        //   而「单脚支撑，保持高度」这个分支全段一次都没露过面）。这里单独留一份，
        //   不改上面那行的优先序，向下兼容既有分析脚本。
        d["squat_limit_reason"] = pd.SquatLimitReason;
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
            ["phase"] = ikEnabled ? d.phase : "—",
            ["pitch_deg"] = ikEnabled ? Deg(d.pitchDeg) : Null(),
            ["slide_cms"] = ikEnabled && d.hasAnchor ? Cms(d.slideSpeed) : Null(),
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
            // ── v5：定位滑步到底出在「锚点没诞生」还是「锚点诞生但够不到」──
            ["h_speed_cms"] = ikEnabled ? Cms(d.hSpeed) : Null(),
            ["cand_time_ms"] = ikEnabled ? Ms(d.candTime) : (object)null,
            ["cand_count"] = ikEnabled ? (object)d.candCount : null,
            ["cand_low"] = d.candLow && ikEnabled,
            ["cand_slow"] = d.candSlow && ikEnabled,
            ["cand_ground"] = d.candGround && ikEnabled,   // v6 触地判据
            ["v_speed_cms_signed"] = ikEnabled ? Cms(d.vSpeed) : Null(),
            ["win_span_ms"] = ikEnabled ? Ms(d.winSpan) : (object)null,
            ["win_drop"] = d.winDrop,
            ["win_dup"] = d.winDup,
            ["leg_reach_cm"] = Cm(d.legReach),
            ["servo_corr_mm"] = ikEnabled ? Math.Round(d.servoCorr * 1000f, 2) : (object)null,
            ["anchor"] = d.hasAnchor ? Vec(d.anchor) : null,
            ["foot"] = Vec(d.footPos),
            ["target"] = Vec(d.target),
            ["root"] = Vec(d.rootPos),
            ["hip"] = Vec(d.hipPos),
        };
        return dict;
    }

    static object Vec(UnityEngine.Vector3 v) =>
        new Dictionary<string, object>
        {
            ["x"] = Math.Round(v.x, 4),
            ["y"] = Math.Round(v.y, 4),
            ["z"] = Math.Round(v.z, 4),
        };

    static object Cm(float m) => float.IsFinite(m) ? Math.Round(m * 100f, 1) : Null();
    static object Cms(float ms) => float.IsFinite(ms) ? Math.Round(ms * 100f, 1) : Null();
    static object Ms(float s) => float.IsFinite(s) ? Math.Round(s * 1000f, 0) : Null();
    static object Deg(float v) => float.IsFinite(v) ? Math.Round(v, 0) : Null();
    static object R2(float v) => float.IsFinite(v) ? Math.Round(v, 2) : Null();
    static object R3(float v) => float.IsFinite(v) ? Math.Round(v, 3) : Null();
    static object Fnum(float v, int digits) => float.IsFinite(v) ? Math.Round(v, digits) : Null();
    static object Null() => null;

    string Encode(Dictionary<string, object> msg)
    {
        try { return JsonConvert.SerializeObject(msg); }
        catch (Exception) { return null; }
    }
}
