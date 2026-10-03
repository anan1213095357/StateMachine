using System;
using System.Threading.Tasks;

public sealed class WashingMachineActions : StateScript
{
    [StateAction("根据衣物自动设定洗涤参数", "洗衣机")]
    public void AutoSetupWashParameters(
        [StateParameter("衣物重量(千克)")] double loadWeight,
        [StateParameter("衣物类型")] string loadType,
        [StateParameter("目标水位变量Key")] string targetWaterLevelKey,
        [StateParameter("目标温度变量Key")] string targetTemperatureKey,
        [StateParameter("洗涤时间变量Key")] string washDurationKey,
        [StateParameter("漂洗次数变量Key")] string rinseTimesKey,
        [StateParameter("脱水转速变量Key")] string spinSpeedKey)
    {
        double waterLevel;
        double targetTemp;
        double washDuration;
        int rinseTimes;
        double spinSpeed;

        switch (loadType)
        {
            case "Cotton":
                waterLevel = 45 + loadWeight * 8;
                targetTemp = 60;
                washDuration = 30;
                rinseTimes = 3;
                spinSpeed = 1200;
                break;
            case "Wool":
                waterLevel = 35 + loadWeight * 5;
                targetTemp = 30;
                washDuration = 15;
                rinseTimes = 2;
                spinSpeed = 400;
                break;
            case "Quick":
                waterLevel = 30 + loadWeight * 4;
                targetTemp = 25;
                washDuration = 10;
                rinseTimes = 1;
                spinSpeed = 1000;
                break;
            default:
                waterLevel = 40 + loadWeight * 6;
                targetTemp = 40;
                washDuration = 20;
                rinseTimes = 2;
                spinSpeed = 800;
                break;
        }

        waterLevel = Math.Clamp(waterLevel, 25, 100);
        washDuration = Math.Clamp(washDuration, 5, 60);
        spinSpeed = Math.Clamp(spinSpeed, 300, 1600);
        if (rinseTimes < 1) rinseTimes = 1;
        if (rinseTimes > 5) rinseTimes = 5;

        Vars.Set(targetWaterLevelKey, waterLevel);
        Vars.Set(targetTemperatureKey, targetTemp);
        Vars.Set(washDurationKey, washDuration);
        Vars.Set(rinseTimesKey, (double)rinseTimes);
        Vars.Set(spinSpeedKey, spinSpeed);
    }

    [StateAction("启动进水", "洗衣机")]
    public void StartFill(
        [StateParameter("进水阀变量Key")] string inletKey,
        [StateParameter("排水阀变量Key")] string drainKey,
        [StateParameter("水位变量Key")] string levelKey)
    {
        Vars.Set(inletKey, true);
        Vars.Set(drainKey, false);
        Vars.Set(levelKey, 0d);
    }

    [StateAction("停止进水", "洗衣机")]
    public void StopInlet(
        [StateParameter("进水阀变量Key")] string inletKey)
    {
        Vars.Set(inletKey, false);
    }

    [StateAction("启动排水", "洗衣机")]
    public void StartDrain(
        [StateParameter("排水阀变量Key")] string drainKey,
        [StateParameter("进水阀变量Key")] string inletKey,
        [StateParameter("电机变量Key")] string motorKey)
    {
        Vars.Set(drainKey, true);
        Vars.Set(inletKey, false);
        Vars.Set(motorKey, false);
    }

    [StateAction("停止排水", "洗衣机")]
    public void StopDrain(
        [StateParameter("排水阀变量Key")] string drainKey)
    {
        Vars.Set(drainKey, false);
    }

    [StateAction("启动电机", "洗衣机")]
    public void StartMotor(
        [StateParameter("电机变量Key")] string motorKey)
    {
        Vars.Set(motorKey, true);
    }

    [StateAction("停止电机", "洗衣机")]
    public void StopMotor(
        [StateParameter("电机变量Key")] string motorKey)
    {
        Vars.Set(motorKey, false);
    }

    [StateAction("增加漂洗计数", "洗衣机")]
    public void IncrementRinseCount(
        [StateParameter("计数器变量Key")] string countKey)
    {
        var current = Vars.Get<double>(countKey, 0d);
        Vars.Set(countKey, current + 1d);
    }

    [StateAction("启动脱水", "洗衣机")]
    public void StartSpin(
        [StateParameter("电机变量Key")] string motorKey,
        [StateParameter("排水阀变量Key")] string drainKey,
        [StateParameter("进水阀变量Key")] string inletKey,
        [StateParameter("计时变量Key")] string timerKey)
    {
        Vars.Set(motorKey, true);
        Vars.Set(drainKey, true);
        Vars.Set(inletKey, false);
        Vars.Set(timerKey, 0d);
    }

    [StateAction("停止脱水", "洗衣机")]
    public void StopSpin(
        [StateParameter("电机变量Key")] string motorKey,
        [StateParameter("排水阀变量Key")] string drainKey)
    {
        Vars.Set(motorKey, false);
        Vars.Set(drainKey, false);
    }

    [StateAction("设置运行阶段", "洗衣机")]
    public void SetMachinePhase(
        [StateParameter("阶段变量Key")] string phaseKey,
        [StateParameter("阶段名称")] string phaseName)
    {
        Vars.Set(phaseKey, phaseName);
    }

    [StateAction("请求上锁", "洗衣机")]
    public void RequestLock(
        [StateParameter("上锁请求变量Key")] string lockRequestKey,
        [StateParameter("解锁请求变量Key")] string unlockRequestKey)
    {
        Vars.Set(lockRequestKey, true);
        Vars.Set(unlockRequestKey, false);
    }

    [StateAction("请求解锁", "洗衣机")]
    public void RequestUnlock(
        [StateParameter("解锁请求变量Key")] string unlockRequestKey,
        [StateParameter("上锁请求变量Key")] string lockRequestKey)
    {
        Vars.Set(unlockRequestKey, true);
        Vars.Set(lockRequestKey, false);
    }

    [StateAction("紧急停止所有执行器", "洗衣机")]
    public void EmergencyStopAll(
        [StateParameter("电机变量Key")] string motorKey,
        [StateParameter("进水阀变量Key")] string inletKey,
        [StateParameter("排水阀变量Key")] string drainKey,
        [StateParameter("加热器变量Key")] string heaterKey)
    {
        Vars.Set(motorKey, false);
        Vars.Set(inletKey, false);
        Vars.Set(drainKey, false);
        Vars.Set(heaterKey, false);
    }

    [StateAction("完成洗涤并解锁", "洗衣机")]
    public void FinishAndUnlock(
        [StateParameter("阶段变量Key")] string phaseKey,
        [StateParameter("解锁请求变量Key")] string unlockRequestKey,
        [StateParameter("上锁请求变量Key")] string lockRequestKey)
    {
        Vars.Set(phaseKey, "Done");
        Vars.Set(unlockRequestKey, true);
        Vars.Set(lockRequestKey, false);
    }
}