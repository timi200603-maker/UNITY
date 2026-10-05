using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 双摄 Root 运动估计器 v7 — 滑动时间窗求速度
/// ======================================
/// v7 变更（相对 v6）★ 抗打滑核心：
///   - **不再用单包差分求速度**。单包差分把关键点抖动直接放大：(±3cm)/(1/30s) = ±0.9 m/s。
///     前摄脚跟中点在"双脚跟中点"上换脚/遮挡时单包能跳 20~37cm → 3~11 m/s 的假冲量，
///     身体被这个假速度猛推一下又反向拉回 → 支撑脚被拖走 = 打滑。
///   - 改成**滑动时间窗首尾差分**（velocityWindow 秒）：抖动按 1/√N 衰减，真实位移线性保留。
///   - **速度闸 maxStepSpeed**：单包位移/包间dt 超过该值判为跳变（换脚/误匹配/遮挡回跳），
///     丢弃该样本并清窗重锚，而不是把它变成身体冲量。
///   - 输出速度幅值截断 maxSpeed、死区 deadZoneSpeed（防噪声积分漂移）。
/// 算法：
///   前摄 foot 世界X → 窗口速度 → dirX
///   左摄 foot 世界Z → 窗口速度 → dirZ
///   低通（只在新包执行）→ rootVelocity（非新包帧保持，防帧间速度跳动）
/// </summary>
public class DualCameraRootEstimator : MonoBehaviour
{
    [Header("References")]
    public UdpPoseReceiver frontReceiver;
    public UdpPoseReceiver leftReceiver;

    [Header("Time Sync")]
    [Tooltip("双摄帧最大允许时间差（秒）")]
    [Range(0.01f, 0.3f)] public float maxTimeDiff = 0.1f;
    [Tooltip("同一路包间最大间隔（秒），超过视为断流，速度归零")]
    [Range(0.1f, 2f)] public float maxGapTime = 0.5f;

    [Header("Filter")]
    [Range(0.02f, 0.9f)] public float lowPassAlpha = 0.25f;
    public float deadZoneMeters = 0.005f;
    [Tooltip("速度估计窗口(秒)：用窗口首尾位移求速度，而不是单包差分。\n" +
             "单包关键点抖动(±3cm)在 1/30s 上会伪装成 ±0.9 m/s，必须靠时间窗压掉。\n" +
             "窗口越长越稳但越滞后；0.20 约滞后 0.1~0.2s。")]
    [Range(0.05f, 0.6f)] public float velocityWindow = 0.20f;
    [Tooltip("窗口速度死区(m/s)，小于此值视为静止，防噪声积分漂移")]
    public float deadZoneSpeed = 0.05f;

    [Header("Safety")]
    [Tooltip("单包最大位移(m)，超过归零")]
    public float maxDeltaPerFrame = 3f;
    [Tooltip("单包位移隐含的最大速度(m/s)：超过即判为关键点跳变(换脚/遮挡/误匹配)，\n" +
             "丢弃该样本并清窗重锚 —— 而不是把它变成 3 m/s 的身体冲量。\n" +
             "正常人行走 <2 m/s，取 3.0 留足余量。")]
    public float maxStepSpeed = 3.0f;
    [Tooltip("输出速度幅值上限(m/s)")]
    public float maxSpeed = 2.0f;

    [Header("Direction Correction")]
    [Tooltip("左右(前摄 world X)位移方向反了时勾选，立即翻转")]
    public bool invertRootX = false;
    [Tooltip("前后(左摄 world Z)位移方向反了时勾选，立即翻转")]
    public bool invertRootZ = false;

    [Header("Calibration")]
    public KeyCode calibrateKey = KeyCode.C;
    public bool autoCalibrateOnStart = true;

    // ── 内部状态 ──
    private float _prevFrontWorldX, _prevLeftWorldZ;
    private double _prevFrontTime, _prevLeftTime;
    private int _lastProcessedFrameId = -1;
    private float _lastPacketWallTime = -1f;
    private bool _frontInvalidated = true;
    private bool _leftInvalidated = true;
    private float _smoothVelX, _smoothVelZ;
    private float _winVelX, _winVelZ;          // 窗口速度（低通前），仅用于 Overlay 观测
    private bool _hasLeft;

    // 滑动时间窗（每通道独立）：最近 velocityWindow 秒内的 (时间, 世界坐标)
    // 时间用 double 存：摄时间戳可能是 epoch 量级(float 只有 ~7 位有效数字，会丢到秒级精度)
    private readonly List<double> _frontWinT = new List<double>();
    private readonly List<double> _frontWinV = new List<double>();
    private readonly List<double> _leftWinT = new List<double>();
    private readonly List<double> _leftWinV = new List<double>();

    // 诊断计数：跳变/重复样本各被挡掉多少次（Overlay 显示，用来判断上游信号有多脏）
    private int _dupSkipX, _dupSkipZ, _jumpDropX, _jumpDropZ;

    // ── 通道健康度（诊断）★★★
    // 动机：Z 轴（前后）完全依赖左摄。左摄一断，Z 就恒为 0，而屏幕上一切"看起来正常"——
    // 2026-10-04 就是靠逐帧解码录屏才发现"左摄 1Hz、关键点数为 0"这个沉默故障，
    // 期间白白怀疑了三轮 Unity 侧参数。现在把它变成屏幕上的红字 + 一条 LogError。
    private float _lastValidXWallTime = -1f;   // 最后一次拿到有效 X 样本的 Time.time
    private float _lastValidZWallTime = -1f;   // 最后一次拿到有效 Z 样本的 Time.time
    private bool _zDeadLogged, _xDeadLogged;
    private float _zDeadSeconds = -1f;         // <0 表示从未拿到过有效样本
    private float _xDeadSeconds = -1f;
    private string _zDeadCause = "";
    [Tooltip("某一轴连续多久没有有效数据就判为停摆并告警（秒）")]
    public float deadChannelWarnSeconds = 1.5f;

    // 有效性去抖：可见度抖动不触发重新锚定，只有连续多包无效才判为真无效。
    private const int InvalidateAfterStreak = 3;
    private int _frontValidStreak, _frontInvalidStreak;
    private int _leftValidStreak, _leftInvalidStreak;

    // ── 输出 ──
    public Vector3 rootVelocity { get; private set; }
    public bool isCalibrated { get; private set; }

    // Debug
    public float rawDX { get; private set; }
    public float rawDZ { get; private set; }
    public Vector3 rootDelta { get { return rootVelocity * Time.deltaTime; } }

    void Start()
    {
        if (frontReceiver == null || leftReceiver == null)
        {
            var receivers = FindObjectsOfType<UdpPoseReceiver>();
            foreach (var r in receivers)
            {
                if (r.port == 8888 && frontReceiver == null) frontReceiver = r;
                else if (r.port == 8889 && leftReceiver == null) leftReceiver = r;
            }
        }

        if (frontReceiver == null)
        {
            Debug.LogError("[RootEst] ❌ 未找到前摄 (port 8888)");
            enabled = false;
            return;
        }

        _hasLeft = leftReceiver != null;
        Debug.Log($"[RootEst] ✅ v6 初始化 — {(_hasLeft ? "双摄" : "单摄")}  仅标定模式  safety={maxDeltaPerFrame}m");

        // 不再用定时器无条件校准 —— 由 LateUpdate 在 CalibActive 时才自动校准，
        // 避免 Python 四点标定尚未完成时从零锚点产生一次跳跃。
    }

    void Update()
    {
        if (Input.GetKeyDown(calibrateKey) && frontReceiver != null && frontReceiver.CalibActive)
            Calibrate();
    }

    void LateUpdate()
    {
        if (frontReceiver == null) return;

        // ── 等待 Python 标定激活 ──
        if (!frontReceiver.CalibActive)
        {
            rootVelocity = Vector3.zero;
            return;
        }

        if (!isCalibrated && autoCalibrateOnStart)
        {
            // 标定数据到达但还没校准 → 自动校准
            Calibrate();
            return;
        }

        if (frontReceiver.LatestTime == 0)
        {
            rootVelocity = Vector3.zero;
            return;
        }

        // ── 1. 按新包计算帧间速度 ──
        ComputeRootFromSyncedFrames();
    }

    void ComputeRootFromSyncedFrames()
    {
        // 只处理新包：包率(30Hz)远低于渲染帧率(60Hz+)
        int fid = frontReceiver.LatestFrameId;
        if (fid == _lastProcessedFrameId)
        {
            // 非新包帧保持上一次速度；断流超过 maxGapTime 才归零，防持续漂移
            if (_lastPacketWallTime >= 0f && Time.time - _lastPacketWallTime > maxGapTime)
            {
                rootVelocity = Vector3.zero;
                ClearWindows();
            }
            return;
        }
        _lastProcessedFrameId = fid;
        _lastPacketWallTime = Time.time;

        double frontTime = frontReceiver.LatestTime;
        float curFrontWorldX = frontReceiver.CalibWorldX;
        bool frontValid = DebounceValid(frontReceiver.CalibXValid,
                                        ref _frontValidStreak, ref _frontInvalidStreak);

        // ── X 轴（前摄 foot 世界X）──
        float rawX = 0f;
        if (!frontValid)
        {
            _frontWinT.Clear(); _frontWinV.Clear();
            _frontInvalidated = true;
        }
        else
        {
            if (!_frontInvalidated && _prevFrontTime > 0)
                rawX = curFrontWorldX - _prevFrontWorldX;
            else
            {
                // 校准后 / 无效恢复首帧：只重新锚定，不输出跨遮挡的位移
                rawX = 0f;
                _frontWinT.Clear(); _frontWinV.Clear();
                _frontInvalidated = false;
            }
            int pr = PushSample(_frontWinT, _frontWinV, frontTime, curFrontWorldX);
            if (pr == PushDup) _dupSkipX++;
            else if (pr == PushDropped) _jumpDropX++;
            _lastValidXWallTime = Time.time;   // 健康度：本包 X 有有效数据
        }
        _prevFrontWorldX = curFrontWorldX;
        _prevFrontTime = frontTime;

        // ── Z 轴（左摄 foot 世界Z）──
        float rawZ = 0f;
        if (!_hasLeft)
        {
            _leftWinT.Clear(); _leftWinV.Clear();
            _leftInvalidated = true;
        }
        else if (leftReceiver.TryGetClosestFrame(frontTime, out var leftFrame))
        {
            double timeDiff = System.Math.Abs(frontTime - leftFrame.time);
            if (timeDiff > maxTimeDiff)
            {
                // 双摄失同步 → 本包 Z 重新锚定，恢复时不突跳
                _leftWinT.Clear(); _leftWinV.Clear();
                _leftInvalidated = true;
            }
            else if (!DebounceValid(leftFrame.calibZValid,
                                    ref _leftValidStreak, ref _leftInvalidStreak))
            {
                _leftWinT.Clear(); _leftWinV.Clear();
                _leftInvalidated = true;
            }
            else
            {
                if (!_leftInvalidated && _prevLeftTime > 0)
                    rawZ = leftFrame.calibWorldZ - _prevLeftWorldZ;
                else
                {
                    rawZ = 0f;
                    _leftWinT.Clear(); _leftWinV.Clear();
                    _leftInvalidated = false;
                }
                int pz = PushSample(_leftWinT, _leftWinV, leftFrame.time, leftFrame.calibWorldZ);
                if (pz == PushDup) _dupSkipZ++;
                else if (pz == PushDropped) _jumpDropZ++;
                _lastValidZWallTime = Time.time;   // 健康度：本包 Z 有有效数据
            }
            _prevLeftWorldZ = leftFrame.calibWorldZ;
            _prevLeftTime = leftFrame.time;
        }
        else
        {
            _leftWinT.Clear(); _leftWinV.Clear();
            _leftInvalidated = true;
        }

        // ── 硬保护（每包位移上限）+ 死区：只影响 rawDX/rawDZ 观测量 ──
        if (Mathf.Abs(rawX) > maxDeltaPerFrame) rawX = 0f;
        if (Mathf.Abs(rawZ) > maxDeltaPerFrame) rawZ = 0f;
        if (Mathf.Abs(rawX) < deadZoneMeters) rawX = 0f;
        if (Mathf.Abs(rawZ) < deadZoneMeters) rawZ = 0f;
        rawDX = rawX;
        rawDZ = rawZ;

        // ── 窗口速度（不是单包差分）→ 低通 → 输出 ──
        _winVelX = WindowVelocity(_frontWinT, _frontWinV);
        _winVelZ = WindowVelocity(_leftWinT, _leftWinV);

        // 低通（只在新包执行，与渲染帧率解耦）
        _smoothVelX = Mathf.Lerp(_smoothVelX, _winVelX, lowPassAlpha);
        _smoothVelZ = Mathf.Lerp(_smoothVelZ, _winVelZ, lowPassAlpha);

        // 速度死区：小于死区视为静止，避免噪声残留积分成缓慢漂移
        if (Mathf.Abs(_smoothVelX) < deadZoneSpeed) _smoothVelX = 0f;
        if (Mathf.Abs(_smoothVelZ) < deadZoneSpeed) _smoothVelZ = 0f;

        // ── 输出 ──
        rootVelocity = new Vector3(
            invertRootX ? -_smoothVelX : _smoothVelX, 0f,
            invertRootZ ? -_smoothVelZ : _smoothVelZ);

        UpdateChannelHealth();
    }

    /// <summary>
    /// 更新两轴的"停摆"状态：多久没拿到有效样本、以及最可能的原因。
    /// 只在收到新包时调用（与渲染帧率解耦），OnGUI 只读缓存值。
    /// </summary>
    void UpdateChannelHealth()
    {
        float now = Time.time;
        _xDeadSeconds = _lastValidXWallTime < 0f ? -1f : now - _lastValidXWallTime;
        _zDeadSeconds = _lastValidZWallTime < 0f ? -1f : now - _lastValidZWallTime;

        bool zDead = _zDeadSeconds < 0f || _zDeadSeconds > deadChannelWarnSeconds;
        if (zDead)
        {
            _zDeadCause = DiagnoseZ();
            if (!_zDeadLogged)
            {
                _zDeadLogged = true;
                Debug.LogError($"[RootEst] ⛔ Z轴(前后)停摆 — {_zDeadCause}");
            }
        }
        else if (_zDeadLogged)
        {
            _zDeadLogged = false;
            _zDeadCause = "";
            Debug.Log("[RootEst] ✅ Z轴(前后)已恢复");
        }

        bool xDead = _xDeadSeconds < 0f || _xDeadSeconds > deadChannelWarnSeconds;
        if (xDead)
        {
            if (!_xDeadLogged)
            {
                _xDeadLogged = true;
                Debug.LogError($"[RootEst] ⛔ X轴(左右)停摆 — 前摄包龄="
                    + $"{frontReceiver.LatestPacketAgeMs:F0}ms validX={frontReceiver.CalibXValid}");
            }
        }
        else _xDeadLogged = false;
    }

    /// <summary>Z 轴停摆的最可能原因。顺序按"上游离得最近"排，先排除断流再看语义。</summary>
    string DiagnoseZ()
    {
        if (!_hasLeft) return "leftReceiver 未接入（Inspector 里没挂左摄接收器）";
        float ageMs = leftReceiver.LatestPacketAgeMs;
        if (float.IsInfinity(ageMs) || ageMs > 1500f)
            return $"左摄断流：已 {ageMs:F0}ms 收不到任何 UDP 包（手机/摄像头掉线、取流卡死）";
        if (!leftReceiver.CalibZValid)
            return "左摄在发数据，但 _calib_z_valid=0 —— 左摄画面里没有可用的脚"
                 + "（看不到人或脚被遮），或前摄的 foot_x 没能同步给左摄";
        return "左摄数据看似正常，但窗口攒不满 2 个样本（内部逻辑问题，需要查代码）";
    }

    void ClearWindows()
    {
        _frontWinT.Clear(); _frontWinV.Clear();
        _leftWinT.Clear(); _leftWinV.Clear();
        _smoothVelX = _smoothVelZ = 0f;
        _winVelX = _winVelZ = 0f;
    }

    /// <summary>
    /// 压入一帧观测。
    ///
    /// ★ 关键：**重复样本必须"跳过"，绝不能"清窗"。**
    ///   Z 轴的样本来自 TryGetClosestFrame(frontTime)：左摄比前摄慢时，
    ///   相邻两个前摄包会匹配到**同一个左摄帧**（dt == 0）。
    ///   如果 dt==0 就清窗，左摄的窗口永远攒不满 2 个样本 → WindowVelocity 恒返回 0
    ///   → 人物不能前进后退；而 X 轴每个前摄新包必定推一个新样本，完全不受影响。
    ///
    /// 清窗只留给"真的断档"：时间倒流、断流超过 maxGapTime、或单帧跳变。
    /// 跳变判据用「位移」配一个 dt 下限，避免 dt 极小时闸门被压成 0 而误杀正常样本。
    /// </summary>
    /// <summary>压入处置结果：0=已入窗 1=重复样本(跳过) 2=判为跳变/断流(已清窗重锚)。</summary>
    const int PushOk = 0, PushDup = 1, PushDropped = 2;

    int PushSample(List<double> ts, List<double> vs, double time, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            ts.Clear(); vs.Clear();
            return PushDropped;
        }

        // 先按当前时间裁剪，保证窗口始终是"最近 velocityWindow 秒"
        TrimWindow(ts, vs, time);

        if (ts.Count == 0)
        {
            ts.Add(time); vs.Add(value);
            return PushOk;
        }

        double dt = time - ts[ts.Count - 1];

        // 同一帧被重复匹配（dt ≈ 0）→ 跳过，保持窗口不动
        if (dt <= 1e-4) return PushDup;

        double step = System.Math.Abs(value - vs[vs.Count - 1]);
        // dt 下限 20ms：dt 很小时"速度"没有意义，用距离闸即可
        double allowed = maxStepSpeed * System.Math.Max(dt, 0.02);

        bool dropped = false;
        if (dt > maxGapTime || step > allowed)
        {
            ts.Clear(); vs.Clear();
            dropped = true;
        }

        ts.Add(time); vs.Add(value);
        TrimWindow(ts, vs, time);
        return dropped ? PushDropped : PushOk;
    }

    /// <summary>丢弃窗口外的旧样本（至少保留 2 个，否则速度无从计算）。</summary>
    void TrimWindow(List<double> ts, List<double> vs, double now)
    {
        while (ts.Count > 2 && now - ts[0] > velocityWindow)
        {
            ts.RemoveAt(0); vs.RemoveAt(0);
        }
    }

    /// <summary>窗口首尾差分求速度，并做幅值截断。样本不足 2 个时返回 0（视为静止）。</summary>
    float WindowVelocity(List<double> ts, List<double> vs)
    {
        int n = ts.Count;
        if (n < 2) return 0f;
        double dt = ts[n - 1] - ts[0];
        if (dt < 1e-4) return 0f;
        double vel = (vs[n - 1] - vs[0]) / dt;
        if (double.IsNaN(vel) || double.IsInfinity(vel)) return 0f;
        return Mathf.Clamp((float)vel, -maxSpeed, maxSpeed);
    }

    /// <summary>
    /// 有效性去抖：可见度偶发抖动（1~2 包无效）仍视为有效、不触发重新锚定；
    /// 只有连续 InvalidateAfterStreak 包无效才判为真无效（清窗重新锚定）。
    /// </summary>
    bool DebounceValid(bool raw, ref int validStreak, ref int invalidStreak)
    {
        if (raw) { validStreak++; invalidStreak = 0; }
        else { invalidStreak++; validStreak = 0; }
        return invalidStreak < InvalidateAfterStreak;
    }

    public void Calibrate()
    {
        if (frontReceiver == null) return;

        _prevFrontWorldX = frontReceiver.CalibWorldX;
        _prevFrontTime = frontReceiver.LatestTime;
        _prevLeftWorldZ = _hasLeft ? leftReceiver.CalibWorldZ : 0f;
        _prevLeftTime = _hasLeft ? leftReceiver.LatestTime : 0;
        _lastProcessedFrameId = frontReceiver.LatestFrameId;
        _lastPacketWallTime = Time.time;
        _frontInvalidated = true;  // 校准后首包只重新锚定，不输出位移
        _leftInvalidated = true;
        _frontValidStreak = 0; _frontInvalidStreak = 0;
        _leftValidStreak = 0; _leftInvalidStreak = 0;
        ClearWindows();
        rawDX = 0f;
        rawDZ = 0f;
        rootVelocity = Vector3.zero;

        // 健康度重新计时，避免校准后立刻误报"停摆"
        _lastValidXWallTime = Time.time;
        _lastValidZWallTime = Time.time;
        _zDeadSeconds = _xDeadSeconds = 0f;
        _zDeadLogged = _xDeadLogged = false;
        _zDeadCause = "";

        isCalibrated = true;

        Debug.Log($"[RootEst] ✅ 校准完成  原点 prevX={_prevFrontWorldX:F3}  prevZ={_prevLeftWorldZ:F3}");
    }

    void OnGUI()
    {
        if (frontReceiver == null) return;
        if (!frontReceiver.CalibActive)
        {
            GUI.Label(new Rect(10, Screen.height - 24, 500, 24),
                $"[RootEst] ⏳ 等待 Python 标定...  [C]手动校准");
            return;
        }

        if (!isCalibrated)
        {
            GUI.Label(new Rect(10, Screen.height - 24, 500, 24),
                "[RootEst] 📐 检测到标定数据，正在校准...");
            return;
        }

        // ── 死轴告警 ★★★
        // Z 轴（前后）完全依赖左摄。上游一断，Unity 侧改任何参数都不可能恢复前后行走，
        // 但屏幕上原本看不出任何异常。这里用红字把原因直接写在脸上。
        if (_zDeadSeconds < 0f || _zDeadSeconds > deadChannelWarnSeconds)
        {
            float bx = 10, by = Screen.height - 116, bw = 1000, blh = 16;
            float leftAgeMs = (_hasLeft && leftReceiver != null) ? leftReceiver.LatestPacketAgeMs : -1f;
            var oldColor = GUI.color;
            GUI.color = new Color(1f, 0.45f, 0.45f);
            GUI.Box(new Rect(bx, by, bw, blh * 3 + 8), "");
            GUI.Label(new Rect(bx + 6, by + 4, bw - 12, blh),
                $"⛔ Z轴(前后)停摆 {(_zDeadSeconds < 0f ? "(从未拿到有效数据)" : $"已 {_zDeadSeconds:F1}s")}"
                + " —— 人物不能前进/后退就是这个原因，改 Unity 参数没用");
            GUI.Label(new Rect(bx + 6, by + 4 + blh, bw - 12, blh),
                $"   诊断：{_zDeadCause}");
            GUI.Label(new Rect(bx + 6, by + 4 + blh * 2, bw - 12, blh),
                $"   左摄包龄={leftAgeMs:F0}ms  validZ={(_hasLeft && leftReceiver != null && leftReceiver.CalibZValid)}"
                + $"  Z窗样本={_leftWinT.Count}  跳变挡掉Z={_jumpDropZ}  重复挡掉Z={_dupSkipZ}");
            GUI.color = oldColor;
        }

        int x = 10, y = 170, w = 420, lh = 16;
        GUI.Box(new Rect(x, y, w, lh * 10 + 8), "");
        y += 4;
        void L(string s) { GUI.Label(new Rect(x + 6, y, w - 12, lh), s); y += lh; }

        L($"⚙️ RootEst v7(窗口{velocityWindow:F2}s)  ★输出 vel=({rootVelocity.x:F2}, {rootVelocity.z:F2}) m/s");
        L($"  rawDX={rawDX:+0.000;-0.000}  rawDZ={rawDZ:+0.000;-0.000}  (每包位移 · 翻转前)");
        L($"  窗速 X={_winVelX:+0.00;-0.00} Z={_winVelZ:+0.00;-0.00}  低通后 X={_smoothVelX:+0.00;-0.00} Z={_smoothVelZ:+0.00;-0.00}");
        L($"  窗内样本 X={_frontWinT.Count} Z={_leftWinT.Count}  (被跳变清空会掉到 0/1)");
        L($"  挡掉: 重复 X={_dupSkipX} Z={_dupSkipZ}   跳变 X={_jumpDropX} Z={_jumpDropZ}");
        L($"  calibWX={frontReceiver.CalibWorldX:F3}  calibWZ={(_hasLeft ? leftReceiver.CalibWorldZ : 0f):F3}");
        L($"  validX={frontReceiver.CalibXValid}  validZ={(_hasLeft ? leftReceiver.CalibZValid : false)}"
          + $"   invertX={invertRootX} invertZ={invertRootZ}");
        L($"  prevX={_prevFrontWorldX:F3}  prevZ={_prevLeftWorldZ:F3}");
        L($"  footGnd 前={frontReceiver.LatestRootGround.x:F4}  左={(_hasLeft ? leftReceiver.LatestRootGround.x : 0f):F4}");
        L($"  双摄={_hasLeft}  fid={_lastProcessedFrameId}  [C]重校");
    }
}
