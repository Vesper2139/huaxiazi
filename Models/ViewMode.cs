namespace PromptFloat.Models;

/// <summary>
/// 主文本框的视图模式：
/// <list type="bullet">
///   <item><description><see cref="Original"/>：显示/编辑用户原始输入；</description></item>
///   <item><description><see cref="Optimized"/>：显示优化结果；差异标记由独立状态控制。</description></item>
/// </list>
/// 用户输入与优化输出共享同一个 TextBox，通过此枚举切换内容，方便对照。
/// </summary>
public enum ViewMode
{
    /// <summary>原始输入视图（可编辑）。</summary>
    Original,

    /// <summary>优化结果视图（只读）。</summary>
    Optimized,

}
