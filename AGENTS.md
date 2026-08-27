# Repository Guidelines

## Structure

- `Class1.cs` owns plugin lifecycle, endpoint registration, response adaptation, and skill-plan calculation.
- `SkillTipsDisplayRenderer.cs` renders immutable display snapshots with Terminal.Gui.
- `GradeRank.cs` contains score thresholds and rank labels.
- `i18n/ParseSkillTipsResponse*.resx` contains user-facing text.
- The dedicated smoke project is `..\tests\SkillTipsResponseAnalyzerSmoke` and is outside this Git root.

## Build Safety

Keep `<IsUraPlugin>true</IsUraPlugin>` in `SkillTipsResponseAnalyzer.csproj`. The umbrella repository's `Directory.Build.targets` supplies the Host project reference and package targets; do not add Host contract NuGet packages or a project-local Host reference.

From this repository, build without packaging or local deployment:

```powershell
dotnet build .\SkillTipsResponseAnalyzer.csproj -c Release -m:1 -p:UraHostProjectPath="<ura-host-project>" -p:GenerateUraPluginManifestOnBuild=false -p:PackageUraPluginOnBuild=false -p:DeployUraPluginToLocalAppDataOnBuild=false
```

## Smoke Test

From the `URA-Plugins` umbrella root:

```powershell
dotnet run --project .\tests\SkillTipsResponseAnalyzerSmoke\SkillTipsResponseAnalyzerSmoke.csproj -c Release -m:1 -p:UraHostProjectPath="<ura-host-project>" -p:GenerateUraPluginManifestOnBuild=false -p:PackageUraPluginOnBuild=false -p:DeployUraPluginToLocalAppDataOnBuild=false
```

Pass a raw Ramen `check_event` or `load` MessagePack body as the final argument when replay coverage is required.

## Code and Localization

- Preserve the wildcard `check_event`, `load`, and `finish` registrations and the owned workspace lifecycle when changing adapters or UI behavior.
- Keep calculation logic in `Class1.cs` and rendering logic in `SkillTipsDisplayRenderer.cs` unless their contract changes together.
- Update the base, `zh-CN`, `en-US`, and `ja-JP` resource files together. Do not edit generated `*.Designer.cs` files by hand.

## Security Boundary

Treat packet bodies and endpoint DTOs as untrusted input. Fail clearly on incompatible data; do not guess schemas or add silent fallbacks. Do not commit captured packets, local paths, credentials, or generated plugin packages.
