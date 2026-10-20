# Accent It

Accent It 是一个自用的 Windows 桌面工具，用于给 Unicode 字母添加组合附加符号，并优先输出预组合字符。

技术栈：C#、.NET 10、WPF。

## 当前状态

项目处于阶段 1 的开发初期。目前只有可构建、可启动的项目骨架，组合功能尚未实现。

阶段 1 计划实现：

- 输入单个基础字母。
- 按“上方 / 下方 / 叠加”选择组合附加符号。
- 即时预览。
- 使用 NFC 优先输出预组合字符；没有完整预组合时输出组合序列。
- 显示 NFC、NFD 和 Unicode 码点诊断信息。
- 复制结果到剪贴板。

## 环境要求

- Windows
- .NET 10 SDK
- .NET 10 Desktop Runtime（运行已构建的应用时）

## 构建、测试与运行

在本仓库根目录执行：

```bash
dotnet build AccentIt.slnx
dotnet test AccentIt.slnx
dotnet run --project src/AccentIt
```

项目使用 .NET 10 默认的 `.slnx` 解决方案格式；使用 `dotnet` CLI 构建即可，不依赖 Visual Studio。

## 阶段 1 的边界

当前阶段不包含：

- 全局快捷键
- 当前输入位置定位
- 向其他应用自动插入文本
- 设置持久化
- 左右、包围类附加符号
- 安装包

## 已知限制

- 预组合依赖 Unicode NFC 的规范组合规则。像 `ø`、`ł`、`đ` 这类没有规范分解的字母，不能仅靠“基础字母 + 组合符号”自动得到。
- 多个组合附加符号的显示效果取决于字体；码点正确不代表所有程序中外观一致。
- Windows 剪贴板与其他应用的文本处理行为可能改变最终的码点表现。
