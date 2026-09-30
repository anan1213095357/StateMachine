using System;
using System.Threading.Tasks;//  大概就是这种 开发模式  

public sealed class UserActions : StateScript
{
    [StateAction("数值变量增加", "C# 热更")]
    public void AddNumber(
        [StateParameter("变量Key")] string variableName,
        [StateParameter("增加值")] double value)
    {
        var current = Vars.Get<double>(variableName, 0d);
        Vars.Set(variableName, current + value);
    }

    [StateAction("数值变量减少", "C# 热更")]
    public void SubtractNumber(
        [StateParameter("变量Key")] string variableName,
        [StateParameter("减少值")] double value)
    {
        var current = Vars.Get<double>(variableName, 0d);
        Vars.Set(variableName, current - value);
    }

    [StateAction("布尔变量取反", "C# 热更")]
    public void ToggleBoolean(
        [StateParameter("变量Key")] string variableName)
    {
        var current = Vars.Get<bool>(variableName, false);
        Vars.Set(variableName, !current);
    }

    [StateAction("设置任意变量", "C# 热更")]
    public void SetVariable(
        [StateParameter("变量Key")] string variableName,
        [StateParameter("值")] JsonNode? value)
    {
        Vars.Set(variableName, value);
    }

    [StateAction("延时后设置布尔变量", "C# 热更")]
    public async Task DelaySetBoolean(
        [StateParameter("延时毫秒")] int milliseconds,
        [StateParameter("变量Key")] string variableName,
        [StateParameter("值")] bool value)
    {
        await Api.Delay(milliseconds);

        Vars.Set(variableName, value);

        Log($"{variableName} = {value}");
    }

    [StateAction("设置布尔变量", "C# 热更")]
    public void SetBoolean(
        [StateParameter("变量Key")] string variableName,
        [StateParameter("值")] bool value)
    {
        Vars.Set(variableName, value);
    }

    [StateAction("设置数值变量", "C# 热更")]
    public void SetNumber(
        [StateParameter("变量Key")] string variableName,
        [StateParameter("值")] double value)
    {
        Vars.Set(variableName, value);
    }

    [StateAction("设置字符串变量", "C# 热更")]
    public void SetString(
        [StateParameter("变量Key")] string variableName,
        [StateParameter("值")] string value)
    {
        Vars.Set(variableName, value);
    }

    [StateAction("调试输出", "C# 热更")]
    public void Print(
        [StateParameter("内容")] string message)
    {
        Console.WriteLine(message);
        Log(message);
    }
}