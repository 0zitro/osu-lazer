// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using osu.Desktop.Performance;
using osu.Desktop.Security;
using osu.Framework.Platform;
using osu.Game;
using osu.Desktop.Updater;
using osu.Framework;
using osu.Framework.Logging;
using osu.Game.Updater;
using osu.Desktop.Windows;
using osu.Framework.Allocation;
using osu.Game.Configuration;
using osu.Game.IO;
using osu.Game.IPC;
using osu.Game.Performance;
using osu.Game.Utils;

namespace osu.Desktop
{
    internal partial class OsuGameDesktop : OsuGame
    {
        private OsuSchemeLinkIPCChannel? osuSchemeLinkIPCChannel;
        private ArchiveImportIPCChannel? archiveImportIPCChannel;

        [Cached(typeof(IHighPerformanceSessionManager))]
        private readonly HighPerformanceSessionManager highPerformanceSessionManager = new HighPerformanceSessionManager();

        public bool IsFirstRun { get; init; }

        public OsuGameDesktop(string[]? args = null)
            : base(args)
        {
        }

        public override StableStorage? GetStorageForStableInstall()
        {
            try
            {
                if (Host is DesktopGameHost desktopHost)
                {
                    string? stablePath = getStableInstallPath();
                    if (!string.IsNullOrEmpty(stablePath))
                        return new StableStorage(stablePath, desktopHost);
                }
            }
            catch (Exception)
            {
                Logger.Log("Could not find a stable install", LoggingTarget.Runtime, LogLevel.Important);
            }

            return null;
        }

        private string? getStableInstallPath()
        {
            static bool checkExists(string p) => Directory.Exists(Path.Combine(p, "Songs")) || File.Exists(Path.Combine(p, "osu!.cfg"));

            string? stableInstallPath;

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    stableInstallPath = getStableInstallPathFromRegistry("osustable.File.osz");

                    if (!string.IsNullOrEmpty(stableInstallPath) && checkExists(stableInstallPath))
                        return stableInstallPath;

                    stableInstallPath = getStableInstallPathFromRegistry("osu!");

                    if (!string.IsNullOrEmpty(stableInstallPath) && checkExists(stableInstallPath))
                        return stableInstallPath;
                }
                catch
                {
                }
            }

            stableInstallPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"osu!");
            if (checkExists(stableInstallPath))
                return stableInstallPath;

            stableInstallPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".osu");
            if (checkExists(stableInstallPath))
                return stableInstallPath;

            return null;
        }

        [SupportedOSPlatform("windows")]
        private string? getStableInstallPathFromRegistry(string progId)
        {
            using (RegistryKey? key = Registry.ClassesRoot.OpenSubKey(progId))
                return key?.OpenSubKey(WindowsAssociationManager.SHELL_OPEN_COMMAND)?.GetValue(string.Empty)?.ToString()?.Split('"')[1].Replace("osu!.exe", "");
        }

        public static bool IsPackageManaged => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OSU_EXTERNAL_UPDATE_PROVIDER"));

        private static void restartPackageManagedProcessWhenCurrentExits()
        {
            string? executable = Environment.ProcessPath;

            if (string.IsNullOrEmpty(executable))
                throw new InvalidOperationException("Could not determine current process path for restart.");

            string[] args = Environment.GetCommandLineArgs();

            // Find and remove the restart marker argument and its value
            int restartMarkerIndex = Array.IndexOf(args, Program.restart_after_pid_argument);

            if (restartMarkerIndex >= 0)
            {
                var filteredArgs = new List<string>(args.Length);

                for (int i = 0; i < args.Length; i++)
                {
                    if (i == restartMarkerIndex)
                    {
                        i++;
                        continue;
                    }

                    filteredArgs.Add(args[i]);
                }

                args = filteredArgs.ToArray();
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory,
            };

            if (args.Length > 0)
            {
                string firstArg = Path.GetFullPath(args[0]);
                string processPath = Path.GetFullPath(executable);

                if (!string.Equals(firstArg, processPath, StringComparison.Ordinal))
                    startInfo.ArgumentList.Add(args[0]);

                for (int i = 1; i < args.Length; i++)
                    startInfo.ArgumentList.Add(args[i]);
            }

            startInfo.ArgumentList.Add(Program.restart_after_pid_argument);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

            Process.Start(startInfo);
        }

        protected override UpdateManager CreateUpdateManager()
        {
            // If this is the first time we've run the game, ie it is being installed,
            // reset the user's release stream to "lazer".
            //
            // This ensures that if a user is trying to recover from a failed startup on an unstable release stream,
            // the game doesn't immediately try and update them back to the release stream after starting up.
            if (IsFirstRun)
                LocalConfig.SetValue(OsuSetting.ReleaseStream, ReleaseStream.Lazer);

            if (IsPackageManaged)
                return new NoActionUpdateManager();

            return new VelopackUpdateManager();
        }

        public override bool RestartAppWhenExited()
        {
            RestartOnExitAction = () =>
            {
                if (IsPackageManaged)
                {
                    try
                    {
                        restartPackageManagedProcessWhenCurrentExits();
                    }
                    catch (Exception e)
                    {
                        Logger.Log($"Failed to restart package-managed installation: {e}", LoggingTarget.Runtime, LogLevel.Important);
                    }
                    return;
                }
                Velopack.UpdateExe.Start(waitPid: (uint)Environment.ProcessId);
            };
            return true;
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            LoadComponentAsync(new DiscordRichPresence(), Add);

            if (RuntimeInfo.OS == RuntimeInfo.Platform.Windows)
                LoadComponentAsync(new GameplayWinKeyBlocker(), Add);

            LoadComponentAsync(new ElevatedPrivilegesChecker(), Add);

            osuSchemeLinkIPCChannel = new OsuSchemeLinkIPCChannel(Host, this);
            archiveImportIPCChannel = new ArchiveImportIPCChannel(Host, this);
        }

        public override void SetHost(GameHost host)
        {
            base.SetHost(host);

            // Apple operating systems use a better icon provided via external assets.
            if (!RuntimeInfo.IsApple)
            {
                var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(GetType(), "lazer.ico");
                if (iconStream != null)
                    host.Window.SetIconFromStream(iconStream);
            }

            host.Window.Title = Name;
        }

        protected override BatteryInfo CreateBatteryInfo() => FrameworkEnvironment.UseSDL3 ? new SDL3BatteryInfo() : new SDL2BatteryInfo();

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            osuSchemeLinkIPCChannel?.Dispose();
            archiveImportIPCChannel?.Dispose();
        }
    }
}
