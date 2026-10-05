using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class SquatAnalysisSetup
{
    [MenuItem("Mocap/Install Squat Analysis Panel")]
    public static void InstallForCurrentScene()
    {
        Install();
    }

    // Batch entry point operates only on the scene inside the task project.
    public static void InstallAndSaveForBatch()
    {
        EditorSceneManager.OpenScene("Assets/动捕.unity", OpenSceneMode.Single);
        Install();
        if (!EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene()))
            throw new InvalidOperationException("Unable to save the squat scene.");
        AssetDatabase.SaveAssets();
        Debug.Log("[SquatAnalysisSetup] Scene saved with the squat analysis panel.");
    }

    private static void Install()
    {
        HumanoidPoseDriver driver = Selection.activeGameObject == null
            ? null : Selection.activeGameObject.GetComponent<HumanoidPoseDriver>();
        if (driver == null)
        {
            var drivers = UnityEngine.Object.FindObjectsOfType<HumanoidPoseDriver>(true);
            if (drivers.Length != 1)
                throw new InvalidOperationException("Select the avatar with HumanoidPoseDriver before installing the panel.");
            driver = drivers[0];
        }
        if (driver.receiver == null)
            throw new InvalidOperationException("Connect the driver's front UdpPoseReceiver before installing the panel.");

        var controller = driver.GetComponent<SquatAnalysisController>();
        if (controller == null) controller = Undo.AddComponent<SquatAnalysisController>(driver.gameObject);
        var overlay = driver.GetComponent<SquatAnalysisOverlay>();
        if (overlay == null) overlay = Undo.AddComponent<SquatAnalysisOverlay>(driver.gameObject);
        Undo.RecordObjects(new UnityEngine.Object[] { controller, overlay }, "Configure squat analysis");
        controller.driver = driver;
        controller.receiver = driver.receiver;
        overlay.controller = controller;
        var desktopBridge = driver.GetComponent<SquatDesktopBridge>();
        if (desktopBridge == null) desktopBridge = Undo.AddComponent<SquatDesktopBridge>(driver.gameObject);
        var sender = UnityEngine.Object.FindObjectOfType<FootIkUdpSender>(true);
        if (sender == null) sender = Undo.AddComponent<FootIkUdpSender>(driver.gameObject);
        Undo.RecordObjects(new UnityEngine.Object[] { desktopBridge, sender }, "Connect desktop squat controls");
        desktopBridge.analysis = controller;
        sender.analysis = controller;
        sender.controller = driver.GetComponent<FootPlantingController>();
        EditorUtility.SetDirty(desktopBridge);
        EditorUtility.SetDirty(sender);
        EditorUtility.SetDirty(controller);
        EditorUtility.SetDirty(overlay);
        EditorSceneManager.MarkSceneDirty(driver.gameObject.scene);
        Selection.activeGameObject = driver.gameObject;
        Debug.Log("[SquatAnalysisSetup] Installed on " + driver.gameObject.name + ".", driver.gameObject);
    }
}
