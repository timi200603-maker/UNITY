using System;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

public static class RepoSafetyEditModeValidation
{
    public static void Run()
    {
        if (!Application.isBatchMode)
            throw new InvalidOperationException("This validation entry point is for batch mode.");
        string[] arguments = Environment.GetCommandLineArgs();
        string resultsPath = null;
        for (int i = 0; i < arguments.Length - 1; i++)
            if (arguments[i] == "-testResults") resultsPath = arguments[i + 1];
        if (string.IsNullOrEmpty(resultsPath))
            throw new ArgumentException("A -testResults path is required.");

        var runner = ScriptableObject.CreateInstance<TestRunnerApi>();
        var callbacks = ScriptableObject.CreateInstance<RepoSafetyTestCallbacks>();
        callbacks.runner = runner;
        callbacks.resultsPath = resultsPath;
        runner.RegisterCallbacks(callbacks);
        runner.Execute(new ExecutionSettings(new Filter { testMode = TestMode.EditMode })
            { runSynchronously = true });
        if (!callbacks.finished || callbacks.exitCode != 0)
            throw new InvalidOperationException("EditMode validation did not complete successfully.");
        runner.UnregisterCallbacks(callbacks);
        UnityEngine.Object.DestroyImmediate(callbacks);
        UnityEngine.Object.DestroyImmediate(runner);
    }
}
