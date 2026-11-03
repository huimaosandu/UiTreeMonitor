// Pattern Action 执行器：通过反射调用 A11yPattern 上带 [PatternMethodAttribute] 标记的方法
// 替代原版 BaseActionViewModel.InvokeMethod 对 ControlPatternAction.RunAction 的依赖，
// 直接在当前进程 STA 线程内执行 UIA Pattern 操作。
using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Axe.Windows.Core.Attributes;
using Axe.Windows.Core.Bases;

namespace UiTreeMonitor.Core.Uia;

/// <summary>单次 Action 执行结果。</summary>
public sealed class ActionResult
{
    public bool Success { get; init; }
    public object? ReturnValue { get; init; }
    public string? Error { get; init; }
    public string? DisplayText => Success
        ? (ReturnValue is not null ? $"Succeeded → {ReturnValue}" : "Succeeded")
        : $"Failed: {Error}";
}

/// <summary>
/// Pattern Action 执行器：对指定 A11yElement 的 A11yPattern 执行带 [PatternMethodAttribute] 标记的方法。
/// 必须在 STA 线程内调用（UIA COM 要求）。
/// </summary>
public static class PatternActionRunner
{
    /// <summary>
    /// 对指定元素执行 Pattern 方法。
    /// </summary>
    /// <param name="element">目标 A11yElement（必须有 UniqueId，且元素仍存活）</param>
    /// <param name="pattern">目标 A11yPattern（必须属于该 element）</param>
    /// <param name="methodName">方法名（如 "Invoke"、"Toggle"、"Expand" 等）</param>
    /// <param name="parameters">方法参数（与 MethodInfo 参数列表一一对应；null=无参方法）</param>
    /// <returns>执行结果（Success/ReturnValue/Error）</returns>
    public static ActionResult Run(A11yElement element, A11yPattern pattern, string methodName, object?[]? parameters = null)
    {
        AppLog.Write("ACTION", $"Run() called: element.UniqueId={element?.UniqueId ?? -1}, pattern={pattern?.Name}, method={methodName}");

        if (element is null) { var r1 = new ActionResult { Success = false, Error = "element is null" }; AppLog.Write("ACTION", $"Run() → {r1.DisplayText}"); return r1; }
        if (pattern is null) { var r2 = new ActionResult { Success = false, Error = "pattern is null" }; AppLog.Write("ACTION", $"Run() → {r2.DisplayText}"); return r2; }
        if (string.IsNullOrEmpty(methodName)) { var r3 = new ActionResult { Success = false, Error = "methodName is empty" }; AppLog.Write("ACTION", $"Run() → {r3.DisplayText}"); return r3; }

        try
        {
            // 查找匹配的方法（按 [PatternMethodAttribute] 标记过滤）
            var methods = GetActionMethods(pattern);
            AppLog.Write("ACTION", $"  Pattern '{pattern.Name}' has {methods.Length} action methods: [{string.Join(", ", methods.Select(m => m.Name))}]");

            var method = FindPatternMethod(pattern, methodName);
            if (method is null)
            {
                var r = new ActionResult { Success = false, Error = $"Method '{methodName}' not found on pattern '{pattern.Name}'" };
                AppLog.Write("ACTION", $"Run() → {r.DisplayText}");
                return r;
            }

            // 转换参数类型并调用
            var convertedParams = ConvertParameters(method, parameters);
            AppLog.Write("ACTION", $"  Invoking {method.Name}({(convertedParams is not null ? string.Join(", ", convertedParams.Select(p => p?.ToString() ?? "null")) : "")})");
            var ret = method.Invoke(pattern, convertedParams);

            var ok = new ActionResult { Success = true, ReturnValue = ret };
            AppLog.Write("ACTION", $"Run() → {ok.DisplayText}");
            return ok;
        }
#pragma warning disable CA1031 // Do not catch general exception types
        catch (TargetInvocationException tie)
        {
            // 反射包装异常：取 InnerException（实际的 UIA COM 异常）
            var inner = tie.InnerException;
            var msg = inner?.Message ?? tie.Message;
            var hResult = inner?.HResult ?? tie.HResult.GetHashCode();
            var err = $"{msg} (0x{hResult:X8})";
            AppLog.Write("ACTION", $"Run() → TargetInvocationException: {err}");
            return new ActionResult { Success = false, Error = err };
        }
        catch (Exception ex)
        {
            AppLog.Write("ACTION", $"Run() → Exception: {ex.GetType().Name}: {ex.Message}");
            return new ActionResult { Success = false, Error = ex.Message };
        }
#pragma warning restore CA1031
    }

    /// <summary>获取 Pattern 上所有可执行的 Action 方法（带 [PatternMethodAttribute] 标记）。</summary>
    public static MethodInfo[] GetActionMethods(A11yPattern pattern)
    {
        if (pattern is null) return Array.Empty<MethodInfo>();
        try
        {
            return pattern.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<PatternMethodAttribute>() != null)
                .ToArray();
        }
        catch
        {
            return Array.Empty<MethodInfo>();
        }
    }

    /// <summary>判断 Pattern 是否有可执行的 Actions。</summary>
    public static bool HasActions(A11yPattern pattern) => GetActionMethods(pattern).Length > 0;

    // ==== 内部方法 ====

    private static MethodInfo? FindPatternMethod(A11yPattern pattern, string methodName)
    {
        return GetActionMethods(pattern)
            .FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.Ordinal));
    }

    private static object?[]? ConvertParameters(MethodInfo method, object?[]? userParams)
    {
        var paramInfos = method.GetParameters();
        if (paramInfos.Length == 0) return null; // 无参方法
        if (userParams is null || userParams.Length == 0)
        {
            // 有参数但用户未提供：用默认值
            return paramInfos.Select(p => p.HasDefaultValue ? p.DefaultValue : GetDefaultForType(p.ParameterType)).ToArray();
        }

        var result = new object?[Math.Min(paramInfos.Length, userParams.Length)];
        for (int i = 0; i < result.Length; i++)
        {
            var val = userParams[i];
            if (val is null) result[i] = paramInfos[i].HasDefaultValue ? paramInfos[i].DefaultValue : null;
            else
            {
                try { result[i] = Convert.ChangeType(val, paramInfos[i].ParameterType, CultureInfo.InvariantCulture); }
                catch { result[i] = val; } // 类型不匹配时原样传入
            }
        }
        return result;
    }

    private static object? GetDefaultForType(Type t)
    {
        if (t == typeof(int)) return 0;
        if (t == typeof(double)) return 0.0;
        if (t == typeof(string)) return string.Empty;
        if (t == typeof(bool)) return false;
        if (t.IsEnum) return Enum.ToObject(t, 0);
        return null;
    }
}
