// 树节点视图模型
using System.Collections.Generic;
using System.Collections.ObjectModel;
using UiTreeMonitor.Core.Models;

namespace UiTreeMonitor.App;

/// <summary>结构树节点 VM（用于 TreeView 显示 + 高亮）。</summary>
public sealed class TreeNodeViewModel
{
    public int Id { get; init; }
    public string Display => $"[{ControlType}] {Name ?? AutomationId ?? ClassName ?? "(无名称)"}";
    public string? Name { get; init; }
    public string ControlType { get; init; } = "";
    public string? AutomationId { get; init; }
    public string? ClassName { get; init; }
    public int ProcessId { get; init; }
    public bool IsHighlighted { get; set; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    public static TreeNodeViewModel FromNode(ElementNode node)
        => new()
        {
            Id = node.Id,
            Name = node.Name,
            ControlType = node.ControlType,
            AutomationId = node.AutomationId,
            ClassName = node.ClassName,
            ProcessId = node.ProcessId,
        };
}
