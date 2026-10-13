using SysBot.Base;
using System;

namespace SysBot.Pokemon.WinForms.WebApi;

public static class BotStatus
{
    public static string Read(BotSource<PokeBotState> source)
    {
        try
        {
            var bot = source.Bot;
            if (!source.IsRunning)
                return "STOPPED";
            if (source.IsStopping)
                return "STOPPING";
            if (source.IsPaused)
                return "IDLE";
            if (!bot.Connection.Connected)
                return "REBOOTING";

            var cfg = bot.Config;
            if (cfg.InitialRoutine == PokeRoutineType.RotatingRaidBot)
                return nameof(PokeRoutineType.RotatingRaidBot);
            if (cfg.CurrentRoutineType != PokeRoutineType.Idle)
                return cfg.CurrentRoutineType.ToString();
            if (cfg.NextRoutineType != PokeRoutineType.Idle)
                return cfg.NextRoutineType.ToString();
            return cfg.InitialRoutine.ToString();
        }
        catch (Exception ex)
        {
            LogUtil.LogError($"Error reading bot state: {ex.Message}", "BotStatus");
            return "ERROR";
        }
    }
}
