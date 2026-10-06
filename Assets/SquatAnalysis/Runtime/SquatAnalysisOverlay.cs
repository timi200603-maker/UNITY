using MotionStudio.SquatAnalysis;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class SquatAnalysisOverlay : MonoBehaviour
{
    public SquatAnalysisController controller;
    public bool showPanel = true;
    private Font panelFont;
    private GUIStyle boxStyle, titleStyle, countStyle, textStyle, buttonStyle;

    private void Awake()
    {
        if (controller == null) controller = GetComponent<SquatAnalysisController>();
    }

    private void OnDestroy()
    {
        if (panelFont != null) Destroy(panelFont);
    }

    private void CreateStyles()
    {
        if (boxStyle != null) return;
        panelFont = Font.CreateDynamicFontFromOSFont(
            new[] { "Microsoft YaHei", "Microsoft YaHei UI", "Arial" }, 18);
        boxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(18, 18, 14, 14) };
        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
        countStyle = new GUIStyle(GUI.skin.label) { fontSize = 38, fontStyle = FontStyle.Bold };
        textStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 16 };
        foreach (var style in new[] { titleStyle, countStyle, textStyle, buttonStyle })
            if (panelFont != null) style.font = panelFont;
    }

    private void OnGUI()
    {
        if (!showPanel || controller == null) return;
        CreateStyles();
        // A fixed reference canvas keeps the controls readable on smaller Game views.
        float scale = Mathf.Min(1f, Screen.width / 800f, Screen.height / 500f);
        Matrix4x4 oldMatrix = GUI.matrix;
        Color oldColor = GUI.color;
        bool oldEnabled = GUI.enabled;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
        float width = 340f;
        GUILayout.BeginArea(new Rect(Screen.width / scale - width - 16f, 16f, width, 350f), boxStyle);
        GUILayout.Label("深蹲运动分析", titleStyle);
        GUILayout.Label(controller.CompletedRepetitions + " 次", countStyle);
        GUILayout.Label("阶段：" + PhaseLabel(controller.Phase), textStyle);
        GUILayout.Space(6f);
        string angles = controller.HasValidMeasurements
            ? string.Format("膝屈曲角（估计）\n左 {0:F0}°  /  右 {1:F0}°",
                controller.EstimatedLeftFlexion, controller.EstimatedRightFlexion)
            : "膝屈曲角：等待有效数据";
        GUILayout.Label(angles, textStyle);
        GUILayout.Label(controller.TrackingMessage ?? "", textStyle, GUILayout.MinHeight(50f));
        GUILayout.Space(8f);
        GUILayout.BeginHorizontal();
        GUI.enabled = oldEnabled && controller.enabled && !controller.IsRunning;
        if (GUILayout.Button("开始 / 继续", buttonStyle, GUILayout.Height(34f))) controller.StartSession();
        GUI.enabled = oldEnabled && controller.IsRunning;
        if (GUILayout.Button("暂停", buttonStyle, GUILayout.Height(34f))) controller.PauseSession();
        GUI.enabled = oldEnabled;
        if (GUILayout.Button("清零", buttonStyle, GUILayout.Height(34f))) controller.ResetSession();
        GUILayout.EndHorizontal();
        GUILayout.Label("双膝达到深度，再站直才计一次", textStyle);
        GUILayout.EndArea();
        GUI.enabled = oldEnabled;
        GUI.color = oldColor;
        GUI.matrix = oldMatrix;
    }

    public static string PhaseLabel(SquatPhase phase)
    {
        switch (phase)
        {
            case SquatPhase.Standing: return "站立";
            case SquatPhase.Descending: return "下降";
            case SquatPhase.Bottom: return "底部";
            case SquatPhase.Ascending: return "上升";
            case SquatPhase.TrackingLost: return "等待恢复追踪";
            case SquatPhase.Paused: return "已暂停";
            default: return "等待站直确认";
        }
    }
}
