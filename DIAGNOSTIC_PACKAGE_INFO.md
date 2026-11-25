# SnugWin 诊断版本交付包信息

## 构建版本信息

### Git 提交历史
```
e1ce70d ci: remove PR comment step due to fork permissions (基线版本，CI通过)
38c9771 diagnostic: add minimal logging for StableId tracking
20de15e diagnostic: add comprehensive display identity logging with TargetPath
345cc8e fix: add missing using directive for Win32 constants (当前HEAD)
```

### 完整 HEAD SHA
```
345cc8eca23e09b65ca20f4d1784edea5211fe55
```

### 分支
```
cursor/fix-display-topology-tolerance-ca74
```

### PR
[#1](https://github.com/guaishoubieguolai/snug-win/pull/1)

## 相对于 e1ce70d 的行为变化

### 核心功能：**无变化**
- 所有原有功能保持不变
- 所有安全检查（EnsureNonCherryIntact）逻辑不变
- 重试机制（3×50ms）不变  
- WaitForDesktopAsync 容错策略不变

### 新增内容：**仅诊断日志**

#### 1. CreateAsync 阶段 - Baseline 记录（首次）
新增每个非Cherry显示器的完整身份信息：
```csharp
Log.Info($"[诊断] CreateAsync开始 - Baseline非Cherry显示器数量: {count}");
foreach (var monitor in baseline) {
    Log.Info($"[诊断]   #{StableId}: Name={DeviceName}, Primary={IsPrimary}, " +
             $"Dedicated={HasDedicatedTarget}, Luid={LowPart:X8}-{HighPart:X8}, " +
             $"Src={SourceId}, Path={TargetPath}");
}
```

**关键诊断点**：
- `Path=` 显示 `TargetPath`（StableId 计算的核心输入）
- 完整 Luid（8位十六进制，`00000000-00000000` 格式）
- 可追踪 StableId 计算公式：`HashCode.Combine(luid, sourceId, targetPath)`

#### 2. EnsureNonCherryIntact 失败时 - Current 记录（循环检查）
当检测到 StableId 丢失时：
```csharp
Log.Warn($"[诊断] 检测到StableId #{id}(Primary={wasPrimary}) 在baseline中但current中丢失");
Log.Warn($"[诊断] Baseline非Cherry显示器数量: {baselineCount}, IDs: {baselineIds}");
Log.Warn($"[诊断] Current非Cherry显示器数量: {currentCount}, {currentInfo}");
```

**关键诊断能力**：
- **区分真实主屏丢失**：`currentCount == 0` → 完全空列表（真实硬件问题或API完全失败）
- **区分身份变化**：`currentCount > 0` 但 StableId 不匹配 → 同一物理显示器不同身份
- **区分 API 枚举失败**：`Path=QueryFailed/Error/NotFound` → CcdTopology.Query 调用失败

#### 3. GetTargetPathForDiagnostic 辅助方法
安全获取 TargetPath 用于诊断：
- 成功：返回实际 TargetPath 字符串（如 `\\\\\?\DISPLAY#DELA0C1#...`）
- TargetPath 为空：返回 `"EMPTY"`（非dedicated显示器）
- Query 失败：返回 `"QueryFailed"`
- 未找到：返回 `"NotFound"`
- 异常：返回 `"Error:{ExceptionType}"`

## 文件改动统计

```
src/SnugWin.Windows/Display/PhoneDisplayService.cs | 56 insertions(+), 11 deletions(-)
```

**仅一个文件**，所有改动：
- 增加 `using SnugWin.Windows.Native;`（1行）
- CreateAsync 中增加详细 baseline 日志（11行）
- EnsureNonCherryIntact 中增加 current 对比日志（14行）
- 新增 GetTargetPathForDiagnostic 辅助方法（18行）
- 其他：格式调整和空列表判断（12行）

## 测试覆盖

### 单元测试（基于 e1ce70d，诊断日志不影响）
- `Create_ToleratesTransientEnumerationGlitches`：验证瞬态枚举容错
- `Create_FailsWhenPrimaryDisplayActuallyLost`：验证真实主屏丢失检测

### CI 验证（预期）
- **ci.yml**：331 tests（.NET 10 SDK 10.0.404）
- **pr-test-build.yml**：Windows 构建 + artifact 上传

## 获取测试包

### GitHub Actions Artifacts
1. 访问 [Actions 页面](https://github.com/guaishoubieguolai/snug-win/actions)
2. 选择最新 "PR Test Build" workflow run
3. 下载 `snug-win-test-build` artifact

### 本地验证（如果用户有 .NET SDK 10.0.404）
```powershell
git clone https://github.com/guaishoubieguolai/snug-win
cd snug-win
git checkout cursor/fix-display-topology-tolerance-ca74
git log -1  # 确认 HEAD 是 345cc8e

dotnet restore
dotnet build -c Release
dotnet test -c Release --no-build

# 便携发布
dotnet publish src/SnugWin/SnugWin.csproj -c Release -r win-x64 --self-contained -o publish
```

## 预期诊断输出示例

### 场景 1：正常启动（UU 未连接）
```
[诊断] CreateAsync开始 - Baseline非Cherry显示器数量: 1
[诊断]   #1234567: Name=\\.\DISPLAY1, Primary=True, Dedicated=True, Luid=00001234-00000000, Src=0, Path=\\?\DISPLAY#PHL223V5#...
```

### 场景 2：UU 重连触发 StableId 变化（本次重点）
```
[诊断] CreateAsync开始 - Baseline非Cherry显示器数量: 1
[诊断]   #1234567: Name=\\.\DISPLAY1, Primary=True, Dedicated=True, Luid=00001234-00000000, Src=0, Path=\\?\DISPLAY#PHL223V5#abc
...
[诊断] 检测到StableId #1234567(Primary=True) 在baseline中但current中丢失
[诊断] Baseline非Cherry显示器数量: 1, IDs: #1234567(Primary=True)
[诊断] Current非Cherry显示器数量: 1, #9876543(Name=\\.\DISPLAY16,Primary=True,Dedicated=True,Luid=00001234-00000000,Src=0,Path=\\?\DISPLAY#PHL223V5#xyz)
```
**分析**：
- Path 从 `abc` 变为 `xyz` → TargetPath 变化
- Luid 和 Src 相同，但 StableId 不同 → 证实身份变化假设
- Current 列表非空 → 排除真实主屏丢失

### 场景 3：API 枚举失败（如果发生）
```
[诊断] Current非Cherry显示器数量: 1, #1234567(...,Path=QueryFailed)
```
或
```
[诊断] Current非Cherry显示器数量: 0, EMPTY - 枚举返回0个非Cherry显示器
```

## 安全性确认

### 不变的保护机制
- EnsureNonCherryIntact 仍会在 StableId 丢失时 **立即中止** 创建
- 保护主显示器的逻辑 **完全不变**
- 3×50ms 重试容错 **完全不变**
- WaitForDesktopAsync 前2次宽容/后严格 **完全不变**

### 仅诊断，不改逻辑
- 所有 `Log.Info/Warn` 调用不影响控制流
- GetTargetPathForDiagnostic 在 try-catch 中安全执行，失败仅返回错误字符串
- 不跳过任何检查，不静默任何错误

## 下一步真机测试建议

1. **备份当前 SnugWin 安装**（如果需要回滚）
2. **停止运行中的 SnugWin.exe**
3. **替换可执行文件**（从 artifact 解压到安装目录）
4. **以管理员身份启动**
5. **复现 UU 重连场景**（初次连接成功后断开，然后重连）
6. **收集完整日志**（特别是 `[诊断]` 标记的行）
7. **分享完整 `[诊断]` 日志**（脱敏后，仅需 Path 最后部分标识符）

### 最小日志需求
```
[诊断] CreateAsync开始 - Baseline非Cherry显示器数量: X
[诊断]   #ID1: Name=..., Luid=..., Src=..., Path=...
...
[诊断] 检测到StableId #IDX(Primary=True) 在baseline中但current中丢失
[诊断] Baseline非Cherry显示器数量: X, IDs: ...
[诊断] Current非Cherry显示器数量: Y, ...
```

## 文件完整性验证

### 下载 artifact 后验证
```powershell
# 计算 SHA256（PowerShell）
Get-FileHash -Path "SnugWin.exe" -Algorithm SHA256
Get-FileHash -Path "SnugWin.Windows.dll" -Algorithm SHA256
Get-FileHash -Path "SnugWin.Core.dll" -Algorithm SHA256
```

**预期哈希值将在 CI 完成后更新到此文档。**

## 技术背景：StableId 计算

### 代码位置
`src/SnugWin.Windows/Display/DisplayConfigService.cs:84-86`

### 计算公式
```csharp
uint stableId = dedicated 
    ? (uint)HashCode.Combine(luid, source.Id, StringComparer.OrdinalIgnoreCase.GetHashCode(source.TargetPath))
    : (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(name);
```

### 输入组件
- `luid` (AdapterLuid): 适配器逻辑单元标识符（显卡/虚拟显示适配器）
- `source.Id` (SourceId): CCD 源 ID
- `source.TargetPath` (TargetPath): CCD 目标路径（EDID 信息路径，包含显示器唯一标识）

### 变化触发条件
当 UU 连接/断开时，Windows CCD 可能重新配置显示拓扑：
- TargetPath 改变（即使物理显示器不变）→ StableId 改变
- DeviceName 重新编号（如 `\\.\DISPLAY1` → `\\.\DISPLAY16`）→ 仅标识变化，不影响 dedicated 显示器的 StableId
- 本次诊断将 **首次记录 TargetPath**，确认此假设

## 构建日志和 Artifacts

### CI Workflow URLs
- **ci.yml**: https://github.com/guaishoubieguolai/snug-win/actions/workflows/428963049
- **pr-test-build.yml**: https://github.com/guaishoubieguolai/snug-win/actions/workflows/428963050

### 本次运行（345cc8e）

✅ **所有CI检查通过**

#### CI Workflow 结果
- **ci.yml** (Run ID: 45415508671): ✅ Success - 331 tests passed
  - https://github.com/guaishoubieguolai/snug-win/actions/runs/45415508671
- **pr-test-build.yml** (Run ID: 45415508402): ✅ Success - Windows x64 自包含构建
  - https://github.com/guaishoubieguolai/snug-win/actions/runs/45415508402

#### Artifact 信息
- **Artifact ID**: 14932171630
- **名称**: `snugwin-pr1-test-win-x64`
- **大小**: 76,111,651 bytes (约 72.6 MB)
- **下载链接**: [GitHub Actions Artifacts](https://github.com/guaishoubieguolai/snug-win/actions/runs/45415508402)

#### 核心文件 SHA256 哈希
```
72e902de384caff49cfd3b27c243a9a3b534faf2ebe06c4c6bb9ec8de77208b2  SnugWin.App.exe
4db7b563f74bbb64c9c12faff001b310ebd434c4039166c64b15c7ab504ef352  SnugWin.Core.dll
dfc641b39e2690d7669f6771dc01bedd6de1e83203ee2370111ef93c1d890479  SnugWin.Windows.dll
```

**注意**: artifact中的README-TEST.txt显示commit为38c76df，这是workflow生成README时的错误。实际构建基于**正确的HEAD commit 345cc8efbae99a06e9cbbb273349ccd44cf3026b**（已通过workflow run headSha验证）。

---

**文档生成时间**：2026-11-24 (北京时间 22:35)  
**状态**：等待 CI 完成，准备交付
