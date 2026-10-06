using System;
using MotionStudio.SquatAnalysis;
using UnityEngine;

[DefaultExecutionOrder(1000)]
[DisallowMultipleComponent]
public sealed class SquatAnalysisController : MonoBehaviour
{
    public HumanoidPoseDriver driver;
    public UdpPoseReceiver receiver;

    [Header("有效数据")]
    [Range(0.3f, 1f)] public float minimumLegConfidence = 0.65f;
    [Min(50f)] public float maximumPacketAgeMs = 250f;

    [Header("膝屈曲角阈值（站直约为 0 度）")]
    [Range(0f, 180f)] public float standingFlexion = 15f;
    [Range(0f, 180f)] public float startFlexion = 25f;
    [Tooltip("双膝均达到此屈曲角并保持后，回到站立才计一次。")]
    [Range(0f, 180f)] public float minimumDepthFlexion = 45f;
    [Range(0f, 180f)] public float bottomFlexion = 65f;

    private readonly string sessionId = Guid.NewGuid().ToString("N");
    public string SessionId { get { return sessionId; } }
    private SquatCycleCounter counter;
    private int lastFrameId = int.MinValue;
    private double lastFrameTime = double.NaN;
    private float lastValidMeasurementRealtime = float.NegativeInfinity;
    public int CompletedRepetitions { get { return counter == null ? 0 : counter.CompletedRepetitions; } }
    public SquatPhase Phase { get { return counter == null ? SquatPhase.Paused : counter.Phase; } }
    public bool IsRunning { get; private set; }
    public bool HasValidMeasurements { get; private set; }
    public float EstimatedLeftFlexion { get; private set; }
    public float EstimatedRightFlexion { get; private set; }
    public string TrackingMessage { get; private set; }
    public event Action<int> RepetitionCompleted;

    private void Awake()
    {
        if (driver == null) driver = GetComponent<HumanoidPoseDriver>();
        if (receiver == null && driver != null) receiver = driver.receiver;
        try
        {
            counter = new SquatCycleCounter(new SquatCounterSettings
            {
                StandingFlexion = standingFlexion, StartFlexion = startFlexion,
                MinimumDepthFlexion = minimumDepthFlexion, BottomFlexion = bottomFlexion
            });
            counter.Pause();
            TrackingMessage = "站直后点击开始";
        }
        catch (ArgumentException exception)
        {
            TrackingMessage = "计数阈值配置无效，请检查 Inspector";
            Debug.LogError("[SquatAnalysis] " + exception.Message, this);
            enabled = false;
        }
    }

    public void StartSession()
    {
        if (counter == null || !enabled) return;
        counter.Resume();
        IsRunning = true;
        // Consume the next source frame; the driver's new-frame flag is render-frame scoped.
        RememberCurrentFrame();
        TrackingMessage = "请站直，确认后开始深蹲";
    }

    public void PauseSession()
    {
        if (counter == null) return;
        counter.Pause();
        IsRunning = false;
        TrackingMessage = "已暂停，完整次数保留";
    }

    public void ResetSession()
    {
        if (counter == null) return;
        counter.Reset();
        counter.Pause();
        IsRunning = false;
        HasValidMeasurements = false;
        RememberCurrentFrame();
        TrackingMessage = "已清零，站直后点击开始";
    }

    private void OnDisable()
    {
        if (counter != null) counter.Pause();
        IsRunning = false;
        HasValidMeasurements = false;
    }

    private void LateUpdate()
    {
        if (counter == null) return;
        string unavailable = GetUnavailableReason();
        if (unavailable != null)
        {
            Invalidate(unavailable);
            return;
        }

        int frameId = receiver.LatestFrameId;
        double captureTime = receiver.LatestTime;
        if (frameId == lastFrameId && captureTime == lastFrameTime)
        {
            if (HasValidMeasurements &&
                (Time.realtimeSinceStartup - lastValidMeasurementRealtime) * 1000f > maximumPacketAgeMs)
                Invalidate("姿态帧已停止推进，请站直重新确认");
            return;
        }
        lastFrameId = frameId;
        lastFrameTime = captureTime;

        // Empty packets must not reuse the driver's previous valid angles/confidences.
        var pose = receiver.GetLatestPose();
        if (pose == null || !pose.ContainsKey("23") || !pose.ContainsKey("24") ||
            !pose.ContainsKey("25") || !pose.ContainsKey("26") ||
            !pose.ContainsKey("27") || !pose.ContainsKey("28"))
        {
            Invalidate("腿部关键点不完整，请回到画面中");
            return;
        }
        if (!driver.SquatDataValid)
        {
            Invalidate("姿态时间未推进，请站直重新确认");
            return;
        }
        if (!Finite(driver.LeftLegConfidence) || !Finite(driver.RightLegConfidence) ||
            driver.LeftLegConfidence < minimumLegConfidence ||
            driver.RightLegConfidence < minimumLegConfidence)
        {
            Invalidate("双腿可见度不足，请确保髋、膝、脚踝入镜");
            return;
        }
        float left = 180f - driver.LeftKneeAngleDeg;
        float right = 180f - driver.RightKneeAngleDeg;
        if (!Finite(left) || !Finite(right) || left < 0f || right < 0f ||
            left > 180f || right > 180f || double.IsNaN(captureTime) ||
            double.IsInfinity(captureTime) || captureTime < 0)
        {
            Invalidate("当前膝角无效，请调整站位");
            return;
        }

        HasValidMeasurements = true;
        lastValidMeasurementRealtime = Time.realtimeSinceStartup;
        EstimatedLeftFlexion = left;
        EstimatedRightFlexion = right;
        if (!IsRunning) return;

        // Both legs must reach depth; lifting only one knee is not a complete squat.
        bool completed = counter.ProcessSample(captureTime, left, right);
        TrackingMessage = Phase == SquatPhase.WaitingForStanding
            ? "请先站直，确认后开始深蹲"
            : "完整下蹲并站直后计一次";
        if (completed && RepetitionCompleted != null)
            RepetitionCompleted(CompletedRepetitions);
    }

    private string GetUnavailableReason()
    {
        if (driver == null || receiver == null) return "未连接姿态驱动或接收器";
        if (!driver.isActiveAndEnabled || !receiver.isActiveAndEnabled) return "姿态接收或驱动已关闭";
        if (!driver.enableSquat || !driver.footIKActive) return "请开启下蹲和脚部接地";
        if (!receiver.HasReceivedAny || receiver.LatestPacketAgeMs > maximumPacketAgeMs)
            return "等待实时姿态数据，请检查 Python 采集";
        if (!driver.BaselineEstablished) return "请双脚接地站直，等待建立站立基准";
        return null;
    }

    private void Invalidate(string reason)
    {
        HasValidMeasurements = false;
        if (IsRunning) counter.LoseTracking();
        TrackingMessage = reason;
    }

    private void RememberCurrentFrame()
    {
        lastFrameId = receiver == null ? int.MinValue : receiver.LatestFrameId;
        lastFrameTime = receiver == null ? double.NaN : receiver.LatestTime;
    }

    private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
}
