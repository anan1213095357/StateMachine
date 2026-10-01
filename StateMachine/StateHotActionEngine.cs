namespace StateMachine;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

public sealed class StateHotActionEngine : IDisposable
{
    private readonly string _directory;
    private readonly Func<string, JsonNode?> _getVariable;
    private readonly Action<string, JsonNode?> _setVariable;
    private readonly Action<string, string, string> _writeLog;
    private readonly object _sync = new();

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private Dictionary<string, HotActionDefinition> _actions =
        new(StringComparer.Ordinal);

    private List<string> _lastErrors = new();

    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;
    private bool _disposed;

    public StateHotActionEngine(
        string directory,
        Func<string, JsonNode?> getVariable,
        Action<string, JsonNode?> setVariable,
        Action<string, string, string> writeLog)
    {
        _directory = directory;
        _getVariable = getVariable;
        _setVariable = setVariable;
        _writeLog = writeLog;
    }

    public event Action? Changed;

    public IReadOnlyList<HotActionDefinition> Actions
    {
        get
        {
            lock (_sync)
            {
                return _actions.Values
                    .OrderBy(x => x.Group)
                    .ThenBy(x => x.DisplayName)
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<string> LastErrors
    {
        get
        {
            lock (_sync)
                return _lastErrors.ToArray();
        }
    }

    public void Start()
    {
        Directory.CreateDirectory(_directory);

        Reload(false);

        _watcher = new FileSystemWatcher(
            _directory,
            "*.stateaction")
        {
            IncludeSubdirectories = true,

            NotifyFilter =
                NotifyFilters.FileName |
                NotifyFilters.LastWrite |
                NotifyFilters.CreationTime |
                NotifyFilters.Size,

            EnableRaisingEvents = true
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
    }

    public void Reload(bool notify = true)
    {
        if (_disposed)
            return;

        var actions =
            new Dictionary<string, HotActionDefinition>(
                StringComparer.Ordinal);

        var errors = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     _directory,
                     "*.stateaction",
                     SearchOption.AllDirectories))
        {
            try
            {
                var action = LoadActionFile(file);

                if (!actions.TryAdd(action.Key, action))
                {
                    throw new InvalidOperationException(
                        $"动作 Key 重复：{action.Key}");
                }
            }
            catch (Exception ex)
            {
                errors.Add(
                    $"{Path.GetFileName(file)}：{ex.Message}");
            }
        }

        lock (_sync)
        {
            _actions = actions;
            _lastErrors = errors;
        }

        if (notify)
            Changed?.Invoke();
    }

    public async Task ExecuteAsync(
        string key,
        IReadOnlyDictionary<string, object?> arguments)
    {
        HotActionDefinition action;

        lock (_sync)
        {
            if (!_actions.TryGetValue(key, out action!))
            {
                throw new InvalidOperationException(
                    $"热更新动作不存在：{key}");
            }
        }

        var api = new StateScriptApi(
            _getVariable,
            _setVariable,
            _writeLog);

        await StateActionScriptRunner.ExecuteAsync(
            action.Script,
            arguments,
            api);
    }

    private HotActionDefinition LoadActionFile(
        string file)
    {
        var lines = File.ReadAllLines(file);

        HotActionHeader? header = null;

        var parameters =
            new List<HotActionParameterDefinition>();

        var bodyStart = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var text = lines[i].Trim();

            if (text.Length == 0 ||
                text.StartsWith("//") ||
                text.StartsWith("#"))
            {
                continue;
            }

            if (text.StartsWith(
                    "@action",
                    StringComparison.OrdinalIgnoreCase))
            {
                header =
                    JsonSerializer.Deserialize<HotActionHeader>(
                        text[7..].Trim(),
                        _jsonOptions)
                    ?? throw new InvalidOperationException(
                        "@action 元数据无效");

                continue;
            }

            if (text.StartsWith(
                    "@param",
                    StringComparison.OrdinalIgnoreCase))
            {
                var parameter =
                    JsonSerializer.Deserialize
                        <HotActionParameterDefinition>(
                            text[6..].Trim(),
                            _jsonOptions)
                    ?? throw new InvalidOperationException(
                        "@param 元数据无效");

                parameters.Add(parameter);

                continue;
            }

            bodyStart = i;
            break;
        }

        var fileKey =
            Path.GetFileNameWithoutExtension(file);

        header ??= new HotActionHeader();

        var key =
            string.IsNullOrWhiteSpace(header.Key)
                ? fileKey
                : header.Key.Trim();

        var displayName =
            string.IsNullOrWhiteSpace(header.DisplayName)
                ? key
                : header.DisplayName.Trim();

        var group =
            string.IsNullOrWhiteSpace(header.Group)
                ? "二次开发"
                : header.Group.Trim();

        foreach (var parameter in parameters)
        {
            if (string.IsNullOrWhiteSpace(
                    parameter.Name))
            {
                throw new InvalidOperationException(
                    "参数 Name 不能为空");
            }

            parameter.Name =
                parameter.Name.Trim();

            parameter.DisplayName =
                string.IsNullOrWhiteSpace(
                    parameter.DisplayName)
                    ? parameter.Name
                    : parameter.DisplayName.Trim();

            parameter.Type =
                NormalizeType(parameter.Type);

            parameter.VariableType =
                NormalizeVariableType(
                    parameter.VariableType);
        }

        var duplicate =
            parameters
                .GroupBy(
                    x => x.Name,
                    StringComparer.Ordinal)
                .FirstOrDefault(x => x.Count() > 1);

        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"参数名重复：{duplicate.Key}");
        }

        var script =
            bodyStart < 0
                ? string.Empty
                : string.Join(
                    Environment.NewLine,
                    lines.Skip(bodyStart));

        if (string.IsNullOrWhiteSpace(script))
        {
            throw new InvalidOperationException(
                "动作脚本为空");
        }

        return new HotActionDefinition
        {
            Key = key,
            DisplayName = displayName,
            Group = group,

            Parameters = parameters,

            Script = script,
            SourceFile = file
        };
    }

    private static string NormalizeType(
        string? type)
    {
        type =
            (type ?? "string")
            .Trim()
            .ToLowerInvariant();

        return type switch
        {
            "number" => type,
            "string" => type,
            "boolean" => type,
            "json" => type,
            "datetime" => type,
            "date" => type,
            "time" => type,
            "guid" => type,

            _ => "string"
        };
    }

    private static string NormalizeVariableType(
        string? type)
    {
        type =
            (type ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

        return type switch
        {
            "number" => type,
            "string" => type,
            "boolean" => type,
            "json" => type,

            _ => string.Empty
        };
    }

    private void OnFileChanged(
        object sender,
        FileSystemEventArgs e)
    {
        if (_disposed)
            return;

        lock (_sync)
        {
            _reloadTimer?.Dispose();

            _reloadTimer =
                new Timer(
                    _ =>
                    {
                        try
                        {
                            Reload();
                        }
                        catch
                        {
                        }
                    },
                    null,
                    150,
                    Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;

            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Deleted -= OnFileChanged;
            _watcher.Renamed -= OnFileChanged;

            _watcher.Dispose();
        }

        lock (_sync)
        {
            _reloadTimer?.Dispose();
            _reloadTimer = null;
        }
    }

    private sealed class HotActionHeader
    {
        public string Key { get; set; } =
            string.Empty;

        public string DisplayName { get; set; } =
            string.Empty;

        public string Group { get; set; } =
            "二次开发";
    }
}

public sealed class HotActionDefinition
{
    public string Key { get; set; } =
        string.Empty;

    public string DisplayName { get; set; } =
        string.Empty;

    public string Group { get; set; } =
        "二次开发";

    public List<HotActionParameterDefinition>
        Parameters { get; set; } = new();

    public string Script { get; set; } =
        string.Empty;

    public string SourceFile { get; set; } =
        string.Empty;
}

public sealed class HotActionParameterDefinition
{
    public string Name { get; set; } =
        string.Empty;

    public string DisplayName { get; set; } =
        string.Empty;

    public string Type { get; set; } =
        "string";

    public bool VariableName { get; set; }

    public string VariableType { get; set; } =
        string.Empty;

    public string VariableValueFor { get; set; } =
        string.Empty;

    public JsonNode? Default { get; set; }

    public List<string> EnumValues { get; set; } =
        new();
}

// ============================================================
// 热更新脚本 API
// ============================================================

public sealed class StateScriptApi
{
    public StateScriptApi(
        Func<string, JsonNode?> getVariable,
        Action<string, JsonNode?> setVariable,
        Action<string, string, string> writeLog)
    {
        Var = new StateScriptVariableApi(
            getVariable,
            setVariable);

        Log = new StateScriptLogApi(
            writeLog);
    }

    public StateScriptVariableApi Var { get; }

    public StateScriptLogApi Log { get; }

    public Task Delay(
        double milliseconds)
    {
        return Task.Delay(
            (int)System.Math.Clamp(
                milliseconds,
                0,
                int.MaxValue));
    }

    public string Now()
    {
        return DateTime.Now.ToString(
            "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture);
    }
}

// ============================================================
// Var.xxx
// ============================================================

public sealed class StateScriptVariableApi
{
    private readonly Func<string, JsonNode?> _get;
    private readonly Action<string, JsonNode?> _set;

    public StateScriptVariableApi(
        Func<string, JsonNode?> get,
        Action<string, JsonNode?> set)
    {
        _get = get;
        _set = set;
    }

    public object? Get(
        string name)
    {
        return Primitive(
            _get(name));
    }

    public double Number(
        string name)
    {
        return TryNumber(
            Primitive(_get(name)),
            out var number)
            ? number
            : 0d;
    }

    public string Text(
        string name)
    {
        return Convert.ToString(
                   Primitive(_get(name)),
                   CultureInfo.InvariantCulture)
               ?? string.Empty;
    }

    public bool Bool(
        string name)
    {
        return ToBool(
            Primitive(_get(name)));
    }

    public object? Set(
        string name,
        object? value)
    {
        _set(
            name,
            ToNode(value));

        return value;
    }

    public double Add(
        string name,
        double value)
    {
        var result =
            Number(name) + value;

        _set(
            name,
            JsonValue.Create(result));

        return result;
    }

    public double Sub(
        string name,
        double value)
    {
        var result =
            Number(name) - value;

        _set(
            name,
            JsonValue.Create(result));

        return result;
    }

    public double Mul(
        string name,
        double value)
    {
        var result =
            Number(name) * value;

        _set(
            name,
            JsonValue.Create(result));

        return result;
    }

    public double Div(
        string name,
        double value)
    {
        if (System.Math.Abs(value) <
            double.Epsilon)
        {
            throw new DivideByZeroException(
                "除数不能为 0");
        }

        var result =
            Number(name) / value;

        _set(
            name,
            JsonValue.Create(result));

        return result;
    }

    public bool Toggle(
        string name)
    {
        var result =
            !Bool(name);

        _set(
            name,
            JsonValue.Create(result));

        return result;
    }

    public object? Copy(
        string sourceName,
        string targetName)
    {
        var node =
            _get(sourceName);

        _set(
            targetName,
            node is null
                ? null
                : JsonNode.Parse(
                    node.ToJsonString()));

        return Primitive(node);
    }

    private static JsonNode? ToNode(
        object? value)
    {
        if (value is null)
            return null;

        if (value is JsonNode node)
        {
            return JsonNode.Parse(
                node.ToJsonString());
        }

        if (value is JsonElement element)
        {
            return JsonNode.Parse(
                element.GetRawText());
        }

        if (value is bool b)
            return JsonValue.Create(b);

        if (value is string s)
            return JsonValue.Create(s);

        if (value is char c)
            return JsonValue.Create(
                c.ToString());

        if (value is
            byte or
            sbyte or
            short or
            ushort or
            int or
            uint or
            long or
            ulong or
            float or
            double or
            decimal)
        {
            return JsonValue.Create(
                Convert.ToDouble(
                    value,
                    CultureInfo.InvariantCulture));
        }

        return JsonSerializer.SerializeToNode(
            value);
    }

    private static object? Primitive(
        JsonNode? node)
    {
        if (node is null)
            return null;

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(
                    out var b))
                return b;

            if (value.TryGetValue<long>(
                    out var l))
                return l;

            if (value.TryGetValue<double>(
                    out var d))
                return d;

            if (value.TryGetValue<string>(
                    out var s))
                return s;
        }

        return node;
    }

    private static bool TryNumber(
        object? value,
        out double number)
    {
        if (value is null)
        {
            number = 0;
            return false;
        }

        if (value is bool b)
        {
            number = b ? 1 : 0;
            return true;
        }

        if (value is
            byte or
            sbyte or
            short or
            ushort or
            int or
            uint or
            long or
            ulong or
            float or
            double or
            decimal)
        {
            number =
                Convert.ToDouble(
                    value,
                    CultureInfo.InvariantCulture);

            return true;
        }

        return double.TryParse(
            Convert.ToString(
                value,
                CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out number);
    }

    private static bool ToBool(
        object? value)
    {
        if (value is bool b)
            return b;

        if (TryNumber(
                value,
                out var number))
        {
            return System.Math.Abs(number) >
                   double.Epsilon;
        }

        var text =
            Convert.ToString(
                value,
                CultureInfo.InvariantCulture)
            ?? string.Empty;

        return
            text == "1" ||
            text.Equals(
                "true",
                StringComparison.OrdinalIgnoreCase);
    }
}

// ============================================================
// Log.xxx
// ============================================================

public sealed class StateScriptLogApi
{
    private readonly
        Action<string, string, string> _write;

    public StateScriptLogApi(
        Action<string, string, string> write)
    {
        _write = write;
    }

    public object? Info(
        object? message)
    {
        return Write(
            "SCRIPT",
            message,
            "info");
    }

    public object? Good(
        object? message)
    {
        return Write(
            "SCRIPT",
            message,
            "good");
    }

    public object? Warn(
        object? message)
    {
        return Write(
            "SCRIPT",
            message,
            "warn");
    }

    public object? Error(
        object? message)
    {
        return Write(
            "SCRIPT",
            message,
            "error");
    }

    public object? Write(
        string type,
        object? message,
        string level = "info")
    {
        var text =
            message is JsonNode node
                ? node.ToJsonString()
                : Convert.ToString(
                      message,
                      CultureInfo.InvariantCulture)
                  ?? string.Empty;

        _write(
            type,
            text,
            level);

        return message;
    }
}

// ============================================================
// 脚本执行器
//
// 支持：
// Var.Set(...);
// Var.Add(...);
// Var.Toggle(...);
// Log.Info(...);
// Delay(...);
//
// 支持嵌套：
// Var.Set("B", Var.Get("A"));
// Log.Info(Var.Get("A"));
// ============================================================

internal static class StateActionScriptRunner
{
    public static async Task ExecuteAsync(
        string script,
        IReadOnlyDictionary<string, object?> arguments,
        StateScriptApi api)
    {
        var executable =
            string.Join(
                Environment.NewLine,
                script
                    .Replace("\r", string.Empty)
                    .Split('\n')
                    .Where(x =>
                        !x.TrimStart()
                            .StartsWith("//") &&
                        !x.TrimStart()
                            .StartsWith("#")));

        foreach (var statement in
                 SplitTopLevel(
                     executable,
                     ';'))
        {
            var text =
                statement.Trim();

            if (text.Length == 0)
                continue;

            await EvaluateAsync(
                text,
                arguments,
                api);
        }
    }

    private static async Task<object?> EvaluateAsync(
        string expression,
        IReadOnlyDictionary<string, object?> arguments,
        StateScriptApi api)
    {
        expression =
            expression.Trim();

        if (expression.Length == 0)
            return null;

        if ((expression[0] == '"' &&
             expression[^1] == '"') ||
            (expression[0] == '\'' &&
             expression[^1] == '\''))
        {
            return Unescape(
                expression[1..^1]);
        }

        if (expression.Equals(
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (expression.Equals(
                "false",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (expression.Equals(
                "null",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (double.TryParse(
                expression,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return number;
        }

        if (arguments.TryGetValue(
                expression,
                out var argument))
        {
            return argument;
        }

        var open =
            FindCallOpenParen(expression);

        if (open <= 0 ||
            !expression.EndsWith(')'))
        {
            throw new InvalidOperationException(
                $"无法识别脚本表达式：{expression}");
        }

        var path =
            expression[..open]
                .Trim()
                .Split(
                    '.',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries);

        if (path.Length == 0)
        {
            throw new InvalidOperationException(
                $"API 名称无效：{expression}");
        }

        var rawArguments =
            SplitTopLevel(
                expression[(open + 1)..^1],
                ',');

        var values =
            new object?[rawArguments.Count];

        for (var i = 0;
             i < rawArguments.Count;
             i++)
        {
            values[i] =
                await EvaluateAsync(
                    rawArguments[i],
                    arguments,
                    api);
        }

        return await InvokeApiAsync(
            api,
            path,
            values);
    }

    private static async Task<object?> InvokeApiAsync(
        StateScriptApi api,
        IReadOnlyList<string> path,
        object?[] arguments)
    {
        object target = api;

        for (var i = 0; i < path.Count - 1; i++)
        {
            var property =
                target.GetType()
                    .GetProperty(
                        path[i],
                        BindingFlags.Public |
                        BindingFlags.Instance |
                        BindingFlags.IgnoreCase)
                ?? throw new InvalidOperationException(
                    $"脚本 API 不存在：{string.Join('.', path.Take(i + 1))}");

            target =
                property.GetValue(target)
                ?? throw new InvalidOperationException(
                    $"脚本 API 为空：{string.Join('.', path.Take(i + 1))}");
        }

        var methodName =
            path[^1];

        var methods =
            target.GetType()
                .GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Instance)
                .Where(x =>
                    x.Name.Equals(
                        methodName,
                        StringComparison.OrdinalIgnoreCase));

        foreach (var method in methods)
        {
            if (!TryBuildArguments(method, arguments, out var converted))
            {
                continue;
            }

            object? result;

            try
            {
                result = method.Invoke(target, converted);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }

            if (result is not Task task) return result;
            await task;

            return task.GetType()
                .GetProperty("Result")
                ?.GetValue(task);
        }

        throw new InvalidOperationException(
            $"找不到脚本 API：{string.Join('.', path)}({arguments.Length} 参数)");
    }

    private static bool TryBuildArguments(MethodInfo method, object?[] source, out object?[] converted)
    {
        var parameters = method.GetParameters();

        var required = parameters.Count(x => !x.HasDefaultValue);

        if (source.Length < required ||
            source.Length > parameters.Length)
        {
            converted = [];

            return false;
        }

        converted = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            if (i >= source.Length)
            {
                converted[i] =
                    parameters[i].DefaultValue;

                continue;
            }

            if (!TryConvert(
                    source[i],
                    parameters[i].ParameterType,
                    out converted[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryConvert(object? value, Type targetType, out object? converted)
    {
        var nullable = Nullable.GetUnderlyingType(targetType);

        var type = nullable ?? targetType;

        if (value is null)
        {
            if (nullable is not null ||
                !targetType.IsValueType)
            {
                converted = null;
                return true;
            }

            converted = null;
            return false;
        }

        if (type == typeof(object) || type.IsInstanceOfType(value))
        {
            converted = value;
            return true;
        }

        try
        {
            if (type == typeof(string))
            {
                converted =
                    Convert.ToString(
                        value,
                        CultureInfo.InvariantCulture)
                    ?? string.Empty;
            }
            else if (type == typeof(bool))
            {
                var text =
                    Convert.ToString(
                        value,
                        CultureInfo.InvariantCulture)
                    ?? string.Empty;

                converted =
                    value is bool b
                        ? b
                        : text == "1" ||
                          text.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else if (type == typeof(JsonNode))
            {
                converted = value is JsonNode node ? node : JsonSerializer.SerializeToNode(value);
            }
            else if (type.IsEnum)
            {
                converted = Enum.Parse(type, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                    true);
            }
            else
            {
                converted = Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
            }

            return true;
        }
        catch
        {
            converted = null;
            return false;
        }
    }

    private static int FindCallOpenParen(string text)
    {
        var quote = '\0';
        var escape = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (escape)
            {
                escape = false;
                continue;
            }

            if (quote != '\0')
            {
                if (c == '\\')
                    escape = true;
                else if (c == quote)
                    quote = '\0';

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    continue;
                case '(':
                    return i;
            }
        }

        return -1;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var result = new List<string>();

        var start = 0;
        var depth = 0;
        var quote = '\0';
        var escape = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (escape)
            {
                escape = false;
                continue;
            }

            if (quote != '\0')
            {
                if (c == '\\') escape = true;
                else if (c == quote) quote = '\0';
                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    continue;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                default:
                {
                    if (c == separator && depth == 0)
                    {
                        result.Add(text[start..i]);
                        start = i + 1;
                    }

                    break;
                }
            }
        }

        if (start < text.Length)
        {
            result.Add(
                text[start..]);
        }

        return result;
    }

    private static string Unescape(string text)
    {
        var builder = new StringBuilder();
        var escape = false;
        foreach (var c in text)
        {
            if (!escape)
            {
                if (c == '\\')
                {
                    escape = true;
                    continue;
                }

                builder.Append(c);
                continue;
            }

            builder.Append(
                c switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => c
                });

            escape = false;
        }

        if (escape) builder.Append('\\');
        return builder.ToString();
    }
}