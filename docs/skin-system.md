# 皮肤系统骨架

## 分层

皮肤系统保持四层职责，业务状态机不依赖具体角色素材：

1. `CompanionVisualState` 定义 12 个业务状态。
2. `SkinContract` 定义状态清单、画布尺寸、安全边距和主题令牌范围。
3. `SkinManifest` / `CompanionVisualProfile` 把业务状态映射到皮肤素材。
4. `CompanionFace` 只按当前状态渲染映射后的独立图片。

`ThemeMode` 与 `SkinId` 是两个正交维度：`SkinId` 决定角色素材、品牌色和皮肤专属材质，`ThemeMode` 决定共享界面的明暗层。选择角色后仍必须允许切换“跟随 Windows / 浅色 / 深色”，不得通过禁用控件或让皮肤 ID 短路主题解析来隐藏该能力。

## 内置角色皮肤目录

每套角色皮肤必须使用相同结构：

```text
Resources/Skins/<SkinId>/
├── States/
│   ├── Idle.png
│   ├── Sleeping.png
│   ├── Happy.png
│   ├── Curious.png
│   ├── Thinking.png
│   ├── Listening.png
│   ├── Working.png
│   ├── Surprised.png
│   ├── Warning.png
│   ├── Error.png
│   ├── Dragging.png
│   └── Expanding.png
├── preview.png
├── portrait.png
├── companion.png
├── anchor.json
└── app-icon-source.png
```

`companion-sprite-sheet.png` 和 `companion-idle-variants.png` 可以作为旧版本兼容或素材审阅文件保留，但内置皮肤运行时不得引用它们。

## 状态图片契约

- 每个状态必须是单独的 PNG 文件，不允许使用界面截图或在运行时从大图裁切。
- 所有状态固定为 `181×181` 透明画布。
- 角色主体四周至少保留 `18px` 透明安全区。
- 同一套皮肤的所有状态必须归一化到同一目标主体包围盒；当前规范化工具使用 `145×145` 目标包围盒，并输出每个状态的 `anchor.json`，避免姿态切换时角色忽大忽小。
- 12 个状态必须完整，且每个状态文件都必须是独立内容。

`tools/NormalizeSkinCompanionAssets.csproj` 负责把原始状态图或兼容精灵表规范化到上述目录。生成过程按状态计算统一目标包围盒，同时生成预览、兼容资源和 `anchor.json`。如果源图缺失，管线不得用猜测的硬编码裁剪框改写已有产物。

## 新增或更新皮肤

1. 把 12 张原始状态图放入皮肤根目录，或提供一张符合旧版 4×3 布局的来源精灵表。
2. 运行规范化工具：

   ```powershell
   dotnet run --project tools/NormalizeSkinCompanionAssets.csproj -- Resources/Skins/<SkinId> Resources/Skins/<SkinId>
   ```

3. 在 `SkinService.RegisterBuiltInSkins` 中调用统一的 `RegisterBuiltInImageSkin` 注册皮肤。
4. 在 `Resources/Themes/<SkinId>.xaml` 中只做必要微调：
   - `SkinCompanionOverscan`：整体缩放，规范素材默认 `1`。
   - `SkinCompanionOffsetY`：垂直定位，规范素材默认 `0`。
5. 运行皮肤契约测试；缺图、尺寸不一致、透明安全区不足或状态映射不完整都会失败。

外部皮肤包使用相同的 12 状态映射和图片契约。安装器会在写入目录前校验每一张状态图，损坏或不规范的包不会进入皮肤目录。
