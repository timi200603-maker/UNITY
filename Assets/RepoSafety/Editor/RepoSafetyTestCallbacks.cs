using System;
using System.IO;
using System.Xml;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

public sealed class RepoSafetyTestCallbacks : ScriptableObject, IErrorCallbacks
{
    public TestRunnerApi runner;
    public string resultsPath;
    public int exitCode = 2;
    public bool finished;

    public void RunStarted(ITestAdaptor testsToRun) { }
    public void TestStarted(ITestAdaptor test) { }
    public void TestFinished(ITestResultAdaptor result) { }

    public void RunFinished(ITestResultAdaptor result)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultsPath)));
            using (var writer = XmlWriter.Create(resultsPath, new XmlWriterSettings { Indent = true }))
            {
                writer.WriteStartElement("test-run");
                writer.WriteAttributeString("result", result.ResultState);
                writer.WriteAttributeString("total",
                    (result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount).ToString());
                writer.WriteAttributeString("passed", result.PassCount.ToString());
                writer.WriteAttributeString("failed", result.FailCount.ToString());
                writer.WriteAttributeString("skipped", result.SkipCount.ToString());
                writer.WriteAttributeString("inconclusive", result.InconclusiveCount.ToString());
                result.ToXml().WriteTo(writer);
                writer.WriteEndElement();
            }
            exitCode = result.ResultState == "Passed" && result.FailCount == 0 &&
                result.PassCount > 0 && result.SkipCount == 0 && result.InconclusiveCount == 0 ? 0 : 2;
            Debug.Log(string.Format("[RepoSafety] EditMode complete: passed={0}, failed={1}; results={2}",
                result.PassCount, result.FailCount, resultsPath));
        }
        catch (Exception exception)
        {
            exitCode = 2;
            Debug.LogException(exception);
        }
        finished = true;
    }

    public void OnError(string message)
    {
        exitCode = 2;
        Debug.LogError("[RepoSafety] " + message);
        finished = true;
    }
}
