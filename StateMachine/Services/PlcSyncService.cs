using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using UniversalModbus;

namespace StateMachine.Services;

public sealed record PlcVariableBinding(
    string Name,
    string Address,
    string Type);

public sealed class PlcSyncService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    private readonly object _sync = new();

    private readonly Dictionary<string, PlcVariableBinding> _bindings =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, JsonNode?> _values =
        new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<WriteRequest> _writeQueue = new();

    private readonly ConcurrentDictionary<string, long> _versions =
        new(StringComparer.Ordinal);

    private ModbusClient? _plc;

    private byte _slaveId = 1;
    private int _pollMs = 100;
    private ByteOrder _byteOrder = ByteOrder.ABCD;

    public event Action<string, JsonNode?>? ValueChanged;

    public PlcSyncService(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public JsonNode? Get(string name)
    {
        lock (_sync)
        {
            return _values.TryGetValue(name, out var value)
                ? value?.DeepClone()
                : null;
        }
    }

    public void Set(
        string name,
        JsonNode? value)
    {
        PlcVariableBinding? binding;

        lock (_sync)
        {
            _values[name] = value?.DeepClone();
            _bindings.TryGetValue(name, out binding);
        }

        RaiseValueChanged(name, value);

        if (binding is null ||
            string.IsNullOrWhiteSpace(binding.Address))
        {
            return;
        }

        var version = _versions.AddOrUpdate(
            name,
            1,
            (_, old) => old + 1);

        _writeQueue.Enqueue(
            new WriteRequest(
                binding,
                value?.DeepClone(),
                version));
    }

    public void UpsertBinding(
        string name,
        string address,
        string type)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                _bindings.Remove(name);
                return;
            }

            _bindings[name] =
                new PlcVariableBinding(
                    name,
                    address.Trim(),
                    type);
        }
    }

    public void RemoveBinding(string name)
    {
        lock (_sync)
        {
            _bindings.Remove(name);
            _values.Remove(name);
        }

        _versions.TryRemove(name, out _);
    }

    public void ReplaceBindings(
        IEnumerable<PlcVariableBinding> bindings)
    {
        lock (_sync)
        {
            _bindings.Clear();

            foreach (var binding in bindings)
            {
                if (string.IsNullOrWhiteSpace(binding.Name) ||
                    string.IsNullOrWhiteSpace(binding.Address))
                {
                    continue;
                }

                _bindings[binding.Name] = binding;
            }
        }
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        LoadBindingsFromConfig();

        var host = _configuration["PLC:Host"];

        if (string.IsNullOrWhiteSpace(host))
            return;

        var port =
            int.TryParse(
                _configuration["PLC:Port"],
                out var portValue)
                ? portValue
                : 502;

        _slaveId =
            byte.TryParse(
                _configuration["PLC:SlaveId"],
                out var slaveId)
                ? slaveId
                : (byte)1;

        _pollMs =
            int.TryParse(
                _configuration["PLC:PollMs"],
                out var pollMs)
                ? Math.Max(20, pollMs)
                : 100;

        if (Enum.TryParse<ByteOrder>(
                _configuration["PLC:ByteOrder"],
                true,
                out var order))
        {
            _byteOrder = order;
        }

        _plc = new ModbusClient(host, port)
        {
            Timeout =
                int.TryParse(
                    _configuration["PLC:Timeout"],
                    out var timeout)
                    ? timeout
                    : 1000,

            RetryCount =
                int.TryParse(
                    _configuration["PLC:RetryCount"],
                    out var retry)
                    ? retry
                    : 1
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_plc.IsConnected)
                    await _plc.ConnectAsync(stoppingToken);

                await FlushWritesAsync(stoppingToken);
                await ReadAllAsync(stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
            }

            try
            {
                await Task.Delay(
                    _pollMs,
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_plc is not null)
        {
            try
            {
                await _plc.DisposeAsync();
            }
            catch
            {
            }

            _plc = null;
        }
    }

    private async Task FlushWritesAsync(
        CancellationToken cancellationToken)
    {
        while (_writeQueue.TryDequeue(out var write))
        {
            try
            {
                await WriteAsync(
                    write.Binding,
                    write.Value,
                    cancellationToken);
            }
            catch
            {
                // 写失败重新入队
                _writeQueue.Enqueue(write);
                break;
            }
        }
    }

    private async Task ReadAllAsync(
        CancellationToken cancellationToken)
    {
        PlcVariableBinding[] bindings;

        lock (_sync)
        {
            bindings = _bindings.Values.ToArray();
        }

        foreach (var binding in bindings)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            try
            {
                var versionBefore =
                    _versions.TryGetValue(
                        binding.Name,
                        out var version)
                        ? version
                        : 0;

                var value =
                    await ReadAsync(
                        binding,
                        cancellationToken);

                var versionAfter =
                    _versions.TryGetValue(
                        binding.Name,
                        out var current)
                        ? current
                        : 0;

                // 读取期间刚好发生写操作，旧值不要覆盖新值
                if (versionBefore != versionAfter)
                    continue;

                var changed = false;

                lock (_sync)
                {
                    if (!_values.TryGetValue(
                            binding.Name,
                            out var oldValue) ||
                        !JsonEquals(oldValue, value))
                    {
                        _values[binding.Name] =
                            value?.DeepClone();

                        changed = true;
                    }
                }

                if (changed)
                {
                    RaiseValueChanged(
                        binding.Name,
                        value);
                }
            }
            catch
            {
            }
        }
    }

    private async Task<JsonNode?> ReadAsync(
        PlcVariableBinding binding,
        CancellationToken cancellationToken)
    {
        if (_plc is null)
            return null;

        var point =
            ParsePoint(
                binding.Address,
                binding.Type);

        switch (point.DataType)
        {
            case "BOOL":
                {
                    var value =
                        await _plc.ReadBoolAsync(
                            _slaveId,
                            point.Area,
                            point.Address,
                            point.Bit,
                            cancellationToken);

                    return JsonValue.Create(value);
                }

            case "I16":
                return JsonValue.Create(
                    (double)await _plc.ReadInt16Async(
                        _slaveId,
                        point.Area,
                        point.Address,
                        cancellationToken));

            case "U16":
                return JsonValue.Create(
                    (double)await _plc.ReadUInt16Async(
                        _slaveId,
                        point.Area,
                        point.Address,
                        cancellationToken));

            case "I32":
                return JsonValue.Create(
                    (double)await _plc.ReadInt32Async(
                        _slaveId,
                        point.Area,
                        point.Address,
                        _byteOrder,
                        cancellationToken));

            case "U32":
                return JsonValue.Create(
                    (double)await _plc.ReadUInt32Async(
                        _slaveId,
                        point.Area,
                        point.Address,
                        _byteOrder,
                        cancellationToken));

            case "F32":
                return JsonValue.Create(
                    (double)await _plc.ReadFloatAsync(
                        _slaveId,
                        point.Area,
                        point.Address,
                        _byteOrder,
                        cancellationToken));

            case "F64":
                return JsonValue.Create(
                    await _plc.ReadDoubleAsync(
                        _slaveId,
                        point.Area,
                        point.Address,
                        _byteOrder,
                        cancellationToken));

            case "STRING":
                return JsonValue.Create(
                    await _plc.ReadStringAsync(
                        _slaveId,
                        point.Area,
                        point.Address,
                        point.Length,
                        cancellationToken:
                            cancellationToken));

            default:
                throw new InvalidOperationException();
        }
    }

    private async Task WriteAsync(
        PlcVariableBinding binding,
        JsonNode? value,
        CancellationToken cancellationToken)
    {
        if (_plc is null)
            return;

        var point =
            ParsePoint(
                binding.Address,
                binding.Type);

        if (point.Area is
            ModbusArea.DiscreteInput or
            ModbusArea.InputRegister)
        {
            return;
        }

        if (point.DataType == "BOOL")
        {
            var boolValue = NodeBool(value);

            if (point.Area == ModbusArea.Coil)
            {
                await _plc.WriteBoolAsync(
                    _slaveId,
                    point.Address,
                    boolValue,
                    cancellationToken);

                return;
            }

            await _plc.WriteRegisterBitAsync(
                _slaveId,
                point.Address,
                point.Bit,
                boolValue,
                cancellationToken);

            return;
        }

        if (point.DataType == "STRING")
        {
            await _plc.WriteStringAsync(
                _slaveId,
                point.Address,
                NodeString(value),
                point.Length,
                cancellationToken:
                    cancellationToken);

            return;
        }

        var number = NodeNumber(value);

        switch (point.DataType)
        {
            case "I16":
                await _plc.WriteInt16Async(
                    _slaveId,
                    point.Address,
                    Convert.ToInt16(number),
                    cancellationToken);
                break;

            case "U16":
                await _plc.WriteUInt16Async(
                    _slaveId,
                    point.Address,
                    Convert.ToUInt16(number),
                    cancellationToken);
                break;

            case "I32":
                await _plc.WriteInt32Async(
                    _slaveId,
                    point.Address,
                    Convert.ToInt32(number),
                    _byteOrder,
                    cancellationToken);
                break;

            case "U32":
                await _plc.WriteUInt32Async(
                    _slaveId,
                    point.Address,
                    Convert.ToUInt32(number),
                    _byteOrder,
                    cancellationToken);
                break;

            case "F32":
                await _plc.WriteFloatAsync(
                    _slaveId,
                    point.Address,
                    Convert.ToSingle(number),
                    _byteOrder,
                    cancellationToken);
                break;

            case "F64":
                await _plc.WriteDoubleAsync(
                    _slaveId,
                    point.Address,
                    number,
                    _byteOrder,
                    cancellationToken);
                break;
        }
    }

    private void LoadBindingsFromConfig()
    {
        try
        {
            var key =
                _configuration["PLC:ConfigKey"]
                ?? "default";

            var path =
                Path.Combine(
                    _environment.ContentRootPath,
                    "StateMachineConfigs",
                    $"{key}.json");

            if (!File.Exists(path))
                return;

            var root =
                JsonNode.Parse(
                    File.ReadAllText(path))
                as JsonObject;

            var variables =
                root?["variables"] as JsonArray
                ?? root?["Variables"] as JsonArray;

            if (variables is null)
                return;

            var list =
                new List<PlcVariableBinding>();

            foreach (var node in variables)
            {
                if (node is not JsonObject obj)
                    continue;

                var name =
                    GetString(obj, "name", "Name");

                var address =
                    GetString(obj, "address", "Address");

                var type =
                    GetString(obj, "type", "Type");

                if (string.IsNullOrWhiteSpace(name) ||
                    string.IsNullOrWhiteSpace(address))
                {
                    continue;
                }

                list.Add(
                    new PlcVariableBinding(
                        name,
                        address,
                        string.IsNullOrWhiteSpace(type)
                            ? "number"
                            : type));
            }

            ReplaceBindings(list);
        }
        catch
        {
        }
    }

    private static string GetString(
        JsonObject obj,
        string camel,
        string pascal)
    {
        if (obj[camel] is JsonValue a &&
            a.TryGetValue<string>(out var av))
        {
            return av ?? string.Empty;
        }

        if (obj[pascal] is JsonValue b &&
            b.TryGetValue<string>(out var bv))
        {
            return bv ?? string.Empty;
        }

        return string.Empty;
    }

    private void RaiseValueChanged(
        string name,
        JsonNode? value)
    {
        var handlers = ValueChanged;

        if (handlers is null)
            return;

        foreach (var handler in
                 handlers.GetInvocationList()
                     .Cast<Action<string, JsonNode?>>())
        {
            try
            {
                handler(
                    name,
                    value?.DeepClone());
            }
            catch
            {
            }
        }
    }

    private static bool JsonEquals(
        JsonNode? a,
        JsonNode? b)
    {
        return string.Equals(
            a?.ToJsonString(),
            b?.ToJsonString(),
            StringComparison.Ordinal);
    }

    private static bool NodeBool(
        JsonNode? node)
    {
        if (node is not JsonValue value)
            return false;

        if (value.TryGetValue<bool>(out var b))
            return b;

        if (value.TryGetValue<double>(out var d))
            return Math.Abs(d) > double.Epsilon;

        if (value.TryGetValue<string>(out var s))
        {
            return s == "1" ||
                   string.Equals(
                       s,
                       "true",
                       StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static double NodeNumber(
        JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var d))
                return d;

            if (value.TryGetValue<long>(out var l))
                return l;

            if (value.TryGetValue<bool>(out var b))
                return b ? 1 : 0;

            if (value.TryGetValue<string>(out var s) &&
                double.TryParse(
                    s,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var result))
            {
                return result;
            }
        }

        return 0;
    }

    private static string NodeString(
        JsonNode? node)
    {
        if (node is JsonValue value &&
            value.TryGetValue<string>(out var text))
        {
            return text ?? string.Empty;
        }

        return node?.ToJsonString() ?? string.Empty;
    }

    private static PlcPoint ParsePoint(
        string address,
        string variableType)
    {
        var parts =
            address.Split(
                ':',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);

        if (parts.Length < 2)
            throw new FormatException(address);

        var area =
            parts[0].ToUpperInvariant() switch
            {
                "C" or "COIL" =>
                    ModbusArea.Coil,

                "DI" =>
                    ModbusArea.DiscreteInput,

                "HR" =>
                    ModbusArea.HoldingRegister,

                "IR" =>
                    ModbusArea.InputRegister,

                _ => throw new FormatException(address)
            };

        if (!ushort.TryParse(
                parts[1],
                out var registerAddress))
        {
            throw new FormatException(address);
        }

        var dataType =
            parts.Length >= 3
                ? parts[2].ToUpperInvariant()
                : area is
                    ModbusArea.Coil or
                    ModbusArea.DiscreteInput
                    ? "BOOL"
                    : variableType switch
                    {
                        "boolean" => "BOOL",
                        "string" => "STRING",
                        _ => "F32"
                    };

        dataType =
            dataType switch
            {
                "FLOAT" => "F32",
                "DOUBLE" => "F64",
                "INT" => "I32",
                "UINT" => "U32",
                "SHORT" => "I16",
                "USHORT" => "U16",
                "BOOLEAN" => "BOOL",
                _ => dataType
            };

        var bit = 0;
        ushort length = 16;

        if (dataType == "BOOL" &&
            parts.Length >= 4)
        {
            bit = int.Parse(parts[3]);
        }

        if (dataType == "STRING" &&
            parts.Length >= 4)
        {
            length = ushort.Parse(parts[3]);
        }

        return new PlcPoint(
            area,
            registerAddress,
            dataType,
            bit,
            length);
    }

    private sealed record PlcPoint(
        ModbusArea Area,
        ushort Address,
        string DataType,
        int Bit,
        ushort Length);

    private sealed record WriteRequest(
        PlcVariableBinding Binding,
        JsonNode? Value,
        long Version);
}