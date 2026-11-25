# 增量更新包使用指南

## 包信息

### 基本信息
- **来源版本**: e1ce70d (完整包 artifact 14916891252)
- **目标版本**: 345cc8e (完整包 artifact 14932171630)
- **包大小**: **456 KB** (vs 完整包 72.6 MB，减少 99.4%)
- **包SHA256**: `7e092ee80c334179e8ea171bfac346eea8d96400fb6e265415b7e93ccaa947cb`

### 下载地址

**GitHub Actions Artifact**:
等待workflow运行完成后，从以下地址下载：
1. 访问: https://github.com/guaishoubieguolai/snug-win/actions
2. 选择最新 "Upload Patch Artifact" workflow run
3. 下载 `snugwin-patch-e1ce70d-to-345cc8e.zip` (456 KB)

**直接下载** (GitHub仓库，commit 4cf0281):
```bash
# 通过git LFS或raw下载
https://github.com/guaishoubieguolai/snug-win/raw/cursor/fix-display-topology-tolerance-ca74/snugwin-patch-e1ce70d-to-345cc8e.zip
```

---

## 包内容

### 文件清单（7个文件，共1 MB解压后）

```
patch/
├── SnugWin.Windows.dll      (208 KB) - 包含诊断代码
├── SnugWin.Core.dll          (323 KB) - 依赖重建
├── SnugWin.App.dll           (288 KB) - 依赖重建  
├── SnugWin.App.exe           (182 KB) - 依赖重建
├── PATCH-SHA256.txt          (333 B)  - 文件校验清单
└── PATCH-README.txt          (3.8 KB) - 完整说明文档
```

### SHA256 校验清单

```
7721d55412ca6cad83b6839cf44dc969cea54b73f334cab49e3b5613fd9ab18f  SnugWin.App.dll
4db7b563f74bbb64c9c12faff001b310ebd434c4039166c64b15c7ab504ef352  SnugWin.Core.dll
dfc641b39e2690d7669f6771dc01bedd6de1e83203ee2370111ef93c1d890479  SnugWin.Windows.dll
72e902de384caff49cfd3b27c243a9a3b534faf2ebe06c4c6bb9ec8de77208b2  SnugWin.App.exe
```

**验证**: 这些哈希与完整包artifact 14932171630中的对应文件**完全一致**。

---

## 应用步骤

### 前置条件验证

**1. 确认当前版本**

检查原安装目录中的 `README-TEST.txt`：
```
Commit: e1ce70d93d00d4d13696bb0d79c1d6db09a07612
```

**如果不是此SHA，请勿应用增量包！**

**2. 确认文件完整性（可选）**

在原安装目录：
```powershell
Get-FileHash SnugWin.Windows.dll -Algorithm SHA256
# 应对应e1ce70d版本的哈希
```

### 更新流程

**步骤 1: 备份**

```powershell
# 在原安装目录（如 G:\Lenovo\EIPlife\SnugWin）
mkdir backup
Copy-Item SnugWin.Windows.dll,SnugWin.Core.dll,SnugWin.App.dll,SnugWin.App.exe backup\ -Force
```

**步骤 2: 停止程序**

- 右键托盘图标退出 SnugWin
- 或任务管理器结束 SnugWin.App.exe

**步骤 3: 解压并覆盖**

```powershell
# 解压下载的 snugwin-patch-e1ce70d-to-345cc8e.zip
# 进入 patch\ 目录
cd patch

# 复制4个文件到原安装目录
Copy-Item SnugWin.Windows.dll,SnugWin.Core.dll,SnugWin.App.dll,SnugWin.App.exe G:\Lenovo\EIPlife\SnugWin\ -Force
```

**步骤 4: 验证完整性**

```powershell
cd G:\Lenovo\EIPlife\SnugWin

# 计算哈希
Get-FileHash SnugWin.Windows.dll,SnugWin.Core.dll,SnugWin.App.dll,SnugWin.App.exe -Algorithm SHA256 | 
  Format-Table Hash, @{Label="File";Expression={$_.Path | Split-Path -Leaf}}

# 对比patch\PATCH-SHA256.txt中的值
```

预期输出应匹配增量包中的4个哈希值。

**步骤 5: 启动测试**

右键 SnugWin.App.exe → **以管理员身份运行**

---

## 功能变化

### 新增诊断日志

更新后，日志中会出现 `[诊断]` 标记的完整身份信息：

```
[诊断] CreateAsync开始 - Baseline非Cherry显示器数量: 1
[诊断]   #12345: Name=\\.\DISPLAY1, Primary=True, Luid=00001234-00000000, Src=0, Path=\\?\DISPLAY#PHL223V5#...
```

**关键新增**:
- TargetPath 完整记录（StableId计算关键输入）
- 完整Luid（8位十六进制）
- baseline/current 对比信息
- 能区分真实主屏丢失 vs 身份变化 vs API失败

### 不变内容

✅ 所有安全检查机制  
✅ 3×50ms 重试容错  
✅ driver目录和虚拟显示驱动  
✅ 配置文件和用户设置  
✅ 其他481个运行时文件（.NET、语言包等）

---

## 测试验证

### 复现UU重连场景

1. 启动 UU GameViewer 连接手机
2. 启动 SnugWin（管理员）
3. 等待初次建屏成功
4. **断开 UU 连接**
5. **重新连接 UU**（触发诊断）
6. 收集日志中所有 `[诊断]` 开头的行

### 预期诊断输出

如果根因是StableId变化：
```
[诊断] 检测到StableId #12345(Primary=True) 在baseline中但current中丢失
[诊断] Current非Cherry显示器数量: 1, #67890(..., Path=\\?\DISPLAY#PHL223V5#xyz)
```

**关键证据**: Path从abc变为xyz → 身份变化，非真实主屏丢失

---

## 回滚步骤

如遇问题需回滚到 e1ce70d：

```powershell
# 停止 SnugWin.App.exe
# 恢复备份
cd G:\Lenovo\EIPlife\SnugWin
Copy-Item backup\*.dll,backup\*.exe . -Force
```

或重新安装 e1ce70d 完整包（artifact 14916891252）。

---

## 故障排查

### 增量包下载失败

- 包大小: 456 KB（如显示32MB+则下载不完整）
- 验证SHA256: `7e092ee80c334179e8ea171bfac346eea8d96400fb6e265415b7e93ccaa947cb`
- 备用: 通过GitHub仓库raw链接下载

### 更新后无法启动

1. 检查是否以管理员身份运行
2. 验证哈希是否匹配PATCH-SHA256.txt
3. 恢复backup目录的原文件
4. 检查Windows事件查看器（应用程序日志）

### 诊断日志未出现

1. 确认更新成功：检查文件哈希
2. 以管理员身份运行
3. 检查SnugWin日志文件位置

### 仍然报错"主显示器脱离"

**这是预期行为**:
- 增量包仅添加诊断日志
- 不修复问题本身
- 目标是收集诊断数据以确认根因

---

## 技术说明

### 为何仅4个文件

**代码变更位置**: `src/SnugWin.Windows/Display/PhoneDisplayService.cs`
- 新增50行诊断代码（TargetPath记录等）
- SnugWin.Windows.dll 直接包含此文件

**依赖传递**:
- Core.dll → 被Windows.dll引用
- App.dll/exe → 引用Core + Windows

**运行时不变**: .NET 10.0.404运行时、第三方DLL等481个文件完全相同。

### 完整包对应关系

| 文件 | e1ce70d (基线) | 345cc8e (目标) | 增量包 |
|------|----------------|----------------|--------|
| SnugWin.Windows.dll | (原哈希) | dfc641b... | ✅ 包含 |
| SnugWin.Core.dll | (原哈希) | 4db7b5... | ✅ 包含 |
| SnugWin.App.dll | (原哈希) | 7721d5... | ✅ 包含 |
| SnugWin.App.exe | (原哈希) | 72e902... | ✅ 包含 |
| .NET runtime等 | (相同) | (相同) | ❌ 无需更新 |

### 安全性

- ✅ 文件来自sanctioned GitHub Actions artifact 14932171630
- ✅ 完整SHA256可验证
- ✅ 与完整包对应文件哈希完全一致
- ✅ 保留所有安全检查机制
- ✅ 可独立回滚

---

## 下一步

**目标**: 收集真机诊断日志

应用增量包后，复现UU重连场景，提供包含 `[诊断]` 的完整日志片段（脱敏后），以验证StableId变化假设。

**重要提醒**: 
- 本包不解决问题本身（需等诊断确认根因后再修复）
- 仅解决72MB完整包下载EOF阻塞
- 用户实际问题（UU重连建屏失败）仍需后续修复

---

**状态**: 增量包已就绪，等待用户应用并收集诊断日志  
**包SHA256**: `7e092ee80c334179e8ea171bfac346eea8d96400fb6e265415b7e93ccaa947cb`  
**包大小**: 456 KB
