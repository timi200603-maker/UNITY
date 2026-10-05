using UnityEngine;
using System.Collections.Generic;   // v5：滑动时间窗用 List<>

/// <summary>
/// Stage A (修正版 v5) — 脚锁 + 直解两骨腿 IK + 步态滚动相 + 根伺服抗滑。
///
/// v5 变更（针对“这一版滑步更明显”）：
///  0) 速度估计去整流（核心）：旧 st.hSpeed = |ΔrawPlan.xz| × fr 有两处结构性错误 ——
///     ① dH.magnitude 是逐帧位移的模长，即【全波整流】：零均值抖动一来一回不抵消，
///        每包全额贡献正值，再 ×fr(≈24~30) 把 ±1.2cm 的抖动恒定读成 ≈0.36 m/s，
///        永远压在 plantSpeedMax=0.30 之上 → candidate 几乎不成立 → 锚点起不来；
///     ② fr = 1/(now-prevTime) 只校验 >1e-4，时间戳毛刺能把 1mm 读成 10 m/s。
///     改法：滑动时间窗「带符号分量差 ÷ 窗口真实跨度」——抖动按 dt/T 衰减、符号自然对消。
///     与 DualCameraRootEstimator v7 是同一套机制（那边已现场验证）。
///     plantSpeedWindow = 0 一键退回旧行为，便于 A/B。
///  1) 计划踝位 3 点分量中值：遮挡/幻觉造成的单包离群点被【整点剔除】，代价仅 1 个包延迟。
///  2) 释放证据计时器改为只在【有效新数据帧】累加（旧代码在渲染帧上重复累加同一份陈旧样本，
///     一个噪声尖峰能被计成多次）。
///  3) 根伺服单位由「米/帧」改为「米/秒」：旧 rootServoMaxPerFrame=0.04 在 32fps 就是 1.28 m/s，
///     帧率一变就反超 AvatarRootFollower 的 maxSpeed，根节点会在推送/拽回之间抽动。
///  4) planted 上升速率提高：旧 8/s 要 0.1125s 才到根伺服门槛 0.9，而释放证据只要 0.06s ——
///     锚点注定在伺服启用前被拆掉。改为 25/s（0.036s 到位）。
///  5) 诊断显性化：hSpeed / 窗口跨度 / 被挡样本数 / low / slow 全部进 Overlay —— 旧版
///     从不打印 hSpeed，正是这次排查要靠逐帧解码录屏才能定位的原因。
///
/// v4 变更（脚锁 + 直解两骨腿 IK + 步态滚动相 + 根伺服抗滑）：
///
/// v4 变更（针对“滑步 / 脚跟先触地过渡不明显 / 走路不自然”）：
///  1) 根伺服（闭环抗滑，核心）：支撑脚被锁在世界锚点上；若腿因够不到而走样，
///     直接把 Avatar Root 按“锚点 - 实际踝位”的水平误差挪回去，再重解一次 IK。
///     于是“脚被身体拖走”变成“身体被脚拽住”，支撑脚在世界上真正静止 → 根除滑步。
///     同时把整体位移从“全局速度估计”换成“由落脚点几何反推”，落脚间距更准。
///  2) 步态滚动相：脚跟着地(脚尖上翘) → 全掌 → 蹬离(脚尖下压) → 摆动。
///     俯仰角由相位驱动，并按枢轴补偿踝高，使脚绕脚跟/脚尖滚动而不是整体平移。
///  3) 锚点 = 锁定瞬间的计划踝位（不再用候选窗口平均），去掉每步落地时的回拉小滑。
///  4) 释放加滑行过渡（速率限制），抬脚/迈步/不可达都不再瞬移。
///  5) 摆动脚也写朝向（按相位俯仰 + 身体前向 + 轻微外八），不再冻结在上一个支撑相。
///  6) 支撑权重回传给 HumanoidPoseDriver，用于骨盆摆动/起伏，并把下蹲判据改成
///     “两腿同时缩短”（min），避免迈步时摆动腿把骨盆拉低。
///  7) 诊断新增：相位、俯仰角、锚定期间累计滑移速度（供 Python 桌面监看）。
///
/// 兼容：场景里缓存的旧默认值（plantHeightMax=0.3 / plantSpeedMax=0.5）会让接触判定过宽，
///       运行时按 fixLegacySceneValues 自动纠正并打印日志。
/// </summary>
[DefaultExecutionOrder(150)]
public class FootPlantingController : MonoBehaviour
{
    [Header("Toggle")]
    public bool enableFootIK = true;

    [Header("References")]
    public Animator animator;
    public HumanoidPoseDriver poseDriver;

    [Header("Ground / Board")]
    public float boardY = 0f;
    public float ankleOffset = 0.08f;

    [Header("Stance (contact candidate)")]
    public float plantHeightMax = 0.16f;   // 踝世界Y-板面 ≤ 此值 → 近地候选
    public float plantSpeedMax = 0.30f;    // 水平速度 ≤ 此值 → 慢
    public float minPlantTime = 0.10f;     // 接触候选最短持续(有效新帧时间)

    [Header("Release: lift (relative to plant base)")]
    public float riseThreshold = 0.14f;    // 相对 plantBaseY 抬升超过 → 明确抬脚(立即放)
    public float liftVSpeed = 0.25f;       // 向上速度(需结合小幅抬升+连续证据)
    public float vEvidenceRise = 0.03f;    // 速度路径需要的最小抬升
    public float vEvidenceTime = 0.06f;    // 速度路径连续证据时长(有效新帧)

    [Header("Release: low-step deviation")]
    public float stepDev = 0.15f;          // 独立目标相对锚点水平偏离超过 → 低位迈步释放
    public float stepDevTime = 0.06f;      // 偏离持续(有效新帧)

    [Header("Release: stuck (anchor unreachable)")]
    public float unreachTolerance = 0.02f; // 超出腿可达的容差(不超过此不判不可达)
    public float unreachTime = 0.15f;      // 不可达持续(有效新帧)

    [Header("Rates")]
    public float plantEntryRate = 10f;     // 支撑权重上升速率
    public float plantExitRateFast = 80f;  // 状态机快速放锚（视觉过渡由 Release Glide 负责）

    [Header("Smoothing")]
    public float swingLowpassTimeConst = 0.05f;
    public float reachMargin = 0.002f;
    public float footRotateSmooth = 30f;

    [Header("Speed deadzone")]
    public float hSpeedDeadzone = 0.05f;
    public float vSpeedDeadzone = 0.05f;

    // ══════════════════════════════════════════════════════════════
    // v5：抗抖动速度估计（核心修复）
    //   旧 st.hSpeed = |ΔrawPlan.xz| × fr 是【全波整流】：零均值抖动不抵消，
    //   ±1.2cm 抖动 × fr≈24 恒定读成 ≈0.30 m/s，正好卡在 plantSpeedMax 上。
    // ══════════════════════════════════════════════════════════════
    [Header("Velocity estimate (anti-jitter, v5)")]
    [Tooltip("求踝位速度的滑动时间窗(s)。设 0 = 退回旧的单包差分（A/B 对比用）。"
           + "★主要调参旋钮：窗越长 → 抗噪越好、但「脚已停住」的判断越滞后。"
           + "因为窗口本身就是时间证据，minPlantTime 已相应下调，避免重复计时。")]
    public float plantSpeedWindow = 0.10f;
    [Tooltip("窗口至少保留的样本数（<2 无法差分）")]
    public int plantWindowKeep = 2;
    [Tooltip("单包位移闸(m/s)：某包水平位移超过 此值×dt 视为幻觉点，丢弃该样本但【不清窗】")]
    public float plantWindowMaxStep = 4.0f;
    [Tooltip("包间隔异常时帧率的上限(Hz)：防时间戳毛刺把 1mm 读成 10m/s")]
    public float frMax = 60f;
    [Tooltip("对计划踝位做 3 点分量中值：遮挡/幻觉的单包离群点被整点剔除（代价 1 个包延迟）")]
    public bool planMedian3 = true;
    [Tooltip("近地判据迟滞(m)：进入用 plantHeightMax，退出用 plantHeightMax+此值，防 Y 抖动反复开关候选窗")]
    public float plantHeightHyst = 0.03f;
    [Tooltip("滑步诊断的噪声地板(m/s)：低于此速度不计入 slideDist，否则一个锁死的脚也会报 cm/s 的假滑")]
    public float slideNoiseFloor = 0.06f;

    // ══════════════════════════════════════════════════════════════
    // v6：准入判据换成「触地几何」（取代世界水平速度）
    // ══════════════════════════════════════════════════════════════
    [Header("Entry gate v6（触地判据）")]
    [Tooltip("v6 主开关。true = 用「触地几何」判准入（推荐）；false = 退回 v5 的「世界水平速度」判据。\n"
           + "★为什么必须换：锚点建立【之前】踝的世界坐标 = 根位移 + FK 相对姿态，根一前进就把位移原样"
           + "泄漏进踝，所以支撑相的 hSpeed ≈ 根速（实测 0.83m/s 行走 → 72cm/s），永远压不过 30cm/s 阈值。"
           + "「脚不动」是锚点的【果】而不是【因】：不锚 → 脚跟着根漂 → 速度降不下来 → 永不锚，闭环死锁。")]
    public bool plantContactGate = true;
    [Tooltip("v6 准入的垂直速度【幅值】上限(m/s)。脚已落地 ⇔ 不再快速上升/下落，所以取 |vSpeed| ≤ 此值。"
           + "垂直分量不被根的水平位移污染，是这里唯一可信的「脚停住了」证据。"
           + "调大 → 落地瞬间就能锚住但可能锚到还在下落的脚；调小 → 更保守但锚点诞生更晚。")]
    public float plantVSpeedMax = 0.15f;
    [Tooltip("v6 兜底：世界水平速度硬上限(m/s)。只用来挡「明显在摆动中段飞过去」的帧，不参与正常判断。")]
    public float plantHSpeedHardMax = 2.5f;

    // ══════════════════════════════════════════════════════════════
    // 新增：步态滚动相（脚跟触地 → 全掌 → 蹬离）
    // ══════════════════════════════════════════════════════════════
    [Header("Gait Phase / Foot Roll")]
    [Tooltip("总开关：按相位驱动足部俯仰，做出脚跟着地/蹬离的滚动")]
    public bool enableFootRoll = true;
    [Tooltip("脚跟着地瞬间脚尖上翘角(度, >0 脚尖上抬)")]
    public float heelStrikePitchDeg = 15f;
    [Tooltip("从脚跟触地滚到全掌的时间(s)")]
    public float heelStrikeTime = 0.10f;
    [Tooltip("蹬离时脚尖下压角(度, 正值表示脚尖朝下)")]
    public float toeOffPitchDeg = 26f;
    [Tooltip("锚点落到髋后方超过此距离 → 开始蹬离(m)")]
    public float toeOffBehindDist = 0.10f;
    [Tooltip("蹬离角从 0 到最大所对应的髋-锚点后移距离(m)")]
    public float toeOffRampDist = 0.16f;
    [Tooltip("摆动中期脚尖上翘角(度)")]
    public float swingToeUpDeg = 7f;
    [Tooltip("站立时脚尖外八角(度)，落地瞬间冻结到整个支撑相")]
    public float toeOutDeg = 6f;
    [Tooltip("外八方向：看起来内八时改成 -1")]
    public float toeOutSign = 1f;
    [Tooltip("脚跟杠杆 = 踝→趾骨世界距离 × 此值")]
    public float heelLeverScale = 0.60f;
    [Tooltip("脚尖杠杆 = 踝→趾骨世界距离 × 此值")]
    public float toeLeverScale = 1.25f;
    [Tooltip("足部俯仰平滑时间常数(s)")]
    public float rollSmoothTime = 0.05f;

    // ══════════════════════════════════════════════════════════════
    // 新增：根伺服（闭环抗滑）
    // ══════════════════════════════════════════════════════════════
    [Header("Root Servo (anti-slide)")]
    [Tooltip("支撑脚锚定期间，用“锚点-实际踝位”的水平误差反向移动 Avatar Root")]
    public bool enableRootServo = true;
    [Range(0f, 1f)] public float rootServoGain = 1f;
    [Tooltip("根伺服最大修正【速度】(m/s)，防止抖动/瞬移。v5：旧字段是 m/帧，32fps 下等于 1.28m/s，"
           + "60fps 变 2.4、120fps 变 4.8，会反超 AvatarRootFollower.maxSpeed=1.5 造成根节点抽动")]
    public float rootServoMaxSpeed = 1.20f;
    [Tooltip("（旧字段，已废弃，仅用于迁移判断，运行时不再读取）")]
    public float rootServoMaxPerFrame = 0.04f;
    [Tooltip("修正后重解 IK 的迭代次数")]
    [Range(1, 4)] public int rootServoIterations = 2;

    // ══════════════════════════════════════════════════════════════
    // 新增：释放滑行（避免瞬移跳变）
    // ══════════════════════════════════════════════════════════════
    [Header("Release glide (avoid pop)")]
    [Tooltip("释放/交接期间 IK 目标的最大移动速度(m/s)")]
    public float releaseGlideSpeed = 2.0f;
    [Tooltip("释放完成后继续限速过渡的时间(s)")]
    public float releaseGlideTime = 0.10f;

    [Header("Diagnostics")]
    public bool showOverlay = true;
    public bool logDiagnostics = false;
    [Tooltip("自动挂载 FootIkUdpSender，把脚锁诊断回传 127.0.0.1:8890")]
    public bool autoSendDiagnostics = true;
    [Tooltip("自动纠正场景里缓存的旧脚本默认值(plantHeightMax 等)")]
    public bool fixLegacySceneValues = true;
    [Tooltip("只读：本帧是否触发腿部不可达")]
    public bool reachLimited;
    [Tooltip("只读：可达限制原因")]
    public string reachReason = "";
    [Tooltip("只读：本帧根伺服水平修正量(m)")]
    public float rootServoCorrection;

    // 骨骼
    private Transform _hips, _lUp, _lLo, _lFoot, _lToe, _rUp, _rLo, _rFoot, _rToe;
    private Quaternion _lUpRest, _lLoRest, _rUpRest, _rLoRest;
    private Vector3 _lUpAxis, _lLoAxis, _rUpAxis, _rLoAxis;
    private float _lThigh, _lShin, _rThigh, _rShin;
    private Vector3 _hipsForwardLocal = Vector3.forward;
    private Quaternion _lFootRestLocal, _rFootRestLocal;
    private Vector3 _lFootFwdLocal, _lFootUpLocal, _rFootFwdLocal, _rFootUpLocal;
    private float _lHeelLever, _lToeLever, _rHeelLever, _rToeLever;

    private Transform _rootMove;   // 实际被根伺服移动的 Transform（Avatar Root）
    private bool _inited;          // 骨骼/杠杆/状态是否已成功初始化（TryInit 成功过）

    // 相位编号
    private const int PhaseSwing = 0, PhaseHeelStrike = 1, PhaseFlat = 2, PhaseToeOff = 3;

    private struct FootState
    {
        public bool hasAnchor;         // 是否已锁定(进入支撑)
        public Vector3 anchor;         // 锁定锚点(y=板面+踝偏移)
        public float plantBaseY;       // 接触候选期计划踝位Y的中值(抬升基准)
        public int anchorId;
        public float planted;          // 0..1 支撑权重
        public bool releasing;         // 完整释放状态:一旦true持续降权到0,中途不回升
        public string releaseReason;   // lifted/liftedV/step/stuck/none/--
        public bool smoothInit;
        public Vector3 smoothPlan;
        public bool init;
        public Vector3 prevPlan;
        public double prevTime;
        public int lastFrameId;
        public float hSpeed, vSpeed;
        public float effDt;            // 本帧用于证据累计的时间(有效新帧)
        public float dataDt;           // v5：仅“有效新数据帧”非零 —— 证据计时器只认它
        // ── v5：滑动时间窗速度估计 ──
        public List<double> winT;      // 窗口样本时间(UDP 采集时间)
        public List<Vector3> winP;     // 窗口样本：计划踝位(世界)
        public float winSpan;          // 只读：窗口实际跨度(s)，诊断用
        public int winDup, winDrop;    // 只读：重复样本 / 跳变样本被挡掉的累计次数
        public Vector3 histP1, histP2; // 前两包原始计划位(3 点中值用)
        public Vector3 filtPlan;       // 中值滤波后的计划踝位(本帧判据用)
        public bool lowHyst;           // 近地迟滞锁存
        public bool groundHyst;        // v6 触地迟滞锁存（与 lowHyst 对称）
        public bool candLow, candSlow; // 只读：候选的两个分量(诊断)
        public bool candGround;        // v6 只读：触地判据(垂直速度已趋零)是否成立
        public int winCount;           // 只读：窗口当前样本数
        // 接触候选统计
        public float candTime;
        public double candSumX, candSumZ;
        public float candLastX, candLastZ;
        public int candCount;
        public float[] candYs;         // 计划踝位Y 环形缓冲(取中值)
        public int candYHead;          // 下一个写入位置
        // 释放证据计时(有效新帧)
        public float vEvidTime;
        public float stepDevTimeAcc;
        public float unreachTimeAcc;
        // ── v4：步态滚动相 ──
        public int phase;
        public float pitchDeg;         // 当前足部俯仰角(度, >0 脚尖上抬)
        public float stanceTime;       // 锁定后的有效帧累计时间
        public Vector3 yawFwd;         // 锁定瞬间冻结的足部水平前向
        // ── v4：IK 目标滑行 ──
        public Vector3 target;
        public bool targetInit;
        public float glideTime;
        // ── v4：滑移度量 ──
        public float slideDist;
        public float anchorAge;
        public float slideSpeed;
        public Vector3 prevFootPos;
        public bool prevFootValid;
        // 诊断
        public float liftRise, anchorDev, anchorDist;
        public bool unreach;
        public bool contact;
        public float distToAnchor;
        public Vector3 polePerp;
    }
    private FootState _l, _r;
    private const int CandBuffer = 16;

    private float _frontAgeMs = float.PositiveInfinity;
    private readonly string _session = System.Guid.NewGuid().ToString("N");

    // ── 结构化诊断快照（供 FootIkUdpSender / Python 回传）──
    public struct FootDiag
    {
        public bool hasAnchor;
        public int anchorId;
        public float planted;
        public bool releasing;
        public string status;        // zh: 支撑/释放/摆动
        public string releaseReason; // zh: 抬脚/持续上升/迈步偏离/锚点不可达/—/支撑
        public string phase;         // zh: 摆动/脚跟着地/全掌支撑/蹬离
        public float pitchDeg;       // 足部俯仰(度, >0 脚尖上抬)
        public float slideSpeed;     // 锚定期间累计滑移速度(m/s)
        public float liftRise;       // m
        public float anchorDev;      // m
        public float hipToAnchor;    // m
        public float ankleToAnchor;  // m
        public float vSpeed;         // m/s
        public bool unreach;
        public float unreachTimeAcc; // s
        public bool contact;
        // ── v5 新增：定位滑步到底出在「锚点没诞生」还是「锚点诞生但够不到」──
        public float hSpeed;         // m/s 计划踝位水平速度（旧版从不上报，是排查死角）
        public float candTime;       // s   候选已积累时长
        public int candCount;        //     候选样本数
        public bool candLow, candSlow; //   候选两个分量
        public bool candGround;        //   v6 触地判据
        public float winSpan;        // s   速度窗实际跨度
        public int winDrop, winDup;  //     被剔除的跳变/重复样本累计
        public Vector3 anchor;       // 世界锚点
        public Vector3 footPos;      // 足骨世界位
        public Vector3 target;       // IK 目标
        public Vector3 rootPos;      // Avatar Root 世界位（看根是否跑飞）
        public Vector3 hipPos;       // 髋关节世界位
        public float legReach;       // 腿长 thigh+shin (m)
        public float servoCorr;      // 本帧根伺服修正量(m)
    }
    public struct Snapshot
    {
        public string session;
        public int seq;
        public bool ikEnabled;
        public bool controllerEnabled;
        public float frontAgeMs;
        public bool published;      // 是否已发布过(有数据)
        public FootDiag left;
        public FootDiag right;
    }

    public Snapshot GetDiagnostics()
    {
        var s = new Snapshot();
        s.session = _session;
        s.seq = 0; // 序号由发送端每次发送递增
        s.ikEnabled = enableFootIK;
        s.controllerEnabled = enabled;
        s.frontAgeMs = _frontAgeMs;
        s.published = animator != null && animator.isHuman && poseDriver != null;
        s.left = BuildFootDiag(_l, true);
        s.right = BuildFootDiag(_r, false);
        return s;
    }

    FootDiag BuildFootDiag(FootState st, bool isLeft)
    {
        var d = new FootDiag();
        Transform fb = isLeft ? _lFoot : _rFoot;
        Transform hb = UpBone(isLeft);
        if (fb != null) d.footPos = fb.position;
        if (hb != null) d.hipPos = hb.position;
        if (_rootMove == null) ResolveRootMove();
        if (_rootMove != null) d.rootPos = _rootMove.position;
        d.legReach = thigh(isLeft) + shin(isLeft);
        d.hasAnchor = st.hasAnchor;
        d.anchorId = st.anchorId;
        d.planted = st.planted;
        d.releasing = st.releasing;
        d.phase = PhaseName(st.phase, st.hasAnchor);
        d.pitchDeg = st.pitchDeg;
        d.slideSpeed = st.slideSpeed;
        d.liftRise = st.liftRise;
        d.anchorDev = st.anchorDev;
        d.hipToAnchor = st.anchorDist;
        d.ankleToAnchor = st.distToAnchor;
        d.vSpeed = st.vSpeed;
        d.unreach = st.unreach;
        d.unreachTimeAcc = st.unreachTimeAcc;
        d.contact = st.contact;
        d.hSpeed = st.hSpeed;
        d.candTime = st.candTime; d.candCount = st.candCount;
        d.candLow = st.candLow; d.candSlow = st.candSlow;
        d.candGround = st.candGround;
        d.winSpan = st.winSpan; d.winDrop = st.winDrop; d.winDup = st.winDup;
        d.anchor = st.anchor; d.target = st.target;
        d.servoCorr = rootServoCorrection;
        if (st.releasing) { d.status = "释放"; d.releaseReason = ZhReason(st.releaseReason); }
        else if (st.hasAnchor) { d.status = st.planted >= 0.5f ? "支撑" : "释放中"; d.releaseReason = "支撑"; }
        else { d.status = "摆动"; d.releaseReason = "—"; }
        return d;
    }

    static string PhaseName(int phase, bool hasAnchor)
    {
        if (!hasAnchor) return "摆动";
        switch (phase)
        {
            case PhaseHeelStrike: return "脚跟着地";
            case PhaseToeOff: return "蹬离";
            case PhaseFlat: return "全掌支撑";
            default: return "支撑";
        }
    }

    static string ZhReason(string r)
    {
        switch (r)
        {
            case "lifted": return "抬脚";
            case "liftedV": return "持续上升";
            case "step": return "迈步偏离";
            case "stuck": return "锚点不可达";
            case "sliding": return "滑移";
            case "none": return "支撑";
            default: return "—";
        }
    }

    void Awake()
    {
        _inited = TryInit();
        if (!_inited)
        {
            // 不静默失败：禁用本组件，让画面上直接没有脚锁效果，而不是每帧抛 NRE
            // 把 LateUpdate 掐断（那种情况看起来就像「脚锁没生效」，排查时极具误导性）。
            Debug.LogError("[FootIK v5] 初始化失败（骨骼缺失或非 Humanoid）→ 已禁用本组件。"
                         + "请检查 Animator 的 Avatar 是否为 Humanoid、以及模型是否有完整的腿骨。");
            enabled = false;
        }
    }

    /// <summary>收集骨骼/杠杆/朝向基底。可重复调用（骨骼在 Awake 时可能还没就位）。</summary>
    bool TryInit()
    {
        if (animator == null) animator = GetComponent<Animator>();
        if (animator == null || !animator.isHuman) return false;

        _hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        _lUp = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
        _lLo = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
        _lFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
        _lToe = animator.GetBoneTransform(HumanBodyBones.LeftToes);
        _rUp = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
        _rLo = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
        _rFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot);
        _rToe = animator.GetBoneTransform(HumanBodyBones.RightToes);

        if (_hips == null || _lUp == null || _lLo == null || _lFoot == null ||
            _rUp == null || _rLo == null || _rFoot == null) return false;

        CacheBone(_lUp, _lLo, out _lUpRest, out _lUpAxis);
        CacheBone(_lLo, _lFoot, out _lLoRest, out _lLoAxis);
        CacheBone(_rUp, _rLo, out _rUpRest, out _rUpAxis);
        CacheBone(_rLo, _rFoot, out _rLoRest, out _rLoAxis);

        _lThigh = Mathf.Max(0.05f, _lLo.localPosition.magnitude);
        _lShin = Mathf.Max(0.05f, _lFoot.localPosition.magnitude);
        _rThigh = Mathf.Max(0.05f, _rLo.localPosition.magnitude);
        _rShin = Mathf.Max(0.05f, _rFoot.localPosition.magnitude);

        if (_hips != null) _hipsForwardLocal = Quaternion.Inverse(_hips.rotation) * _hips.forward;
        CacheFootBasis(_lFoot, _lToe, out _lFootRestLocal, out _lFootFwdLocal, out _lFootUpLocal);
        CacheFootBasis(_rFoot, _rToe, out _rFootRestLocal, out _rFootFwdLocal, out _rFootUpLocal);

        // 杠杆臂按“世界尺寸”推导，避免模型导入缩放(cm/m)带来的量纲问题
        float toeWorldL = _lToe != null ? Vector3.Distance(_lFoot.position, _lToe.position) : 0.10f;
        float toeWorldR = _rToe != null ? Vector3.Distance(_rFoot.position, _rToe.position) : 0.10f;
        toeWorldL = Mathf.Clamp(toeWorldL, 0.03f, 0.30f);
        toeWorldR = Mathf.Clamp(toeWorldR, 0.03f, 0.30f);
        _lHeelLever = toeWorldL * heelLeverScale; _lToeLever = toeWorldL * toeLeverScale;
        _rHeelLever = toeWorldR * heelLeverScale; _rToeLever = toeWorldR * toeLeverScale;

        FixLegacySceneValues();
        ResetState(ref _l); ResetState(ref _r);
        EnablePoseDriver();
        EnsureDiagnosticsSender();

        Debug.Log($"[FootIK v4] 踝→趾 世界距离 L={toeWorldL:F3} R={toeWorldR:F3} m → " +
                  $"脚跟杠杆 L={_lHeelLever:F3} 脚尖杠杆 L={_lToeLever:F3}（可用 heelLeverScale/toeLeverScale 微调）");
        return true;
    }

    /// <summary>
    /// 场景里若缓存了旧脚本版本的默认值（接触判定过宽 → 每步过早锁脚 → 落地回拉打滑），
    /// 只纠正“明显超出合理范围”的项，不动用户手动调过的合理值。
    /// </summary>
    void FixLegacySceneValues()
    {
        if (!fixLegacySceneValues) return;
        if (plantHeightMax > 0.22f)
        {
            Debug.LogWarning($"[FootIK v4] 场景缓存 plantHeightMax={plantHeightMax:F2}（旧默认）→ 修正为 0.16。接触判定更贴合真实接地高度。");
            plantHeightMax = 0.16f;
        }
        if (plantSpeedMax > 0.35f)
        {
            Debug.LogWarning($"[FootIK v4] 场景缓存 plantSpeedMax={plantSpeedMax:F2}（旧默认）→ 修正为 0.30。");
            plantSpeedMax = 0.30f;
        }
        // ── v5 迁移 ──
        // planted 上升速率：旧的 8/s 需要 0.1125s 才越过根伺服门槛 0.9，
        // 而释放侧的证据门槛只有 0.06s —— 锚点注定在伺服启用之前被拆掉，
        // 表现为「锚点诞生 → 2 个渲染帧后释放 → 根伺服全程离线 → 滑步」。
        if (plantEntryRate < 20f)
        {
            Debug.LogWarning($"[FootIK v5] 场景缓存 plantEntryRate={plantEntryRate:F0}/s（旧默认）→ 修正为 25/s。"
                           + "旧的 8/s 要 0.1125s 才让 planted 达到根伺服门槛 0.9，而释放证据只要 0.06s。");
            plantEntryRate = 25f;
        }
        // minPlantTime 与速度窗是【重复计时】：窗口已经要求「过去 plantSpeedWindow 秒内
        // 踝位几乎没动」才判 slow，再叠加 0.12s 的候选时长，锚点要落地后约 0.22s 才诞生 ——
        // 而脚跟触地→全掌只有 heelStrikeTime=0.10s，整个滚动相会被跳过。
        // 窗口接管时间证据后，这里只需挡住单包瞬态，0.05s 足够。
        if (minPlantTime > 0.08f)
        {
            Debug.LogWarning($"[FootIK v5] 场景缓存 minPlantTime={minPlantTime:F2}s（旧默认）→ 修正为 0.05s。"
                           + $"速度窗 plantSpeedWindow={plantSpeedWindow:F2}s 已提供时间证据，两者叠加会让锚点诞生过晚。");
            minPlantTime = 0.05f;
        }
        // 速度估计退回单包差分 = 已知会整流偏置，明确提示（除非是用户特意做 A/B）
        if (plantSpeedWindow <= 0f)
            Debug.LogWarning("[FootIK v5] plantSpeedWindow = 0：已退回旧的单包差分速度估计（全波整流），仅用于 A/B 对比。");

        // v6：确认准入判据走的是哪一套
        if (plantContactGate)
            Debug.Log($"[FootIK v6] 准入判据 = 触地几何（垂直速度 ≤ {plantVSpeedMax:F2} m/s + 近地 + 水平硬上限 "
                    + $"{plantHSpeedHardMax:F1} m/s）。不再用世界水平速度 ≤ {plantSpeedMax:F2} m/s —— "
                    + "锚点建立前踝的世界速度 ≈ 根速，那条判据在行走时永远不成立。");
        else
            Debug.LogWarning($"[FootIK v6] plantContactGate = false：已退回 v5 的「世界水平速度 ≤ {plantSpeedMax:F2} m/s」准入判据。"
                           + "行走时 hSpeed ≈ 根速（0.8~1.5 m/s），锚点将几乎无法诞生 —— 仅供 A/B 对比。");
    }

    void EnsureDiagnosticsSender()
    {
        if (!autoSendDiagnostics) return;
        if (GetComponent<FootIkUdpSender>() != null) return;
        gameObject.AddComponent<FootIkUdpSender>();
        Debug.Log("[FootIK v4] 已自动挂载 FootIkUdpSender → 127.0.0.1:8890（Python 桌面监看可看相位/俯仰/滑移）。");
    }

    void EnablePoseDriver()
    {
        if (poseDriver == null && animator != null) poseDriver = animator.GetComponent<HumanoidPoseDriver>();
        if (poseDriver != null) poseDriver.footIKActive = enableFootIK;
    }

    /// <summary>
    /// 自愈式状态初始化。
    /// 为什么需要：`FootState` 是 struct，`candYs`/`winT` 是引用字段，默认值是 null。
    /// 正常路径由 Awake→ResetState 分配，但只要 Awake 在 ResetState 之前因任何原因
    /// 提前返回（骨骼缺失、isHuman 判定失败…），而 enabled 仍为 true，
    /// LateUpdate 就会每帧在 MedianY 里对 null 取下标 —— 抛出的 NRE 会掐断整个 LateUpdate，
    /// 第二只脚、ApplyFootIK、根伺服、滑移度量全部不执行 = 脚锁系统静默死亡，只剩纯 FK。
    /// 症状看起来就是「滑步照旧」，极难从画面上和「锚点没生效」区分。所以这里兜一道底。
    /// </summary>
    void EnsureState(ref FootState st)
    {
        if (st.candYs == null || st.candYs.Length != CandBuffer)
            st.candYs = new float[CandBuffer];
        if (st.winT == null) st.winT = new List<double>();
        if (st.winP == null) st.winP = new List<Vector3>();
    }

    // 组件被重新启用（Inspector 勾选 / 脚本置位）时 Awake 不会重跑，这里补一次初始化
    void OnEnable() { EnsureState(ref _l); EnsureState(ref _r); }

    void ResetState(ref FootState s)
    {
        s = new FootState();
        s.prevTime = -1;
        s.lastFrameId = -1;
        s.planted = 0f;
        s.releaseReason = "--";
        s.candYs = new float[CandBuffer];
        s.candYHead = 0;
        s.winT = new List<double>();
        s.winP = new List<Vector3>();
        s.winSpan = 0f; s.winDup = 0; s.winDrop = 0; s.winCount = 0;
        s.histP1 = s.histP2 = Vector3.zero;
        s.filtPlan = Vector3.zero;
        s.lowHyst = false; s.groundHyst = false;
        s.candLow = false; s.candSlow = false; s.candGround = false;
        s.dataDt = 0f;
        s.phase = PhaseSwing;
        s.yawFwd = Vector3.forward;
        s.target = Vector3.zero;
        s.targetInit = false;
    }

    void CacheBone(Transform bone, Transform child, out Quaternion rest, out Vector3 axis)
    {
        rest = bone.localRotation;
        axis = child == null ? Vector3.forward : child.localPosition.normalized;
        if (axis.sqrMagnitude < 1e-6f) axis = Vector3.forward;
    }

    void CacheFootBasis(Transform foot, Transform toe, out Quaternion rest, out Vector3 fwdLocal, out Vector3 upLocal)
    {
        rest = foot.localRotation;
        fwdLocal = toe != null ? toe.localPosition.normalized : Vector3.forward;
        if (fwdLocal.sqrMagnitude < 1e-6f) fwdLocal = Vector3.forward;
        upLocal = foot.InverseTransformDirection(Vector3.up);
        upLocal = Vector3.ProjectOnPlane(upLocal, fwdLocal).normalized;
        if (upLocal.sqrMagnitude < 1e-6f) upLocal = Vector3.up;
    }

    void LateUpdate()
    {
        // Awake 可能早于 Animator 的 Avatar 就位（Prefab 实例化 / 换模型的时序），
        // 所以这里再给一次机会；仍然失败就干脆不动，绝不半初始化地跑。
        if (!_inited)
        {
            _inited = TryInit();
            if (!_inited) return;
        }
        EnablePoseDriver();
        if (!enableFootIK || animator == null || !animator.isHuman) return;
        if (poseDriver == null) return;

        _frontAgeMs = poseDriver.receiver != null ? poseDriver.receiver.LatestPacketAgeMs : float.PositiveInfinity;

        // ── 1) 状态机 + 目标（每帧只推进一次）──
        UpdateFootState(ref _l, poseDriver.PlannedLeftAnkle, true);
        UpdateFootState(ref _r, poseDriver.PlannedRightAnkle, false);

        // ── 2) 解算 IK ──
        int iters = Mathf.Clamp(rootServoIterations, 1, 4);
        float orientFactor = enableRootServo ? 1f / (iters + 1f) : 1f;
        ApplyFootIK(ref _l, true, orientFactor);
        ApplyFootIK(ref _r, false, orientFactor);

        // ── 3) 根伺服：把“脚够不到”的误差还给整体位移（抗滑核心）──
        rootServoCorrection = 0f;
        if (enableRootServo)
        {
            Vector3 corr = ComputeRootCorrection();
            rootServoCorrection = corr.magnitude;
            if (corr.sqrMagnitude > 1e-10f)
            {
                if (_rootMove == null) ResolveRootMove();
                if (_rootMove != null) _rootMove.position += corr;
                for (int i = 0; i < iters; i++)
                {
                    ApplyFootIK(ref _l, true, orientFactor);
                    ApplyFootIK(ref _r, false, orientFactor);
                }
            }
        }

        // ── 4) 滑移度量 + 支撑权重回传（供骨盆摆动/起伏 + 下蹲判据）──
        UpdateSlideMetrics();
        if (poseDriver != null)
        {
            poseDriver.LeftSupportWeight = _l.hasAnchor && !_l.releasing ? _l.planted : 0f;
            poseDriver.RightSupportWeight = _r.hasAnchor && !_r.releasing ? _r.planted : 0f;
        }

        reachLimited = _l.unreach || _r.unreach;
        reachReason = reachLimited ? "腿部不可达" : "";

        if (logDiagnostics && Time.frameCount % 30 == 0)
            Debug.Log($"[FootIK] age={_frontAgeMs:F0}ms servo={rootServoCorrection * 100f:F1}cm | L:[{Diag(_l)}] | R:[{Diag(_r)}]");
    }

    void ResolveRootMove()
    {
        var follower = GetComponent<AvatarRootFollower>();
        _rootMove = (follower != null && follower.avatarRoot != null) ? follower.avatarRoot : transform;
    }

    string Diag(FootState s) =>
        $"plant={s.planted:F2} anchor={s.anchorId} phase={PhaseName(s.phase, s.hasAnchor)} pitch={s.pitchDeg:+0;-0}° " +
        $"rel={(s.hasAnchor || s.releasing ? s.releaseReason : s.releaseReason + "(上段)")} " +
        $"rise={s.liftRise:F2} dev={s.anchorDev:F2} adist={s.anchorDist:F2} " +
        $"dist={(s.hasAnchor ? s.distToAnchor.ToString("F3") : "-")} " +
        // ★v5：hSpeed 与候选两个分量过去从不显示 —— 正是这次滑步排查最难看到的一组量
        $"h={s.hSpeed:F2} v={s.vSpeed:+0.00;-0.00} " +
        $"low={(s.candLow ? "1" : "0")} slow={(s.candSlow ? "1" : "0")} gnd={(s.candGround ? "1" : "0")} " +
        $"ct={s.candTime:F2} n={s.candCount} " +
        $"win={s.winSpan:F3}s/{s.winCount}{(s.winDrop > 0 ? $" drop={s.winDrop}" : "")}{(s.winDup > 0 ? $" dup={s.winDup}" : "")} " +
        $"slide={s.slideSpeed * 100f:F1}cm/s unreach={s.unreach}";

    // ══════════════════════════════════════════════════════════════
    // 状态机：接触候选 → 锚点 → 释放 + 步态相位 + 目标
    // ══════════════════════════════════════════════════════════════

    void UpdateFootState(ref FootState st, Vector3 rawPlan, bool isLeft)
    {
        EnsureState(ref st);   // 兜底：Awake 若提前返回，这里补上引用字段，绝不抛 NRE
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        bool newFrame = false;
        bool validFrame = false;
        float effDt = dt;

        var recv = poseDriver.receiver;
        if (recv != null && recv.LatestFrameId != st.lastFrameId)
        {
            double now = recv.LatestTime;
            // v5：先用 3 点分量中值剔掉单包离群点，再入窗 —— 遮挡/幻觉尖峰不再污染速度
            st.filtPlan = ComputeFiltPlan(ref st, rawPlan);

            if (st.lastFrameId >= 0 && now > st.prevTime && now - st.prevTime > 1e-4)
            {
                validFrame = true;
                effDt = Mathf.Clamp((float)(now - st.prevTime), 1e-4f, 0.5f);   // v5：钳位，防时间戳毛刺
                PushWindow(ref st, now, st.filtPlan);
                WindowVelocity(ref st, out st.hSpeed, out st.vSpeed, out st.winSpan);
            }
            else PushWindow(ref st, now, st.filtPlan);   // 首包/时间戳回退：只入窗，不产生速度

            st.lastFrameId = recv.LatestFrameId;
            st.prevPlan = rawPlan;
            st.prevTime = now;
            st.init = true;
            newFrame = true;
        }
        else if (recv == null)
        {
            // 无接收器（离线回放）：没有采集时间戳，用渲染时间近似
            st.filtPlan = ComputeFiltPlan(ref st, rawPlan);
            PushWindow(ref st, Time.timeAsDouble, st.filtPlan);
            WindowVelocity(ref st, out st.hSpeed, out st.vSpeed, out st.winSpan);
            st.prevPlan = rawPlan;
            validFrame = true;
            newFrame = true;
        }
        st.effDt = validFrame ? effDt : dt;
        st.dataDt = validFrame ? effDt : 0f;   // v5：证据计时器只认这个（渲染帧恒为 0）
        st.winCount = st.winT != null ? st.winT.Count : 0;

        if (st.hSpeed < hSpeedDeadzone) st.hSpeed = 0f;
        if (Mathf.Abs(st.vSpeed) < vSpeedDeadzone) st.vSpeed = 0f;

        // 低通计划踝位（摆动跟踪 / 交接基准）
        if (!st.smoothInit) { st.smoothPlan = rawPlan; st.smoothInit = true; }
        else
        {
            float a = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, swingLowpassTimeConst));
            st.smoothPlan = Vector3.Lerp(st.smoothPlan, rawPlan, a);
        }

        // v5：足高用中值滤波后的计划位；近地判据加迟滞（进入 plantHeightMax，退出 +plantHeightHyst），
        //     否则 Y 的 ±抖动会让候选窗在阈值上反复开关，candTime 永远攒不满 minPlantTime
        float footH = st.filtPlan.y - boardY;
        bool low = st.lowHyst ? (footH <= plantHeightMax + plantHeightHyst)
                              : (footH <= plantHeightMax);
        st.lowHyst = low;
        // v5 判据（保留：仅诊断 + A/B 对比开关用）
        bool slow = st.hSpeed <= plantSpeedMax;
        // ★v6：触地判据。垂直速度不被根的水平位移污染，是这里唯一可信的「脚停住了」的证据。
        // 取绝对值：判的是「垂直运动已停止」。上行(蹬离抬跟)和快速下落(还没落地)都不该被锚住。
        //
        // ★v6.1：必须和上面的 low 一样加迟滞 —— 这正是第 730-731 行作者给 low 加迟滞时
        //   写下的同一个理由。vSpeed 是零均值 ~2Hz 振荡，行走时有 25~30% 的帧自然越阈；
        //   而第 762-766 行是【硬清零】(candTime=0, candCount=0)，任何一帧越阈就把之前
        //   攒的候选全部作废。没有迟滞时实测 candCount 在未锚定状态下【最大恒为 1】，
        //   锚点因此永远诞生不了。进入 0.15 / 退出 0.30（退出门沿用已有的 plantSpeedMax，
        //   正好 2×，与 low 的 plantHeightHyst 同比例），不新增任何参数。
        if (Mathf.Abs(st.vSpeed) <= plantVSpeedMax) st.groundHyst = true;
        else if (Mathf.Abs(st.vSpeed) > plantSpeedMax) st.groundHyst = false;
        bool grounded = st.groundHyst;
        bool sane = st.hSpeed <= plantHSpeedHardMax;   // 兜底：挡摆动中段飞过去的帧
        bool candidate = low && sane && (plantContactGate ? grounded : slow);
        st.candLow = low; st.candSlow = slow;      // 诊断显性化
        st.candGround = grounded;

        // ── 候选统计（仅有效新帧）──
        if (newFrame && validFrame)
        {
            if (candidate)
            {
                st.candTime += effDt;
                st.candSumX += rawPlan.x;
                st.candSumZ += rawPlan.z;
                st.candLastX = rawPlan.x;
                st.candLastZ = rawPlan.z;
                st.candCount++;
                st.candYs[st.candYHead] = rawPlan.y;
                st.candYHead = (st.candYHead + 1) % CandBuffer;
            }
            else
            {
                st.candTime = 0f;
                st.candSumX = st.candSumZ = 0;
                st.candLastX = st.candLastZ = 0f;
                st.candCount = 0;
                st.candYHead = 0;
            }
        }

        // ── 锚点生命周期 ──
        if (!st.hasAnchor)
        {
            if (!st.releasing && st.candTime >= minPlantTime && candidate && st.candCount > 0)
            {
                st.plantBaseY = MedianY(st);
                // v4：锚点取“锁定瞬间的计划踝位”，不再用候选窗口平均 → 去掉落地回拉小滑
                float ax = st.candCount > 0 ? st.candLastX : rawPlan.x;
                float az = st.candCount > 0 ? st.candLastZ : rawPlan.z;
                st.anchor = new Vector3(ax, boardY + ankleOffset, az);
                st.hasAnchor = true;
                st.anchorId++;
                st.planted = 0f;
                st.releasing = false;
                st.releaseReason = "none";
                st.vEvidTime = 0f; st.stepDevTimeAcc = 0f; st.unreachTimeAcc = 0f;
                st.stanceTime = 0f;
                st.yawFwd = SwingYawFlat(isLeft);        // 落地瞬间冻结足部朝向 → 支撑相不旋转
                st.slideDist = 0f; st.anchorAge = 0f; st.slideSpeed = 0f;
            }
            st.distToAnchor = 0f;
            st.phase = PhaseSwing;
        }
        else
        {
            float liftRise = rawPlan.y - st.plantBaseY;
            float anchorDev = HorizontalLen(st.smoothPlan - st.anchor);
            float anchorDist = Vector3.Distance(UpBone(isLeft).position, st.anchor);
            st.liftRise = liftRise;
            st.anchorDev = anchorDev;
            st.anchorDist = anchorDist;
            float reach = thigh(isLeft) + shin(isLeft) + unreachTolerance;

            // ★v5：整段释放判据只在【有效新数据帧】上评估。
            //   旧代码没有这道门：同一份陈旧样本会在每个渲染帧被重新测试一遍，
            //   一个瞬时噪声尖峰能被重复计成好几份证据 —— 释放侧因此比进入侧快得多
            //   （进入要 3 个干净数据包，释放只要 0.06s 的墙钟时间），
            //   锚点几乎注定在 planted 升到 0.9 之前就被拆掉。
            if (!st.releasing && validFrame)
            {
                float evDt = st.dataDt;      // 有效数据帧的真实包间隔
                // A) 明确抬升(相对基准) → 立即放
                if (liftRise > riseThreshold)
                {
                    st.releasing = true;
                    st.releaseReason = "lifted";
                }
                // D) 持续不可达(针对锚点,含容差) → 放
                else if (anchorDist > reach)
                {
                    st.unreachTimeAcc += evDt;
                    if (st.unreachTimeAcc >= unreachTime) { st.releasing = true; st.releaseReason = "stuck"; }
                }
                else
                {
                    st.unreachTimeAcc = 0f;
                    // B) 向上速度 + 小幅实际抬升 + 连续证据
                    if (st.vSpeed > liftVSpeed && liftRise > vEvidenceRise)
                    {
                        st.vEvidTime += evDt;
                        if (st.vEvidTime >= vEvidenceTime) { st.releasing = true; st.releaseReason = "liftedV"; }
                    }
                    else st.vEvidTime = Mathf.Max(0f, st.vEvidTime - evDt);

                    // C) 低位迈步释放(独立目标偏离锚点)
                    if (anchorDev > stepDev)
                    {
                        st.stepDevTimeAcc += evDt;
                        if (st.stepDevTimeAcc >= stepDevTime) { st.releasing = true; st.releaseReason = "step"; }
                    }
                    else st.stepDevTimeAcc = Mathf.Max(0f, st.stepDevTimeAcc - evDt);

                    if (!st.releasing)
                    {
                        st.releaseReason = "none";
                        st.planted = Mathf.MoveTowards(st.planted, 1f, plantEntryRate * dt);
                    }
                }
            }

            if (st.releasing)
            {
                st.planted = Mathf.MoveTowards(st.planted, 0f, plantExitRateFast * dt);
                if (st.planted <= 0f)
                {
                    st.hasAnchor = false;
                    st.planted = 0f;
                    st.releasing = false;
                    st.glideTime = releaseGlideTime;     // 离锚后继续限速过渡，避免瞬移
                    st.candTime = 0f; st.candSumX = st.candSumZ = 0; st.candCount = 0; st.candYHead = 0;
                    st.candLastX = st.candLastZ = 0f;
                    st.vEvidTime = 0f; st.stepDevTimeAcc = 0f; st.unreachTimeAcc = 0f;
                    st.distToAnchor = 0f;
                }
            }
        }

        // ── 步态相位 → 俯仰角 ──
        float pitchTargetDeg = ComputePitchTarget(ref st, isLeft, rawPlan);
        {
            float a = 1f - Mathf.Exp(-dt / Mathf.Max(0.005f, rollSmoothTime));
            st.pitchDeg = Mathf.Lerp(st.pitchDeg, pitchTargetDeg, a);
        }

        // ── IK 目标 ──
        Vector3 desired;
        if (st.hasAnchor)
        {
            float p = st.pitchDeg * Mathf.Deg2Rad;
            float lever = p >= 0f ? HeelLever(isLeft) : -ToeLever(isLeft);
            // 踝高随俯仰做枢轴补偿：脚绕脚跟/脚尖滚动，而不是整只脚平移
            float ankleY = boardY + ankleOffset * Mathf.Cos(p) + lever * Mathf.Sin(p);
            float y = Mathf.Lerp(st.smoothPlan.y, ankleY, Mathf.Clamp01(st.planted));
            desired = new Vector3(st.anchor.x, y, st.anchor.z);
            if (st.releasing) desired = Vector3.Lerp(st.smoothPlan, desired, st.planted);
        }
        else desired = st.smoothPlan;

        // 释放/交接期间限速滑行，消除 12.5ms 瞬移
        if (!st.targetInit) { st.target = desired; st.targetInit = true; }
        else
        {
            float maxMove = (st.hasAnchor || st.glideTime > 0f) ? releaseGlideSpeed * dt : float.PositiveInfinity;
            st.target = Vector3.MoveTowards(st.target, desired, maxMove);
        }
        if (st.glideTime > 0f) st.glideTime = Mathf.Max(0f, st.glideTime - dt);
    }

    /// <summary>按相位给出足部俯仰角（度，>0 脚尖上抬）。</summary>
    float ComputePitchTarget(ref FootState st, bool isLeft, Vector3 rawPlan)
    {
        if (!enableFootRoll)
        {
            st.phase = st.hasAnchor ? PhaseFlat : PhaseSwing;
            return 0f;
        }

        if (!st.hasAnchor)
        {
            // 摆动三段（用“脚相对髋的前后位置”区分早/中/晚，比只看高度可靠）：
            //   刚离地(在身后) → 保持趾屈拖尾；中段(髋下) → 轻微背屈；临近落地(在前方) → 明显背屈(脚跟着地准备)
            st.phase = PhaseSwing;
            Vector3 sd = rawPlan - UpBone(isLeft).position; sd.y = 0f;
            float sBehind = -Vector3.Dot(sd, BodyFwdFlat());     // >0 脚在身后
            float backW = Mathf.Clamp01((sBehind - 0.03f) / 0.15f);
            float frontW = Mathf.Clamp01((-sBehind) / 0.12f);
            float p = Mathf.Lerp(swingToeUpDeg, heelStrikePitchDeg, frontW);
            return Mathf.Lerp(p, -toeOffPitchDeg * 0.6f, backW);
        }

        st.stanceTime += st.effDt;
        if (!st.releasing && st.stanceTime < heelStrikeTime)
        {
            // 脚跟着地 → 全掌：脚尖从抬起角滚到 0
            st.phase = PhaseHeelStrike;
            float t = Mathf.Clamp01(st.stanceTime / Mathf.Max(0.01f, heelStrikeTime));
            return heelStrikePitchDeg * (1f - t);
        }

        // 蹬离：锚点落到髋后方 toeOffBehindDist 之外，脚尖逐渐下压
        Vector3 fwdFlat = BodyFwdFlat();
        Vector3 d = st.anchor - UpBone(isLeft).position; d.y = 0f;
        float behind = -Vector3.Dot(d, fwdFlat);
        float ramp = Mathf.Clamp01((behind - toeOffBehindDist) / Mathf.Max(0.01f, toeOffRampDist));
        if (ramp > 0f) { st.phase = PhaseToeOff; return -toeOffPitchDeg * ramp; }
        st.phase = PhaseFlat;
        return 0f;
    }

    float MedianY(FootState st)
    {
        int n = Mathf.Min(st.candCount, CandBuffer);
        if (n <= 0 || st.candYs == null) return st.prevPlan.y;
        if (n > st.candYs.Length) n = st.candYs.Length;
        float[] tmp = new float[n];
        int start = (st.candYHead - n + CandBuffer) % CandBuffer;
        for (int i = 0; i < n; i++) tmp[i] = st.candYs[(start + i) % CandBuffer];
        System.Array.Sort(tmp);
        if ((n & 1) == 1) return tmp[n / 2];
        return (tmp[n / 2 - 1] + tmp[n / 2]) * 0.5f;
    }

    static float HorizontalLen(Vector3 v)
    {
        v.y = 0f;
        return v.magnitude;
    }

    static bool Finite(Vector3 v)
    {
        return !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
                 float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));
    }

    static Vector3 Median3(Vector3 a, Vector3 b, Vector3 c)
    {
        return new Vector3(Med3(a.x, b.x, c.x), Med3(a.y, b.y, c.y), Med3(a.z, b.z, c.z));
    }

    static float Med3(float a, float b, float c)
    {
        if (a > b) { float t = a; a = b; b = t; }
        if (b > c) { b = c; }
        return a > b ? a : b;
    }

    /// <summary>v5：3 点分量中值。遮挡/幻觉造成的单包离群点被整点剔除，代价仅 1 个包延迟。</summary>
    Vector3 ComputeFiltPlan(ref FootState st, Vector3 rawPlan)
    {
        if (!Finite(rawPlan)) rawPlan = st.histP1;
        Vector3 f = planMedian3 ? Median3(st.histP2, st.histP1, rawPlan) : rawPlan;
        if (!Finite(f)) f = rawPlan;
        st.histP2 = st.histP1;
        st.histP1 = rawPlan;
        return f;
    }

    const int WinCap = 32;

    /// <summary>
    /// v5：把样本压进滑动时间窗。重复样本与超速跳变样本【只丢弃该样本，不清空窗口】——
    /// 旧实现遇到坏包会把候选链整个清零，噪声一来锚点就归零重攒。
    /// </summary>
    void PushWindow(ref FootState st, double time, Vector3 plan)
    {
        if (st.winT == null) { st.winT = new List<double>(); st.winP = new List<Vector3>(); }
        if (!Finite(plan)) return;

        TrimWindow(st.winT, st.winP, time);
        if (st.winT.Count == 0) { st.winT.Add(time); st.winP.Add(plan); return; }

        double dtPrev = time - st.winT[st.winT.Count - 1];
        if (dtPrev <= 1e-4) { st.winDup++; return; }              // 同一包的重复投递

        float dxy = HorizontalLen(plan - st.winP[st.winP.Count - 1]);
        float allowed = plantWindowMaxStep * Mathf.Max((float)dtPrev, 0.02f);
        if (dxy > allowed) { st.winDrop++; return; }              // 幻觉跳变 → 丢该样本，留窗降噪

        st.winT.Add(time); st.winP.Add(plan);
        if (st.winT.Count > WinCap) { st.winT.RemoveAt(0); st.winP.RemoveAt(0); }
        TrimWindow(st.winT, st.winP, time);
    }

    void TrimWindow(List<double> ts, List<Vector3> ps, double now)
    {
        // plantSpeedWindow = 0 → 只留两个样本 ≈ 退回旧的单包差分，用于 A/B
        if (plantSpeedWindow <= 0f)
        {
            while (ts.Count > 2) { ts.RemoveAt(0); ps.RemoveAt(0); }
            return;
        }
        int keep = Mathf.Max(2, plantWindowKeep);
        while (ts.Count > keep && now - ts[0] > plantSpeedWindow) { ts.RemoveAt(0); ps.RemoveAt(0); }
    }

    /// <summary>
    /// ★核心：窗口首尾差分。对 X/Z/Y 各自【带符号】差分再合成水平模长。
    /// 旧实现逐帧 dH.magnitude 是全波整流 —— 抖动一来一回不抵消，每包全额贡献正值，
    /// 再乘 fr≈24 就把 ±1.2cm 抖动恒定读成 ≈0.30 m/s，永远压着 plantSpeedMax。
    /// 窗口化后抖动按 dt/T 衰减，且带符号差分的期望为零 → 系统性正偏置消失。
    /// </summary>
    void WindowVelocity(ref FootState st, out float hSpeed, out float vSpeed, out float span)
    {
        hSpeed = 0f; vSpeed = 0f; span = 0f;
        int n = st.winT != null ? st.winT.Count : 0;
        if (n < 2) return;
        double dtW = st.winT[n - 1] - st.winT[0];
        if (dtW < 1e-4) return;
        // 时间戳毛刺 → 包间隔短得不可能，本帧不给速度（宁可保持上一帧，也别把 1mm 读成 10m/s）
        if (1.0 / dtW > frMax) return;
        span = (float)dtW;
        Vector3 d = st.winP[n - 1] - st.winP[0];
        // 除以真实窗口跨度即得速度；不需要额外的 fr —— 旧代码的 fr 就来自单包间隔
        hSpeed = new Vector2(d.x, d.z).magnitude / span;
        vSpeed = d.y / span;
        if (float.IsNaN(hSpeed) || float.IsInfinity(hSpeed)) hSpeed = 0f;
        if (float.IsNaN(vSpeed) || float.IsInfinity(vSpeed)) vSpeed = 0f;
    }

    // ══════════════════════════════════════════════════════════════
    // IK 应用 + 足部朝向
    // ══════════════════════════════════════════════════════════════

    void ApplyFootIK(ref FootState st, bool isLeft, float orientFactor)
    {
        Transform up = UpBone(isLeft);
        Transform lo = isLeft ? _lLo : _rLo;
        Transform foot = isLeft ? _lFoot : _rFoot;

        Solve2BoneIKDirect(up, lo, st.target,
            isLeft ? _lUpRest : _rUpRest, isLeft ? _lUpAxis : _rUpAxis,
            isLeft ? _lLoRest : _rLoRest, isLeft ? _lLoAxis : _rLoAxis,
            thigh(isLeft), shin(isLeft), ref st.polePerp, out bool clamped);
        st.unreach = clamped;
        st.distToAnchor = st.hasAnchor ? Vector3.Distance(foot.position, st.anchor) : 0f;

        // v4：摆动脚也写朝向（不再冻结在上一个支撑相）
        Vector3 fwdFlat = st.hasAnchor ? st.yawFwd : SwingYawFlat(isLeft);
        if (fwdFlat.sqrMagnitude < 1e-6f) fwdFlat = BodyFwdFlat();
        float p = st.pitchDeg * Mathf.Deg2Rad;
        Vector3 worldFwd = fwdFlat * Mathf.Cos(p) + Vector3.up * Mathf.Sin(p);
        Vector3 worldUp = Vector3.up * Mathf.Cos(p) - fwdFlat * Mathf.Sin(p);
        SetFootOrientation(foot,
            isLeft ? _lFootRestLocal : _rFootRestLocal,
            isLeft ? _lFootFwdLocal : _rFootFwdLocal,
            isLeft ? _lFootUpLocal : _rFootUpLocal,
            worldFwd, worldUp, footRotateSmooth * Mathf.Clamp(orientFactor, 0.05f, 1f));
    }

    /// <summary>把足骨转到“脚尖朝 worldFwd、脚背朝 worldUp”的朝向（从 rest 基底换算）。</summary>
    void SetFootOrientation(Transform foot, Quaternion rest, Vector3 fwdLocal, Vector3 upLocal,
        Vector3 worldFwd, Vector3 worldUp, float smooth)
    {
        if (foot == null) return;
        if (worldFwd.sqrMagnitude < 1e-6f || worldUp.sqrMagnitude < 1e-6f) return;

        Vector3 desiredParentFwd = foot.parent != null ? foot.parent.InverseTransformDirection(worldFwd.normalized) : worldFwd.normalized;
        Vector3 desiredParentUp = foot.parent != null ? foot.parent.InverseTransformDirection(worldUp.normalized) : worldUp.normalized;
        Vector3 restParentFwd = rest * fwdLocal;
        Vector3 restParentUp = rest * upLocal;
        Quaternion restBasis = SafeLook(restParentFwd, restParentUp);
        Quaternion targetBasis = SafeLook(desiredParentFwd, desiredParentUp);
        Quaternion targetLocal = targetBasis * Quaternion.Inverse(restBasis) * rest;
        foot.localRotation = Quaternion.Slerp(foot.localRotation, targetLocal, Time.deltaTime * smooth);
    }

    Transform UpBone(bool isLeft) { return isLeft ? _lUp : _rUp; }
    float thigh(bool isLeft) { return isLeft ? _lThigh : _rThigh; }
    float shin(bool isLeft) { return isLeft ? _lShin : _rShin; }
    float HeelLever(bool isLeft) { return isLeft ? _lHeelLever : _rHeelLever; }
    float ToeLever(bool isLeft) { return isLeft ? _lToeLever : _rToeLever; }

    /// <summary>身体水平前向（不含俯仰）。</summary>
    Vector3 BodyFwdFlat()
    {
        Vector3 fwd = _hips != null ? (_hips.rotation * _hipsForwardLocal) : Vector3.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-6f) return Vector3.forward;
        return fwd.normalized;
    }

    /// <summary>摆动/落地瞬间的足部水平朝向 = 身体前向 + 轻微外八。</summary>
    Vector3 SwingYawFlat(bool isLeft)
    {
        Vector3 fwd = BodyFwdFlat();
        float ang = toeOutDeg * toeOutSign * (isLeft ? -1f : 1f);
        if (Mathf.Abs(ang) > 0.01f) fwd = Quaternion.AngleAxis(ang, Vector3.up) * fwd;
        fwd.y = 0f;
        return fwd.sqrMagnitude < 1e-6f ? Vector3.forward : fwd.normalized;
    }

    // ══════════════════════════════════════════════════════════════
    // 根伺服
    // ══════════════════════════════════════════════════════════════

    Vector3 ComputeRootCorrection()
    {
        Vector3 acc = Vector3.zero;
        float wsum = 0f;
        AccumulateCorrection(ref _l, _lFoot, ref acc, ref wsum);
        AccumulateCorrection(ref _r, _rFoot, ref acc, ref wsum);
        if (wsum <= 1e-4f) return Vector3.zero;
        Vector3 corr = acc / wsum;
        corr.y = 0f;
        if (!float.IsFinite(corr.x) || !float.IsFinite(corr.z)) return Vector3.zero;
        corr *= Mathf.Clamp01(rootServoGain);
        // ★v5：单位由“米/帧”改为“米/秒”。旧 rootServoMaxPerFrame=0.04 在 32fps 下等于 1.28m/s，
        //   60fps→2.4、120fps→4.8，会反超 AvatarRootFollower.maxSpeed=1.5，根节点在推送/拽回间抽动。
        float maxStep = Mathf.Max(0f, rootServoMaxSpeed) * Mathf.Max(Time.deltaTime, 1e-4f);
        return Vector3.ClampMagnitude(corr, maxStep);
    }

    void AccumulateCorrection(ref FootState st, Transform foot, ref Vector3 acc, ref float wsum)
    {
        // 只在“稳定支撑”（权重已满、未在释放）时伺服：此时若腿没被拉长，误差≈0，不会乱动身体
        if (!st.hasAnchor || st.releasing || st.planted < 0.9f) return;
        if (foot == null) return;
        Vector3 err = st.anchor - foot.position;
        err.y = 0f;
        if (err.sqrMagnitude > 0.25f) return;      // 误差 >50cm 视为数据异常，不伺服
        acc += err * st.planted;
        wsum += st.planted;
    }

    void UpdateSlideMetrics()
    {
        TrackSlide(ref _l, _lFoot);
        TrackSlide(ref _r, _rFoot);
    }

    void TrackSlide(ref FootState st, Transform foot)
    {
        if (foot == null) return;
        if (st.hasAnchor && !st.releasing && st.planted >= 0.9f)
        {
            if (st.prevFootValid)
            {
                Vector3 d = foot.position - st.prevFootPos;
                d.y = 0f;
                st.anchorAge += Mathf.Max(0f, st.effDt);
                // ★v5：先扣掉噪声地板再累计。旧代码用 d.magnitude 直接累加，是整流量 ——
                //   一个锁死的脚在抖动下也会稳定报出 cm/s 的假滑步，诊断本身在说谎。
                float inst = d.magnitude;
                float instSpeed = st.effDt > 1e-4f ? inst / st.effDt : 0f;
                if (instSpeed > slideNoiseFloor)
                {
                    float excess = inst - slideNoiseFloor * Mathf.Max(st.effDt, 0f);
                    st.slideDist += Mathf.Max(0f, excess);
                }
                st.slideSpeed = st.anchorAge > 1e-3f ? st.slideDist / st.anchorAge : 0f;
            }
            st.prevFootPos = foot.position;
            st.prevFootValid = true;
        }
        else
        {
            st.prevFootValid = false;
            st.slideSpeed = 0f;
        }
    }

    // ══════════════════════════════════════════════════════════════
    // 两骨 IK
    // ══════════════════════════════════════════════════════════════

    void Solve2BoneIKDirect(Transform up, Transform lo, Vector3 targetAnkle,
        Quaternion upRest, Vector3 upAxis, Quaternion loRest, Vector3 loAxis,
        float thigh, float shin, ref Vector3 cachedPolePerp, out bool clamped)
    {
        clamped = false;
        if (up == null || lo == null) return;

        Vector3 hipPos = up.position;
        Vector3 toTarget = targetAnkle - hipPos;
        float rawD = toTarget.magnitude;
        float maxD = thigh + shin - reachMargin;
        float minD = Mathf.Abs(thigh - shin) + reachMargin;

        if (rawD > maxD + 1e-4f || rawD < minD - 1e-4f) clamped = true;
        float d = Mathf.Clamp(rawD, minD, maxD);
        Vector3 dirN = d > 1e-5f ? toTarget.normalized : Vector3.down;
        Vector3 clampedTarget = hipPos + dirN * d;

        float a = (thigh * thigh - shin * shin + d * d) / (2f * d);
        float h = Mathf.Sqrt(Mathf.Max(0f, thigh * thigh - a * a));

        Vector3 pole = _hips != null ? (_hips.rotation * _hipsForwardLocal) : Vector3.forward;
        Vector3 desired = pole - Vector3.Project(pole, dirN);
        if (desired.sqrMagnitude < 1e-6f)
        {
            if (cachedPolePerp.sqrMagnitude < 1e-6f) desired = Vector3.Cross(dirN, Vector3.up);
            else desired = cachedPolePerp;
        }
        if (desired.sqrMagnitude < 1e-6f) desired = Vector3.forward;
        desired = Vector3.ProjectOnPlane(desired, dirN);
        if (desired.sqrMagnitude < 1e-6f) desired = Vector3.Cross(dirN, Vector3.up);
        desired.Normalize();
        if (cachedPolePerp.sqrMagnitude > 1e-6f && Vector3.Dot(cachedPolePerp, desired) < 0f)
            desired = -desired;
        cachedPolePerp = desired;

        Vector3 kneePos = hipPos + dirN * a + desired * h;
        OrientBoneDirect(up, upRest, upAxis, (kneePos - hipPos).normalized);
        OrientBoneDirect(lo, loRest, loAxis, (clampedTarget - kneePos).normalized);
    }

    void OrientBoneDirect(Transform bone, Quaternion rest, Vector3 restAxisLocal, Vector3 desiredWorldDir)
    {
        if (bone == null || desiredWorldDir.sqrMagnitude < 1e-6f) return;
        Vector3 desiredParent = bone.parent != null
            ? bone.parent.InverseTransformDirection(desiredWorldDir.normalized)
            : desiredWorldDir.normalized;
        if (desiredParent.sqrMagnitude < 1e-6f) return;
        Vector3 restParent = rest * restAxisLocal;
        if (restParent.sqrMagnitude < 1e-6f) return;
        Quaternion delta = Quaternion.FromToRotation(restParent.normalized, desiredParent.normalized);
        bone.localRotation = delta * rest;
    }

    Quaternion SafeLook(Vector3 fwd, Vector3 up)
    {
        if (fwd.sqrMagnitude < 1e-6f) return Quaternion.identity;
        fwd.Normalize();
        up = Vector3.ProjectOnPlane(up, fwd);
        if (up.sqrMagnitude < 1e-6f) up = Vector3.ProjectOnPlane(Vector3.up, fwd);
        if (up.sqrMagnitude < 1e-6f) return Quaternion.identity;
        return Quaternion.LookRotation(fwd, up.normalized);
    }

    void OnGUI()
    {
        if (!showOverlay || !enableFootIK) return;
        float x = 10, y = 350, w = 760, lh = 15;   // y 从 220 下移：原来和 RootEst 面板叠在一起看不清
        GUI.Box(new Rect(x, y, w, lh * 11 + 8), "");
        y += 4;
        void L(string s) { GUI.Label(new Rect(x + 6, y, w - 12, lh), s); y += lh; }
        L($"[FootIK v5] 数据帧龄 {(_frontAgeMs == float.PositiveInfinity ? "--" : _frontAgeMs.ToString("F0"))} ms  (非端到端延迟)");
        L($"  L: {Diag(_l)}");
        L($"  R: {Diag(_r)}");
        L($"  根伺服修正 {rootServoCorrection * 100f:F1} cm/帧  上限 {rootServoMaxSpeed:F2} m/s×dt   迭代={rootServoIterations} 增益={rootServoGain:F2}");
        L("  plant=支撑权重 anchor=锚点编号 rel=释放原因 rise=相对基准抬升 dev=偏离锚点 adist=髋->锚点");
        L("  h/v=计划踝位水平/垂向速度(m/s)  low/slow=候选两分量  ct=候选已积累时长 n=候选样本数");
        L("  win=速度窗实际跨度/样本数  drop/dup=被剔除的跳变/重复样本累计  slide=锚定滑移(越小越好)");
        L("  ★ low=1 且 slow=1 才成为候选；ct 必须攒满 minPlantTime 才诞生锚点。若 slow 长期=0 → 速度仍偏高（旧版的整流偏置）");
        L("  [空格测试] 关闭 EnableFootIK 对比 FK。");
    }

    /// <summary>应用修正版释放参数（用于更新场景里缓存的旧默认值）。</summary>
    public void ApplyCorrectedParams()
    {
        plantHeightMax = 0.16f;
        plantSpeedMax = 0.30f;
        minPlantTime = 0.10f;
        riseThreshold = 0.14f;
        liftVSpeed = 0.25f;
        vEvidenceRise = 0.03f;
        vEvidenceTime = 0.06f;
        stepDev = 0.15f;
        stepDevTime = 0.06f;
        unreachTolerance = 0.02f;
        unreachTime = 0.15f;
        vSpeedDeadzone = 0.05f;
        hSpeedDeadzone = 0.05f;
        // ── v5 ──
        plantEntryRate = 25f;          // 0.036s 即达根伺服门槛 0.9（旧的 8/s 需 0.1125s）
        rootServoMaxSpeed = 1.20f;     // m/s（旧的 rootServoMaxPerFrame 是 m/帧，已废弃）
        plantSpeedWindow = 0.10f;
        minPlantTime = 0.05f;          // 时间证据已由速度窗提供，不再叠加
        planMedian3 = true;
        frMax = 60f;
        // ── v6 ──
        plantContactGate = true;
        plantVSpeedMax = 0.15f;
        plantHSpeedHardMax = 2.5f;
        Debug.Log($"[FootIK v6] 已应用修正版参数（准入=触地几何 |vSpeed|≤{plantVSpeedMax:F2} / 水平硬上限 "
                + $"{plantHSpeedHardMax:F1} m/s / 速度窗 {plantSpeedWindow:F2}s / minPlantTime {minPlantTime:F2}s / "
                + $"plantEntryRate {plantEntryRate:F0}/s / 根伺服上限 {rootServoMaxSpeed:F2} m/s）。");
    }
}
