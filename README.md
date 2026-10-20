# Accent It

Accent It 是一个自用的 Windows 桌面工具，用于给 Unicode 字母添加组合附加符号，并优先输出预组合字符。

技术栈：C#、.NET 10、WPF。

## 当前进度

阶段 1 的核心逻辑已经实现并通过自动化测试：

- 从 JSONC 文件读取符号目录、位置分组和组合策略。
- 校验配置内容；无效配置不会让程序崩溃。
- 第一次运行时，从程序自带的默认目录生成一份可编辑的用户目录；之后不会覆盖用户修改。
- 支持 NFC 组合、严格模式，以及顺序无关的显式例外规则。
- 组合结果包含原始序列、NFC、NFD、码点和实际使用的组合方式，供后续界面显示诊断信息。

目前 WPF 窗口仍然是空骨架，还没有接入这些核心逻辑。运行 `dotnet run --project src/AccentIt` 只会打开一个空白窗口，这是当前阶段的预期状态。下一步才会把符号目录和组合功能接入界面。

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

当前测试覆盖配置解析与校验、用户配置创建与回退、NFC/严格组合、部分预组合、例外规则和 Unicode 非 BMP 字符。

## 可编辑的符号目录

符号目录不是写死在 C# 里的。界面使用的数据来自程序目录旁的 JSONC 文件，并且分为两个文件：

| 文件 | 作用 | 是否会被程序覆盖 |
| --- | --- | --- |
| `Data/accents.default.jsonc` | 程序自带的默认目录（种子） | 构建时可能被源文件更新 |
| `Data/accents.jsonc` | 用户实际编辑的目录 | 已存在时不会被覆盖 |

`Data` 是相对于可执行文件所在目录的。开发时它们位于：

```text
src/AccentIt/bin/Debug/net10.0-windows/Data/accents.default.jsonc
src/AccentIt/bin/Debug/net10.0-windows/Data/accents.jsonc
```

加载逻辑第一次运行时，如果 `accents.jsonc` 不存在，会把它从 `accents.default.jsonc` 复制出来。之后只读取 `accents.jsonc`，不会用默认文件覆盖它。目前窗口还没有接入这段逻辑，所以启动当前版本的程序暂时不会生成用户文件；接入界面后才会自动生成。

源码中的默认目录位于：

```text
src/AccentIt/Data/accents.default.jsonc
```

修改建议：

1. 界面接入后，先启动一次程序，让 `Data/accents.jsonc` 自动生成。当前也可以手动把 `accents.default.jsonc` 复制为 `accents.jsonc` 来准备配置。
2. 编辑 `Data/accents.jsonc`。
3. 重启程序后生效；当前没有文件热重载。
4. 如果 `accents.jsonc` 有错误，加载逻辑会报告问题并回退到默认目录，不会直接崩溃。界面接入后，这些诊断会显示在窗口中。

> **重要：** 用户配置在构建输出目录里，执行 `dotnet clean`、删除 `bin` 或清理发布目录会把它一起删掉。请定期备份 `Data/accents.jsonc`。日常使用时，建议发布到一个独立文件夹，而不是长期依赖 `bin/Debug`。

## JSONC 配置格式

配置文件使用 UTF-8 编码，接受 `//` 注释和尾随逗号。格式错误的 JSON 会被拒绝。

顶层结构：

```jsonc
{
  "schemaVersion": 1,

  "positions": [
    { "id": "above", "displayName": "上方" },
    { "id": "below", "displayName": "下方" },
    { "id": "overlay", "displayName": "叠加" }
  ],

  "marks": [
    {
      "id": "acute",
      "codePoint": "U+0301",
      "name": "锐音符",
      "position": "above",
      "description": "可选说明"
    }
  ],

  "composition": {
    "mode": "nfc",
    "rules": []
  }
}
```

字段说明：

- `schemaVersion`：当前只支持 `1`。
- `positions`：位置分组，数组顺序就是界面显示顺序。`id` 只能包含 ASCII 字母、数字、`-`、`_`、`.`，且不能重复。
- `marks`：组合附加符号。`codePoint` 推荐使用 `U+XXXX` 形式；符号必须是 Unicode 组合附加符号（nonspacing、spacing combining 或 enclosing mark）。`position` 必须引用已定义的位置。`id` 和码点都不能重复。
- `composition.mode`：
  - `"nfc"`：输出 NFC 规范化结果。若无法完整预组合，可能得到“部分预组合”的序列。例如 `a` + 锐音符 + 下点符可能得到 `ạ` 加上一个锐音符。
  - `"precomposed-or-raw"`：只有整个结果能变成一个 Unicode 标量（单个字符）时才使用预组合；否则输出“基础字母 + 已选符号”的原始序列，保持选择顺序。
- `composition.rules`：显式例外映射。某些字符（例如 `ø`、`ł`、`đ`）没有 NFC 规范分解，不能仅靠基础字母加组合符号自动得到，需要在这里写规则。

一条例外规则：

```jsonc
{
  "base": "o",
  "marks": ["overlay-long-slash"],
  "resultCodePoint": "U+00F8",
  "note": "o + 长斜线 → ø；这是显式映射"
}
```

规则行为：

- `base` 必须是单个 Unicode 字母。
- `marks` 必须引用 `marks` 中已定义的符号 id，不能重复。
- `resultCodePoint` 必须是单个 Unicode 字母。
- 规则匹配只看“基础字母和符号 id 的集合”，与符号选择顺序无关。
- 只要匹配到规则，规则优先于 `composition.mode`。
- 同一基础字母和同一组符号 id 不能定义两条重复规则。

## 已知限制

- 预组合依赖 Unicode NFC 的规范组合规则。像 `ø`、`ł`、`đ` 这类没有规范分解的字母，不能仅靠“基础字母 + 组合符号”自动得到；需要显式规则。
- 多个组合附加符号的显示效果取决于字体；码点正确不代表所有程序中外观一致。
- Windows 剪贴板与其他应用的文本处理行为可能改变最终的码点表现。
- 当前阶段不包含全局快捷键、插入位置定位、向其他应用自动插入文本和安装包。
