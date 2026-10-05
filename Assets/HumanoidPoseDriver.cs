using UnityEngine;
using System.Collections.Generic;

public class HumanoidPoseDriver : MonoBehaviour
{
    public UdpPoseReceiver receiver;        // 前摄 port 8888
    public UdpPoseReceiver leftReceiver;     // 左摄 port 8889
    public Animator targetAnimator;

    [Header("Pose Source")]
    [Tooltip("true=姿态全部用前摄(v4 单摄闭环，推荐)；false=左摄参与四肢混合(需外参标定，当前会拉低腿部质量)")]
    public bool frontOnlyPose = true;

    [Header("Dual-Cam Blend")]
    [Tooltip("左摄对腿骨的混合权重 (0=仅前摄, 0.5=平均, 1=仅左摄)")]
    [Range(0f, 1f)] public float legBlendWeight = 0.5f;
    [Tooltip("禁用身体旋转以单独测试 Root Motion（调试用）")]
    public bool disableBodyRotation = false;

    [Header("Sync / Gating")]
    [Tooltip("前/左摄姿态最大允许时间差（秒），超差退化为仅前摄")]
    public float maxTimeDiff = 0.1f;
    [Tooltip("关键点可见度门控：低于此值的点不参与双摄混合")]
    [Range(0f, 1f)] public float minLandmarkVisibility = 0.3f;

    [Header("Body")]
    public bool enableBodyRotation = true;
    // 配合 Python FRONT_POSE_ALWAYS_SWAP：前摄翻转图 + swap 使肩线恒偏 180°，
    // 恒翻转躯干前向恢复正确朝向（自动前后 + 人物转身）。
    [Tooltip("翻转躯干前向：配合 Python swap 恒需 true")]
    public bool invertBodyForward = true;
    public float bodyRotationSmooth = 8f;

    [Header("Head")]
    public bool enableHeadRotation = true;
    public float headRotationSmooth = 10f;

    [Header("Arms / Legs / Fingers")]
    public float rotationSmooth = 10f;

    [Header("Hands")]
    public bool enableFingerRotation = true;
    public float fingerRotationSmooth = 14f;

    [Header("Foot IK (3rd-party)")]
    [Tooltip("为 true 时，腿部 FK 只计算并暴露“独立计划值”，不直接写显示骨；由 FootPlantingController 接管腿部")]
    public bool footIKActive = false;

    // 独立 FK 计划值（不受 IK 污染）：FootPlantingController 读取作为“期望脚位”来源
    public Vector3 PlannedLeftAnkle;
    public Vector3 PlannedRightAnkle;
    public Quaternion LeftUpperPlanned, LeftLowerPlanned;
    public Quaternion RightUpperPlanned, RightLowerPlanned;
    public Quaternion LeftFootPlanned, RightFootPlanned;
    public bool LeftFootPlannedValid, RightFootPlannedValid;

    [Header("Squat / Pelvis Height")]
    [Tooltip("启用下蹲骨盆升降。需要 footIKActive 生效。")]
    public bool enableSquat = true;
    public float boardY = 0f;                      // 板面高度（骨盆/脚判定用）
    public float squatRecognizeTime = 0.6f;        // 站立基准需持续的秒数(仅有效新帧)
    public float squatMinVisibility = 0.65f;       // 站立识别最低关键点可见度
    public float squatKneeAngleMin = 165f;         // 站立识别膝角下限(度)
    [Range(0f, 0.9f)] public float squatMaxDescentRatio = 0.55f; // 最大下降 = 平均腿长 × 此值
    public float squatSmoothTimeConst = 0.05f;     // 高度平滑时间常数(s)
    public float squatMaxSpeed = 1.5f;             // 升降速度上限(m/s)
    public float squatDeadzone = 0.01f;            // 高度死区(m)
    [Tooltip("在 Inspector 勾选并手动触发一次“重新识别站立基准”")]
    public bool reidentifyBaseline = false;
    [Tooltip("迈步时摆动腿屈膝会让该腿伸展量骤减；下降量取两腿的较小值（=更伸展的那条腿），避免骨盆被摆动腿拉低")]
    public bool squatUseMostExtendedLeg = true;
    [Tooltip("两腿伸展量差超过此值(m)视为单侧屈膝(迈步)，保持高度不更新")]
    public float squatAsymmetryHold = 0.06f;
    [Tooltip("连续多久拿不到可信的下蹲测量(s)就把下降量收回站立。真实下蹲时两脚不离地、置信度也不低，" +
             "该计时恒被新测量刷新，不会触发；只有腿关键点崩塌(踝被算到髋之上)才超时自愈")]
    public float squatTrustTimeout = 0.6f;

    [Header("Gait Pelvis Bob / Sway (走路自然度)")]
    [Tooltip("由 FootPlantingController 每帧写入的支撑权重驱动：支撑脚侧骨盆平移 + 双支撑期骨盆下沉")]
    public bool enableGaitBob = true;
    [Tooltip("骨盆左右摆动幅度(m)。单脚支撑时朝向支撑脚")]
    public float gaitSwayAmplitude = 0.020f;
    [Tooltip("双支撑期骨盆下沉幅度(m)。单脚支撑时回到基准高度")]
    public float gaitBobAmplitude = 0.012f;
    [Tooltip("由 FootPlantingController 写入的支撑权重（只读）")]
    [Range(0f, 1f)] public float LeftSupportWeight;
    [Range(0f, 1f)] public float RightSupportWeight;

    // 只读接口（供 FootPlantingController 等读取，避免各组件对同一帧不同判断）
    public bool BaselineEstablished { get; private set; }
    public float DescentMeters { get; private set; }
    public float SquatTargetDescent { get; private set; }
    public float LeftKneeAngleDeg { get; private set; }
    public float RightKneeAngleDeg { get; private set; }
    public bool SquatDataValid { get; private set; }
    public bool SquatReachLimited { get; private set; }
    public string SquatFailReason { get; private set; } = "";
    public float LeftLegConfidence { get; private set; }
    public float RightLegConfidence { get; private set; }
    public string SquatLimitReason { get; private set; } = "";
    public bool SquatPaused { get; private set; }

    [Header("Debug")]
    public bool showBoneWarnings = true;
    [Header("Squat Diagnostics")]
    public bool showSquatOverlay = true;

    private Transform _hips, _spine, _chest, _upperChest, _head, _leftFoot, _rightFoot;
    private Quaternion _spineRestLocalRot, _chestRestLocalRot, _upperChestRestLocalRot;

    private BoneRig _leftUpperArmRig = new BoneRig(), _leftLowerArmRig = new BoneRig();
    private BoneRig _rightUpperArmRig = new BoneRig(), _rightLowerArmRig = new BoneRig();
    private BoneRig _leftUpperLegRig = new BoneRig(), _leftLowerLegRig = new BoneRig();
    private BoneRig _rightUpperLegRig = new BoneRig(), _rightLowerLegRig = new BoneRig();
    private BoneRig _leftFootRig = new BoneRig(), _rightFootRig = new BoneRig();

    private BasisRig _leftHandRig = new BasisRig(), _rightHandRig = new BasisRig();
    private FingerRigChain[] _leftFingers = new FingerRigChain[5];
    private FingerRigChain[] _rightFingers = new FingerRigChain[5];

    // ── squat 内部状态 ──
    private Transform _hipsParent;
    private Vector3 _hipsBaseLocalPos;
    private bool _hipsBaseCaptured;
    private double _standTime;                // 有效新帧累计站立时间
    private int _squatLastFrameId = -1;
    private double _squatPrevTime = -1;
    private float _H0;                        // 站立基准的腿伸展量(每帧重算自基准姿态的H0)
    private float _smoothDescent;
    private float _squatTargetDescent;
    private bool _squatBaselineEstablished;
    private float _leftH, _rightH;
    private Vector3 _leftThighDir, _leftShinDir, _rightThighDir, _rightShinDir;
    private float _leftThighLen, _leftShinLen, _rightThighLen, _rightShinLen;
    private float _leftKneeDeg, _rightKneeDeg;
    private Vector3 _lastPlannedL, _lastPlannedR;   // 上一帧已投影的计划脚位(判近地)
    private bool _plannedValid;                     // 是否已有有效计划脚位(非初始零)
    private float _leftConf, _rightConf;            // 左右腿置信度(分腿)
    private float _squatInvalidTime;                // 有效新帧之外累计的无效时长(s)
    private float _squatTrustTime;                  // 距上次可信下蹲测量的时长(s)，超阈值收回站立
    private float _recoverTimer;                    // 恢复平滑(200ms)
    private bool _wasInvalid;                       // 上次是否处于无效/暂停

    private class BoneRig
    {
        public Transform bone;
        public Quaternion restLocalRotation;
        public Vector3 restAxisLocal = Vector3.forward;
    }

    private class BasisRig
    {
        public Transform bone;
        public Quaternion restLocalRotation;
        public Vector3 restForwardLocal = Vector3.forward;
        public Vector3 restUpLocal = Vector3.up;
    }

    private class FingerRigChain
    {
        public BoneRig proximal = new BoneRig();
        public BoneRig intermediate = new BoneRig();
        public BoneRig distal = new BoneRig();
    }

    void Start()
    {
        if (receiver == null)
        {
            var receivers = FindObjectsOfType<UdpPoseReceiver>();
            foreach (var r in receivers) { if (r.port == 8888) { receiver = r; break; } }
        }
        if (leftReceiver == null)
        {
            var receivers = FindObjectsOfType<UdpPoseReceiver>();
            foreach (var r in receivers) { if (r.port == 8889) { leftReceiver = r; break; } }
        }
        if (targetAnimator == null) targetAnimator = GetComponent<Animator>();
        if (receiver == null) { Debug.LogError("❌ 没找到 UdpPoseReceiver!"); return; }
        if (targetAnimator == null) { Debug.LogError("❌ 没找到 Animator!"); return; }
        if (!targetAnimator.isHuman) { Debug.LogError("❌ 不是 Humanoid!"); return; }

        InitFingerChains();
        CacheBones();
        targetAnimator.applyRootMotion = false;

        Debug.Log($"✅ HumanoidPoseDriver — {(leftReceiver != null ? "双摄" : "仅前摄")} legBlend={legBlendWeight:F1}");
    }

    void InitFingerChains()
    {
        for (int i = 0; i < 5; i++) { _leftFingers[i] = new FingerRigChain(); _rightFingers[i] = new FingerRigChain(); }
    }

    void CacheBones()
    {
        _hips = targetAnimator.GetBoneTransform(HumanBodyBones.Hips);
        _spine = targetAnimator.GetBoneTransform(HumanBodyBones.Spine);
        _chest = targetAnimator.GetBoneTransform(HumanBodyBones.Chest);
        _upperChest = targetAnimator.GetBoneTransform(HumanBodyBones.UpperChest);
        _head = targetAnimator.GetBoneTransform(HumanBodyBones.Head);

        if (_hips != null)
        {
            _hipsParent = _hips.parent;
            _hipsBaseLocalPos = _hips.localPosition;
            _hipsBaseCaptured = true;
        }

        if (_spine != null) _spineRestLocalRot = _spine.localRotation;
        if (_chest != null) _chestRestLocalRot = _chest.localRotation;
        if (_upperChest != null) _upperChestRestLocalRot = _upperChest.localRotation;

        CacheBoneRig(_leftUpperArmRig,  targetAnimator.GetBoneTransform(HumanBodyBones.LeftUpperArm),  targetAnimator.GetBoneTransform(HumanBodyBones.LeftLowerArm),  Vector3.forward);
        CacheBoneRig(_leftLowerArmRig,  targetAnimator.GetBoneTransform(HumanBodyBones.LeftLowerArm),  targetAnimator.GetBoneTransform(HumanBodyBones.LeftHand),      Vector3.forward);
        CacheBoneRig(_rightUpperArmRig, targetAnimator.GetBoneTransform(HumanBodyBones.RightUpperArm), targetAnimator.GetBoneTransform(HumanBodyBones.RightLowerArm), Vector3.forward);
        CacheBoneRig(_rightLowerArmRig, targetAnimator.GetBoneTransform(HumanBodyBones.RightLowerArm), targetAnimator.GetBoneTransform(HumanBodyBones.RightHand),     Vector3.forward);
        CacheBoneRig(_leftUpperLegRig,  targetAnimator.GetBoneTransform(HumanBodyBones.LeftUpperLeg),  targetAnimator.GetBoneTransform(HumanBodyBones.LeftLowerLeg),  Vector3.forward);
        CacheBoneRig(_leftLowerLegRig,  targetAnimator.GetBoneTransform(HumanBodyBones.LeftLowerLeg),  targetAnimator.GetBoneTransform(HumanBodyBones.LeftFoot),      Vector3.forward);
        CacheBoneRig(_rightUpperLegRig, targetAnimator.GetBoneTransform(HumanBodyBones.RightUpperLeg), targetAnimator.GetBoneTransform(HumanBodyBones.RightLowerLeg), Vector3.forward);
        CacheBoneRig(_rightLowerLegRig, targetAnimator.GetBoneTransform(HumanBodyBones.RightLowerLeg), targetAnimator.GetBoneTransform(HumanBodyBones.RightFoot),     Vector3.forward);

        _leftFoot  = targetAnimator.GetBoneTransform(HumanBodyBones.LeftFoot);
        _rightFoot = targetAnimator.GetBoneTransform(HumanBodyBones.RightFoot);
        CacheBoneRig(_leftFootRig,  _leftFoot,  targetAnimator.GetBoneTransform(HumanBodyBones.LeftToes),  Vector3.forward);
        CacheBoneRig(_rightFootRig, _rightFoot, targetAnimator.GetBoneTransform(HumanBodyBones.RightToes), Vector3.forward);

        // 左手手指
        CacheBoneRig(_leftFingers[0].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftThumbProximal), targetAnimator.GetBoneTransform(HumanBodyBones.LeftThumbIntermediate), Vector3.forward);
        CacheBoneRig(_leftFingers[0].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.LeftThumbIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.LeftThumbDistal), _leftFingers[0].proximal.restAxisLocal);
        CacheBoneRig(_leftFingers[0].distal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftThumbDistal), null, _leftFingers[0].intermediate.restAxisLocal);
        CacheBoneRig(_leftFingers[1].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftIndexProximal), targetAnimator.GetBoneTransform(HumanBodyBones.LeftIndexIntermediate), Vector3.forward);
        CacheBoneRig(_leftFingers[1].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.LeftIndexIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.LeftIndexDistal), _leftFingers[1].proximal.restAxisLocal);
        CacheBoneRig(_leftFingers[1].distal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftIndexDistal), null, _leftFingers[1].intermediate.restAxisLocal);
        CacheBoneRig(_leftFingers[2].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal), targetAnimator.GetBoneTransform(HumanBodyBones.LeftMiddleIntermediate), Vector3.forward);
        CacheBoneRig(_leftFingers[2].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.LeftMiddleIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.LeftMiddleDistal), _leftFingers[2].proximal.restAxisLocal);
        CacheBoneRig(_leftFingers[2].distal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftMiddleDistal), null, _leftFingers[2].intermediate.restAxisLocal);
        CacheBoneRig(_leftFingers[3].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftRingProximal), targetAnimator.GetBoneTransform(HumanBodyBones.LeftRingIntermediate), Vector3.forward);
        CacheBoneRig(_leftFingers[3].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.LeftRingIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.LeftRingDistal), _leftFingers[3].proximal.restAxisLocal);
        CacheBoneRig(_leftFingers[3].distal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftRingDistal), null, _leftFingers[3].intermediate.restAxisLocal);
        CacheBoneRig(_leftFingers[4].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftLittleProximal), targetAnimator.GetBoneTransform(HumanBodyBones.LeftLittleIntermediate), Vector3.forward);
        CacheBoneRig(_leftFingers[4].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.LeftLittleIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.LeftLittleDistal), _leftFingers[4].proximal.restAxisLocal);
        CacheBoneRig(_leftFingers[4].distal, targetAnimator.GetBoneTransform(HumanBodyBones.LeftLittleDistal), null, _leftFingers[4].intermediate.restAxisLocal);

        // 右手手指
        CacheBoneRig(_rightFingers[0].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.RightThumbProximal), targetAnimator.GetBoneTransform(HumanBodyBones.RightThumbIntermediate), Vector3.forward);
        CacheBoneRig(_rightFingers[0].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.RightThumbIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.RightThumbDistal), _rightFingers[0].proximal.restAxisLocal);
        CacheBoneRig(_rightFingers[0].distal, targetAnimator.GetBoneTransform(HumanBodyBones.RightThumbDistal), null, _rightFingers[0].intermediate.restAxisLocal);
        CacheBoneRig(_rightFingers[1].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.RightIndexProximal), targetAnimator.GetBoneTransform(HumanBodyBones.RightIndexIntermediate), Vector3.forward);
        CacheBoneRig(_rightFingers[1].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.RightIndexIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.RightIndexDistal), _rightFingers[1].proximal.restAxisLocal);
        CacheBoneRig(_rightFingers[1].distal, targetAnimator.GetBoneTransform(HumanBodyBones.RightIndexDistal), null, _rightFingers[1].intermediate.restAxisLocal);
        CacheBoneRig(_rightFingers[2].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.RightMiddleProximal), targetAnimator.GetBoneTransform(HumanBodyBones.RightMiddleIntermediate), Vector3.forward);
        CacheBoneRig(_rightFingers[2].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.RightMiddleIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.RightMiddleDistal), _rightFingers[2].proximal.restAxisLocal);
        CacheBoneRig(_rightFingers[2].distal, targetAnimator.GetBoneTransform(HumanBodyBones.RightMiddleDistal), null, _rightFingers[2].intermediate.restAxisLocal);
        CacheBoneRig(_rightFingers[3].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.RightRingProximal), targetAnimator.GetBoneTransform(HumanBodyBones.RightRingIntermediate), Vector3.forward);
        CacheBoneRig(_rightFingers[3].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.RightRingIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.RightRingDistal), _rightFingers[3].proximal.restAxisLocal);
        CacheBoneRig(_rightFingers[3].distal, targetAnimator.GetBoneTransform(HumanBodyBones.RightRingDistal), null, _rightFingers[3].intermediate.restAxisLocal);
        CacheBoneRig(_rightFingers[4].proximal, targetAnimator.GetBoneTransform(HumanBodyBones.RightLittleProximal), targetAnimator.GetBoneTransform(HumanBodyBones.RightLittleIntermediate), Vector3.forward);
        CacheBoneRig(_rightFingers[4].intermediate, targetAnimator.GetBoneTransform(HumanBodyBones.RightLittleIntermediate), targetAnimator.GetBoneTransform(HumanBodyBones.RightLittleDistal), _rightFingers[4].proximal.restAxisLocal);
        CacheBoneRig(_rightFingers[4].distal, targetAnimator.GetBoneTransform(HumanBodyBones.RightLittleDistal), null, _rightFingers[4].intermediate.restAxisLocal);

        CacheHandRig(_leftHandRig,  targetAnimator.GetBoneTransform(HumanBodyBones.LeftHand),  _leftFingers[1].proximal.bone,  _leftFingers[2].proximal.bone,  _leftFingers[4].proximal.bone);
        CacheHandRig(_rightHandRig, targetAnimator.GetBoneTransform(HumanBodyBones.RightHand), _rightFingers[1].proximal.bone, _rightFingers[2].proximal.bone, _rightFingers[4].proximal.bone);

        if (showBoneWarnings) WarnAll();
    }

    void CacheBoneRig(BoneRig rig, Transform bone, Transform child, Vector3 fallbackAxisLocal)
    {
        rig.bone = bone;
        if (bone == null) return;
        rig.restLocalRotation = bone.localRotation;
        Vector3 axis = fallbackAxisLocal;
        if (child != null)
        {
            Vector3 childLocalDir = child.localPosition;
            if (childLocalDir.sqrMagnitude < 1e-6f) childLocalDir = bone.InverseTransformDirection(child.position - bone.position);
            if (childLocalDir.sqrMagnitude > 1e-6f) axis = childLocalDir.normalized;
        }
        if (axis.sqrMagnitude < 1e-6f) axis = Vector3.forward;
        rig.restAxisLocal = axis.normalized;
    }

    void CacheHandRig(BasisRig rig, Transform hand, Transform indexRoot, Transform middleRoot, Transform pinkyRoot)
    {
        rig.bone = hand;
        if (hand == null) return;
        rig.restLocalRotation = hand.localRotation;

        Vector3 indexDir = GetLocalDirection(hand, indexRoot);
        Vector3 middleDir = GetLocalDirection(hand, middleRoot);
        Vector3 pinkyDir = GetLocalDirection(hand, pinkyRoot);

        Vector3 forward = Vector3.zero; int count = 0;
        if (indexDir.sqrMagnitude > 1e-6f) { forward += indexDir.normalized; count++; }
        if (middleDir.sqrMagnitude > 1e-6f) { forward += middleDir.normalized; count++; }
        if (pinkyDir.sqrMagnitude > 1e-6f) { forward += pinkyDir.normalized; count++; }
        if (count == 0) { rig.restForwardLocal = Vector3.forward; rig.restUpLocal = Vector3.up; return; }
        forward.Normalize();

        Vector3 up = Vector3.Cross(indexDir, pinkyDir);
        if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
        up = Vector3.ProjectOnPlane(up, forward);
        if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
        rig.restForwardLocal = forward.normalized;
        rig.restUpLocal = up.normalized;
    }

    Vector3 GetLocalDirection(Transform parent, Transform child)
    {
        if (parent == null || child == null) return Vector3.zero;
        Vector3 local = child.localPosition;
        if (local.sqrMagnitude < 1e-6f) local = parent.InverseTransformDirection(child.position - parent.position);
        return local.sqrMagnitude < 1e-6f ? Vector3.zero : local.normalized;
    }

    void WarnAll()
    {
        WarnIfNull("Hips", _hips); WarnIfNull("Spine", _spine); WarnIfNull("Chest", _chest);
        WarnIfNull("UpperChest", _upperChest); WarnIfNull("Head", _head);
        WarnIfNull("LeftUpperArm", _leftUpperArmRig.bone); WarnIfNull("LeftLowerArm", _leftLowerArmRig.bone);
        WarnIfNull("RightUpperArm", _rightUpperArmRig.bone); WarnIfNull("RightLowerArm", _rightLowerArmRig.bone);
        WarnIfNull("LeftUpperLeg", _leftUpperLegRig.bone); WarnIfNull("LeftLowerLeg", _leftLowerLegRig.bone);
        WarnIfNull("RightUpperLeg", _rightUpperLegRig.bone); WarnIfNull("RightLowerLeg", _rightLowerLegRig.bone);
    }
    void WarnIfNull(string name, Transform t) { if (t == null) Debug.LogWarning($"⚠️ 骨骼未映射: {name}"); }

    // ══════════════════════════════════════════════════════════════
    // 主循环
    // ══════════════════════════════════════════════════════════════

    void LateUpdate()
    {
        if (receiver == null) return;

        if (reidentifyBaseline)
        {
            reidentifyBaseline = false;
            ReidentifyStandingBaseline();
        }

        var frontPose = receiver.GetLatestPose();
        if (frontPose == null || frontPose.Count == 0) return;

        Dictionary<string, Landmark> leftPose = null;
        if (leftReceiver != null && !frontOnlyPose)
        {
            // 时间匹配：取左摄中最接近前摄时间的帧；超差/空则本帧退化为仅前摄，
            // 避免左摄卡住后旧姿态仍以固定权重混入造成重影/拉扯。
            double frontTime = receiver.LatestTime;
            if (leftReceiver.TryGetClosestFrame(frontTime, out var leftFrame)
                && System.Math.Abs(frontTime - leftFrame.time) <= maxTimeDiff
                && leftFrame.pose != null && leftFrame.pose.Count > 0)
            {
                leftPose = leftFrame.pose;
            }
        }

        if (enableBodyRotation && !disableBodyRotation)
            ApplyBodyRotation(frontPose);

        if (enableHeadRotation)
            ApplyHeadRotation(frontPose);

        ApplyLimbRigDual(_leftUpperArmRig,  _leftLowerArmRig,  frontPose, leftPose, "11", "13", "15", 0.3f);
        ApplyLimbRigDual(_rightUpperArmRig, _rightLowerArmRig, frontPose, leftPose, "12", "14", "16", 0.3f);

        // ── 腿部：测量腿几何（下蹲/投影共用），不写显示骨，关节由 FootPlantingController 接管 ──
        HandleLeg(_leftUpperLegRig,  _leftLowerLegRig,  frontPose, leftPose, "23", "25", "27", legBlendWeight,
                  isLeftFoot: true,  ref PlannedLeftAnkle,  ref LeftUpperPlanned,  ref LeftLowerPlanned);
        HandleLeg(_rightUpperLegRig, _rightLowerLegRig, frontPose, leftPose, "24", "26", "28", legBlendWeight,
                  isLeftFoot: false, ref PlannedRightAnkle, ref RightUpperPlanned, ref RightLowerPlanned);
        PlannedLeftAnkle  = _leftUpperLegRig.bone != null ? PlannedLeftAnkle : Vector3.zero;
        PlannedRightAnkle = _rightUpperLegRig.bone != null ? PlannedRightAnkle : Vector3.zero;

        // ── 下蹲：识别站立基准 → 目标下降量（仅新帧）→ 每帧平滑 → 应用骨盆（仅脚 IK 生效时）──
        if (footIKActive)
        {
            UpdateSquat(frontPose); // 测量：仅有效新帧更新目标下降量
            if (enableSquat)
            {
                SmoothAndApplySquat(Time.deltaTime);
            }
            else
            {
                SquareFailWhenDisabled();
                SmoothAndApplySquat(Time.deltaTime); // 下蹲关闭时仍保留步态起伏/摆动
            }
            // 应用骨盆后再投影独立踝位（FootPlantingController 读到骨盆修正后的脚位）
            PlannedLeftAnkle = FKProjectAnkle(_leftUpperLegRig, _leftLowerLegRig,
                                              LeftUpperPlanned, LeftLowerPlanned, _leftFootRig);
            PlannedRightAnkle = FKProjectAnkle(_rightUpperLegRig, _rightLowerLegRig,
                                               RightUpperPlanned, RightLowerPlanned, _rightFootRig);
            _lastPlannedL = PlannedLeftAnkle;
            _lastPlannedR = PlannedRightAnkle;
            _plannedValid = true;
        }
        else
        {
            // 脚 IK 关闭 → 下蹲控制同步退出，恢复骨盆基准高度
            RestoreHipsToBase();
        }

        if (!footIKActive)
        {
            // 脚部回正：仅在非 IK 时由本驱动写入；IK 模式下由 FootPlantingController 负责小腿后恢复脚朝向
            ApplyFootRigDual(frontPose, leftPose, true, legBlendWeight);
            ApplyFootRigDual(frontPose, leftPose, false, legBlendWeight);
        }

        if (enableFingerRotation)
        {
            ApplyHandAndFingers(frontPose, true);
            ApplyHandAndFingers(frontPose, false);
        }
    }

    /// <summary>
    /// 处理单条腿。foreIKActive 时：计算方向的加权结果 → 目标局部旋转 →（若脚 IK 开启）仅投影出“独立踝位”不写骨；
    /// 否则照旧写入 FK。
    /// </summary>
    void HandleLeg(BoneRig upperRig, BoneRig lowerRig,
        Dictionary<string, Landmark> front, Dictionary<string, Landmark> left,
        string a, string b, string c, float leftWeight, bool isLeftFoot,
        ref Vector3 plannedAnkle, ref Quaternion upperPlanned, ref Quaternion lowerPlanned)
    {
        GetLegDirs(front, left, a, b, c, leftWeight, out Vector3 dirUpper, out Vector3 dirLower);

        Quaternion upperTarget = ComputeBoneRigTarget(upperRig, dirUpper);
        Quaternion lowerTarget = ComputeBoneRigTarget(lowerRig, dirLower);
        upperPlanned = upperTarget;
        lowerPlanned = lowerTarget;

        // 测量腿几何（独立于髋位置）：H 伸展量 + 膝角 + 世界骨长/方向（供下蹲 & 平面投影共用）
        MeasureLeg(upperRig, lowerRig, upperTarget, lowerTarget, isLeftFoot);

        plannedAnkle = Vector3.zero; // 投影推迟到 squat 应用骨盆后由 ProjectPlannedAnkles() 统一计算

        if (!footIKActive)
        {
            ApplyBoneRig(upperRig, dirUpper, rotationSmooth);
            ApplyBoneRig(lowerRig, dirLower, rotationSmooth);
        }
    }

    /// <summary>测量单腿：世界大腿/小腿方向、骨长、H(垂直伸展量)、膝内角。不使用髋世界位置(保证独立)。</summary>
    void MeasureLeg(BoneRig upperRig, BoneRig lowerRig,
        Quaternion upperTarget, Quaternion lowerTarget, bool isLeft)
    {
        Transform up = upperRig.bone, low = lowerRig.bone;
        if (up == null || low == null) return;
        Quaternion upParentRot = up.parent != null ? up.parent.rotation : Quaternion.identity;
        Quaternion upWorldRot = upParentRot * upperTarget;
        Quaternion lowWorldRot = upWorldRot * lowerTarget;

        Vector3 thighDir = upWorldRot * upperRig.restAxisLocal;
        Vector3 shinDir = lowWorldRot * lowerRig.restAxisLocal;
        float thighLen = low.localPosition.magnitude;
        float shinLen = (isLeft ? _leftFootRig : _rightFootRig).bone != null
            ? (isLeft ? _leftFootRig : _rightFootRig).bone.localPosition.magnitude
            : 0.05f;

        float H = -(thighLen * thighDir + shinLen * shinDir).y;   // 髋→踝垂直伸展(越直越大)
        float kneeDeg = 180f - Vector3.Angle(thighDir, shinDir);  // 膝内角(站立≈180)

        if (isLeft)
        {
            _leftThighDir = thighDir; _leftShinDir = shinDir;
            _leftThighLen = thighLen; _leftShinLen = shinLen;
            _leftH = H; _leftKneeDeg = kneeDeg;
        }
        else
        {
            _rightThighDir = thighDir; _rightShinDir = shinDir;
            _rightThighLen = thighLen; _rightShinLen = shinLen;
            _rightH = H; _rightKneeDeg = kneeDeg;
        }
    }

    /// <summary>求一条腿的 上/下 世界方向（前摄 + 左摄加权），不写骨。方向分量会被归一化。</summary>
    void GetLegDirs(Dictionary<string, Landmark> front, Dictionary<string, Landmark> left,
        string a, string b, string c, float leftWeight, out Vector3 dirUpper, out Vector3 dirLower)
    {
        dirUpper = Vector3.zero;
        dirLower = Vector3.zero;
        bool fOk = front != null && LandmarksPresent(front, a, b, c);
        bool lOk = left != null && LandmarksUsable(left, a, b, c);
        float totalW = 0f;

        if (fOk)
        {
            Vector3 p1 = CoordinateConverter.ToUnityWorld(front[a]);
            Vector3 p2 = CoordinateConverter.ToUnityWorld(front[b]);
            Vector3 p3 = CoordinateConverter.ToUnityWorld(front[c]);
            float w = 1f - leftWeight;
            dirUpper += (p2 - p1).normalized * w;
            dirLower += (p3 - p2).normalized * w;
            totalW += w;
        }
        if (lOk)
        {
            Vector3 p1 = CoordinateConverter.ToUnityWorldLeft(left[a]);
            Vector3 p2 = CoordinateConverter.ToUnityWorldLeft(left[b]);
            Vector3 p3 = CoordinateConverter.ToUnityWorldLeft(left[c]);
            dirUpper += (p2 - p1).normalized * leftWeight;
            dirLower += (p3 - p2).normalized * leftWeight;
            totalW += leftWeight;
        }
        if (dirUpper.sqrMagnitude > 1e-6f) dirUpper.Normalize();
        if (dirLower.sqrMagnitude > 1e-6f) dirLower.Normalize();
    }

    /// <summary>
    /// 用“计划 FK 局部旋转”正向运动学投影出独立踝位。
    /// 使用髋(上腿.parent)当前旋转（未被腿 IK 改写）+ 计划旋转 + 骨段长度，不读显示骨当前值。
    /// </summary>
    Vector3 FKProjectAnkle(BoneRig upperRig, BoneRig lowerRig,
        Quaternion upperTarget, Quaternion lowerTarget, BoneRig footRig)
    {
        Transform up = upperRig.bone, low = lowerRig.bone;
        if (up == null || low == null) return Vector3.zero;

        Quaternion upParentRot = up.parent != null ? up.parent.rotation : Quaternion.identity;
        Quaternion upWorldRot = upParentRot * upperTarget;
        Vector3 hipPos = up.position;                              // 髋关节（由根+身体旋转决定，非腿IK）
        Vector3 kneeWorld = hipPos + upWorldRot * low.localPosition;   // 膝偏移在 upper 自身局部空间
        Quaternion lowWorldRot = upWorldRot * lowerTarget;
        Vector3 ankleLocal = footRig != null && footRig.bone != null
            ? footRig.bone.localPosition
            : low.localPosition;
        Vector3 ankleWorld = kneeWorld + lowWorldRot * ankleLocal;
        return ankleWorld;
    }

    // ══════════════════════════════════════════════════════════════
    // 下蹲：（仅脚 IK 生效时）站立基准识别 + 下降量估计 + 骨盆垂直偏移
    // ══════════════════════════════════════════════════════════════

    /// <summary>识别站立基准 + 计算目标下降量（仅有效新帧更新；渲染帧只平滑）。</summary>
    /// <summary>测量（仅有效新帧更新目标下降量；渲染帧不重置）。</summary>
    void UpdateSquat(Dictionary<string, Landmark> pose)
    {
        bool validData = false;
        double frameDt = 0;
        var recv = receiver;
        if (recv != null)
        {
            if (recv.LatestFrameId != _squatLastFrameId)
            {
                double now = recv.LatestTime;
                if (_squatPrevTime >= 0 && now > _squatPrevTime && now - _squatPrevTime > 1e-4)
                {
                    validData = true;
                    frameDt = now - _squatPrevTime;
                }
                else
                {
                    _standTime = 0d; // 时间戳倒退/重置 → 清空候选计时
                }
                _squatPrevTime = now;
                _squatLastFrameId = recv.LatestFrameId;
            }
        }

        // 分腿置信度（左右腿独立）
        float lc = 1f, rc = 1f;
        foreach (var k in new[] { "23", "25", "27" }) { if (pose.TryGetValue(k, out var lm)) lc = Mathf.Min(lc, lm.visibility); else { lc = 0f; break; } }
        foreach (var k in new[] { "24", "26", "28" }) { if (pose.TryGetValue(k, out var lm)) rc = Mathf.Min(rc, lm.visibility); else { rc = 0f; break; } }
        _leftConf = lc; _rightConf = rc;
        LeftLegConfidence = lc; RightLegConfidence = rc;
        LeftKneeAngleDeg = _leftKneeDeg; RightKneeAngleDeg = _rightKneeDeg;

        // 接地：必须已有有效计划脚位（不采用初始零计划位）
        bool nearGround = _plannedValid &&
            (_lastPlannedL.y - boardY) <= 0.30f && (_lastPlannedR.y - boardY) <= 0.30f;

        bool kneeOk = _leftKneeDeg >= squatKneeAngleMin && _rightKneeDeg >= squatKneeAngleMin;
        bool visOk = lc >= squatMinVisibility && rc >= squatMinVisibility;
        bool standing = kneeOk && visOk && nearGround;

        SquatFailReason = "";
        if (!kneeOk) SquatFailReason = "膝角不足(需≥" + squatKneeAngleMin.ToString("F0") + "°)";
        else if (!visOk) SquatFailReason = "可见度不足(需≥" + squatMinVisibility.ToString("F2") + ")";
        else if (!nearGround) SquatFailReason = "双脚未稳定接地";

        SquatDataValid = validData && (lc >= 0.30f || rc >= 0.30f);
        if (validData) _standTime = standing ? _standTime + frameDt : 0d;

        if (!_squatBaselineEstablished && _standTime >= squatRecognizeTime && standing)
        {
            _H0 = (_leftH + _rightH) * 0.5f;
            _squatBaselineEstablished = true;
            _smoothDescent = 0f;
            _squatTargetDescent = 0f;
            _squatInvalidTime = 0f; _recoverTimer = 0f; _wasInvalid = false;
            Debug.Log("[Squat] 站立基准已建立.");
        }
        BaselineEstablished = _squatBaselineEstablished;

        // ★ 接地上门：nearGround 在上面的 570-571 行就算好了，但一直只喂给 standing
        //   （见 575 行），没有进下面的目标写入条件。后果是致命的：
        //   腿关键点崩塌时踝会被抬到髋之上（物理不可能），H 随之塌掉，dL/dR 变得极大，
        //   于是 target 被 Clamp 顶到 maxDescent 上限（实测 45.49cm = 平均腿长×0.55）
        //   并写进 _squatTargetDescent；此后 conf 掉到 0.30 以下走 637 行的「保持」分支
        //   （那行只有注释、没有赋值），SquatPaused 又因 _squatInvalidTime 由「包是否到达」
        //   驱动而永不触发，于是骨盆被永久钉低 45cm，全程无法自愈。
        //   实测：本采集健康段 two-feet target.y<=0.30 成立 99.3%（摆动腿正常只抬 14cm），
        //   加上这道门只影响 0.7% 的帧；而崩溃段能挡掉 44%，钉扎帧（t=77.395）两脚
        //   分别在 0.756/0.931m 高空，当场拦下。语义上也正是对的：
        //   只有两只脚都确实踩在地上，腿的伸展量才可以被解读为骨盆高度。
        if (_squatBaselineEstablished && SquatDataValid && nearGround)
        {
            // 平均腿长 = (左大腿+左小腿+右大腿+右小腿) / 2
            float avgLegLen = (_leftThighLen + _leftShinLen + _rightThighLen + _rightShinLen) * 0.5f;
            float maxDescent = Mathf.Max(0.01f, avgLegLen * squatMaxDescentRatio);
            bool dual = lc >= 0.30f && rc >= 0.30f;
            float target;
            SquatLimitReason = "";
            if (dual)
            {
                float dL = _H0 - _leftH;
                float dR = _H0 - _rightH;
                // 走路时摆动腿必然屈膝（伸展量骤减），若像下蹲一样取两腿平均/相加，
                // 每迈一步骨盆都会被拉低一次。真正下蹲是“两腿同时缩短”，
                // 所以下降量取两者较小值 = 更伸展的那条腿决定高度。
                if (squatUseMostExtendedLeg)
                {
                    target = Mathf.Min(dL, dR);
                    if (Mathf.Abs(dL - dR) > squatAsymmetryHold)
                        SquatLimitReason = "单侧屈膝(迈步)，取伸展腿";
                }
                else
                {
                    float wL = Mathf.Clamp01(lc), wR = Mathf.Clamp01(rc);
                    target = (dL * wL + dR * wR) / Mathf.Max(1e-4f, wL + wR);
                }
            }
            else
            {
                // 单脚迈步/抬腿：保持已有下降量，不把抬膝当蹲
                target = _smoothDescent;
                SquatLimitReason = "单脚支撑，保持高度";
            }
            target = Mathf.Clamp(target, 0f, maxDescent);
            if (target < squatDeadzone) target = 0f;
            _squatTargetDescent = target;
            _squatTrustTime = 0f;                       // ★ 拿到可信测量，失效时钟清零
        }
        else if (!_squatBaselineEstablished)
        {
            _squatTargetDescent = 0f;
            _squatTrustTime = 0f;
        }
        else
        {
            // ★ 保持上一个 _squatTargetDescent（重复帧不重置为 0）——但不能永久保持。
            //   这段「保持」分支原先只有这一行注释、没有任何赋值，一旦有垃圾值被写进来
            //   就再也出不去：实测腿关键点崩塌时踝会被算到髋之上（物理不可能），H 随之
            //   塌掉，target 被 Clamp 顶到上限 45.49cm 并写进字段；随后 conf 掉到 0.30
            //   以下、SquatDataValid 恒为 false，骨盆被钉死在半蹲姿态直到采集结束。
            //   全程 SquatPaused 一次都没触发——因为 _squatInvalidTime 由「包是否到达」
            //   驱动，而包一直在到（front_age_ms 最大 115ms），它压根不看数据质量。
            //   这里补一个由**数据质量**驱动的失效时钟：连续 squatTrustTimeout 秒拿不到
            //   可信测量，就把目标收回站立，交由 SmoothAndApplySquat 里既有的
            //   squatMaxSpeed 限速平滑放下。真实下蹲（蹲住不动）两脚不离地、置信度也不低，
            //   计时恒被上面的写入分支刷新——本采集健康段最长断流仅 0.254s，阈值 0.6s
            //   有 2.4 倍余量，不会把真人误弹回站立。
            //   ⚠️ 边界（刻意为之）：若包【完全停到】超过 0.25s，SquatPaused 会为真，
            //   而 SmoothAndApplySquat 在暂停时是不做平滑的（保持最后高度）——此时本计时
            //   虽把目标清成 0，骨盆却不会跟着动。这是有意保留的：包停了意味着输入整个
            //   断了，原设计就是冻住而不是塌回站立，否则真实下蹲时丢 1 秒跟踪就会被弹
            //   起来。包一恢复，上述两个分支立刻重新生效并自动纠正（计时值未被重置，
            //   若恢复后的数据仍不可信，会当场继续走衰减）。
            _squatTrustTime += Time.deltaTime;
            if (_squatTrustTime > squatTrustTimeout) _squatTargetDescent = 0f;
        }

        // 无效时长/暂停/恢复
        if (validData) _squatInvalidTime = 0f;
        else _squatInvalidTime += Time.deltaTime;
        SquatPaused = _squatBaselineEstablished && _squatInvalidTime > 0.25f;
        if (SquatPaused) _wasInvalid = true;
        else if (_wasInvalid) { _recoverTimer = 0.2f; _wasInvalid = false; }
        if (_recoverTimer > 0f) _recoverTimer = Mathf.Max(0f, _recoverTimer - Time.deltaTime);

        // ★ 把内部目标同步到公开属性。此前 SquatTargetDescent 全文件只在
        //   SquareFailWhenDisabled() 里被赋 0，内部逻辑写的一直是私有字段
        //   _squatTargetDescent —— 于是日志里的 target_cm 恒为 0（四份采集、两万多帧，
        //   非零次数为 0），整条「目标被顶到上限」的故障链因此完全不可观测，
        //   只剩滞后的 actual_cm 能看，没法定位到帧。
        //   唯一消费者是 FootIkUdpSender 的诊断日志，不影响任何控制逻辑。
        SquatTargetDescent = _squatTargetDescent;
    }

    /// <summary>每渲染帧：向缓存目标平滑，随后应用骨盆位置。下蹲关闭时目标下降量恒为 0，仅保留步态起伏。</summary>
    void SmoothAndApplySquat(float dt)
    {
        float targetDescent = (enableSquat && _squatBaselineEstablished) ? _squatTargetDescent : 0f;
        if (_squatBaselineEstablished || enableGaitBob)
        {
            if (!SquatPaused || !enableSquat)
            {
                float a = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, squatSmoothTimeConst));
                float smoothed = Mathf.Lerp(_smoothDescent, targetDescent, a);
                float maxStep = squatMaxSpeed * dt;
                _smoothDescent = Mathf.MoveTowards(_smoothDescent, smoothed, maxStep);
            }
            // 恢复(200ms)由时间常数平滑自然完成；暂停时保持最后安全高度
        }
        else
        {
            _smoothDescent = 0f;
        }
        DescentMeters = Mathf.Max(0f, _smoothDescent);
        ApplyPelvisOffset();
    }

    /// <summary>将骨盘 Hips 沿世界竖直方向下移（单写入者；从基准局部位置计算，不逐帧累加；修正父缩放）。</summary>
    /// <summary>
    /// 骨盆世界偏移 = 下蹲下降 + 步态起伏/摆动。
    /// 步态部分由 FootPlantingController 写入的支撑权重驱动：
    ///   双支撑（两脚都在地上）→ 骨盆最低；单支撑 → 骨盆抬到基准高度并偏向支撑脚。
    /// 注意：MeasureLeg 的伸展量 H 只由骨骼“方向”决定，不含髋位置，
    ///       所以这里的骨盆偏移不会反过来改变下蹲判据（无自激反馈）。
    /// </summary>
    void ApplyPelvisOffset()
    {
        if (!_hipsBaseCaptured || _hips == null || _hipsParent == null) return;
        if (!_squatBaselineEstablished && !enableGaitBob)
        {
            RestoreHipsToBase();
            return;
        }

        Vector3 baseWorld = _hipsParent.TransformPoint(_hipsBaseLocalPos);
        Vector3 offsetWorld = Vector3.down * Mathf.Max(0f, _smoothDescent);

        if (enableGaitBob)
        {
            float wl = Mathf.Clamp01(LeftSupportWeight);
            float wr = Mathf.Clamp01(RightSupportWeight);
            if (wl > 0.01f || wr > 0.01f)
            {
                // 摆动：骨盆朝支撑脚一侧平移（双脚支撑时差值≈0，自然回到中线）
                float sway = (wr - wl) * gaitSwayAmplitude;
                Vector3 right = _hips.rotation * Vector3.right;
                right.y = 0f;
                if (right.sqrMagnitude > 1e-6f) right.Normalize();
                offsetWorld += right * sway;
                // 起伏：双支撑期最低（重心在两脚之间），单支撑期回到基准
                offsetWorld += Vector3.down * (Mathf.Min(wl, wr) * gaitBobAmplitude);
            }
        }

        Vector3 targetWorld = baseWorld + offsetWorld;
        _hips.localPosition = _hipsParent.InverseTransformPoint(targetWorld);
    }

    void SquareFailWhenDisabled()
    {
        SquatTargetDescent = 0f;
        DescentMeters = 0f;
        SquatPaused = false;
        SquatLimitReason = "下蹲已关闭";
    }

    void RestoreHipsToBase()
    {
        if (!_hipsBaseCaptured || _hips == null) return;
        _hips.localPosition = _hipsBaseLocalPos;
    }
    public void ReidentifyStandingBaseline()
    {
        _squatBaselineEstablished = false;
        _standTime = 0d;
        _smoothDescent = 0f;
        _squatTargetDescent = 0f;
        _squatLastFrameId = -1;
        _squatPrevTime = -1;
        RestoreHipsToBase();
        Debug.Log("[Squat] 重新识别站立基准已触发—请站直。");
    }

    /// <summary>返回目标局部旋转（不写骨）；等价 ApplyBoneRig 中的 targetLocalRot。</summary>
    Quaternion ComputeBoneRigTarget(BoneRig rig, Vector3 targetDirWorld)
    {
        if (rig?.bone == null || targetDirWorld.sqrMagnitude < 1e-6f)
            return rig != null ? rig.restLocalRotation : Quaternion.identity;

        Vector3 desiredParentDir = rig.bone.parent != null
            ? rig.bone.parent.InverseTransformDirection(targetDirWorld.normalized)
            : targetDirWorld.normalized;
        if (desiredParentDir.sqrMagnitude < 1e-6f) return rig.restLocalRotation;

        Vector3 restParentDir = rig.restLocalRotation * rig.restAxisLocal;
        if (restParentDir.sqrMagnitude < 1e-6f) return rig.restLocalRotation;

        Quaternion delta = Quaternion.FromToRotation(restParentDir.normalized, desiredParentDir.normalized);
        return delta * rig.restLocalRotation;
    }

    // ══════════════════════════════════════════════════════════════
    // 双摄混合
    // ══════════════════════════════════════════════════════════════

    void ApplyLimbRigDual(BoneRig upperRig, BoneRig lowerRig,
        Dictionary<string, Landmark> front, Dictionary<string, Landmark> left,
        string a, string b, string c, float leftWeight)
    {
        // 前摄是已验证的 v4 基准姿态源：与 v4 一样，只要求关键点存在。
        // visibility 门槛只用于左摄贡献；否则前摄任一点短暂低于阈值时，
        // 整条肢体会冻结在上一姿势，看起来像映射修改完全没有生效。
        bool fOk = front != null && LandmarksPresent(front, a, b, c);
        bool lOk = left  != null && LandmarksUsable(left,  a, b, c);
        if (!fOk && !lOk) return;

        Vector3 dirUpper = Vector3.zero, dirLower = Vector3.zero;
        float totalW = 0f;

        if (fOk)
        {
            Vector3 p1 = CoordinateConverter.ToUnityWorld(front[a]);
            Vector3 p2 = CoordinateConverter.ToUnityWorld(front[b]);
            Vector3 p3 = CoordinateConverter.ToUnityWorld(front[c]);
            float w = 1f - leftWeight;
            dirUpper += (p2 - p1).normalized * w;
            dirLower += (p3 - p2).normalized * w;
            totalW += w;
        }
        if (lOk)
        {
            // ⚠️ 左摄坐标系矫正: X↔Z 交换
            //    左摄画面X=房间前后 → 应映射到Unity Z
            Vector3 p1 = CoordinateConverter.ToUnityWorldLeft(left[a]);
            Vector3 p2 = CoordinateConverter.ToUnityWorldLeft(left[b]);
            Vector3 p3 = CoordinateConverter.ToUnityWorldLeft(left[c]);
            dirUpper += (p2 - p1).normalized * leftWeight;
            dirLower += (p3 - p2).normalized * leftWeight;
            totalW += leftWeight;
        }

        if (dirUpper.sqrMagnitude > 1e-6f) dirUpper.Normalize();
        if (dirLower.sqrMagnitude > 1e-6f) dirLower.Normalize();
        ApplyBoneRig(upperRig, dirUpper, rotationSmooth);
        ApplyBoneRig(lowerRig, dirLower, rotationSmooth);
    }

    void ApplyFootRigDual(Dictionary<string, Landmark> front, Dictionary<string, Landmark> left,
        bool isLeftFoot, float leftWeight)
    {
        BoneRig rig = isLeftFoot ? _leftFootRig : _rightFootRig;
        if (rig.bone == null) return;
        string ankle = isLeftFoot ? "27" : "28";
        string toe   = isLeftFoot ? "31" : "32";

        bool fOk = front != null && LandmarksPresent(front, ankle, toe);
        bool lOk = left  != null && LandmarksUsable(left,  ankle, toe);
        if (!fOk && !lOk) return;

        Vector3 dir = Vector3.zero;
        if (fOk) { Vector3 a = CoordinateConverter.ToUnityWorld(front[ankle]); Vector3 t = CoordinateConverter.ToUnityWorld(front[toe]); dir += (t - a).normalized * (1f - leftWeight); }
        if (lOk) { Vector3 a = CoordinateConverter.ToUnityWorldLeft(left[ankle]);  Vector3 t = CoordinateConverter.ToUnityWorldLeft(left[toe]);  dir += (t - a).normalized * leftWeight; }
        if (dir.sqrMagnitude > 1e-6f) dir.Normalize();
        ApplyBoneRig(rig, dir, rotationSmooth);
    }

    // ══════════════════════════════════════════════════════════════
    // 身体 / 头 / 手指
    // ══════════════════════════════════════════════════════════════

    void ApplyBodyRotation(Dictionary<string, Landmark> pose)
    {
        if (_hips == null) return;
        if (!pose.ContainsKey("11") || !pose.ContainsKey("12") || !pose.ContainsKey("23") || !pose.ContainsKey("24")) return;

        Vector3 ls = CoordinateConverter.ToUnityWorld(pose["11"]);
        Vector3 rs = CoordinateConverter.ToUnityWorld(pose["12"]);
        Vector3 lh = CoordinateConverter.ToUnityWorld(pose["23"]);
        Vector3 rh = CoordinateConverter.ToUnityWorld(pose["24"]);

        Vector3 shoulderCenter = (ls + rs) * 0.5f;
        Vector3 hipCenter = (lh + rh) * 0.5f;

        Vector3 up = shoulderCenter - hipCenter;
        if (up.sqrMagnitude < 1e-6f) return;
        up.Normalize();

        Vector3 shoulderRight = rs - ls;
        if (shoulderRight.sqrMagnitude < 1e-6f) return;
        shoulderRight.Normalize();

        Vector3 forward = Vector3.Cross(shoulderRight, up).normalized;
        if (invertBodyForward) forward = -forward;

        Quaternion targetHipsWorldRot = Quaternion.LookRotation(forward, up);
        _hips.rotation = Quaternion.Slerp(_hips.rotation, targetHipsWorldRot, Time.deltaTime * bodyRotationSmooth);

        Vector3 hipRight = rh - lh;
        if (hipRight.sqrMagnitude < 1e-6f) return;
        hipRight.Normalize();

        Vector3 hipRightFlat = Vector3.ProjectOnPlane(hipRight, up);
        Vector3 shoulderRightFlat = Vector3.ProjectOnPlane(shoulderRight, up);
        if (hipRightFlat.sqrMagnitude < 1e-6f || shoulderRightFlat.sqrMagnitude < 1e-6f) return;
        hipRightFlat.Normalize(); shoulderRightFlat.Normalize();

        float twistAngle = Mathf.Clamp(Vector3.SignedAngle(hipRightFlat, shoulderRightFlat, up), -70f, 70f);
        DriveSpineBone(_spine, _spineRestLocalRot, twistAngle * 0.40f);
        DriveSpineBone(_chest, _chestRestLocalRot, twistAngle * 0.35f);
        DriveSpineBone(_upperChest, _upperChestRestLocalRot, twistAngle * 0.25f);
    }

    void DriveSpineBone(Transform bone, Quaternion restLocalRot, float twistDeg)
    {
        if (bone == null) return;
        Quaternion targetLocal = restLocalRot * Quaternion.AngleAxis(twistDeg, Vector3.up);
        bone.localRotation = Quaternion.Slerp(bone.localRotation, targetLocal, Time.deltaTime * bodyRotationSmooth);
    }

    void ApplyHeadRotation(Dictionary<string, Landmark> pose)
    {
        if (_head == null || !pose.ContainsKey("0")) return;
        Vector3 nose = CoordinateConverter.ToUnityWorld(pose["0"]);

        if (pose.ContainsKey("7") && pose.ContainsKey("8"))
        {
            Vector3 ear1 = CoordinateConverter.ToUnityWorld(pose["7"]);
            Vector3 ear2 = CoordinateConverter.ToUnityWorld(pose["8"]);
            Vector3 earCenter = (ear1 + ear2) * 0.5f;

            // 头部前向：鼻子 → 远离耳朵中心
            Vector3 fwdWorld = nose - earCenter;
            if (fwdWorld.sqrMagnitude < 1e-6f) return;
            fwdWorld.Normalize();

            // 耳线方向：pose[7]↔pose[8] 经 FRONT_POSE_SIDE_SWAP 后左右
            // 含义反转（7=右耳, 8=左耳）。以下用世界 up 自动判断正确符号：
            Vector3 earDir = ear2 - ear1;                    // 可能是左或右
            Vector3 approxRight = Vector3.Cross(Vector3.up, fwdWorld);
            // 若 earDir 和 approxRight 反向 → 翻转 earDir
            if (Vector3.Dot(earDir, approxRight) < 0f)
                earDir = -earDir;

            Vector3 upWorld = Vector3.Cross(fwdWorld, earDir.normalized);
            if (upWorld.sqrMagnitude < 1e-6f) upWorld = Vector3.up; else upWorld.Normalize();

            Quaternion targetWorld = SafeLookRotation(fwdWorld, upWorld);
            Quaternion targetLocal = _head.parent != null
                ? Quaternion.Inverse(_head.parent.rotation) * targetWorld
                : targetWorld;

            _head.localRotation = Quaternion.Slerp(
                _head.localRotation, targetLocal,
                Time.deltaTime * headRotationSmooth);
        }
        else if (pose.ContainsKey("11") && pose.ContainsKey("12"))
        {
            Vector3 sc = (CoordinateConverter.ToUnityWorld(pose["11"]) +
                          CoordinateConverter.ToUnityWorld(pose["12"])) * 0.5f;
            Vector3 fwdWorld = nose - sc;
            if (fwdWorld.sqrMagnitude < 1e-6f) return;
            fwdWorld.Normalize();

            Quaternion targetWorld = SafeLookRotation(fwdWorld, Vector3.up);
            Quaternion targetLocal = _head.parent != null
                ? Quaternion.Inverse(_head.parent.rotation) * targetWorld
                : targetWorld;

            _head.localRotation = Quaternion.Slerp(
                _head.localRotation, targetLocal,
                Time.deltaTime * headRotationSmooth);
        }
    }

    void ApplyHandAndFingers(Dictionary<string, Landmark> pose, bool isLeft)
    {
        string prefix = isLeft ? "LH_" : "RH_";
        BasisRig handRig = isLeft ? _leftHandRig : _rightHandRig;
        if (handRig.bone == null) return;

        if (TryGetHandPoint(pose, prefix, 0, out Vector3 wrist) &&
            TryGetHandPoint(pose, prefix, 5, out Vector3 iMcp) &&
            TryGetHandPoint(pose, prefix, 9, out Vector3 mMcp) &&
            TryGetHandPoint(pose, prefix, 17, out Vector3 pMcp))
        {
            Vector3 fwd = mMcp - wrist;
            if (fwd.sqrMagnitude > 1e-6f)
            {
                fwd.Normalize();
                Vector3 up = Vector3.Cross(iMcp - wrist, pMcp - wrist);
                if (up.sqrMagnitude < 1e-6f) up = Vector3.up; else up.Normalize();
                ApplyBasisRig(handRig, fwd, up, fingerRotationSmooth);
            }
        }

        ApplyFingerChains(pose, prefix, isLeft ? _leftFingers : _rightFingers);
    }

    void ApplyFingerChains(Dictionary<string, Landmark> pose, string prefix, FingerRigChain[] chains)
    {
        for (int f = 0; f < 5; f++)
        {
            var c = chains[f]; if (c == null) continue;
            if (!TryGetHandPoint(pose, prefix, FingerIdx(f, 1), out Vector3 p1)) continue;
            if (!TryGetHandPoint(pose, prefix, FingerIdx(f, 2), out Vector3 p2)) continue;
            if (!TryGetHandPoint(pose, prefix, FingerIdx(f, 3), out Vector3 p3)) continue;
            TryGetHandPoint(pose, prefix, FingerIdx(f, 4), out Vector3 p4);
            ApplyBoneRig(c.proximal, p2 - p1, fingerRotationSmooth);
            ApplyBoneRig(c.intermediate, p3 - p2, fingerRotationSmooth);
            ApplyBoneRig(c.distal, p4.sqrMagnitude > 0f ? p4 - p3 : p3 - p2, fingerRotationSmooth);
        }
    }

    int FingerIdx(int f, int j) { return f * 4 + j; }

    /// <summary>
    /// 该路姿态的关键点是否可用于双摄混合：都存在且可见度达标。
    /// 遮挡/低置信度时剔除该摄贡献，避免旧姿态混入。
    /// </summary>
    bool LandmarksUsable(Dictionary<string, Landmark> pose, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!pose.TryGetValue(k, out var lm)) return false;
            if (lm.visibility < minLandmarkVisibility) return false;
        }
        return true;
    }

    bool LandmarksPresent(Dictionary<string, Landmark> pose, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!pose.ContainsKey(k)) return false;
        }
        return true;
    }

    bool TryGetHandPoint(Dictionary<string, Landmark> pose, string prefix, int idx, out Vector3 w)
    {
        string key = prefix + idx.ToString();
        if (pose != null && pose.ContainsKey(key)) { w = CoordinateConverter.ToUnityWorld(pose[key]); return true; }
        w = Vector3.zero; return false;
    }

    // ══════════════════════════════════════════════════════════════
    // 骨骼驱动工具
    // ══════════════════════════════════════════════════════════════

    void ApplyBoneRig(BoneRig rig, Vector3 targetDirWorld, float smooth)
    {
        if (rig?.bone == null || targetDirWorld.sqrMagnitude < 1e-6f) return;

        Vector3 desiredParentDir = rig.bone.parent != null
            ? rig.bone.parent.InverseTransformDirection(targetDirWorld.normalized)
            : targetDirWorld.normalized;
        if (desiredParentDir.sqrMagnitude < 1e-6f) return;

        Vector3 restParentDir = rig.restLocalRotation * rig.restAxisLocal;
        if (restParentDir.sqrMagnitude < 1e-6f) return;

        Quaternion delta = Quaternion.FromToRotation(restParentDir.normalized, desiredParentDir.normalized);
        Quaternion targetLocalRot = delta * rig.restLocalRotation;

        rig.bone.localRotation = Quaternion.Slerp(rig.bone.localRotation, targetLocalRot, Time.deltaTime * smooth);
    }

    void ApplyBasisRig(BasisRig rig, Vector3 targetForwardWorld, Vector3 targetUpWorld, float smooth)
    {
        if (rig?.bone == null) return;
        if (targetForwardWorld.sqrMagnitude < 1e-6f || targetUpWorld.sqrMagnitude < 1e-6f) return;

        Vector3 desiredParentForward = rig.bone.parent != null ? rig.bone.parent.InverseTransformDirection(targetForwardWorld.normalized) : targetForwardWorld.normalized;
        Vector3 desiredParentUp = rig.bone.parent != null ? rig.bone.parent.InverseTransformDirection(targetUpWorld.normalized) : targetUpWorld.normalized;

        Vector3 restParentForward = rig.restLocalRotation * rig.restForwardLocal;
        Vector3 restParentUp = rig.restLocalRotation * rig.restUpLocal;

        Quaternion restBasis = SafeLookRotation(restParentForward, restParentUp);
        Quaternion targetBasis = SafeLookRotation(desiredParentForward, desiredParentUp);
        Quaternion delta = targetBasis * Quaternion.Inverse(restBasis);
        Quaternion targetLocalRot = delta * rig.restLocalRotation;

        rig.bone.localRotation = Quaternion.Slerp(rig.bone.localRotation, targetLocalRot, Time.deltaTime * smooth);
    }

    Quaternion SafeLookRotation(Vector3 forward, Vector3 upHint)
    {
        if (forward.sqrMagnitude < 1e-6f) return Quaternion.identity;
        forward.Normalize();
        upHint = Vector3.ProjectOnPlane(upHint, forward);
        if (upHint.sqrMagnitude < 1e-6f) upHint = Vector3.ProjectOnPlane(Vector3.up, forward);
        if (upHint.sqrMagnitude < 1e-6f) return Quaternion.identity;
        return Quaternion.LookRotation(forward, upHint.normalized);
    }

    void OnGUI()
    {
        if (!showSquatOverlay || !footIKActive) return;
        float x = 10, y = 90, w = 440, lh = 15;
        GUI.Box(new Rect(x, y, w, lh * 6 + 8), "");
        y += 4;
        void L(string s) { GUI.Label(new Rect(x + 6, y, w - 12, lh), s); y += lh; }
        string age = receiver != null && receiver.HasReceivedAny
            ? receiver.LatestPacketAgeMs.ToString("F0") + " ms" : "--";
        L($"[Squat] 基准={(_squatBaselineEstablished ? "已建立" : "未建立(等待站直0.6s)")}  数据帧龄={age}  (非端到端延迟)");
        L($"  目标下降={_squatTargetDescent:F3}  实际下降={DescentMeters:F3} m");
        L($"  左膝={_leftKneeDeg:F0}°  右膝={_rightKneeDeg:F0}°  (站立∈[165,180])");
        L($"  可见度≥{squatMinVisibility:F2}={SquatDataValid}  (蹲姿时腿部可见度需保留)");
        L($"  [勾选 enableSquat 生效；Mocap→Re-identify Standing Baseline 可重测基准]");
    }
}
