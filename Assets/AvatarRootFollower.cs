using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Avatar Root 移动控制器（v16 — 双摄 Root 估计驱动）
/// ====================================================
/// 职责：接收 DualCameraRootEstimator 计算好的位移量，平滑移动 Avatar Root。
/// 不再自己计算方向/速度 — 所有 Root 估计逻辑都在 DualCameraRootEstimator 中。
///
/// 用法:
///   1. 挂载到 Avatar Root GameObject 上
///   2. 拖入 DualCameraRootEstimator 引用
///   3. 运行，按 C 校准，人物走动时角色跟随移动
///
/// 执行顺序 -20：本组件必须早于 HumanoidPoseDriver(0) 移动根节点。
/// 否则姿态计划用的是“移动前”的根位置，计划踝位会带上 v×dt 的系统性偏移，
/// 落脚点整体偏短，脚锁锚点也跟着偏。顺序改为“整体位移 → 姿态计划 → 脚锁”。
/// </summary>
[DefaultExecutionOrder(-20)]
public class AvatarRootFollower : MonoBehaviour
{
    [Header("References")]
    public DualCameraRootEstimator rootEstimator;
    public Transform avatarRoot;
    public Animator animator;

    [Header("Movement")]
    public float maxSpeed = 1.5f;
    public float acceleration = 4f;
    public float deceleration = 6f;

    [Header("Plane")]
    public bool lockToPlane = true;
    public float planeHeight = 0f;

    [Header("Rotation")]
    public bool lockRootRotation = true;
    public bool rotateToMovement = false;
    public float rotationSpeed = 10f;

    [Header("Calibration")]
    public bool autoCalibrateOnStart = true;
    public KeyCode recalibrateKey = KeyCode.C;

    [Header("Test")]
    public bool allowSpaceTest = true;
    public float testSpeed = 2f;

    [Header("Debug")]
    public bool showDebugGUI = true;

    // ── 内部状态 ──
    private Vector3 _velocity;
    private float _currentSpeed;
    private bool _hasEstimator;
    private Quaternion _initialRootRotation;

    // Debug
    private string _status = "";

    // ────────────────────────────────────────────────
    // Unity Lifecycle
    // ────────────────────────────────────────────────

    void Start()
    {
        if (rootEstimator == null)
            rootEstimator = FindObjectOfType<DualCameraRootEstimator>();

        if (avatarRoot == null)
            avatarRoot = transform;

        if (animator == null)
            animator = GetComponent<Animator>();

        if (animator != null)
            animator.applyRootMotion = false;

        _hasEstimator = rootEstimator != null;
        _initialRootRotation = avatarRoot.rotation;

        if (_hasEstimator)
            Debug.Log($"[v16] ✅ AvatarRootFollower — 使用 DualCameraRootEstimator");
        else
            Debug.LogWarning("[v16] ⚠️ 未找到 DualCameraRootEstimator，角色不会移动！");
    }

    void Update()
    {
        if (Input.GetKeyDown(recalibrateKey))
        {
            // 仅在 Python 标定激活后允许重校（避免未标定时从零锚点）
            if (_hasEstimator && rootEstimator.isCalibrated)
                rootEstimator.Calibrate();
        }
    }

    void LateUpdate()
    {
        if (!_hasEstimator || !rootEstimator.isCalibrated)
        {
            if (_hasEstimator)
                _status = "⏳ 等待校准 — 按 C";
            else
                _status = "❌ 缺少 DualCameraRootEstimator";
            return;
        }

        // ── 1. 从 Root 估计器获取速度（m/s，已按 UDP 包时间计算）──
        Vector3 targetVel;
        float targetSpeed;

        if (allowSpaceTest && Input.GetKey(KeyCode.Space))
        {
            targetVel = avatarRoot.forward * testSpeed;
            targetSpeed = testSpeed;
            _status = "🔬 空格测试";
        }
        else
        {
            targetVel = rootEstimator.rootVelocity;
            targetVel.y = 0f;
            targetSpeed = targetVel.magnitude;

            // 速度上限
            if (targetSpeed > maxSpeed)
                targetVel = targetVel.normalized * maxSpeed;

            // 状态
            if (targetSpeed > 0.1f)
                _status = "🏃 步行";
            else
                _status = "🛑 静止";
        }

        // 平滑
        float smooth = targetVel.sqrMagnitude > 0.001f ? acceleration : deceleration;
        _velocity = Vector3.Lerp(_velocity, targetVel, Time.deltaTime * smooth);
        if (_velocity.sqrMagnitude < 0.0005f) _velocity = Vector3.zero;
        _currentSpeed = _velocity.magnitude;

        // ── 3. 移动 Avatar Root ──
        avatarRoot.position += _velocity * Time.deltaTime;

        if (lockToPlane)
        {
            Vector3 p = avatarRoot.position;
            p.y = planeHeight;
            avatarRoot.position = p;
        }

        // ── 4. Root 只负责世界平面位移；默认锁旋转，避免和 Hips 身体朝向叠加成绕点旋转 ──
        if (lockRootRotation)
        {
            avatarRoot.rotation = _initialRootRotation;
        }
        else if (rotateToMovement && _currentSpeed > 0.05f)
        {
            Vector3 flat = new Vector3(_velocity.x, 0, _velocity.z);
            if (flat.sqrMagnitude > 0.001f)
            {
                avatarRoot.rotation = Quaternion.Slerp(
                    avatarRoot.rotation,
                    Quaternion.LookRotation(flat.normalized, Vector3.up),
                    Time.deltaTime * rotationSpeed
                );
            }
        }
    }

    // ────────────────────────────────────────────────
    // Debug GUI
    // ────────────────────────────────────────────────

    void OnGUI()
    {
        if (!showDebugGUI) return;

        string line = $"[v16] est={_hasEstimator} cal={(rootEstimator != null ? rootEstimator.isCalibrated : false)} {_status}";
        GUI.Box(new Rect(5, Screen.height - 28, Screen.width - 10, 24), line);

        if (rootEstimator == null || !rootEstimator.isCalibrated)
            return;

        int x = 10, y = 10, w = 400, lh = 17;
        int lines = 10;
        GUI.Box(new Rect(x, y, w, lines * lh + 12), "");
        void L(string s) { GUI.Label(new Rect(x + 6, y, w - 12, lh), s); y += lh; }
        y += 4;

        L($"🎯 v16  Root 估计驱动 [{_status}]"); y += 2;
        L("══ ⚙️ DualCameraRootEstimator ══");
        L($"  vel=({rootEstimator.rootVelocity.x:F2}, {rootEstimator.rootVelocity.z:F2}) m/s");
        L($"  rawDX={rootEstimator.rawDX:+0.0000;-0.0000}  rawDZ={rootEstimator.rawDZ:+0.0000;-0.0000}");
        L("══ 🏃 移动 ══");
        L($"  速度={_currentSpeed:F2} m/s  vel=({_velocity.x:F2}, {_velocity.z:F2})");
        L($"  pos=({avatarRoot.position.x:F1}, {avatarRoot.position.z:F1})");
        y += 2;
        L($"  [C]校准  [空格]测试");
    }
}
