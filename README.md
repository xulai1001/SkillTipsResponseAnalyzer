# SkillTipsResponseAnalyzer

育成角色进入结束状态且没有未处理事件时，计算技能评分方案并在 `SkillTipsResponseAnalyzer` workspace 中显示结果。single-mode `finish` 和插件卸载都会移除该 workspace。

## 构建

```powershell
git -c core.longpaths=true submodule update --init --recursive
dotnet build .\SkillTipsResponseAnalyzer.csproj -c Release -m:1 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PlatformTarget=AnyCPU -p:DeployUraPluginToLocalAppDataOnBuild=false
dotnet run --project .\tests\SkillTipsResponseAnalyzerSmoke\SkillTipsResponseAnalyzerSmoke.csproj -c Release -p:GenerateUraPluginManifestOnBuild=false -p:PackageUraPluginOnBuild=false -p:DeployUraPluginToLocalAppDataOnBuild=false
```
