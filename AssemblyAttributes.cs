using System.Runtime.CompilerServices;

// 允许独立的单元测试项目（Huaxiazi.Tests）访问 internal 成员（如 AIService.ParseContent），
// 便于直接编写针对解析逻辑与配置读写等的单元测试，无需反射。
[assembly: InternalsVisibleTo("Huaxiazi.Tests")]
