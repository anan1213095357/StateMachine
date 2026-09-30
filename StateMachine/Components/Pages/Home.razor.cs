using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using StateMachine.Scripting;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
namespace StateMachine.Components.Pages;

public partial class Home
{
    private string ConfigDirectory =>
    Path.Combine(HostEnvironment.ContentRootPath, "StateMachineConfigs");
    private static readonly string[] Operators = { "==", "!=", ">", ">=", "<", "<=", "contains", "startsWith", "endsWith" };
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    [Inject]
    private IWebHostEnvironment HostEnvironment { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;

    [Inject]
    private IConfiguration Configuration { get; set; } = default!;

    private MachineModel Machine { get; set; } = new();
    private RuntimeModel Runtime { get; } = new();
    private List<LogEntry> Logs { get; } = new();
    private StateScriptContext _scriptContext = null!;
    private StateScriptHost? _scriptHost;
    private readonly Dictionary<string, ReflectedActionDescriptor> _actionMethods = new(StringComparer.Ordinal);
    private IEnumerable<ReflectedActionDescriptor> ActionMethods => _actionMethods.Values
        .OrderBy(x => x.Group, StringComparer.Ordinal)
        .ThenBy(x => x.DisplayName, StringComparer.Ordinal);
    private DotNetObjectReference<JsBridge>? _dotNetRef;
    private JsBridge? _jsBridge;
    private bool _jsReady;
    private bool _scrollLog;
    private int _runToken;
    private string _configKey = "default";
    private List<string> _configKeys = new();

    private static readonly HttpClient AiHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private bool _aiDialogOpen;
    private bool _aiBusy;

    private string _aiInput = string.Empty;
    private string _aiError = string.Empty;

    private AiMachinePlan? _pendingAiPlan;

    private readonly List<AiChatItem> _aiMessages = new();

    private string? SelectedRegionId { get; set; }
    private string? SelectedStateId { get; set; }
    private string? SelectedEdgeId { get; set; }
    private PendingConnectionModel? PendingConnection { get; set; }

    private StateModel? SelectedState => string.IsNullOrWhiteSpace(SelectedStateId) ? null : GetState(SelectedStateId!);

    protected override void OnInitialized()
    {
        CreateDemo();
        ResetRuntimeCore(logInitialStates: true);
        InitializeScriptHost();
        DiscoverActionMethods();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _jsBridge = new JsBridge(StateMoved, SelectStateFromJs, SelectEdgeFromJs, ClearSelectionFromJs, EscapeFromJs, DeleteSelectionFromJs);
            _dotNetRef = DotNetObjectReference.Create(_jsBridge);
            await JS.InvokeVoidAsync("industrialStateMachineUi.init", _dotNetRef);
            _jsReady = true;
            await RefreshConfigListAsync();
            await JS.InvokeVoidAsync("industrialStateMachineUi.setZoom", 0.85, false);
            await JS.InvokeVoidAsync("industrialStateMachineUi.setInitialScroll", 40, 50);
            StateHasChanged();
            return;
        }

        if (_jsReady)
        {
            await SyncJsAsync();
            if (_scrollLog)
            {
                _scrollLog = false;
                await JS.InvokeVoidAsync("industrialStateMachineUi.scrollConsoleToBottom");
            }
        }
    }

    private async Task SyncJsAsync()
    {
        await JS.InvokeVoidAsync("industrialStateMachineUi.sync", new
        {
            edges = Machine.Edges,
            selectedEdgeId = SelectedEdgeId,
            runningEdgeIds = Runtime.CurrentEdgeIds.ToArray(),
            pending = PendingConnection
        });
    }

    // =========================================================
    // REFLECTION ACTION SYSTEM
    // =========================================================
    private void InitializeScriptHost()
    {
        _scriptContext = new StateScriptContext(
            Services,
            name => Runtime.Variables.TryGetValue(name, out var value)
                ? CloneNode(value)
                : null,
            SetRuntimeVariable,
            AddLog);

        var scriptDirectory = Path.Combine(
            HostEnvironment.ContentRootPath,
            "HotScripts");

        _scriptHost = new StateScriptHost(
            _scriptContext,
            scriptDirectory);

        _scriptHost.Reloaded += OnScriptsReloaded;

        var result = _scriptHost.Reload();

        if (result.Success)
        {
            AddLog(
                "SCRIPT",
                $"热更加载成功：{result.ScriptCount} 个 csx，{result.ActionClassCount} 个动作类，目录：{scriptDirectory}",
                "good");
        }
        else
        {
            AddLog(
                "SCRIPT",
                string.Join(" | ", result.Errors),
                "error");
        }
    }

    private void OnScriptsReloaded(ScriptReloadResult result)
    {
        _ = InvokeAsync(() =>
        {
            if (result.Success)
            {
                DiscoverActionMethods();
                AddLog("SCRIPT", $"热更完成：{result.ScriptCount} 个脚本，{result.ActionClassCount} 个动作类", "good");
            }
            else
            {
                AddLog("SCRIPT", string.Join(" | ", result.Errors), "error");
            }

            StateHasChanged();
        });
    }

    private void DiscoverActionMethods()
    {
        _actionMethods.Clear();

        RegisterActionTarget(this, isHotScript: false);

        if (_scriptHost is not null)
        {
            foreach (var target in _scriptHost.ActionTargets)
                RegisterActionTarget(target, isHotScript: true);
        }

        foreach (var state in Machine.States)
        {
            state.BeforeLeaveAction ??= new StateActionCallModel();
            state.LoopAction ??= new StateActionCallModel();
            state.AfterEnterAction ??= new StateActionCallModel();

            NormalizeActionArguments(state.BeforeLeaveAction);
            NormalizeActionArguments(state.LoopAction);
            NormalizeActionArguments(state.AfterEnterAction);
        }
    }

    private void RegisterActionTarget(object target, bool isHotScript)
    {
        var methods = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttribute<StateActionAttribute>()
            })
            .Where(x => x.Attribute is not null)
            .ToArray();

        foreach (var item in methods)
        {
            var method = item.Method;
            var attribute = item.Attribute!;

            if (method.ContainsGenericParameters)
                throw new InvalidOperationException($"状态动作不能是泛型方法：{method.Name}");

            if (method.GetParameters().Any(x => x.ParameterType.IsByRef || x.IsOut))
                throw new InvalidOperationException($"状态动作不能包含 ref/out 参数：{method.Name}");

            if (!IsSupportedActionReturnType(method.ReturnType))
                throw new InvalidOperationException($"状态动作返回类型只支持 void、Task、Task<T>、ValueTask、ValueTask<T>：{method.Name}");

            var key = isHotScript
                ? $"{target.GetType().FullName}.{method.Name}"
                : method.Name;

            if (_actionMethods.ContainsKey(key))
                throw new InvalidOperationException($"检测到重复状态动作方法：{key}。带 [StateAction] 的方法不要重载。");

            var parameters = method.GetParameters()
                .Select(CreateActionParameterDescriptor)
                .ToList();

            _actionMethods[key] = new ReflectedActionDescriptor
            {
                Key = key,
                DisplayName = string.IsNullOrWhiteSpace(attribute.DisplayName) ? method.Name : attribute.DisplayName,
                Group = string.IsNullOrWhiteSpace(attribute.Group) ? "二次开发" : attribute.Group,
                Method = method,
                Target = target,
                IsHotScript = isHotScript,
                Parameters = parameters
            };
        }
    }

    private static bool IsSupportedActionReturnType(Type type)
    {
        if (type == typeof(void) || type == typeof(Task) || type == typeof(ValueTask)) return true;
        if (typeof(Task).IsAssignableFrom(type)) return true;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>)) return true;
        return false;
    }

    private static ActionParameterDescriptor CreateActionParameterDescriptor(ParameterInfo parameter)
    {
        var parameterAttribute = parameter.GetCustomAttribute<StateParameterAttribute>();
        var nullableType = Nullable.GetUnderlyingType(parameter.ParameterType);
        var effectiveType = nullableType ?? parameter.ParameterType;

        return new ActionParameterDescriptor
        {
            Name = parameter.Name ?? $"arg{parameter.Position}",
            DisplayName = parameterAttribute?.DisplayName ?? parameter.Name ?? $"参数 {parameter.Position + 1}",
            ParameterType = parameter.ParameterType,
            EffectiveType = effectiveType,
            IsNullable = nullableType is not null || !parameter.ParameterType.IsValueType,
            EditorKind = GetEditorKind(effectiveType),
            EnumValues = effectiveType.IsEnum ? Enum.GetNames(effectiveType) : Array.Empty<string>(),
            DefaultLiteral = GetDefaultLiteral(parameter, effectiveType)
        };
    }

    private static string GetEditorKind(Type type)
    {
        if (type == typeof(bool)) return "boolean";
        if (type.IsEnum) return "enum";
        if (IsNumericType(type)) return "number";
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return "datetime";
        if (type == typeof(DateOnly)) return "date";
        if (type == typeof(TimeOnly)) return "time";
        if (type == typeof(Guid)) return "guid";
        if (type == typeof(string) || type == typeof(char)) return "string";
        return "json";
    }

    private static bool IsNumericType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(byte) || type == typeof(sbyte) ||
               type == typeof(short) || type == typeof(ushort) ||
               type == typeof(int) || type == typeof(uint) ||
               type == typeof(long) || type == typeof(ulong) ||
               type == typeof(float) || type == typeof(double) ||
               type == typeof(decimal);
    }

    private static string GetDefaultLiteral(ParameterInfo parameter, Type effectiveType)
    {
        if (parameter.HasDefaultValue && parameter.DefaultValue is not null && parameter.DefaultValue != DBNull.Value)
        {
            if (effectiveType == typeof(bool))
                return (bool)parameter.DefaultValue ? "true" : "false";
            if (effectiveType.IsEnum)
                return parameter.DefaultValue.ToString() ?? string.Empty;
            if (effectiveType == typeof(DateTime))
                return ((DateTime)parameter.DefaultValue).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
            return Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        if (effectiveType == typeof(bool)) return "false";
        if (effectiveType.IsEnum) return Enum.GetNames(effectiveType).FirstOrDefault() ?? string.Empty;
        if (IsNumericType(effectiveType)) return "0";
        if (effectiveType == typeof(DateTime) || effectiveType == typeof(DateTimeOffset))
            return DateTime.Now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
        if (effectiveType == typeof(DateOnly)) return DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (effectiveType == typeof(TimeOnly)) return TimeOnly.FromDateTime(DateTime.Now).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (effectiveType == typeof(Guid)) return Guid.Empty.ToString();
        if (effectiveType == typeof(JsonNode) || effectiveType == typeof(JsonElement)) return "null";
        return string.Empty;
    }

    private ReflectedActionDescriptor? GetActionDescriptor(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return _actionMethods.TryGetValue(key, out var descriptor) ? descriptor : null;
    }

    private static IEnumerable<(string Key, string Title, StateActionCallModel Action)> GetLifecycleActions(StateModel state)
    {
        yield return ("enter", "进入状态", state.BeforeLeaveAction);
        yield return ("loop", "循环", state.LoopAction);
        yield return ("leave", "离开状态", state.AfterEnterAction);
    }

    private StateActionCallModel GetStateAction(StateModel state, string lifecycle)
    {
        return lifecycle switch
        {
            "enter" => state.BeforeLeaveAction,
            "loop" => state.LoopAction,
            "leave" => state.AfterEnterAction,
            _ => throw new InvalidOperationException($"未知状态生命周期：{lifecycle}")
        };
    }

    private StateActionCallModel? GetStateAction(string stateId, string lifecycle)
    {
        var state = GetState(stateId);
        return state is null ? null : GetStateAction(state, lifecycle);
    }

    private ActionArgumentModel? GetActionArgument(StateActionCallModel action, string parameterName) =>
        action.ActionArguments.FirstOrDefault(x => x.ParameterName == parameterName);

    private void NormalizeActionArguments(StateActionCallModel action, ReflectedActionDescriptor? descriptor = null)
    {
        action.ActionMethod ??= string.Empty;
        action.ActionArguments ??= new List<ActionArgumentModel>();

        descriptor ??= GetActionDescriptor(action.ActionMethod);
        if (descriptor is null)
        {
            action.ActionArguments.Clear();
            return;
        }

        var old = action.ActionArguments
            .GroupBy(x => x.ParameterName, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);

        var normalized = new List<ActionArgumentModel>();
        foreach (var parameter in descriptor.Parameters)
        {
            if (!old.TryGetValue(parameter.Name, out var argument))
            {
                argument = new ActionArgumentModel
                {
                    ParameterName = parameter.Name,
                    Source = "literal",
                    LiteralValue = parameter.DefaultLiteral
                };
            }

            argument.ParameterName = parameter.Name;

            if (argument.Source != "literal" && argument.Source != "variable")
                argument.Source = "literal";

            if (argument.Source == "variable" &&
                !GetCompatibleVariables(action, parameter)
                    .Any(x => x.Name == argument.VariableName))
            {
                argument.VariableName =
                    GetCompatibleVariables(action, parameter)
                        .FirstOrDefault()?.Name ?? string.Empty;
            }

            normalized.Add(argument);
        }

        action.ActionArguments = normalized;
    }

    private void ChangeStateActionMethod(string stateId, string lifecycle, string value)
    {
        var action = GetStateAction(stateId, lifecycle);
        if (action is null) return;

        action.ActionMethod = value;
        action.ActionArguments.Clear();
        NormalizeActionArguments(action);
    }

    private void ChangeStateActionArgumentSource(
        string stateId,
        string lifecycle,
        string parameterName,
        string source)
    {
        var action = GetStateAction(stateId, lifecycle);
        if (action is null) return;

        var argument = GetActionArgument(action, parameterName);
        var descriptor = GetActionDescriptor(action.ActionMethod);
        var parameter = descriptor?.Parameters.FirstOrDefault(x => x.Name == parameterName);
        if (argument is null || parameter is null) return;

        argument.Source = source == "variable" ? "variable" : "literal";
        if (argument.Source == "variable")
            argument.VariableName = GetCompatibleVariables(action, parameter).FirstOrDefault()?.Name ?? string.Empty;
    }

    private void ChangeStateActionArgumentVariable(
        string stateId,
        string lifecycle,
        string parameterName,
        string variableName)
    {
        var action = GetStateAction(stateId, lifecycle);
        var argument = action is null ? null : GetActionArgument(action, parameterName);
        if (argument is not null) argument.VariableName = variableName;
    }

    private void ChangeStateActionArgumentLiteral(
        string stateId,
        string lifecycle,
        string parameterName,
        string value)
    {
        var action = GetStateAction(stateId, lifecycle);
        var argument = action is null ? null : GetActionArgument(action, parameterName);
        if (argument is not null) argument.LiteralValue = value;
    }

    private IEnumerable<VariableModel> GetCompatibleVariables(
    StateActionCallModel action,
    ActionParameterDescriptor parameter)
    {
        var requiredType = parameter.EditorKind switch
        {
            "boolean" => "boolean",
            "number" => "number",
            "json" => "json",
            _ => "string"
        };

        return Machine.Variables.Where(x => x.Type == requiredType);
    }

    private bool CanBindGlobalVariable(
    StateActionCallModel action,
    ActionParameterDescriptor parameter) =>
    GetCompatibleVariables(action, parameter).Any();

    private string GetParameterEditorKind(
    StateActionCallModel action,
    ActionParameterDescriptor parameter)
    {
        return parameter.EditorKind;
    }

    private string GetParameterTypeLabel(
    StateActionCallModel action,
    ActionParameterDescriptor parameter)
    {
        return parameter.EffectiveType.Name;
    }

    private async Task InvokeActionAsync(StateActionCallModel action)
    {
        if (string.IsNullOrWhiteSpace(action.ActionMethod)) return;

        if (!_actionMethods.TryGetValue(action.ActionMethod, out var descriptor))
            throw new InvalidOperationException($"动作方法不存在：{action.ActionMethod}");

        NormalizeActionArguments(action, descriptor);

        var values = new object?[descriptor.Parameters.Count];
        for (var i = 0; i < descriptor.Parameters.Count; i++)
        {
            var parameter = descriptor.Parameters[i];
            var argument = GetActionArgument(action, parameter.Name)
                           ?? throw new InvalidOperationException($"动作参数不存在：{parameter.DisplayName}");
            values[i] = ResolveActionArgument(action, parameter, argument);
        }

        var callText = string.Join(", ", descriptor.Parameters.Select((p, i) => $"{p.Name}={FormatActionLogValue(values[i])}"));
        AddLog("CALL", $"{descriptor.DisplayName}({callText})", "info");

        object? result;
        try
        {
            result = descriptor.Method.Invoke(descriptor.Target, values);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException(ex.InnerException.Message, ex.InnerException);
        }

        if (result is Task task)
        {
            await task;
            return;
        }

        if (result is ValueTask valueTask)
        {
            await valueTask;
            return;
        }

        if (result is not null)
        {
            var resultType = result.GetType();
            if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            {
                var asTask = resultType
                    .GetMethod("AsTask", BindingFlags.Instance | BindingFlags.Public)
                    ?.Invoke(result, null) as Task;

                if (asTask is not null)
                    await asTask;
            }
        }
    }

    private object? ResolveActionArgument(
        StateActionCallModel action,
        ActionParameterDescriptor parameter,
        ActionArgumentModel argument)
    {
        if (argument.Source == "variable")
        {
            if (!Runtime.Variables.TryGetValue(argument.VariableName, out var runtimeValue))
                throw new InvalidOperationException($"全局变量不存在：{argument.VariableName}");

            return ConvertRuntimeNodeToType(runtimeValue, parameter.ParameterType);
        }

        return ConvertLiteralToType(argument.LiteralValue, parameter.ParameterType);
    }
    private object? ConvertRuntimeNodeToType(JsonNode? node, Type targetType)
    {
        var nullableType = Nullable.GetUnderlyingType(targetType);
        var effectiveType = nullableType ?? targetType;

        if (node is null)
        {
            if (nullableType is not null || !targetType.IsValueType) return null;
            return Activator.CreateInstance(effectiveType);
        }

        if (effectiveType == typeof(JsonNode)) return CloneNode(node);
        if (effectiveType == typeof(JsonElement))
        {
            using var document = JsonDocument.Parse(node.ToJsonString());
            return document.RootElement.Clone();
        }
        if (effectiveType == typeof(object)) return Primitive(node);
        if (effectiveType == typeof(string)) return NodeString(node);
        if (effectiveType == typeof(char)) return NodeString(node).FirstOrDefault();
        if (effectiveType == typeof(bool)) return NodeBool(node);
        if (effectiveType.IsEnum) return Enum.Parse(effectiveType, NodeString(node), ignoreCase: true);
        if (effectiveType == typeof(Guid)) return Guid.Parse(NodeString(node));
        if (effectiveType == typeof(DateTime)) return DateTime.Parse(NodeString(node), CultureInfo.InvariantCulture);
        if (effectiveType == typeof(DateTimeOffset)) return DateTimeOffset.Parse(NodeString(node), CultureInfo.InvariantCulture);
        if (effectiveType == typeof(DateOnly)) return DateOnly.Parse(NodeString(node), CultureInfo.InvariantCulture);
        if (effectiveType == typeof(TimeOnly)) return TimeOnly.Parse(NodeString(node), CultureInfo.InvariantCulture);
        if (IsNumericType(effectiveType))
        {
            if (!TryNumber(Primitive(node), out var number))
                throw new InvalidOperationException($"无法把变量值转换成 {effectiveType.Name}");
            return Convert.ChangeType(number, effectiveType, CultureInfo.InvariantCulture);
        }

        return JsonSerializer.Deserialize(node.ToJsonString(), effectiveType, _jsonOptions);
    }

    private object? ConvertLiteralToType(string? text, Type targetType)
    {
        text ??= string.Empty;
        var nullableType = Nullable.GetUnderlyingType(targetType);
        var effectiveType = nullableType ?? targetType;

        if (nullableType is not null && string.IsNullOrWhiteSpace(text)) return null;
        if (effectiveType == typeof(string)) return text;
        if (effectiveType == typeof(char)) return text.FirstOrDefault();
        if (effectiveType == typeof(bool)) return text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (effectiveType.IsEnum) return Enum.Parse(effectiveType, text, ignoreCase: true);
        if (effectiveType == typeof(Guid)) return Guid.Parse(text);
        if (effectiveType == typeof(DateTime)) return DateTime.Parse(text, CultureInfo.InvariantCulture);
        if (effectiveType == typeof(DateTimeOffset)) return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
        if (effectiveType == typeof(DateOnly)) return DateOnly.Parse(text, CultureInfo.InvariantCulture);
        if (effectiveType == typeof(TimeOnly)) return TimeOnly.Parse(text, CultureInfo.InvariantCulture);
        if (effectiveType == typeof(JsonNode)) return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        if (effectiveType == typeof(JsonElement))
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "null" : text);
            return document.RootElement.Clone();
        }
        if (effectiveType == typeof(object))
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        if (IsNumericType(effectiveType))
            return Convert.ChangeType(text, effectiveType, CultureInfo.InvariantCulture);

        return JsonSerializer.Deserialize(text, effectiveType, _jsonOptions);
    }

    private static string FormatActionLogValue(object? value)
    {
        if (value is null) return "null";
        if (value is string s) return $"\"{s}\"";
        if (value is JsonNode node) return node.ToJsonString();
        if (value is JsonElement element) return element.GetRawText();
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    // =========================================================
    // SYSTEM BUILT-IN ACTIONS
    // =========================================================

    [StateAction("写日志", "系统内置")]
    private void SystemLog(
        [StateParameter("内容")] string message)
    {
        AddLog("ACTION", message, "good");
    }

    [StateAction("设置变量", "系统内置")]
    private void SystemSetVariable(
        [StateParameter("目标变量")] string variableName,
        [StateParameter("值")] JsonNode? value)
    {
        _scriptContext.Vars.Set(variableName, value);
    }

    [StateAction("布尔变量取反", "系统内置")]
    private void SystemToggleVariable(
        [StateParameter("布尔变量")] string variableName)
    {
        var current = _scriptContext.Vars.Get<bool>(variableName, false);
        _scriptContext.Vars.Set(variableName, !current);
    }

    [StateAction("数值变量增加", "系统内置")]
    private void SystemAddNumber(
        [StateParameter("数值变量")] string variableName,
        [StateParameter("增加值")] double value)
    {
        var current = _scriptContext.Vars.Get<double>(variableName, 0d);
        _scriptContext.Vars.Set(variableName, current + value);
    }

    [StateAction("数值变量减少", "系统内置")]
    private void SystemSubtractNumber(
        [StateParameter("数值变量")] string variableName,
        [StateParameter("减少值")] double value)
    {
        var current = _scriptContext.Vars.Get<double>(variableName, 0d);
        _scriptContext.Vars.Set(variableName, current - value);
    }

    private static string Uid(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private RegionModel CreateRegion(string? name = null) => new()
    {
        Id = Uid("region"),
        Name = string.IsNullOrWhiteSpace(name) ? "状态域" : name
    };

    private VariableModel CreateVariable(string name, string type, JsonNode? value) => new()
    {
        Id = Uid("variable"),
        Name = name,
        Type = type,
        Value = CloneNode(value)
    };

    private InputPortModel CreateInputPort() => new()
    {
        Id = Uid("input"),
        Name = "进入"
    };

    private ConditionModel CreateCondition() => new()
    {
        Id = Uid("condition"),
        Left = Machine.Variables.FirstOrDefault()?.Name ?? string.Empty,
        Operator = "==",
        RightMode = "literal",
        RightType = "boolean",
        RightValue = JsonValue.Create(true)
    };

    private OutputPortModel CreateOutputPort() => new()
    {
        Id = Uid("output"),
        Name = "切换",
        MatchMode = "all",
        Conditions = new List<ConditionModel> { CreateCondition() }
    };

    private StateModel CreateState(string regionId, double x, double y) => new()
    {
        Id = Uid("state"),
        RegionId = regionId,
        Name = "新状态",
        X = x,
        Y = y,
        IsStart = false,
        BeforeLeaveAction = new StateActionCallModel(),
        LoopAction = new StateActionCallModel(),
        AfterEnterAction = new StateActionCallModel(),
        Inputs = new List<InputPortModel> { CreateInputPort() },
        Outputs = new List<OutputPortModel> { CreateOutputPort() }
    };

    private void CreateDemo()
    {
        Machine = new MachineModel();
        var robotRegion = CreateRegion("机器人");
        var conveyorRegion = CreateRegion("输送线");
        Machine.Regions.AddRange(new[] { robotRegion, conveyorRegion });
        SelectedRegionId = robotRegion.Id;

        Machine.Variables.AddRange(new[]
        {
            CreateVariable("ProductReady", "boolean", JsonValue.Create(false)),
            CreateVariable("ConveyorReady", "boolean", JsonValue.Create(false)),
            CreateVariable("RobotDone", "boolean", JsonValue.Create(false)),
            CreateVariable("Emergency", "boolean", JsonValue.Create(false)),
            CreateVariable("Temperature", "number", JsonValue.Create(30d))
        });

        var robotIdle = CreateState(robotRegion.Id, 180, 150);
        robotIdle.Name = "机器人待机";
        robotIdle.IsStart = true;
        robotIdle.Inputs[0].Name = "返回待机";
        robotIdle.Outputs = new List<OutputPortModel> { CreateOutputPort(), CreateOutputPort() };
        robotIdle.Outputs[0].Name = "开始抓取";
        robotIdle.Outputs[0].MatchMode = "all";
        robotIdle.Outputs[0].Conditions = new List<ConditionModel>
        {
            NewCondition("ProductReady", "==", "literal", "boolean", JsonValue.Create(true)),
            NewCondition("ConveyorReady", "==", "literal", "boolean", JsonValue.Create(true)),
            NewCondition("Emergency", "==", "literal", "boolean", JsonValue.Create(false))
        };
        robotIdle.Outputs[1].Name = "紧急停止";
        robotIdle.Outputs[1].Conditions = new List<ConditionModel>
        {
            NewCondition("Emergency", "==", "literal", "boolean", JsonValue.Create(true))
        };

        var robotBusy = CreateState(robotRegion.Id, 850, 120);
        robotBusy.Name = "机器人抓取中";
        robotBusy.Inputs[0].Name = "启动抓取";
        robotBusy.Outputs[0].Name = "抓取完成";
        robotBusy.Outputs[0].Conditions = new List<ConditionModel>
        {
            NewCondition("RobotDone", "==", "literal", "boolean", JsonValue.Create(true))
        };

        var robotEmergency = CreateState(robotRegion.Id, 850, 500);
        robotEmergency.Name = "机器人急停";
        robotEmergency.Inputs[0].Name = "急停进入";
        robotEmergency.Outputs[0].Name = "解除急停";
        robotEmergency.Outputs[0].Conditions = new List<ConditionModel>
        {
            NewCondition("Emergency", "==", "literal", "boolean", JsonValue.Create(false))
        };

        var conveyorStop = CreateState(conveyorRegion.Id, 180, 900);
        conveyorStop.Name = "输送线停止";
        conveyorStop.IsStart = true;
        conveyorStop.Outputs[0].Name = "启动";
        conveyorStop.Outputs[0].Conditions = new List<ConditionModel>
        {
            NewCondition("ProductReady", "==", "literal", "boolean", JsonValue.Create(true))
        };

        var conveyorRunning = CreateState(conveyorRegion.Id, 850, 900);
        conveyorRunning.Name = "输送线运行";
        conveyorRunning.Outputs[0].Name = "停止";
        conveyorRunning.Outputs[0].Conditions = new List<ConditionModel>
        {
            NewCondition("RobotDone", "==", "literal", "boolean", JsonValue.Create(true))
        };

        Machine.States.AddRange(new[] { robotIdle, robotBusy, robotEmergency, conveyorStop, conveyorRunning });
        robotIdle.Inputs.Add(CreateInputPort());
        robotIdle.Inputs[1].Name = "工作完成";
        robotBusy.Inputs.Add(CreateInputPort());
        robotBusy.Inputs[1].Name = "重新进入";
        conveyorStop.Inputs.Add(CreateInputPort());
        conveyorStop.Inputs[1].Name = "运行完成";

        ConnectRaw(robotIdle, robotIdle.Outputs[0], robotBusy, robotBusy.Inputs[0]);
        ConnectRaw(robotBusy, robotBusy.Outputs[0], robotIdle, robotIdle.Inputs[1]);
        ConnectRaw(robotIdle, robotIdle.Outputs[1], robotEmergency, robotEmergency.Inputs[0]);
        ConnectRaw(robotEmergency, robotEmergency.Outputs[0], robotIdle, robotIdle.Inputs[0]);
        ConnectRaw(conveyorStop, conveyorStop.Outputs[0], conveyorRunning, conveyorRunning.Inputs[0]);
        ConnectRaw(conveyorRunning, conveyorRunning.Outputs[0], conveyorStop, conveyorStop.Inputs[1]);
    }

    private ConditionModel NewCondition(string left, string op, string rightMode, string rightType, JsonNode? rightValue) => new()
    {
        Id = Uid("condition"),
        Left = left,
        Operator = op,
        RightMode = rightMode,
        RightType = rightType,
        RightValue = CloneNode(rightValue)
    };

    private void ConnectRaw(StateModel fromState, OutputPortModel output, StateModel toState, InputPortModel input)
    {
        Machine.Edges.Add(new EdgeModel
        {
            Id = Uid("edge"),
            FromStateId = fromState.Id,
            FromPortId = output.Id,
            ToStateId = toState.Id,
            ToPortId = input.Id
        });
    }

    // =========================================================
    // VALUE / CONDITION
    // =========================================================

    private static JsonNode? CloneNode(JsonNode? node)
    {
        if (node is null) return null;
        return JsonNode.Parse(node.ToJsonString());
    }

    private JsonNode? ConvertValue(string? value, string type)
    {
        value ??= string.Empty;
        switch (type)
        {
            case "number":
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariantNumber))
                    return JsonValue.Create(invariantNumber);
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var localNumber))
                    return JsonValue.Create(localNumber);
                return JsonValue.Create(0d);
            case "boolean":
                return JsonValue.Create(value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
            case "json":
                try { return JsonNode.Parse(value); }
                catch { return null; }
            default:
                return JsonValue.Create(value);
        }
    }


    private JsonNode? ConvertNodeValue(JsonNode? node, string type)
    {
        var primitive = Primitive(node);
        switch (type)
        {
            case "number":
                if (TryNumber(primitive, out var number)) return JsonValue.Create(number);
                return JsonValue.Create(0d);
            case "boolean":
                if (primitive is bool b) return JsonValue.Create(b);
                if (primitive is not null && TryNumber(primitive, out var n) && Math.Abs(n - 1d) < 1e-12) return JsonValue.Create(true);
                var text = Convert.ToString(primitive, CultureInfo.InvariantCulture) ?? string.Empty;
                return JsonValue.Create(text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase));
            case "json":
                if (node is JsonObject or JsonArray) return CloneNode(node);
                try { return JsonNode.Parse(DisplayNode(node)); } catch { return null; }
            default:
                return JsonValue.Create(JsString(primitive));
        }
    }

    private static string DisplayNode(JsonNode? node, string? type = null)
    {
        if (node is null) return string.Empty;
        if (type == "json") return node.ToJsonString();
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b)) return b ? "true" : "false";
            if (value.TryGetValue<string>(out var s)) return s;
            if (value.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue<long>(out var l)) return l.ToString(CultureInfo.InvariantCulture);
        }
        return node.ToJsonString();
    }

    private static string NodeString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var s)) return s ?? string.Empty;
        return DisplayNode(node);
    }

    private static bool NodeBool(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b)) return b;
            if (value.TryGetValue<double>(out var d)) return Math.Abs(d) > double.Epsilon;
            if (value.TryGetValue<string>(out var s)) return s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static object? Primitive(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b)) return b;
            if (value.TryGetValue<long>(out var l)) return l;
            if (value.TryGetValue<double>(out var d)) return d;
            if (value.TryGetValue<string>(out var s)) return s;
        }
        return node.ToJsonString();
    }

    private static bool TryNumber(object? value, out double number)
    {
        if (value is null) { number = 0; return false; }
        if (value is bool b) { number = b ? 1 : 0; return true; }
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static bool LooseEquals(object? left, object? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is bool || right is bool || left is double || right is double || left is long || right is long)
        {
            if (TryNumber(left, out var ln) && TryNumber(right, out var rn)) return Math.Abs(ln - rn) < 1e-12;
        }
        return string.Equals(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private bool EvaluateCondition(ConditionModel condition)
    {
        Runtime.Variables.TryGetValue(condition.Left, out var leftNode);
        JsonNode? rightNode;
        if (condition.RightMode == "variable")
        {
            Runtime.Variables.TryGetValue(NodeString(condition.RightValue), out rightNode);
        }
        else
        {
            rightNode = ConvertNodeValue(condition.RightValue, condition.RightType);
        }

        var left = Primitive(leftNode);
        var right = Primitive(rightNode);
        switch (condition.Operator)
        {
            case "==": return LooseEquals(left, right);
            case "!=": return !LooseEquals(left, right);
            case ">": return Compare(left, right) > 0;
            case ">=": return Compare(left, right) >= 0;
            case "<": return Compare(left, right) < 0;
            case "<=": return Compare(left, right) <= 0;
            case "contains": return JsString(left).Contains(JsString(right), StringComparison.Ordinal);
            case "startsWith": return JsString(left).StartsWith(JsString(right), StringComparison.Ordinal);
            case "endsWith": return JsString(left).EndsWith(JsString(right), StringComparison.Ordinal);
            default: return false;
        }
    }

    private static int Compare(object? left, object? right)
    {
        if (TryNumber(left, out var ln) && TryNumber(right, out var rn)) return ln.CompareTo(rn);
        return string.CompareOrdinal(JsString(left), JsString(right));
    }

    private static string JsString(object? value) => value switch
    {
        null => "undefined",
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private OutputEvaluation EvaluateOutputDetailed(OutputPortModel output)
    {
        if (output.MatchMode == "always")
            return new OutputEvaluation { Matched = true };

        var result = new OutputEvaluation();
        foreach (var condition in output.Conditions)
            result.Conditions[condition.Id] = EvaluateCondition(condition);

        if (output.Conditions.Count == 0)
            result.Matched = false;
        else if (output.MatchMode == "any")
            result.Matched = result.Conditions.Values.Any(x => x);
        else
            result.Matched = result.Conditions.Values.All(x => x);
        return result;
    }

    private void RefreshCurrentConditionResults()
    {
        Runtime.ConditionResults.Clear();
        foreach (var region in Machine.Regions)
        {
            if (!Runtime.CurrentStates.TryGetValue(region.Id, out var currentStateId)) continue;
            var state = GetState(currentStateId);
            if (state is null) continue;
            var map = new Dictionary<string, OutputEvaluation>();
            foreach (var output in state.Outputs)
                map[output.Id] = EvaluateOutputDetailed(output);
            Runtime.ConditionResults[state.Id] = map;
        }
    }

    private OutputEvaluation? GetConditionResult(string stateId, string outputId)
    {
        if (Runtime.ConditionResults.TryGetValue(stateId, out var outputs) && outputs.TryGetValue(outputId, out var detail))
            return detail;
        return null;
    }

    // =========================================================
    // MACHINE LOOKUPS
    // =========================================================
    private RegionModel? GetRegion(string id) => Machine.Regions.FirstOrDefault(x => x.Id == id);
    private StateModel? GetState(string id) => Machine.States.FirstOrDefault(x => x.Id == id);
    private StateModel? GetCurrentState(string regionId) => Runtime.CurrentStates.TryGetValue(regionId, out var id) ? GetState(id) : null;
    private IEnumerable<StateModel> GetStatesInRegion(string regionId) => Machine.States.Where(x => x.RegionId == regionId);
    private EdgeModel? GetEdgeFromPort(string stateId, string portId) => Machine.Edges.FirstOrDefault(x => x.FromStateId == stateId && x.FromPortId == portId);
    private EdgeModel? GetEdgeToPort(string stateId, string portId) => Machine.Edges.FirstOrDefault(x => x.ToStateId == stateId && x.ToPortId == portId);

    // =========================================================
    // REGION / VARIABLE
    // =========================================================
    private void SelectRegion(string regionId) => SelectedRegionId = regionId;

    private void AddRegion()
    {
        var region = CreateRegion($"状态域 {Machine.Regions.Count + 1}");
        Machine.Regions.Add(region);
        SelectedRegionId = region.Id;
    }

    private void DeleteRegion(string regionId)
    {
        var stateIds = Machine.States.Where(x => x.RegionId == regionId).Select(x => x.Id).ToHashSet();
        Machine.Edges.RemoveAll(x => stateIds.Contains(x.FromStateId) || stateIds.Contains(x.ToStateId));
        Machine.States.RemoveAll(x => x.RegionId == regionId);
        Machine.Regions.RemoveAll(x => x.Id == regionId);
        Runtime.CurrentStates.Remove(regionId);
        Runtime.EnteredStates.Remove(regionId);
        if (SelectedRegionId == regionId) SelectedRegionId = Machine.Regions.FirstOrDefault()?.Id;
        if (SelectedStateId is not null && stateIds.Contains(SelectedStateId)) SelectedStateId = null;
        RefreshCurrentConditionResults();
    }

    private void AddVariable()
    {
        var variable = CreateVariable($"Variable{Machine.Variables.Count + 1}", "number", JsonValue.Create(0d));
        Machine.Variables.Add(variable);
        Runtime.Variables[variable.Name] = CloneNode(variable.Value);
        RefreshCurrentConditionResults();
    }

    private void RenameVariable(string variableId, string newName)
    {
        var variable = Machine.Variables.FirstOrDefault(x => x.Id == variableId);
        if (variable is null) return;

        var oldName = variable.Name;
        newName = newName.Trim();
        if (string.IsNullOrWhiteSpace(newName)) newName = "Variable";
        variable.Name = newName;

        if (Runtime.Variables.TryGetValue(oldName, out var runtimeValue))
        {
            Runtime.Variables[newName] = runtimeValue;
            Runtime.Variables.Remove(oldName);
        }

        foreach (var state in Machine.States)
        {
            foreach (var output in state.Outputs)
            {
                foreach (var condition in output.Conditions)
                {
                    if (condition.Left == oldName)
                        condition.Left = newName;

                    if (condition.RightMode == "variable" &&
                        NodeString(condition.RightValue) == oldName)
                    {
                        condition.RightValue = JsonValue.Create(newName);
                    }
                }
            }

            foreach (var action in new[]
            {
                state.BeforeLeaveAction,
                state.LoopAction,
                state.AfterEnterAction
            })
            {
                foreach (var argument in action.ActionArguments)
                {
                    if (argument.VariableName == oldName)
                        argument.VariableName = newName;
                }
            }
        }

        RefreshCurrentConditionResults();
    }

    private void ChangeVariableType(string variableId, string type)
    {
        var variable = Machine.Variables.FirstOrDefault(x => x.Id == variableId);
        if (variable is null) return;
        variable.Type = type;
        variable.Value = ConvertNodeValue(variable.Value, type);
        Runtime.Variables[variable.Name] = CloneNode(variable.Value);
        RefreshCurrentConditionResults();
    }

    private void ChangeVariableValue(string variableId, string value)
    {
        var variable = Machine.Variables.FirstOrDefault(x => x.Id == variableId);
        if (variable is null) return;
        variable.Value = ConvertValue(value, variable.Type);
        Runtime.Variables[variable.Name] = CloneNode(variable.Value);
        RefreshCurrentConditionResults();
    }

    private void DeleteVariable(string variableId)
    {
        var variable = Machine.Variables.FirstOrDefault(x => x.Id == variableId);
        if (variable is null) return;
        Machine.Variables.Remove(variable);
        Runtime.Variables.Remove(variable.Name);
        RefreshCurrentConditionResults();
    }

    // =========================================================
    // STATE / PORT / CONDITION EDITING
    // =========================================================
    private async Task AddStateAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedRegionId))
        {
            if (Machine.Regions.Count == 0)
            {
                var region = CreateRegion("状态域 1");
                Machine.Regions.Add(region);
                SelectedRegionId = region.Id;
            }
            else
            {
                SelectedRegionId = Machine.Regions[0].Id;
            }
        }

        var point = _jsReady
            ? await JS.InvokeAsync<CanvasPoint>("industrialStateMachineUi.getAddStatePosition")
            : new CanvasPoint { X = 280, Y = 140 };
        var state = CreateState(SelectedRegionId!, point.X, point.Y);
        if (!GetStatesInRegion(SelectedRegionId!).Any()) state.IsStart = true;
        Machine.States.Add(state);
        SelectedStateId = state.Id;
        SelectedEdgeId = null;
    }

    private void RenameState(string stateId, string name)
    {
        var state = GetState(stateId);
        if (state is not null) state.Name = name;
    }

    private void ChangeStateRegion(string stateId, string newRegionId)
    {
        var state = GetState(stateId);
        if (state is null || state.RegionId == newRegionId) return;
        Machine.Edges.RemoveAll(x => x.FromStateId == state.Id || x.ToStateId == state.Id);
        state.RegionId = newRegionId;
        state.IsStart = false;
        if (!GetStatesInRegion(newRegionId).Any(x => x.Id != state.Id && x.IsStart)) state.IsStart = true;
        ResetRuntimeCore(logInitialStates: true);
    }

    private void ChangeStateStart(string stateId, bool isStart)
    {
        var state = GetState(stateId);
        if (state is null) return;
        if (isStart)
        {
            foreach (var item in GetStatesInRegion(state.RegionId)) item.IsStart = false;
            state.IsStart = true;
        }
        else
        {
            state.IsStart = false;
        }
    }

    private void AddInput(string stateId)
    {
        var state = GetState(stateId);
        state?.Inputs.Add(CreateInputPort());
    }

    private void QuickAddInput(string stateId)
    {
        AddInput(stateId);
        SelectedStateId = stateId;
        SelectedEdgeId = null;
    }

    private void AddOutput(string stateId)
    {
        var state = GetState(stateId);
        state?.Outputs.Add(CreateOutputPort());
        RefreshCurrentConditionResults();
    }

    private void QuickAddOutput(string stateId)
    {
        AddOutput(stateId);
        SelectedStateId = stateId;
        SelectedEdgeId = null;
    }

    private void RenameInput(string stateId, string inputId, string name)
    {
        var input = GetState(stateId)?.Inputs.FirstOrDefault(x => x.Id == inputId);
        if (input is not null) input.Name = name;
    }

    private void RenameOutput(string stateId, string outputId, string name)
    {
        var output = GetState(stateId)?.Outputs.FirstOrDefault(x => x.Id == outputId);
        if (output is not null) output.Name = name;
    }

    private void RemoveInput(string stateId, string inputId)
    {
        var state = GetState(stateId);
        if (state is null) return;
        Machine.Edges.RemoveAll(x => x.ToStateId == state.Id && x.ToPortId == inputId);
        state.Inputs.RemoveAll(x => x.Id == inputId);
    }

    private void RemoveOutput(string stateId, string outputId)
    {
        var state = GetState(stateId);
        if (state is null) return;
        Machine.Edges.RemoveAll(x => x.FromStateId == state.Id && x.FromPortId == outputId);
        state.Outputs.RemoveAll(x => x.Id == outputId);
        RefreshCurrentConditionResults();
    }

    private void MoveOutput(string stateId, string outputId, int direction)
    {
        var state = GetState(stateId);
        if (state is null) return;
        var index = state.Outputs.FindIndex(x => x.Id == outputId);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= state.Outputs.Count) return;
        (state.Outputs[index], state.Outputs[target]) = (state.Outputs[target], state.Outputs[index]);
        RefreshCurrentConditionResults();
    }

    private void ChangeOutputMatchMode(string stateId, string outputId, string mode)
    {
        var output = GetState(stateId)?.Outputs.FirstOrDefault(x => x.Id == outputId);
        if (output is null) return;
        output.MatchMode = mode;
        RefreshCurrentConditionResults();
    }

    private void AddCondition(string stateId, string outputId)
    {
        var output = GetState(stateId)?.Outputs.FirstOrDefault(x => x.Id == outputId);
        if (output is null) return;
        output.Conditions.Add(CreateCondition());
        RefreshCurrentConditionResults();
    }

    private void RemoveCondition(string stateId, string outputId, string conditionId)
    {
        var output = GetState(stateId)?.Outputs.FirstOrDefault(x => x.Id == outputId);
        if (output is null) return;
        output.Conditions.RemoveAll(x => x.Id == conditionId);
        RefreshCurrentConditionResults();
    }

    private ConditionModel? FindCondition(string stateId, string outputId, string conditionId) =>
        GetState(stateId)?.Outputs.FirstOrDefault(x => x.Id == outputId)?.Conditions.FirstOrDefault(x => x.Id == conditionId);

    private void ChangeConditionLeft(string stateId, string outputId, string conditionId, string value)
    {
        var condition = FindCondition(stateId, outputId, conditionId);
        if (condition is null) return;
        condition.Left = value;
        RefreshCurrentConditionResults();
    }

    private void ChangeConditionOperator(string stateId, string outputId, string conditionId, string value)
    {
        var condition = FindCondition(stateId, outputId, conditionId);
        if (condition is null) return;
        condition.Operator = value;
        RefreshCurrentConditionResults();
    }

    private void ChangeConditionRightMode(string stateId, string outputId, string conditionId, string value)
    {
        var condition = FindCondition(stateId, outputId, conditionId);
        if (condition is null) return;
        condition.RightMode = value;
        if (value == "variable") condition.RightValue = JsonValue.Create(Machine.Variables.FirstOrDefault()?.Name ?? string.Empty);
        RefreshCurrentConditionResults();
    }

    private void ChangeConditionRightType(string stateId, string outputId, string conditionId, string value)
    {
        var condition = FindCondition(stateId, outputId, conditionId);
        if (condition is null) return;
        condition.RightType = value;
        if (value == "boolean") condition.RightValue = JsonValue.Create(false);
        else condition.RightValue = ConvertNodeValue(condition.RightValue, value);
        RefreshCurrentConditionResults();
    }

    private void ChangeConditionRightValue(string stateId, string outputId, string conditionId, string value)
    {
        var condition = FindCondition(stateId, outputId, conditionId);
        if (condition is null) return;
        condition.RightValue = condition.RightMode == "variable" ? JsonValue.Create(value) : ConvertValue(value, condition.RightType);
        RefreshCurrentConditionResults();
    }

    // =========================================================
    // CONNECTION
    // =========================================================
    private async Task OnOutputPortClickAsync(string stateId, string portId)
    {
        var existing = GetEdgeFromPort(stateId, portId);
        if (existing is not null)
        {
            SelectedEdgeId = existing.Id;
            SelectedStateId = null;
            PendingConnection = null;
            return;
        }
        PendingConnection = new PendingConnectionModel { StateId = stateId, PortId = portId };
        SelectedEdgeId = null;
        if (_jsReady) await SyncJsAsync();
    }

    private async Task OnInputPortClickAsync(string stateId, string portId)
    {
        if (PendingConnection is not null)
        {
            var fromState = GetState(PendingConnection.StateId);
            var toState = GetState(stateId);
            if (fromState is null || toState is null)
            {
                PendingConnection = null;
                return;
            }
            if (fromState.RegionId != toState.RegionId)
            {
                AddLog("CONNECT", "不同状态域不能直接连接，请通过全局变量通信。", "error");
                PendingConnection = null;
                return;
            }
            if (fromState.Id == toState.Id)
            {
                AddLog("CONNECT", "当前版本不允许状态直接连接自己。", "warn");
                PendingConnection = null;
                return;
            }
            if (GetEdgeFromPort(PendingConnection.StateId, PendingConnection.PortId) is not null)
            {
                AddLog("CONNECT", "右侧插头已经被占用。", "error");
                PendingConnection = null;
                return;
            }
            if (GetEdgeToPort(stateId, portId) is not null)
            {
                AddLog("CONNECT", "左侧插头已经被占用。", "error");
                PendingConnection = null;
                return;
            }

            Machine.Edges.Add(new EdgeModel
            {
                Id = Uid("edge"),
                FromStateId = PendingConnection.StateId,
                FromPortId = PendingConnection.PortId,
                ToStateId = stateId,
                ToPortId = portId
            });
            AddLog("CONNECT", $"{fromState.Name} → {toState.Name}", "good");
            PendingConnection = null;
            RefreshCurrentConditionResults();
            return;
        }

        var existing = GetEdgeToPort(stateId, portId);
        if (existing is not null)
        {
            SelectedEdgeId = existing.Id;
            SelectedStateId = null;
        }
        await Task.CompletedTask;
    }

    private void DeleteSelectedEdge()
    {
        if (string.IsNullOrWhiteSpace(SelectedEdgeId)) return;
        Machine.Edges.RemoveAll(x => x.Id == SelectedEdgeId);
        SelectedEdgeId = null;
        RefreshCurrentConditionResults();
    }

    private void DeleteSelectedState()
    {
        if (string.IsNullOrWhiteSpace(SelectedStateId)) return;
        var stateId = SelectedStateId!;
        var state = GetState(stateId);
        Machine.Edges.RemoveAll(x => x.FromStateId == stateId || x.ToStateId == stateId);
        Machine.States.RemoveAll(x => x.Id == stateId);
        if (state is not null &&
            Runtime.CurrentStates.TryGetValue(state.RegionId, out var current) &&
            current == stateId)
        {
            Runtime.CurrentStates.Remove(state.RegionId);
            Runtime.EnteredStates.Remove(state.RegionId);
        }
        SelectedStateId = null;
        RefreshCurrentConditionResults();
    }

    // =========================================================
    // RUNTIME
    // =========================================================
    private void ResetRuntime() => ResetRuntimeCore(logInitialStates: true);

    private void ResetRuntimeCore(bool logInitialStates)
    {
        _runToken++;
        Runtime.Running = false;
        Runtime.StopRequested = false;

        Runtime.Variables.Clear();
        Runtime.CurrentStates.Clear();
        Runtime.EnteredStates.Clear();
        Runtime.CurrentEdgeIds.Clear();
        Runtime.ConditionResults.Clear();

        foreach (var variable in Machine.Variables)
            Runtime.Variables[variable.Name] = CloneNode(variable.Value);

        foreach (var region in Machine.Regions)
        {
            var start = GetStatesInRegion(region.Id).FirstOrDefault(x => x.IsStart);
            if (start is null) continue;

            Runtime.CurrentStates[region.Id] = start.Id;

            if (logInitialStates)
                AddLog("STATE", $"{region.Name} → {start.Name}", "good");
        }

        RefreshCurrentConditionResults();
    }

    private async Task<bool> ExecuteRegionStepAsync(RegionModel region)
    {
        if (!Runtime.CurrentStates.TryGetValue(region.Id, out var currentStateId))
            return false;

        var state = GetState(currentStateId);
        if (state is null)
            return false;

        // =====================================================
        // 切换前委托
        // 当前卡片刚准备进入时，只执行一次
        // =====================================================
        if (!Runtime.EnteredStates.TryGetValue(region.Id, out var enteredStateId) ||
            enteredStateId != state.Id)
        {
            await InvokeActionAsync(state.BeforeLeaveAction);
            Runtime.EnteredStates[region.Id] = state.Id;
        }

        // =====================================================
        // 循环委托
        // 当前卡片存续期间，每个周期执行
        // =====================================================
        await InvokeActionAsync(state.LoopAction);

        foreach (var output in state.Outputs)
        {
            var detail = EvaluateOutputDetailed(output);

            if (!Runtime.ConditionResults.TryGetValue(state.Id, out var resultMap))
            {
                resultMap = new Dictionary<string, OutputEvaluation>();
                Runtime.ConditionResults[state.Id] = resultMap;
            }

            resultMap[output.Id] = detail;

            var edge = GetEdgeFromPort(state.Id, output.Id);
            if (edge is null || !detail.Matched)
                continue;

            var targetState = GetState(edge.ToStateId);
            if (targetState is null)
                continue;

            if (targetState.RegionId != region.Id)
            {
                AddLog(
                    "ERROR",
                    $"非法跨状态域连线：{state.Name} → {targetState.Name}",
                    "error");

                continue;
            }

            // =====================================================
            // 切换后委托
            // 当前卡片即将离开、切换到别的卡片之前执行
            // =====================================================
            await InvokeActionAsync(state.AfterEnterAction);

            Runtime.CurrentEdgeIds.Add(edge.Id);

            AddLog(
                "SWITCH",
                $"[{region.Name}] {state.Name} → {targetState.Name} · {output.Name}",
                "good");

            await InvokeAsync(StateHasChanged);
            await Task.Delay(100);

            // =====================================================
            // 目标卡片的切换前委托
            // 在真正进入目标卡片之前执行
            // =====================================================
            await InvokeActionAsync(targetState.BeforeLeaveAction);

            // 正式进入目标卡片
            Runtime.CurrentStates[region.Id] = targetState.Id;
            Runtime.CurrentEdgeIds.Remove(edge.Id);

            // 标记目标卡片已经执行过“切换前委托”
            // 防止下一周期再次执行
            Runtime.EnteredStates[region.Id] = targetState.Id;

            RefreshCurrentConditionResults();
            await InvokeAsync(StateHasChanged);

            return true;
        }

        return false;
    }
    private async Task<bool> ExecuteStepAsync()
    {
        var switched = false;
        RefreshCurrentConditionResults();
        foreach (var region in Machine.Regions.ToArray())
        {
            if (await ExecuteRegionStepAsync(region)) switched = true;
        }
        RefreshCurrentConditionResults();
        await InvokeAsync(StateHasChanged);
        return switched;
    }

    private async Task StepAsync()
    {
        if (Runtime.Running) return;
        if (Runtime.CurrentStates.Count == 0) ResetRuntimeCore(logInitialStates: true);
        await ExecuteStepAsync();
    }

    private async Task RunMachineAsync()
    {
        if (Runtime.Running) return;
        if (Runtime.CurrentStates.Count == 0) ResetRuntimeCore(logInitialStates: true);
        Runtime.Running = true;
        Runtime.StopRequested = false;
        var token = ++_runToken;
        await InvokeAsync(StateHasChanged);

        while (Runtime.Running && !Runtime.StopRequested && token == _runToken)
        {
            try
            {
                await ExecuteStepAsync();
            }
            catch (Exception ex)
            {
                AddLog("ERROR", ex.Message, "error");
                Runtime.Running = false;
                break;
            }
            if (!Runtime.Running || Runtime.StopRequested) break;
            await Task.Delay(Math.Max(20, Machine.Settings.CycleDelay));
        }

        if (token == _runToken) Runtime.Running = false;
        await InvokeAsync(StateHasChanged);
    }

    private void StopRuntime()
    {
        Runtime.StopRequested = true;
        Runtime.Running = false;
        _runToken++;
        Runtime.CurrentEdgeIds.Clear();
        AddLog("SYSTEM", "运行停止", "warn");
    }

    private void ChangeRuntimeVariable(string variableId, string value)
    {
        var variable = Machine.Variables.FirstOrDefault(x => x.Id == variableId);
        if (variable is null) return;
        var converted = ConvertValue(value, variable.Type);
        Runtime.Variables[variable.Name] = CloneNode(converted);
        variable.Value = CloneNode(converted);
        RefreshCurrentConditionResults();
    }

    private void SetRuntimeVariable(string name, JsonNode? value)
    {
        var variable = Machine.Variables.FirstOrDefault(x => x.Name == name);
        if (variable is not null)
        {
            var converted = ConvertNodeValue(value, variable.Type);
            variable.Value = CloneNode(converted);
            Runtime.Variables[name] = CloneNode(converted);
        }
        else
        {
            Runtime.Variables[name] = CloneNode(value);
        }
        RefreshCurrentConditionResults();
    }

    // =========================================================
    // CONFIG
    // =========================================================
    private void ConfigSelectionChanged(ChangeEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Value?.ToString()))
            _configKey = e.Value!.ToString()!;
    }

    private string GetConfigFilePath(string key)
    {
        key = key.Trim();

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("配置 Key 不能为空。");

        if (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            key.Contains("..") ||
            key.Contains('/') ||
            key.Contains('\\'))
        {
            throw new InvalidOperationException("配置 Key 包含非法字符。");
        }

        Directory.CreateDirectory(ConfigDirectory);

        return Path.Combine(ConfigDirectory, $"{key}.json");
    }

    private Task RefreshConfigListAsync(string? selected = null)
    {
        Directory.CreateDirectory(ConfigDirectory);

        _configKeys = Directory
            .GetFiles(ConfigDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(selected))
            _configKey = selected;

        return Task.CompletedTask;
    }

    private async Task SaveConfigAsync()
    {
        var key = _configKey.Trim();

        if (string.IsNullOrWhiteSpace(key))
        {
            AddLog("SAVE", "请输入配置 Key。", "error");
            return;
        }

        try
        {
            var path = GetConfigFilePath(key);

            var json = JsonSerializer.Serialize(Machine, _jsonOptions);

            await File.WriteAllTextAsync(
                path,
                json,
                Encoding.UTF8);

            await RefreshConfigListAsync(key);

            AddLog("SAVE", $"配置已保存：{key}", "good");
        }
        catch (Exception ex)
        {
            AddLog("ERROR", ex.Message, "error");
        }
    }

    private async Task LoadConfigAsync()
    {
        var key = _configKey.Trim();

        if (string.IsNullOrWhiteSpace(key))
        {
            AddLog("LOAD", "请输入配置 Key。", "error");
            return;
        }

        try
        {
            var path = GetConfigFilePath(key);

            if (!File.Exists(path))
            {
                AddLog("LOAD", $"没有找到配置：{key}", "warn");
                return;
            }

            var json = await File.ReadAllTextAsync(
                path,
                Encoding.UTF8);

            Machine = JsonSerializer.Deserialize<MachineModel>(
                          json,
                          _jsonOptions)
                      ?? new MachineModel();

            NormalizeMachine();

            SelectedRegionId = Machine.Regions.FirstOrDefault()?.Id;
            SelectedStateId = null;
            SelectedEdgeId = null;
            PendingConnection = null;

            ResetRuntimeCore(logInitialStates: true);

            await RefreshConfigListAsync(key);

            AddLog("LOAD", $"已读取配置：{key}", "good");
        }
        catch (Exception ex)
        {
            AddLog("ERROR", ex.Message, "error");
        }
    }

    private async Task DeleteConfigAsync()
    {
        var key = _configKey.Trim();

        if (string.IsNullOrWhiteSpace(key))
        {
            AddLog("CONFIG", "请输入配置 Key。", "error");
            return;
        }

        try
        {
            var path = GetConfigFilePath(key);

            if (!File.Exists(path))
            {
                AddLog("CONFIG", $"配置不存在：{key}", "warn");
                return;
            }

            File.Delete(path);

            await RefreshConfigListAsync();

            if (_configKeys.Count > 0)
                _configKey = _configKeys[0];
            else
                _configKey = "default";

            AddLog("CONFIG", $"已删除配置：{key}", "warn");
        }
        catch (Exception ex)
        {
            AddLog("ERROR", ex.Message, "error");
        }
    }

    private async Task ExportConfigAsync()
    {
        var key = string.IsNullOrWhiteSpace(_configKey) ? "state-machine" : _configKey.Trim();
        var json = JsonSerializer.Serialize(Machine, _jsonOptions);
        await JS.InvokeVoidAsync("industrialStateMachineUi.downloadText", $"{key}.json", json, "application/json");
    }

    private async Task ImportConfigAsync(InputFileChangeEventArgs e)
    {
        try
        {
            var file = e.File;
            await using var stream = file.OpenReadStream(10 * 1024 * 1024);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            var machine = JsonSerializer.Deserialize<MachineModel>(json, _jsonOptions);
            if (machine is null) throw new InvalidOperationException("状态机 JSON 格式错误");
            Machine = machine;
            NormalizeMachine();
            SelectedRegionId = Machine.Regions.FirstOrDefault()?.Id;
            SelectedStateId = null;
            SelectedEdgeId = null;
            PendingConnection = null;
            _configKey = Path.GetFileNameWithoutExtension(file.Name);
            ResetRuntimeCore(logInitialStates: true);
            AddLog("IMPORT", "导入成功", "good");
        }
        catch (Exception ex)
        {
            AddLog("ERROR", ex.Message, "error");
        }
    }

    private void NormalizeMachine()
    {
        Machine.Regions ??= new List<RegionModel>();
        Machine.Variables ??= new List<VariableModel>();
        Machine.States ??= new List<StateModel>();
        Machine.Edges ??= new List<EdgeModel>();
        Machine.Settings ??= new MachineSettings();

        if (Machine.Settings.CycleDelay <= 0)
            Machine.Settings.CycleDelay = 350;

        foreach (var region in Machine.Regions)
        {
            if (string.IsNullOrWhiteSpace(region.Id))
                region.Id = Uid("region");

            region.Name ??= "状态域";
        }

        foreach (var variable in Machine.Variables)
        {
            if (string.IsNullOrWhiteSpace(variable.Id))
                variable.Id = Uid("variable");

            variable.Name ??= "Variable";
            variable.Type ??= "number";
        }

        foreach (var state in Machine.States)
        {
            if (string.IsNullOrWhiteSpace(state.Id))
                state.Id = Uid("state");

            state.Name ??= "新状态";
            state.RegionId ??= Machine.Regions.FirstOrDefault()?.Id ?? string.Empty;

            state.BeforeLeaveAction ??= new StateActionCallModel();
            state.LoopAction ??= new StateActionCallModel();
            state.AfterEnterAction ??= new StateActionCallModel();

            state.Inputs ??= new List<InputPortModel>();
            state.Outputs ??= new List<OutputPortModel>();

            foreach (var input in state.Inputs)
            {
                if (string.IsNullOrWhiteSpace(input.Id))
                    input.Id = Uid("input");

                input.Name ??= "进入";
            }

            foreach (var output in state.Outputs)
            {
                if (string.IsNullOrWhiteSpace(output.Id))
                    output.Id = Uid("output");

                output.Name ??= "切换";
                output.MatchMode ??= "all";
                output.Conditions ??= new List<ConditionModel>();

                // 仅用于兼容旧配置。新版运行时不再执行输出口动作。
                output.ActionMethod ??= string.Empty;
                output.ActionArguments ??= new List<ActionArgumentModel>();
                output.ActionPath ??= string.Empty;
                output.ActionArgs ??= "[]";

                foreach (var condition in output.Conditions)
                {
                    if (string.IsNullOrWhiteSpace(condition.Id))
                        condition.Id = Uid("condition");

                    condition.Left ??= Machine.Variables.FirstOrDefault()?.Name ?? string.Empty;
                    condition.Operator ??= "==";
                    condition.RightMode ??= "literal";
                    condition.RightType ??= "boolean";
                    condition.RightValue ??= JsonValue.Create(false);
                }
            }

            // 旧配置如果把动作挂在输出口上，迁移第一个动作到“切换前委托”。
            if (string.IsNullOrWhiteSpace(state.BeforeLeaveAction.ActionMethod))
            {
                var legacyAction = state.Outputs
                    .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.ActionMethod));

                if (legacyAction is not null)
                {
                    state.BeforeLeaveAction.ActionMethod = legacyAction.ActionMethod;
                    state.BeforeLeaveAction.ActionArguments = legacyAction.ActionArguments
                        .Select(CloneActionArgument)
                        .ToList();
                }
            }

            NormalizeActionArguments(state.BeforeLeaveAction);
            NormalizeActionArguments(state.LoopAction);
            NormalizeActionArguments(state.AfterEnterAction);
        }

        foreach (var edge in Machine.Edges)
        {
            if (string.IsNullOrWhiteSpace(edge.Id))
                edge.Id = Uid("edge");
        }
    }

    private static ActionArgumentModel CloneActionArgument(ActionArgumentModel source) => new()
    {
        ParameterName = source.ParameterName,
        Source = source.Source,
        LiteralValue = source.LiteralValue,
        VariableName = source.VariableName
    };

    // =========================================================
    // JS CALLBACKS / SELECTION / CANVAS
    // =========================================================
    private Task StateMoved(string stateId, double x, double y)
    {
        var state = GetState(stateId);
        if (state is not null)
        {
            state.X = Math.Max(0, x);
            state.Y = Math.Max(0, y);
            SelectedStateId = state.Id;
            SelectedRegionId = state.RegionId;
            SelectedEdgeId = null;
        }
        return InvokeAsync(StateHasChanged);
    }

    private Task SelectStateFromJs(string stateId)
    {
        var state = GetState(stateId);
        if (state is not null)
        {
            SelectedStateId = state.Id;
            SelectedRegionId = state.RegionId;
            SelectedEdgeId = null;
        }
        return InvokeAsync(StateHasChanged);
    }

    private Task SelectEdgeFromJs(string edgeId)
    {
        if (Machine.Edges.Any(x => x.Id == edgeId))
        {
            SelectedEdgeId = edgeId;
            SelectedStateId = null;
        }
        return InvokeAsync(StateHasChanged);
    }

    private Task ClearSelectionFromJs()
    {
        SelectedStateId = null;
        SelectedEdgeId = null;
        return InvokeAsync(StateHasChanged);
    }

    private Task EscapeFromJs()
    {
        PendingConnection = null;
        SelectedStateId = null;
        SelectedEdgeId = null;
        return InvokeAsync(StateHasChanged);
    }

    private Task DeleteSelectionFromJs()
    {
        if (!string.IsNullOrWhiteSpace(SelectedEdgeId)) DeleteSelectedEdge();
        else if (!string.IsNullOrWhiteSpace(SelectedStateId)) DeleteSelectedState();
        return InvokeAsync(StateHasChanged);
    }

    private Task ZoomInAsync() => JS.InvokeVoidAsync("industrialStateMachineUi.adjustZoom", 0.1, true).AsTask();
    private Task ZoomOutAsync() => JS.InvokeVoidAsync("industrialStateMachineUi.adjustZoom", -0.1, true).AsTask();
    private Task FitCanvasAsync() => JS.InvokeVoidAsync("industrialStateMachineUi.fitCanvas").AsTask();

    private void AddLog(string type, string message, string level = "info")
    {
        Logs.Add(new LogEntry
        {
            Id = Uid("log"),
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Type = type,
            Message = message,
            Level = level
        });
        if (Logs.Count > 1000) Logs.RemoveRange(0, Logs.Count - 1000);
        _scrollLog = true;
    }

    private void ClearLogs() => Logs.Clear();

    private static string ArgText(JsonElement[] args, int index)
    {
        if (index >= args.Length) return string.Empty;
        var element = args[index];
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();
    }

    public async ValueTask DisposeAsync()
    {
        _runToken++;
        Runtime.Running = false;

        if (_scriptHost is not null)
        {
            _scriptHost.Reloaded -= OnScriptsReloaded;
            _scriptHost.Dispose();
            _scriptHost = null;
        }

        if (_jsReady)
        {
            try { await JS.InvokeVoidAsync("industrialStateMachineUi.dispose"); }
            catch { }
        }
        _dotNetRef?.Dispose();
    }


    public sealed class JsBridge
    {
        private readonly Func<string, double, double, Task> _stateMoved;
        private readonly Func<string, Task> _selectState;
        private readonly Func<string, Task> _selectEdge;
        private readonly Func<Task> _clearSelection;
        private readonly Func<Task> _escape;
        private readonly Func<Task> _deleteSelection;

        public JsBridge(
            Func<string, double, double, Task> stateMoved,
            Func<string, Task> selectState,
            Func<string, Task> selectEdge,
            Func<Task> clearSelection,
            Func<Task> escape,
            Func<Task> deleteSelection)
        {
            _stateMoved = stateMoved;
            _selectState = selectState;
            _selectEdge = selectEdge;
            _clearSelection = clearSelection;
            _escape = escape;
            _deleteSelection = deleteSelection;
        }

        [JSInvokable] public Task StateMoved(string stateId, double x, double y) => _stateMoved(stateId, x, y);
        [JSInvokable] public Task SelectStateFromJs(string stateId) => _selectState(stateId);
        [JSInvokable] public Task SelectEdgeFromJs(string edgeId) => _selectEdge(edgeId);
        [JSInvokable] public Task ClearSelectionFromJs() => _clearSelection();
        [JSInvokable] public Task EscapeFromJs() => _escape();
        [JSInvokable] public Task DeleteSelectionFromJs() => _deleteSelection();
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class StateActionAttribute : Attribute
    {
        public string DisplayName { get; }
        public string Group { get; }

        public StateActionAttribute(string displayName, string group = "二次开发")
        {
            DisplayName = displayName;
            Group = group;
        }
    }

    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public sealed class StateParameterAttribute : Attribute
    {
        public string DisplayName { get; }
        public StateParameterAttribute(string displayName) => DisplayName = displayName;
    }

    private sealed class ReflectedActionDescriptor
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public MethodInfo Method { get; set; } = null!;
        public object Target { get; set; } = null!;
        public bool IsHotScript { get; set; }
        public List<ActionParameterDescriptor> Parameters { get; set; } = new();
    }

    private sealed class ActionParameterDescriptor
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public Type ParameterType { get; set; } = typeof(string);
        public Type EffectiveType { get; set; } = typeof(string);
        public bool IsNullable { get; set; }
        public string EditorKind { get; set; } = "string";
        public string[] EnumValues { get; set; } = Array.Empty<string>();
        public string DefaultLiteral { get; set; } = string.Empty;
    }
    // =========================================================
    // AI STATE MACHINE BUILDER
    // =========================================================

    private void OpenAiDialog()
    {
        _aiDialogOpen = true;
        _aiError = string.Empty;
    }

    private void CloseAiDialog()
    {
        _aiDialogOpen = false;
    }

    private void ClearAiConversation()
    {
        _aiMessages.Clear();
        _pendingAiPlan = null;
        _aiError = string.Empty;
        _aiInput = string.Empty;
    }

    private void SetAiExample(string text)
    {
        _aiInput = text;
        _aiError = string.Empty;
    }

    private async Task GenerateAiPlanAsync()
    {
        if (_aiBusy)
            return;

        var prompt = _aiInput.Trim();

        if (string.IsNullOrWhiteSpace(prompt))
        {
            _aiError = "请输入需要生成的流程。";
            return;
        }

        _aiBusy = true;
        _aiError = string.Empty;
        _pendingAiPlan = null;

        _aiMessages.Add(new AiChatItem
        {
            Role = "user",
            Text = prompt
        });

        _aiInput = string.Empty;

        try
        {
            var apiKey =
                Configuration["AI:ApiKey"];

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey =
                    Environment.GetEnvironmentVariable(
                        "DASHSCOPE_API_KEY");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    "没有配置 AI:ApiKey 或 DASHSCOPE_API_KEY。");

            var endpoint =
                Configuration["AI:Endpoint"]
                ?? "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions";

            var model =
                Configuration["AI:Model"]
                ?? "qwen3.8-max";

            var actionCatalog = ActionMethods
                .Select(action => new
                {
                    key = action.Key,
                    name = action.DisplayName,
                    group = action.Group,

                    parameters = action.Parameters
                        .Select(parameter => new
                        {
                            name = parameter.Name,
                            displayName = parameter.DisplayName,
                            type = parameter.EffectiveType.Name,
                            editorKind = parameter.EditorKind,
                            nullable = parameter.IsNullable,
                            defaultValue = parameter.DefaultLiteral
                        })
                        .ToArray()
                })
                .ToArray();

            var actionJson =
                JsonSerializer.Serialize(
                    actionCatalog,
                    _jsonOptions);

            var currentMachineJson =
                JsonSerializer.Serialize(
                    Machine,
                    _jsonOptions);

            var systemPrompt = $$"""
你是工业状态机逻辑编排器。

你的任务是根据用户自然语言生成完整状态机规划。

========================
状态机规则
========================

1. 一个状态机包含：
   regions
   variables
   states
   transitions

2. 不同 Region 之间禁止直接连线。
   不同 Region 只能通过全局变量通信。

3. 每个 Region 必须至少有一个 State。

4. 每个 Region 只能有一个 isStart=true。

5. transition.from 和 transition.to 使用 State.key，不使用 GUID。

6. 不需要生成：
   stateId
   regionId
   inputId
   outputId
   edgeId
   conditionId
   x
   y

这些全部由 C# 自动创建。

========================
条件规则
========================

operator 只允许：

==
!=
>
>=
<
<=
contains
startsWith
endsWith

matchMode 只允许：

all
any
always

rightMode：

literal
variable

变量类型只允许：

number
string
boolean
json

========================
状态生命周期
========================

enterAction：
刚进入当前状态时执行一次。

loopAction：
状态持续期间每个周期执行一次。

leaveAction：
准备离开当前状态、切换到下一个状态之前执行一次。

========================
动作调用规则
========================

只能调用下面动作目录中真实存在的动作。

禁止编造任何不存在的方法。

action 字段必须优先填写动作目录里的 key。

如果状态不需要动作：

action = ""
arguments = []

argument.source：

literal
表示直接传递固定值。

variable
表示参数值来自某个全局变量。

特别注意：

如果参数本身叫：
variableName
变量Key
变量名

这种参数通常是在告诉动作“操作哪个变量”。

这种情况下：

source 必须使用 literal

例如：

AddNumber(
    variableName = "Count",
    value = 1
)

应该生成：

variableName:
source = literal
value = Count

value:
source = literal
value = 1

不要把 variableName 错误设置成 source=variable。

========================
当前可调用动作
========================

{actionJson}

========================
当前状态机
========================

{currentMachineJson}

========================
修改规则
========================

如果用户说：

创建
重新做
重新生成
新建

则按照用户描述生成新的完整状态机。

如果用户说：

增加
修改
删除
把某个状态改成
在当前流程基础上

则参考“当前状态机”，返回修改后的完整状态机。

最终只能输出合法 JSON。

禁止输出 Markdown。
禁止输出 ```json。
禁止输出解释文字。
禁止在 JSON 前后输出任何其他内容。

必须严格按照下面结构输出：

{
  "reply": "对本次编排的简短说明",
  "regions": [
    {
      "key": "motor",
      "name": "电机"
    }
  ],
  "variables": [
    {
      "name": "MotorStart",
      "type": "boolean",
      "value": "false"
    }
  ],
  "states": [
    {
      "key": "stopped",
      "regionKey": "motor",
      "name": "电机停止",
      "isStart": true,
      "enterAction": {
        "action": "",
        "arguments": []
      },
      "loopAction": {
        "action": "",
        "arguments": []
      },
      "leaveAction": {
        "action": "",
        "arguments": []
      }
    }
  ],
  "transitions": [
    {
      "from": "stopped",
      "to": "running",
      "name": "启动",
      "matchMode": "all",
      "conditions": [
        {
          "left": "MotorStart",
          "operator": "==",
          "rightMode": "literal",
          "rightType": "boolean",
          "rightValue": "true"
        }
      ]
    }
  ]
}

注意：

regions、variables、states、transitions 必须始终存在。

每个 state 必须始终包含：

enterAction
loopAction
leaveAction

即使没有动作也必须返回：

{
  "action": "",
  "arguments": []
}

每个 transition 必须始终包含 conditions。

所有 value 和 rightValue 都用字符串表示。

只能使用“当前可调用动作”中存在的 action key。

必须输出 JSON。

非常重要：

输出必须尽量精简。

reply 最多 50 个汉字。

状态名称尽量简短。

不要输出任何解释、注释、分析过程。

如果用户描述的系统非常复杂，也必须优先保证 JSON 完整结束，
宁可减少不重要的辅助状态，也绝对不能输出到一半。

单次最多生成：
- 4 个状态域
- 20 个状态
- 30 个变量
- 30 条 transitions

不要重复描述相同内容。
""";

            var requestBody = new
            {
                model,

                messages = new object[]
    {
        new
        {
            role = "system",
            content = systemPrompt
        },

        new
        {
            role = "user",
            content = prompt
        }
    },

                response_format = new
                {
                    type = "json_object"
                },

                max_tokens = 8192,
                temperature = 0.1
            };

            var requestJson =
                JsonSerializer.Serialize(
                    requestBody,
                    _jsonOptions);

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    endpoint);

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    apiKey);

            request.Content =
                new StringContent(
                    requestJson,
                    Encoding.UTF8,
                    "application/json");

            using var response =
                await AiHttpClient.SendAsync(request);

            var responseText =
                await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"AI请求失败 {(int)response.StatusCode}：{LimitText(responseText, 1500)}");
            }

            using var document =
                JsonDocument.Parse(responseText);

            var choice = document.RootElement.GetProperty("choices")[0];

            var finishReason =
                choice.TryGetProperty("finish_reason", out var finishNode)
                    ? finishNode.GetString()
                    : null;

            var content = choice
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

            if (string.Equals(
                    finishReason,
                    "length",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "AI输出内容过长，被模型截断了。请把流程拆小一点生成，或者减少一次生成的状态数量。");
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException(
                    "AI没有返回编排数据。");
            }

            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException(
                    "AI没有返回编排数据。");

            var plan =
                JsonSerializer.Deserialize<AiMachinePlan>(
                    content,
                    _jsonOptions);

            if (plan is null)
                throw new InvalidOperationException(
                    "AI返回数据无法解析。");

            ValidateAiPlan(plan);

            _pendingAiPlan = plan;

            _aiMessages.Add(new AiChatItem
            {
                Role = "assistant",
                Text = BuildAiPlanPreview(plan)
            });
        }
        catch (Exception ex)
        {
            _aiError = ex.GetBaseException().Message;

            _aiMessages.Add(new AiChatItem
            {
                Role = "assistant",
                Text = $"生成失败：{_aiError}"
            });
        }
        finally
        {
            _aiBusy = false;
        }
    }

    private async Task ApplyPendingAiPlanAsync()
    {
        if (_pendingAiPlan is null)
            return;

        _aiError = string.Empty;

        try
        {
            ApplyAiPlan(_pendingAiPlan);

            var stateCount =
                _pendingAiPlan.States.Count;

            var edgeCount =
                _pendingAiPlan.Transitions.Count;

            _aiMessages.Add(new AiChatItem
            {
                Role = "assistant",
                Text =
                    $"已应用到画布：{stateCount} 个状态，{edgeCount} 条连线。"
            });

            _pendingAiPlan = null;
            _aiDialogOpen = false;

            await InvokeAsync(StateHasChanged);

            await Task.Delay(80);

            if (_jsReady)
            {
                await SyncJsAsync();
                await FitCanvasAsync();
            }
        }
        catch (Exception ex)
        {
            _aiError =
                ex.GetBaseException().Message;
        }
    }

    private void ApplyAiPlan(
        AiMachinePlan plan)
    {
        ValidateAiPlan(plan);

        var oldMachine = Machine;

        try
        {
            var next =
                new MachineModel
                {
                    Settings =
                        new MachineSettings
                        {
                            CycleDelay =
                                oldMachine.Settings.CycleDelay
                        }
                };

            Machine = next;

            // =====================================================
            // REGION
            // =====================================================

            foreach (var source in plan.Regions)
            {
                next.Regions.Add(
                    new RegionModel
                    {
                        Id = Uid("region"),
                        Name = source.Name.Trim()
                    });
            }

            var regionMap =
                plan.Regions
                    .Select((planRegion, index) => new
                    {
                        planRegion.Key,
                        Region = next.Regions[index]
                    })
                    .ToDictionary(
                        x => x.Key,
                        x => x.Region,
                        StringComparer.Ordinal);

            // =====================================================
            // VARIABLES
            // =====================================================

            foreach (var variable in plan.Variables)
            {
                var value =
                    ConvertValue(
                        variable.Value,
                        variable.Type);

                next.Variables.Add(
                    new VariableModel
                    {
                        Id = Uid("variable"),
                        Name = variable.Name.Trim(),
                        Type = variable.Type,
                        Value = CloneNode(value)
                    });
            }

            // =====================================================
            // STATES
            // =====================================================

            var stateMap =
                new Dictionary<string, StateModel>(
                    StringComparer.Ordinal);

            var yCursor = 130d;

            foreach (var regionPlan in plan.Regions)
            {
                var region =
                    regionMap[regionPlan.Key];

                var statePlans =
                    plan.States
                        .Where(x =>
                            x.RegionKey ==
                            regionPlan.Key)
                        .ToArray();

                var startAlreadyAssigned = false;

                for (var index = 0;
                     index < statePlans.Length;
                     index++)
                {
                    var statePlan =
                        statePlans[index];

                    var column =
                        index % 4;

                    var row =
                        index / 4;

                    var start =
                        statePlan.IsStart &&
                        !startAlreadyAssigned;

                    if (start)
                        startAlreadyAssigned = true;

                    var state =
                        new StateModel
                        {
                            Id = Uid("state"),

                            RegionId =
                                region.Id,

                            Name =
                                statePlan.Name.Trim(),

                            X =
                                180 +
                                column * 650,

                            Y =
                                yCursor +
                                row * 390,

                            IsStart =
                                start,

                            BeforeLeaveAction =
                                BuildAiAction(
                                    statePlan.EnterAction),

                            LoopAction =
                                BuildAiAction(
                                    statePlan.LoopAction),

                            AfterEnterAction =
                                BuildAiAction(
                                    statePlan.LeaveAction),

                            Inputs =
                                new List<InputPortModel>(),

                            Outputs =
                                new List<OutputPortModel>()
                        };

                    next.States.Add(state);

                    stateMap.Add(
                        statePlan.Key,
                        state);
                }

                var statesInRegion =
                    next.States
                        .Where(x =>
                            x.RegionId ==
                            region.Id)
                        .ToArray();

                if (!statesInRegion.Any(x => x.IsStart) &&
                    statesInRegion.Length > 0)
                {
                    statesInRegion[0].IsStart = true;
                }

                var rows =
                    Math.Max(
                        1,
                        (statePlans.Length + 3) / 4);

                yCursor +=
                    Math.Max(
                        650,
                        rows * 390 + 260);
            }

            // =====================================================
            // TRANSITIONS / PORTS / EDGES
            // =====================================================

            foreach (var transition
                     in plan.Transitions)
            {
                var from =
                    stateMap[transition.From];

                var to =
                    stateMap[transition.To];

                if (from.RegionId !=
                    to.RegionId)
                {
                    throw new InvalidOperationException(
                        $"AI产生非法跨状态域连线：{from.Name} → {to.Name}");
                }

                var output =
                    new OutputPortModel
                    {
                        Id = Uid("output"),

                        Name =
                            string.IsNullOrWhiteSpace(
                                transition.Name)
                                ? $"{from.Name} → {to.Name}"
                                : transition.Name.Trim(),

                        MatchMode =
                            transition.MatchMode,

                        Conditions =
                            new List<ConditionModel>()
                    };

                if (transition.MatchMode !=
                    "always")
                {
                    foreach (var condition
                             in transition.Conditions)
                    {
                        JsonNode? rightValue;

                        if (condition.RightMode ==
                            "variable")
                        {
                            rightValue =
                                JsonValue.Create(
                                    condition.RightValue);
                        }
                        else
                        {
                            rightValue =
                                ConvertValue(
                                    condition.RightValue,
                                    condition.RightType);
                        }

                        output.Conditions.Add(
                            new ConditionModel
                            {
                                Id =
                                    Uid("condition"),

                                Left =
                                    condition.Left,

                                Operator =
                                    condition.Operator,

                                RightMode =
                                    condition.RightMode,

                                RightType =
                                    condition.RightType,

                                RightValue =
                                    CloneNode(
                                        rightValue)
                            });
                    }
                }

                var input =
                    new InputPortModel
                    {
                        Id = Uid("input"),

                        Name =
                            string.IsNullOrWhiteSpace(
                                transition.Name)
                                ? $"来自 {from.Name}"
                                : transition.Name.Trim()
                    };

                from.Outputs.Add(output);
                to.Inputs.Add(input);

                ConnectRaw(
                    from,
                    output,
                    to,
                    input);
            }

            // 没有任何入口的状态仍然显示一个输入端口
            foreach (var state in
                     next.States)
            {
                if (state.Inputs.Count == 0)
                {
                    state.Inputs.Add(
                        new InputPortModel
                        {
                            Id = Uid("input"),
                            Name = "进入"
                        });
                }
            }

            NormalizeMachine();

            SelectedRegionId =
                next.Regions
                    .FirstOrDefault()?.Id;

            SelectedStateId = null;
            SelectedEdgeId = null;
            PendingConnection = null;

            ResetRuntimeCore(
                logInitialStates: true);

            DiscoverActionMethods();

            AddLog(
                "AI",
                $"AI编排完成：{next.States.Count} 个状态，{next.Edges.Count} 条连线",
                "good");
        }
        catch
        {
            Machine = oldMachine;
            throw;
        }
    }
    private sealed class AiChatItem
    {
        public string Role { get; set; } =
            string.Empty;

        public string Text { get; set; } =
            string.Empty;
    }

    private sealed class AiMachinePlan
    {
        public string Reply { get; set; } =
            string.Empty;

        public List<AiRegionPlan> Regions
        {
            get;
            set;
        } = new();

        public List<AiVariablePlan> Variables
        {
            get;
            set;
        } = new();

        public List<AiStatePlan> States
        {
            get;
            set;
        } = new();

        public List<AiTransitionPlan> Transitions
        {
            get;
            set;
        } = new();
    }

    private sealed class AiRegionPlan
    {
        public string Key { get; set; } =
            string.Empty;

        public string Name { get; set; } =
            string.Empty;
    }

    private sealed class AiVariablePlan
    {
        public string Name { get; set; } =
            string.Empty;

        public string Type { get; set; } =
            "number";

        public string Value { get; set; } =
            "0";
    }

    private sealed class AiStatePlan
    {
        public string Key { get; set; } =
            string.Empty;

        public string RegionKey { get; set; } =
            string.Empty;

        public string Name { get; set; } =
            string.Empty;

        public bool IsStart { get; set; }

        public AiActionPlan EnterAction
        {
            get;
            set;
        } = new();

        public AiActionPlan LoopAction
        {
            get;
            set;
        } = new();

        public AiActionPlan LeaveAction
        {
            get;
            set;
        } = new();
    }

    private sealed class AiActionPlan
    {
        public string Action { get; set; } =
            string.Empty;

        public List<AiActionArgumentPlan> Arguments
        {
            get;
            set;
        } = new();
    }

    private sealed class AiActionArgumentPlan
    {
        public string Name { get; set; } =
            string.Empty;

        public string Source { get; set; } =
            "literal";

        public string Value { get; set; } =
            string.Empty;
    }

    private sealed class AiTransitionPlan
    {
        public string From { get; set; } =
            string.Empty;

        public string To { get; set; } =
            string.Empty;

        public string Name { get; set; } =
            string.Empty;

        public string MatchMode { get; set; } =
            "all";

        public List<AiConditionPlan> Conditions
        {
            get;
            set;
        } = new();
    }

    private sealed class AiConditionPlan
    {
        public string Left { get; set; } =
            string.Empty;

        public string Operator { get; set; } =
            "==";

        public string RightMode { get; set; } =
            "literal";

        public string RightType { get; set; } =
            "boolean";

        public string RightValue { get; set; } =
            "false";
    }
    private StateActionCallModel BuildAiAction(
        AiActionPlan source)
    {
        if (source is null ||
            string.IsNullOrWhiteSpace(
                source.Action))
        {
            return new StateActionCallModel();
        }

        var descriptor =
            ResolveAiActionDescriptor(
                source.Action);

        var action =
            new StateActionCallModel
            {
                ActionMethod =
                    descriptor.Key
            };

        // 先生成完整默认参数列表
        NormalizeActionArguments(
            action,
            descriptor);

        foreach (var sourceArgument
                 in source.Arguments)
        {
            var parameter =
                descriptor.Parameters
                    .FirstOrDefault(x =>
                        string.Equals(
                            x.Name,
                            sourceArgument.Name,
                            StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            x.DisplayName,
                            sourceArgument.Name,
                            StringComparison.OrdinalIgnoreCase));

            if (parameter is null)
            {
                throw new InvalidOperationException(
                    $"动作 {descriptor.DisplayName} 不存在参数：{sourceArgument.Name}");
            }

            var argument =
                GetActionArgument(
                    action,
                    parameter.Name)!;

            if (sourceArgument.Source ==
                "variable")
            {
                var variable =
                    Machine.Variables
                        .FirstOrDefault(x =>
                            x.Name ==
                            sourceArgument.Value);

                if (variable is null)
                {
                    throw new InvalidOperationException(
                        $"动作 {descriptor.DisplayName} 引用了不存在的变量：{sourceArgument.Value}");
                }

                var compatible =
                    GetCompatibleVariables(
                            action,
                            parameter)
                        .Any(x =>
                            x.Name ==
                            variable.Name);

                if (!compatible)
                {
                    throw new InvalidOperationException(
                        $"变量 {variable.Name} 无法绑定到动作参数 {parameter.DisplayName}");
                }

                argument.Source =
                    "variable";

                argument.VariableName =
                    variable.Name;
            }
            else
            {
                // 这里顺便验证 AI 生成的参数是否能转换成真实 C# 类型
                _ =
                    ConvertLiteralToType(
                        sourceArgument.Value,
                        parameter.ParameterType);

                argument.Source =
                    "literal";

                argument.LiteralValue =
                    sourceArgument.Value;
            }
        }

        return action;
    }

    private ReflectedActionDescriptor
        ResolveAiActionDescriptor(
            string action)
    {
        action = action.Trim();

        if (_actionMethods.TryGetValue(
                action,
                out var descriptor))
        {
            return descriptor;
        }

        var matches =
            ActionMethods
                .Where(x =>
                    string.Equals(
                        x.DisplayName,
                        action,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();

        if (matches.Length == 1)
            return matches[0];

        throw new InvalidOperationException(
            $"AI调用了不存在的动作：{action}");
    }

    private void ValidateAiPlan(
        AiMachinePlan plan)
    {
        if (plan.Regions.Count == 0)
            throw new InvalidOperationException(
                "AI没有生成任何状态域。");

        if (plan.States.Count == 0)
            throw new InvalidOperationException(
                "AI没有生成任何状态。");

        var duplicateRegion =
            plan.Regions
                .GroupBy(
                    x => x.Key,
                    StringComparer.Ordinal)
                .FirstOrDefault(x =>
                    x.Count() > 1);

        if (duplicateRegion is not null)
            throw new InvalidOperationException(
                $"AI生成重复状态域 Key：{duplicateRegion.Key}");

        var duplicateState =
            plan.States
                .GroupBy(
                    x => x.Key,
                    StringComparer.Ordinal)
                .FirstOrDefault(x =>
                    x.Count() > 1);

        if (duplicateState is not null)
            throw new InvalidOperationException(
                $"AI生成重复状态 Key：{duplicateState.Key}");

        var duplicateVariable =
            plan.Variables
                .GroupBy(
                    x => x.Name,
                    StringComparer.Ordinal)
                .FirstOrDefault(x =>
                    x.Count() > 1);

        if (duplicateVariable is not null)
            throw new InvalidOperationException(
                $"AI生成重复变量：{duplicateVariable.Key}");

        var regionKeys =
            plan.Regions
                .Select(x => x.Key)
                .ToHashSet(
                    StringComparer.Ordinal);

        var stateKeys =
            plan.States
                .Select(x => x.Key)
                .ToHashSet(
                    StringComparer.Ordinal);

        var variableNames =
            plan.Variables
                .Select(x => x.Name)
                .ToHashSet(
                    StringComparer.Ordinal);

        foreach (var variable
                 in plan.Variables)
        {
            if (variable.Type is not
                ("number" or
                 "string" or
                 "boolean" or
                 "json"))
            {
                throw new InvalidOperationException(
                    $"不支持的变量类型：{variable.Type}");
            }
        }

        foreach (var state
                 in plan.States)
        {
            if (!regionKeys.Contains(
                    state.RegionKey))
            {
                throw new InvalidOperationException(
                    $"状态 {state.Name} 引用了不存在的状态域：{state.RegionKey}");
            }

            ValidateAiAction(
                state.EnterAction);

            ValidateAiAction(
                state.LoopAction);

            ValidateAiAction(
                state.LeaveAction);
        }

        foreach (var region
                 in plan.Regions)
        {
            if (!plan.States.Any(x =>
                    x.RegionKey ==
                    region.Key))
            {
                throw new InvalidOperationException(
                    $"状态域 {region.Name} 没有状态。");
            }
        }

        foreach (var transition
                 in plan.Transitions)
        {
            if (!stateKeys.Contains(
                    transition.From))
            {
                throw new InvalidOperationException(
                    $"连线起点不存在：{transition.From}");
            }

            if (!stateKeys.Contains(
                    transition.To))
            {
                throw new InvalidOperationException(
                    $"连线终点不存在：{transition.To}");
            }

            if (transition.MatchMode is not
                ("all" or "any" or "always"))
            {
                throw new InvalidOperationException(
                    $"错误 MatchMode：{transition.MatchMode}");
            }

            var from =
                plan.States.First(x =>
                    x.Key ==
                    transition.From);

            var to =
                plan.States.First(x =>
                    x.Key ==
                    transition.To);

            if (from.RegionKey !=
                to.RegionKey)
            {
                throw new InvalidOperationException(
                    $"禁止跨状态域直接连线：{from.Name} → {to.Name}");
            }

            foreach (var condition
                     in transition.Conditions)
            {
                if (!variableNames.Contains(
                        condition.Left))
                {
                    throw new InvalidOperationException(
                        $"条件引用不存在变量：{condition.Left}");
                }

                if (!Operators.Contains(
                        condition.Operator))
                {
                    throw new InvalidOperationException(
                        $"不支持条件操作符：{condition.Operator}");
                }

                if (condition.RightMode is not
                    ("literal" or "variable"))
                {
                    throw new InvalidOperationException(
                        $"错误 RightMode：{condition.RightMode}");
                }

                if (condition.RightMode ==
                        "variable" &&
                    !variableNames.Contains(
                        condition.RightValue))
                {
                    throw new InvalidOperationException(
                        $"条件引用不存在变量：{condition.RightValue}");
                }
            }
        }
    }

    private void ValidateAiAction(
        AiActionPlan action)
    {
        if (action is null ||
            string.IsNullOrWhiteSpace(
                action.Action))
        {
            return;
        }

        var descriptor =
            ResolveAiActionDescriptor(
                action.Action);

        foreach (var argument
                 in action.Arguments)
        {
            var exists =
                descriptor.Parameters.Any(x =>
                    string.Equals(
                        x.Name,
                        argument.Name,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        x.DisplayName,
                        argument.Name,
                        StringComparison.OrdinalIgnoreCase));

            if (!exists)
            {
                throw new InvalidOperationException(
                    $"动作 {descriptor.DisplayName} 不存在参数：{argument.Name}");
            }
        }
    }

    private string BuildAiPlanPreview(
        AiMachinePlan plan)
    {
        var text =
            new StringBuilder();

        if (!string.IsNullOrWhiteSpace(
                plan.Reply))
        {
            text.AppendLine(
                plan.Reply.Trim());

            text.AppendLine();
        }

        text.AppendLine(
            $"状态域：{plan.Regions.Count}");

        text.AppendLine(
            $"变量：{plan.Variables.Count}");

        text.AppendLine(
            $"状态：{plan.States.Count}");

        text.AppendLine(
            $"连线：{plan.Transitions.Count}");

        text.AppendLine();

        foreach (var state in plan.States)
        {
            text.Append(
                "● ");

            text.AppendLine(
                state.Name);

            AppendAiActionPreview(
                text,
                "进入",
                state.EnterAction);

            AppendAiActionPreview(
                text,
                "循环",
                state.LoopAction);

            AppendAiActionPreview(
                text,
                "离开",
                state.LeaveAction);
        }

        if (plan.Transitions.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("连线：");

            foreach (var transition
                     in plan.Transitions)
            {
                text.Append("  ");
                text.Append(transition.From);
                text.Append(" → ");
                text.Append(transition.To);

                if (!string.IsNullOrWhiteSpace(
                        transition.Name))
                {
                    text.Append(" · ");
                    text.Append(
                        transition.Name);
                }

                text.AppendLine();
            }
        }

        return text.ToString().TrimEnd();
    }

    private static void AppendAiActionPreview(
        StringBuilder text,
        string lifecycle,
        AiActionPlan action)
    {
        if (action is null ||
            string.IsNullOrWhiteSpace(
                action.Action))
        {
            return;
        }

        text.Append("   ");
        text.Append(lifecycle);
        text.Append("：");
        text.Append(action.Action);
        text.Append("(");

        text.Append(
            string.Join(
                ", ",
                action.Arguments.Select(
                    x =>
                        $"{x.Name}={x.Value}")));

        text.AppendLine(")");
    }

    private static string LimitText(
        string value,
        int length)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length <= length)
        {
            return value;
        }

        return value[..length] + "...";
    }

    // =========================================================
    // MODELS
    // =========================================================
    public sealed class MachineModel
    {
        public List<RegionModel> Regions { get; set; } = new();
        public List<VariableModel> Variables { get; set; } = new();
        public List<StateModel> States { get; set; } = new();
        public List<EdgeModel> Edges { get; set; } = new();
        public MachineSettings Settings { get; set; } = new();
    }

    public sealed class MachineSettings
    {
        public int CycleDelay { get; set; } = 50;
    }

    public sealed class RegionModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public sealed class VariableModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = "number";
        public JsonNode? Value { get; set; }
    }

    public sealed class StateModel
    {
        public string Id { get; set; } = string.Empty;
        public string RegionId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public bool IsStart { get; set; }

        public StateActionCallModel BeforeLeaveAction { get; set; } = new();
        public StateActionCallModel LoopAction { get; set; } = new();
        public StateActionCallModel AfterEnterAction { get; set; } = new();

        public List<InputPortModel> Inputs { get; set; } = new();
        public List<OutputPortModel> Outputs { get; set; } = new();
    }

    public sealed class StateActionCallModel
    {
        public string ActionMethod { get; set; } = string.Empty;
        public List<ActionArgumentModel> ActionArguments { get; set; } = new();
    }

    public sealed class InputPortModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public sealed class OutputPortModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string MatchMode { get; set; } = "all";
        public List<ConditionModel> Conditions { get; set; } = new();

        // 仅用于兼容旧 JSON。新版 UI/运行时不再执行输出口动作。
        public string ActionMethod { get; set; } = string.Empty;
        public List<ActionArgumentModel> ActionArguments { get; set; } = new();
        public string ActionPath { get; set; } = string.Empty;
        public string ActionArgs { get; set; } = "[]";
    }

    public sealed class ActionArgumentModel
    {
        public string ParameterName { get; set; } = string.Empty;
        public string Source { get; set; } = "literal";
        public string LiteralValue { get; set; } = string.Empty;
        public string VariableName { get; set; } = string.Empty;
    }

    public sealed class ConditionModel
    {
        public string Id { get; set; } = string.Empty;
        public string Left { get; set; } = string.Empty;
        public string Operator { get; set; } = "==";
        public string RightMode { get; set; } = "literal";
        public string RightType { get; set; } = "boolean";
        public JsonNode? RightValue { get; set; }
    }

    public sealed class EdgeModel
    {
        public string Id { get; set; } = string.Empty;
        public string FromStateId { get; set; } = string.Empty;
        public string FromPortId { get; set; } = string.Empty;
        public string ToStateId { get; set; } = string.Empty;
        public string ToPortId { get; set; } = string.Empty;
    }

    public sealed class PendingConnectionModel
    {
        public string StateId { get; set; } = string.Empty;
        public string PortId { get; set; } = string.Empty;
    }

    public sealed class RuntimeModel
    {
        public bool Running { get; set; }
        public bool StopRequested { get; set; }
        public Dictionary<string, JsonNode?> Variables { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> CurrentStates { get; } = new();
        public Dictionary<string, string> EnteredStates { get; } = new();
        public HashSet<string> CurrentEdgeIds { get; } = new();
        public Dictionary<string, Dictionary<string, OutputEvaluation>> ConditionResults { get; } = new();
    }

    public sealed class OutputEvaluation
    {
        public bool Matched { get; set; }
        public Dictionary<string, bool> Conditions { get; set; } = new();
    }

    public sealed class LogEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Time { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Level { get; set; } = "info";
    }

    public sealed class CanvasPoint
    {
        public double X { get; set; }
        public double Y { get; set; }
    }
}



