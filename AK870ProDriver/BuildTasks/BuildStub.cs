// AK870ProDriver 构建环境补丁 — Appx/PRI 任务的透传 stub
// 背景: 本机 VS Build Tools 未装 "MSIX 打包工具" 组件, WindowsAppSDK 1.6 的
// MrtCore.PriGen.targets 引用 VS 私有的 AppxPackage 任务 DLL 导致构建失败。
// 本应用为 unpackaged 且无 MRT 本地化资源, 以下任务用直通实现即可。
// 用 MSBuild 自带 Roslyn csc 编译:
//   "...\MSBuild\Current\Bin\Roslyn\csc.exe" -nologo -target:library
//     -out:Ak870BuildTasksStub.dll BuildStub.cs
//     -r:"...\MSBuild\Current\Bin\Microsoft.Build.Framework.dll"
//     -r:"...\MSBuild\Current\Bin\Microsoft.Build.Utilities.Core.dll"
using Microsoft.Build.Framework;

namespace Microsoft.Build.AppxPackage
{
    public class ExpandPayloadDirectories : Microsoft.Build.Utilities.Task
    {
        public ITaskItem[] Inputs { get; set; } = System.Array.Empty<ITaskItem>();
        public string? VsTelemetrySession { get; set; }
        [Output] public ITaskItem[] Expanded { get; set; } = System.Array.Empty<ITaskItem>();

        public override bool Execute() { Expanded = Inputs ?? System.Array.Empty<ITaskItem>(); return true; }
    }

    public class RemovePayloadDuplicates : Microsoft.Build.Utilities.Task
    {
        public ITaskItem[] Inputs { get; set; } = System.Array.Empty<ITaskItem>();
        public string? ProjectName { get; set; }
        public string? Platform { get; set; }
        public string? VsTelemetrySession { get; set; }
        [Output] public ITaskItem[] Filtered { get; set; } = System.Array.Empty<ITaskItem>();

        public override bool Execute() { Filtered = Inputs ?? System.Array.Empty<ITaskItem>(); return true; }
    }

    public class GetSdkFileFullPath : Microsoft.Build.Utilities.Task
    {
        public string? FileName { get; set; }
        public string? FileArchitecture { get; set; }
        public bool RequireExeExtension { get; set; }
        public string? TargetPlatformSdkRootOverride { get; set; }
        public string? SDKIdentifier { get; set; }
        public string? SDKVersion { get; set; }
        public string? TargetPlatformIdentifier { get; set; }
        public string? TargetPlatformMinVersion { get; set; }
        [Output] public string? FullFilePath { get; set; }

        public override bool Execute() { return true; }
    }

    public class GetSdkPropertyValue : Microsoft.Build.Utilities.Task
    {
        public string? TargetPlatformSdkRootOverride { get; set; }
        public string? SDKIdentifier { get; set; }
        public string? SDKVersion { get; set; }
        public string? TargetPlatformIdentifier { get; set; }
        public string? TargetPlatformMinVersion { get; set; }
        public string? TargetPlatformVersion { get; set; }
        public string? PropertyName { get; set; }
        public string? VsTelemetrySession { get; set; }
        [Output] public string? PropertyValue { get; set; }

        public override bool Execute() { return true; }
    }

    public class GetPackageArchitecture : Microsoft.Build.Utilities.Task
    {
        public string? Platform { get; set; }
        public ITaskItem[] ProjectArchitecture { get; set; } = System.Array.Empty<ITaskItem>();
        public ITaskItem[] RecursiveProjectArchitecture { get; set; } = System.Array.Empty<ITaskItem>();
        public string? VsTelemetrySession { get; set; }
        [Output] public string? PackageArchitecture { get; set; }

        public override bool Execute() { PackageArchitecture = Platform; return true; }
    }

    public class GetDefaultResourceLanguage : Microsoft.Build.Utilities.Task
    {
        public string? DefaultLanguage { get; set; }
        public ITaskItem[] SourceAppxManifest { get; set; } = System.Array.Empty<ITaskItem>();
        public string? VsTelemetrySession { get; set; }
        [Output] public string? DefaultResourceLanguage { get; set; }

        public override bool Execute() { DefaultResourceLanguage = DefaultLanguage; return true; }
    }

    public class ValidateConfiguration : Microsoft.Build.Utilities.Task
    {
        public string? TargetPlatformMinVersion { get; set; }
        public string? TargetPlatformVersion { get; set; }
        public string? ProjectLanguage { get; set; }
        public string? VsTelemetrySession { get; set; }
        public string? TargetPlatformIdentifier { get; set; }
        public string? Platform { get; set; }

        public override bool Execute() { return true; }
    }
}

namespace Microsoft.Build.Packaging.Pri.Tasks
{
    public class ExpandPriContent : Microsoft.Build.Utilities.Task
    {
        public ITaskItem[] Inputs { get; set; } = System.Array.Empty<ITaskItem>();
        public string? MakePriExeFullPath { get; set; }
        public string? MakePriExtensionPath { get; set; }
        public string? IntermediateDirectory { get; set; }
        public string? AdditionalMakepriExeParameters { get; set; }
        public bool ExcludeXamlFromLibraryLayoutsWhenXbfIsPresent { get; set; }
        public string? VsTelemetrySession { get; set; }
        [Output] public ITaskItem[] Expanded { get; set; } = System.Array.Empty<ITaskItem>();
        [Output] public ITaskItem[] IntermediateFileWrites { get; set; } = System.Array.Empty<ITaskItem>();

        public override bool Execute() { Expanded = Inputs ?? System.Array.Empty<ITaskItem>(); return true; }
    }
}
