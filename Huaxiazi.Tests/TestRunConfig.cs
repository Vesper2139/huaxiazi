using Xunit;

// 关闭 xUnit 并行执行。
//
// 原因：本测试套件中 ConfigServiceTests / SettingsViewModelTests / MainViewModelTests
// 等通过 TestHelpers.RedirectConfigTo 用反射把 ConfigService 的 static 字段
// AppDataFolder / ConfigFilePath 重定向到各自的临时目录，属于跨测试的“全局可变状态”。
// xUnit 默认按测试类并行执行，不同类对同一个 static 配置路径的重定向会互相覆盖，
// 偶发“配置文件未写回 / 字段未持久化”的伪失败（典型如
// ConfigServiceTests.Load_WhenFileAbsent_CreatesDefaultConfig 在整跑时失败、单独跑却通过）。
//
// 串行执行可彻底消除该竞态；当前测试量级下串行性能影响可忽略。
// 若将来把配置路径改为实例级注入（而非 static），可重新开启并行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
