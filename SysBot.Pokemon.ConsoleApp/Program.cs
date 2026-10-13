using PKHeX.Core;
using SysBot.Base;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace SysBot.Pokemon.ConsoleApp
{
    public static class Program
    {
        private const string ConfigPath = "config.json";

        private static void Main(string[] args)
        {
            Console.WriteLine("Starting up...");
            if (args.Length > 0)
                Console.WriteLine("This program does not support command line arguments.");

            if (!File.Exists(ConfigPath))
            {
                ExitNoConfig();
                return;
            }

            try
            {
                var lines = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize(lines, ProgramConfigContext.Default.ProgramConfig) ?? new ProgramConfig();
                BotContainer.RunBots(cfg);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unable to start bots with saved config file: {ex.Message}");
                Console.WriteLine("Please copy your config from the WinForms project or delete it and reconfigure.");
                WaitForKey();
            }
        }

        // ReadKey throws when input is redirected (a service, a scheduled task, Docker).
        public static void WaitForKey()
        {
            if (!Console.IsInputRedirected)
                Console.ReadKey(true);
        }

        private static void ExitNoConfig()
        {
            var bot = new PokeBotState { Connection = new SwitchConnectionConfig { IP = "192.168.0.1", Port = 6000 }, InitialRoutine = PokeRoutineType.RotatingRaidBot };
            var cfg = new ProgramConfig { Bots = new[] { bot } };
            var created = JsonSerializer.Serialize(cfg, ProgramConfigContext.Default.ProgramConfig);
            File.WriteAllText(ConfigPath, created);
            Console.WriteLine("Created new config file since none was found in the program's path. Please configure it and restart the program.");
            Console.WriteLine("It is suggested to configure this config file using the GUI project if possible, as it will help you assign values correctly.");
            Console.WriteLine("Press any key to exit.");
            WaitForKey();
        }
    }

    [JsonSerializable(typeof(ProgramConfig))]
    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    public sealed partial class ProgramConfigContext : JsonSerializerContext
    { }

    public static class BotContainer
    {
        public static void RunBots(ProgramConfig prog)
        {
            IPokeBotRunner env = GetRunner(prog);
            foreach (var bot in prog.Bots.Take(1))
            {
                bot.Initialize();
                if (!AddBot(env, bot, prog.Mode))
                    Console.WriteLine($"Failed to add bot: {bot}");
            }
            if (prog.Bots.Length > 1)
            {
                Console.WriteLine(PokeBotRunner<PK9>.OneSwitchPerProgram);
                foreach (var bot in prog.Bots.Skip(1))
                    Console.WriteLine($"Not hosting: {bot.Connection}");
            }

            LogUtil.Forwarders.Add((msg, ident) => Console.WriteLine($"{ident}: {msg}"));
            env.StartAll();
            Console.WriteLine($"Started all bots (Count: {env.Bots.Count}).");
            WaitForQuit();

            Console.WriteLine("Stopping...");
            env.StopAll();
            // A bot can take up to two minutes to let go of the Switch; quitting sooner
            // can leave the controller attached and the screen off.
            var deadline = DateTime.Now + StopWait;
            while (env.IsRunning && DateTime.Now < deadline)
                Thread.Sleep(250);
        }

        private static readonly TimeSpan StopWait = TimeSpan.FromMinutes(2.5);

        private static readonly ManualResetEventSlim QuitRequested = new();

        private static void WaitForQuit()
        {
            // The first Ctrl+C stops the bots cleanly; a second one quits on the spot.
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = !QuitRequested.IsSet;
                QuitRequested.Set();
            };

            if (Console.IsInputRedirected)
            {
                Console.WriteLine("Press Ctrl+C to stop execution and quit.");
                QuitRequested.Wait();
                return;
            }

            Console.WriteLine("Press any key (or Ctrl+C) to stop execution and quit. Feel free to minimize this window!");
            while (!QuitRequested.Wait(100))
            {
                if (Console.KeyAvailable)
                {
                    Console.ReadKey(true);
                    QuitRequested.Set();
                    return;
                }
            }
        }

        private static IPokeBotRunner GetRunner(ProgramConfig prog) => prog.Mode switch
        {
            ProgramMode.SV => new PokeBotRunnerImpl<PK9>(prog.Hub, new BotFactory9SV()),
            _ => throw new IndexOutOfRangeException("Unsupported mode."),
        };

        private static bool AddBot(IPokeBotRunner env, PokeBotState cfg, ProgramMode mode)
        {
            if (!cfg.IsValid())
            {
                Console.WriteLine($"{cfg}'s config is not valid.");
                return false;
            }

            PokeRoutineExecutorBase newBot;
            try
            {
                newBot = env.CreateBotFromConfig(cfg);
            }
            catch
            {
                Console.WriteLine($"Current Mode ({mode}) does not support this type of bot ({cfg.CurrentRoutineType}).");
                return false;
            }
            try
            {
                env.Add(newBot);
            }
            catch (ArgumentException ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }

            Console.WriteLine($"Added: {cfg}: {cfg.InitialRoutine}");
            return true;
        }
    }
}