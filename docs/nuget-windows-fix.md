# Windows 本机 NuGet / .NET 修复备忘

## 症状

`dotnet restore` / `dotnet test` 报：

```text
Value cannot be null. (Parameter 'path1')
The type initializer for 'NuGet.Configuration.ConfigurationDefaults' threw an exception.
```

`dotnet nuget locals all --list` 同样失败。

## 根因

进程环境中 **`ProgramFiles(x86)` 为 null**（本应 `C:\Program Files (x86)`）。

NuGet.Common 的 `NuGetEnvironment.CalculateFolderPath(MachineWideSettingsBaseDirectory)` 会：

```csharp
Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles(x86)"), ...)
```

path1 为 null → 直接抛异常，整个 restore 图生成失败。

## 修复（当前 PowerShell 会话）

```powershell
[Environment]::SetEnvironmentVariable('ProgramFiles(x86)', 'C:\Program Files (x86)', 'Process')
[Environment]::SetEnvironmentVariable('ProgramW6432', 'C:\Program Files', 'Process')
```

持久化（用户级，需重开终端）：

```powershell
[Environment]::SetEnvironmentVariable('ProgramFiles(x86)', 'C:\Program Files (x86)', 'User')
[Environment]::SetEnvironmentVariable('ProgramW6432', 'C:\Program Files', 'User')
```

## 验证

```powershell
dotnet nuget locals all --list
# 应输出 http-cache / global-packages / temp / plugins-cache
```
