# Settings / Ability Management

These rules extend `design-system/huaxiazi/MASTER.md` for the settings window.

## Information architecture

Use a stable `200px` left navigation and a single page-level scroll owner. Keep the
content column at a maximum width of `780px`. Navigation follows user tasks in this
order: “表达与生成”, “模型连接”, “外观与窗口”, “快捷键”, “历史与留存”,
“数据维护”, and “关于与更新”. “表达与生成” is the default destination.

The Skill master-detail workspace is entered from the “表达与生成” overview via
its “管理能力” action, so a separate “专业 Skill” destination is not used. It has a fixed
`440px` working height,
and its list and long detail content scroll inside their own columns so one verbose
Skill cannot stretch the entire settings page.

## Skill manager states

The right pane has exactly one visible state:

1. empty state — no skill selected, one short explanation and the import action;
2. details state — selected skill metadata and actions;
3. editor state — a custom copy being edited.

Details are a two-column metadata grid (label/value), with ellipsis for long hashes
and source names. The installed list uses a compact row: name, purpose, and enabled
status. Editing a read-only preset creates a custom copy; the preset itself remains
unchanged.

## Layout targets

- Group headings sit outside their card; related settings share one card and use
  internal dividers. Supporting copy is at least `12.5px` and wraps.
- “历史与留存” owns retention rules, search, browsing, preview, and record-level
  actions. “数据维护” owns backups, imports, migration, cache cleanup, and the
  isolated danger zone.
- Destructive operations use a red bordered zone with irreversible wording and a
  confirmation flow. “恢复默认” actions sit at the bottom of the relevant group.
- Content frame has 28px left inset and 18px right inset from the shell.
- List/detail columns use a 12px gap and never collide with the scrollbar.
- The Skill workspace stays 440px tall; list, details and editor content are clipped
  to that frame and use local vertical scrolling when needed.
- List and editor scrollbars use the compact overlay treatment; never expose the
  bright native Windows scrollbar inside the dark Skill workspace.
- Secondary metadata stays below the action row; it must not compete with the title.
- Empty and details states never occupy the same visual tree.
# 设置页状态提交契约

## 单一出口

设置页不提供独立的“保存”“完成”或“取消”按钮。用户通过右上角关闭按钮离开时，容器执行唯一的 `CommitAndCloseAsync` 流程；子页面只编辑内存草稿或返回上一级列表，不自行落盘。

## 提交顺序

1. 所有普通字段先写入内存草稿。
2. 连接相关字段发生变化时，关闭设置会自动执行一次最小连接验证。
3. 验证成功且本地校验通过，原子写入配置与 DPAPI 密钥，并立即启用当前配置。
4. 验证失败时，错误必须显示在设置页顶部；只有用户明确确认“保存为草稿”后才允许保存未验证配置。未确认时保留草稿，设置窗口不关闭。
5. 任一写入失败都回滚本轮密钥、配置和快捷键变更，不产生半保存状态。

## 交互约束

- 页面内不得出现与容器冲突的“保存更改”按钮。
- 连接状态、校验错误和草稿状态使用内联反馈，不用模态窗口遮挡主内容；仅在“保存为草稿”和破坏性操作时请求确认。
- 返回配置列表只改变视图，不代表保存或放弃；最终提交由设置页右上角关闭动作统一负责。
- 普通用户不接触协议、映射和采样参数；高级参数保留兼容性，但必须提供中文说明和可见范围。
