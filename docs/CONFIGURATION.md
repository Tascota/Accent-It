# Accent It 配置说明

本文说明 Accent It 的配置文件位置、符号目录 JSONC 格式和全局快捷键设置。快速开始见 [README](../README.md)，完整使用细节见 [使用说明](USAGE.md)。

## 配置文件

符号目录、字母变形路径和全局快捷键都不是写死在代码里的。界面使用的数据来自程序目录旁的 JSONC 文件：

| 文件 | 作用 | 是否会被程序覆盖 |
| --- | --- | --- |
| `Data/accents.default.jsonc` | 符号目录、字母变形路径和其他字符；程序直接读取此文件 | 源码文件较新时，构建会更新输出目录中的副本 |
| `Data/settings.jsonc` | 用户编辑的全局快捷键设置 | 首次运行创建，已存在时不会被覆盖 |
| `Data/theme.json` | 程序记录上次选择的界面主题 | 切换主题时由程序写入，不包含快捷键或符号配置 |

`Data` 相对于可执行文件所在目录。开发运行时默认位于：

```text
src/AccentIt/bin/Debug/net10.0-windows/Data/accents.default.jsonc
src/AccentIt/bin/Debug/net10.0-windows/Data/settings.jsonc
src/AccentIt/bin/Debug/net10.0-windows/Data/theme.json
```

程序直接读取 `Data/accents.default.jsonc`，不再生成或读取 `Data/accents.jsonc`。旧版本留下的 `accents.jsonc` 会保留在原处，但不会参与加载。源码中的默认目录位于 `src/AccentIt/Data/accents.default.jsonc`，构建时复制到输出目录。

修改配置的步骤：

1. 编辑 `src/AccentIt/Data/accents.default.jsonc`，或直接编辑输出目录中的 `Data/accents.default.jsonc`，修改符号、字母路径和其他字符。
2. 编辑 `Data/settings.jsonc` 修改全局快捷键；该文件在首次运行时自动创建。
3. 重启程序后生效；当前没有文件热重载。
4. 符号目录有错误时，程序会在“诊断详情”中列出原因并拒绝加载，没有备用目录；快捷键配置无效时使用默认值。

> **重要：** 源码中的符号目录比输出目录新时，构建会覆盖输出目录里的副本。`settings.jsonc` 和 `theme.json` 位于构建输出目录，执行 `dotnet clean`、删除 `bin` 或清理发布目录会一并删除。请定期备份修改过的符号目录和快捷键设置；日常使用时，建议发布到一个独立文件夹，而不是长期依赖 `bin/Debug`。

## 符号目录与字母路径 JSONC 格式

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
  },

  // 可选：字母变形路径与其他字符，详见下文。
  "letterComposition": {}
}
```

字段说明：

- `schemaVersion`：当前只支持 `1`。
- `positions`：位置分组，数组顺序就是完整编辑器的分组显示顺序。`id` 只能包含 ASCII 字母、数字、`-`、`_`、`.`，且不能重复。
  - 紧凑窗口用位置 id 决定符号在预览字母四边的位置：`above` / `top` 显示在上方，`below` / `bottom` / `under` 显示在下方，`left` / `right` 显示在左右；其他 id（包括默认的 `overlay`）按数量均衡分配到左右两侧。这个映射只影响紧凑窗口，完整编辑器仍按 `positions` 数组顺序显示。
- `marks`：组合附加符号。`codePoint` 推荐使用 `U+XXXX` 形式；符号必须是 Unicode 组合附加符号（nonspacing、spacing combining 或 enclosing mark）。`position` 必须引用已定义的位置。`id` 和码点都不能重复。
- `composition.mode`：
  - `"nfc"`：默认。输出 NFC 规范化结果。若无法完整预组合，可能得到“部分预组合”的序列。例如 `a` + 锐音符 + 下点符可能得到 `ạ` 加上一个锐音符。
  - `"nfd"`：完全分解输出，永远不使用预组合字符，只输出基础字母与按 NFD 规范排列的组合符号。例如 `a` + 锐音符 + 下点符得到 `U+0061 U+0323 U+0301`。此模式不应用符号例外规则；字母变形路径仍会先生成结果字母。
- `composition.rules`：显式例外映射，只在 NFC 模式下生效。某些字符（例如 `ø`、`ł`、`đ`）没有 NFC 规范分解，不能仅靠基础字母加组合符号自动得到，需要在这里写规则。

一条例外规则：

```jsonc
{
  "base": "o",
  "marks": ["overlay-long-slash"],
  "resultCodePoint": "U+00F8",
  "note": "o + 长斜线 → ø"
}
```

规则行为：

- `base` 必须是单个 Unicode 字母。
- `marks` 必须引用 `marks` 中已定义的符号 id，不能重复。
- `resultCodePoint` 必须是单个 Unicode 字母。
- 规则匹配只看“基础字母和符号 id 的集合”，与符号选择顺序无关。
- 在 NFC 模式下，只要匹配到规则，规则优先于自动组合。
- NFD 模式完全分解，不应用符号例外规则；`letterComposition.rules` 中的变形路径不受影响。
- 同一基础字母和同一组符号 id 不能定义两条重复规则。

### 字母变形与其他字符（letterComposition）

可选的顶级字段 `letterComposition` 用来定义抽象变形操作、字母路径和“其他字符”列表。结构示例：

```jsonc
"letterComposition": {
  "blocks": [
    {
      "id": "latin-1-supplement",
      "displayName": "Latin-1 增补",
      "firstCodePoint": "U+0080",
      "lastCodePoint": "U+00FF"
    }
  ],

  "operations": [
    { "id": "turn", "name": "倒转", "group": "方向", "description": "旋转 180°" },
    {
      "id": "ligature",
      "name": "合字",
      "group": "结构",
      "description": "把两个字母合成一个字符",
      "requiresSecondLetter": true
    }
  ],

  "rules": [
    { "base": "M", "operations": ["turn"], "resultCodePoint": "U+019C", "note": "M + 倒转 → Ɯ" },
    { "base": "i", "second": "j", "operations": ["ligature"], "resultCodePoint": "U+0133", "note": "i + j + 合字 → ĳ" }
  ],

  "otherLetters": [
    {
      "codePoint": "U+00FE",
      "name": "LATIN SMALL LETTER THORN",
      "block": "latin-1-supplement",
      "aliases": ["thorn"]
    }
  ]
}
```

字段说明：

- `blocks`：给“其他字符”分区和搜索用的 Unicode 区块。`firstCodePoint` / `lastCodePoint` 是闭区间，区块范围不能重叠。
- `operations`：抽象变形操作。操作本身没有码点，只作为路径的一部分显示。
  - `group`：操作按钮的分组，缺省为“其他”。
  - `description`：可选说明，显示在悬停提示中。
  - `requiresSecondLetter`：为 `true` 时，使用该操作的规则必须提供第二个字母，界面会显示第二字母输入框。
- `rules`：路径规则，`marks` 与 `operations` 二选一。
  - `operations`：抽象变形路径。按“基础字母 + 操作集合（+ 可选第二个字母）”精确匹配，与操作选择顺序无关，区分大小写。例如 `M + 倒转 → Ɯ`、`i + j + 合字 → ĳ`。这类路径在 NFC 和 NFD 下都会生效。
  - `marks`：组合符号例外映射，写法与 `composition.rules` 相同，只在 NFC 下生效。
  - `second`：仅用于需要第二字母的变形路径；`marks` 形式不能指定。
  - `resultCodePoint`：必须是单个 Unicode 字母。
  - 同一基础字母、同一组操作/符号和同一第二个字母不能重复定义。找不到匹配规则时，界面提示“没有这条组合路径”，不会猜测结果。
- `otherLetters`：没有通用变形路径、需要直接选择的字符。
  - `codePoint` 必须是字母，`name` 用于显示和搜索。
  - `block` 可选，必须引用 `blocks` 中定义的 id；不填时归入“未分组”。
  - `aliases` 可选，用于补充搜索关键词（例如 `thorn`）。
  - 已有路径结果的字符不应放进 `otherLetters`，否则配置校验会报告重复。

如果 `letterComposition` 存在但内容有误（例如引用了不存在的操作、规则重复、结果不是字母），整份符号目录会被拒绝，原因显示在“诊断详情”中；程序只保留这一份符号目录，不会回退到其他文件。

## 全局快捷键设置

设置文件 `Data/settings.jsonc` 在第一次启动时自动创建，默认内容：

```jsonc
{
  "schemaVersion": 1,

  // 全局快捷键：至少一个修饰键 + 一个主键。
  // 支持 Ctrl / Alt / Shift / Win，以及字母、数字、Space、F1-F24（F12 除外）。
  // 修改后重启 Accent It 生效。
  "hotkey": "Ctrl+Alt+Space"
}
```

- `schemaVersion`：当前只支持 `1`。
- `hotkey`：至少包含一个修饰键（Ctrl、Alt、Shift、Win），加一个主键。
- 支持字母 A–Z、数字 0–9、Space、F1–F24。F12 保留给系统调试，配置时会被拒绝。
- 修饰键和主键不区分大小写，例如 `ctrl + shift + a` 会规范化为 `Ctrl+Shift+A`。
- 建议避开常用软件占用的组合，例如 QQ 的 `Ctrl+Alt+A`、微信的 `Alt+A`。
- 修改后重启程序生效；当前不监听设置文件变化。
- 如果组合已被其他程序占用，程序启动时注册失败，主界面会显示冲突提示；其他功能仍可使用，改好设置后重启即可。程序不会自动重新注册。
- 如果设置文件 JSON 格式错误、`schemaVersion` 不支持或快捷键无效，程序使用默认 `Ctrl+Alt+Space`，并在“诊断详情 → 设置问题”中说明原因，不会覆盖原文件。
- 主界面的“打开设置文件”按钮可以直接打开这个文件。
- Win 组合键能否注册取决于系统和已运行程序，可能被系统保留。
