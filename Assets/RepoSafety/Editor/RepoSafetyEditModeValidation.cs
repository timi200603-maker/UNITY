using System;
using System.Reflection;
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

        RequireSynchronousSuite();
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
    // Unity's synchronous filter omits coroutine tests. Reject those explicitly
    // rather than silently accepting a partially executed future suite.
    private static void RequireSynchronousSuite()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic ||
                !Array.Exists(assembly.GetReferencedAssemblies(), item => item.Name == "nunit.framework"))
                continue;
            foreach (var type in assembly.GetTypes())
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    foreach (var attribute in method.GetCustomAttributesData())
                    {
                        string name = attribute.AttributeType.FullName;
                        if (name == "UnityEngine.TestTools.UnityTestAttribute" ||
                            name == "UnityEngine.TestTools.UnitySetUpAttribute" ||
                            name == "UnityEngine.TestTools.UnityTearDownAttribute")
                            throw new InvalidOperationException(
                                "Coroutine test hooks require an asynchronous validation entry point: " +
                                type.FullName + "." + method.Name);
                    }
        }
    }

}
