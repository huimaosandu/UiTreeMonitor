namespace UiTreeMonitor.Core.Models;

/// <summary>单条录制的 Action 操作记录。</summary>
public sealed record ActionRecord
{
    public DateTime Timestamp { get; init; }
    public string ElementPath { get; init; } = "";        // 目标元素结构路径（回放时重定位用）
    public string ElementName { get; init; } = "";         // 目标元素 Name（日志/显示用）
    public string PatternName { get; init; } = "";          // Pattern 名称
    public string MethodName { get; init; } = "";           // 方法名（如 "Invoke", "Toggle"）
    public object?[]? Parameters { get; init; }             // 方法参数（JSON 序列化）
    public bool Success { get; init; }                      // 执行是否成功
    public string? Result { get; init; }                    // 返回值/错误信息（显示用）

    /// <summary>简短描述（用于列表显示）</summary>
    public string DisplayText => Success
        ? $"{MethodName}() ✓"
        : $"{MethodName}() ✗ {Result}";
}
