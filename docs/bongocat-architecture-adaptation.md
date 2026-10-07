# BongoCat 架构研究与项目适配

参考项目：[ayangweb/BongoCat](https://github.com/ayangweb/BongoCat)。本项目只借鉴可迁移的架构边界，不复制其 Live2D、Pixi.js 或 Tauri 技术实现。

## BongoCat 的可迁移原则

### 1. 模型资源与运行状态分离

BongoCat 的模型仓库只保存模型身份、路径、模式和是否为预设；按键、鼠标和表情通过运行时参数驱动。模型目录自行包含背景、模型和覆盖层素材，切换模型不要求业务输入层理解素材细节。

本项目对应为：

```text
CompanionVisualState
        ↓
SkinManifest / CompanionVisualProfile
        ↓
Resources/Skins/<SkinId>/States/<State>.png
```

业务层只产生 `CompanionVisualState`，皮肤清单负责状态到独立 PNG 的映射，`CompanionFace` 不包含某套皮肤的目录分支。

### 2. 单一渲染表面

BongoCat 主页面让根容器、Live2D canvas、背景和按键覆盖层全部占满同一个窗口画布。模型尺寸或缩放变化时，原生窗口直接同步为缩放后的模型尺寸，不在透明窗口外围增加另一层不可见留白。

本项目对应为：

```text
AppSettings.FloatingBallSize
        ↓
CompanionDisplayMetrics.ResolveLayout
        ├── WindowSize
        ├── HitTargetSize
        └── ViewportSize
```

三个尺寸当前保持严格相等。禁止窗口、命中区或 `Viewbox` 各自追加边距；皮肤内部留白只能存在于规范化的透明状态图片中。

### 3. 固有画布与用户缩放分离

BongoCat 先读取模型固有宽高，再用用户比例计算窗口物理尺寸；模型渲染仍以自己的固有画布为基准。这样不会在每次缩放时重新修改模型资源或叠加多套比例。

本项目的固有素材画布固定为 `181×181`，WPF 的 `Viewbox` 统一投影到用户选择的 `28–72 DIP`。素材文件不因设置值而重采样，窗口与视口同步变化。

### 4. 交互驱动姿态，不修改根画布

BongoCat 的键鼠输入驱动 Live2D 参数或同画布覆盖层。交互不会临时改变原生窗口与模型画布的对应关系。

本项目因此规定：拖动可切换 `Dragging.png`、改变小幅位移和旋转，但不得放大 `BodyScale`。这避免拖动瞬间角色变大、越过视口并被透明窗口裁切。

## 皮肤更新边界

- 新皮肤补齐统一的 12 状态目录和主题令牌。
- 图片尺寸、透明安全区和状态完整性由 `SkinContract` / `SkinPackageService` 校验。
- 普通差异通过状态素材表达。
- 仅视觉定位差异使用 `SkinCompanionOverscan` 和 `SkinCompanionOffsetY`。
- 不允许通过修改窗口尺寸、命中区域或业务状态机适配单套皮肤。

更完整的目录与素材规范见 [skin-system.md](skin-system.md)。
