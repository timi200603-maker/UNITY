# 本机检查点验证入口

配对任务与回滚规则继续使用 AGENTS.md 和 Tools/RepoSafety/ 下的脚本。

本机 Unity 2022.3.62f3c1 的命令行异步测试完成后，编辑器会卡在退出阶段。当前深蹲套件全部是普通 NUnit EditMode 测试，因此检查点脚本通过 RepoSafetyEditModeValidation 同步运行当前套件，再正常批处理退出。

验证仍必须同时满足：测试 XML 为 Passed、至少执行一项测试、没有失败/跳过/无法确定的测试、Unity 正常退出，以及 Python 编译、导入和 pytest 全部通过。失败时不创建稳定标签。

同步入口会明确拒绝 UnityTest、UnitySetUp、UnityTearDown 协程测试；以后引入这类测试时应改用支持协程且能正常退出的异步入口，不能静默跳过。

本次同时修复了 PowerShell 单行 Git 输出被索引成字符的问题，以及首次检查点状态没有 lastCheckpoint 字段时的写入错误。现有永久 checkpoint/... 标签不作替换。
