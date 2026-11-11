namespace UXLabelshop.Rendering;

/// <summary>
/// 公式引擎的外部能力宿主：目前只有「查库取单值」一项。
/// 公式引擎本身不依赖 EF / MySqlConnector，业务层实现本接口注入即可，
/// 自检时可以塞一个假宿主，做到脱库测试。
/// </summary>
public interface IFormulaHost
{
    /// <summary>
    /// 执行一条**已参数化**的只读查询，返回首行首列；无行返回 null。
    /// sql 中的参数占位符形如 @p0 / @p1，<see cref="args"/> 按序对应。
    /// </summary>
    string? Scalar(string sql, IReadOnlyList<string?> args);
}
