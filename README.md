# SkillTipsResponseAnalyzer

育成角色进入结束状态且没有未处理事件时，计算技能评分方案并在 `SkillTipsResponseAnalyzer` workspace 中显示结果。single-mode `finish` 和插件卸载都会移除该 workspace。

## 构建

```powershell
dotnet build .\SkillTipsResponseAnalyzer.csproj -c Release -m:1 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PlatformTarget=AnyCPU -p:DeployUraPluginToLocalAppDataOnBuild=false
```

Host-dependent smoke 位于 `tests/SkillTipsResponseAnalyzerSmoke`。

## 验证与发布

在 Windows 仓库根执行 `act workflow_dispatch --artifact-server-path "$env:TEMP/ura-act-artifacts"`。本地与 GitHub 使用同一份 workflow；版本 tag 触发 GitHub Release 发布。环境要求、共用 workflow 本地映射和发布规则见 [URA plugin workflows](https://github.com/URA-Plugins/.github/blob/v1/README.md)。
