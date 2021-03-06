﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2017 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Localization;
using NLog;
using NLog.Targets;
using SteamKit2;

namespace ArchiSteamFarm {
	internal static class Program {
		internal static byte LoadBalancingDelay {
			get {
				byte result = GlobalConfig?.LoginLimiterDelay ?? GlobalConfig.DefaultLoginLimiterDelay;
				return result < GlobalConfig.DefaultLoginLimiterDelay ? GlobalConfig.DefaultLoginLimiterDelay : result;
			}
		}

		internal static GlobalConfig GlobalConfig { get; private set; }
		internal static GlobalDatabase GlobalDatabase { get; private set; }
		internal static WebBrowser WebBrowser { get; private set; }

		private static readonly object ConsoleLock = new object();
		private static readonly ManualResetEventSlim ShutdownResetEvent = new ManualResetEventSlim(false);

		private static bool ShutdownSequenceInitialized;

		internal static async Task Exit(byte exitCode = 0) {
			if (exitCode != 0) {
				ASF.ArchiLogger.LogGenericError(Strings.ErrorExitingWithNonZeroErrorCode);
			}

			await Shutdown().ConfigureAwait(false);
			Environment.Exit(exitCode);
		}

		internal static string GetUserInput(ASF.EUserInputType userInputType, string botName = SharedInfo.ASF) {
			if (userInputType == ASF.EUserInputType.Unknown) {
				return null;
			}

			if (GlobalConfig.Headless) {
				ASF.ArchiLogger.LogGenericWarning(Strings.ErrorUserInputRunningInHeadlessMode);
				return null;
			}

			string result;
			lock (ConsoleLock) {
				Logging.OnUserInputStart();
				switch (userInputType) {
					case ASF.EUserInputType.DeviceID:
						Console.Write(Bot.FormatBotResponse(Strings.UserInputDeviceID, botName));
						break;
					case ASF.EUserInputType.IPCHostname:
						Console.Write(Bot.FormatBotResponse(Strings.UserInputIPCHost, botName));
						break;
					case ASF.EUserInputType.Login:
						Console.Write(Bot.FormatBotResponse(Strings.UserInputSteamLogin, botName));
						break;
					case ASF.EUserInputType.Password:
						Console.Write(Bot.FormatBotResponse(Strings.UserInputSteamPassword, botName));
						break;
					case ASF.EUserInputType.SteamGuard:
						Console.Write(Bot.FormatBotResponse(Strings.UserInputSteamGuard, botName));
						break;
					case ASF.EUserInputType.SteamParentalPIN:
						Console.Write(Bot.FormatBotResponse(Strings.UserInputSteamParentalPIN, botName));
						break;
					case ASF.EUserInputType.TwoFactorAuthentication:
						Console.Write(Bot.FormatBotResponse(Strings.UserInputSteam2FA, botName));
						break;
					default:
						ASF.ArchiLogger.LogGenericWarning(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(userInputType), userInputType));
						Console.Write(Bot.FormatBotResponse(string.Format(Strings.UserInputUnknown, userInputType), botName));
						break;
				}

				result = Console.ReadLine();

				if (!Console.IsOutputRedirected) {
					Console.Clear(); // For security purposes
				}

				Logging.OnUserInputEnd();
			}

			return !string.IsNullOrEmpty(result) ? result.Trim() : null;
		}

		internal static async Task Restart() {
			if (!await InitShutdownSequence().ConfigureAwait(false)) {
				return;
			}

			string executable = Process.GetCurrentProcess().MainModule.FileName;
			string executableName = Path.GetFileNameWithoutExtension(executable);

			IEnumerable<string> arguments = Environment.GetCommandLineArgs().Skip(executableName.Equals(SharedInfo.AssemblyName) ? 1 : 0);

			try {
				Process.Start(executable, string.Join(" ", arguments));
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}

			// Give new process some time to take over the window (if needed)
			await Task.Delay(2000).ConfigureAwait(false);

			ShutdownResetEvent.Set();
			Environment.Exit(0);
		}

		private static async Task Init(string[] args) {
			AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionHandler;
			TaskScheduler.UnobservedTaskException += UnobservedTaskExceptionHandler;

			// We must register our logging target as soon as possible
			Target.Register<SteamTarget>(SteamTarget.TargetName);

			InitCore(args);
			await InitASF(args).ConfigureAwait(false);
		}

		private static async Task InitASF(string[] args) {
			ASF.ArchiLogger.LogGenericInfo("ASF V" + SharedInfo.Version);

			await InitGlobalConfigAndLanguage().ConfigureAwait(false);
			await InitGlobalDatabaseAndServices().ConfigureAwait(false);

			// If debugging is on, we prepare debug directory prior to running
			if (GlobalConfig.Debug) {
				Logging.EnableTraceLogging();

				if (Directory.Exists(SharedInfo.DebugDirectory)) {
					try {
						Directory.Delete(SharedInfo.DebugDirectory, true);
						await Task.Delay(1000).ConfigureAwait(false); // Dirty workaround giving Windows some time to sync
					} catch (IOException e) {
						ASF.ArchiLogger.LogGenericException(e);
					}
				}

				Directory.CreateDirectory(SharedInfo.DebugDirectory);

				DebugLog.AddListener(new Debugging.DebugListener());
				DebugLog.Enabled = true;
			}

			// Parse post-init args
			if (args != null) {
				ParsePostInitArgs(args);
			}

			if (!Debugging.IsDebugBuild) {
				await ASF.CheckForUpdate().ConfigureAwait(false);
			}

			await ASF.InitBots().ConfigureAwait(false);
			ASF.InitEvents();
		}

		private static void InitCore(string[] args) {
			string homeDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
			if (!string.IsNullOrEmpty(homeDirectory)) {
				Directory.SetCurrentDirectory(homeDirectory);

				// Allow loading configs from source tree if it's a debug build
				if (Debugging.IsDebugBuild) {
					// Common structure is bin/(x64/)Debug/ArchiSteamFarm.exe, so we allow up to 4 directories up
					for (byte i = 0; i < 4; i++) {
						Directory.SetCurrentDirectory("..");
						if (Directory.Exists(SharedInfo.ConfigDirectory)) {
							break;
						}
					}

					// If config directory doesn't exist after our adjustment, abort all of that
					if (!Directory.Exists(SharedInfo.ConfigDirectory)) {
						Directory.SetCurrentDirectory(homeDirectory);
					}
				}
			}

			// Parse pre-init args
			if (args != null) {
				ParsePreInitArgs(args);
			}

			Logging.InitLoggers();
		}

		private static async Task InitGlobalConfigAndLanguage() {
			string globalConfigFile = Path.Combine(SharedInfo.ConfigDirectory, SharedInfo.GlobalConfigFileName);

			GlobalConfig = GlobalConfig.Load(globalConfigFile);
			if (GlobalConfig == null) {
				ASF.ArchiLogger.LogGenericError(string.Format(Strings.ErrorGlobalConfigNotLoaded, globalConfigFile));
				await Task.Delay(5 * 1000).ConfigureAwait(false);
				await Exit(1).ConfigureAwait(false);
				return;
			}

			if (GCSettings.IsServerGC) {
				Hacks.Init();
			}

			if (!string.IsNullOrEmpty(GlobalConfig.CurrentCulture)) {
				try {
					// GetCultureInfo() would be better but we can't use it for specifying neutral cultures such as "en"
					CultureInfo culture = CultureInfo.CreateSpecificCulture(GlobalConfig.CurrentCulture);
					CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
				} catch (CultureNotFoundException) {
					ASF.ArchiLogger.LogGenericError(Strings.ErrorInvalidCurrentCulture);
				}
			}

			if (CultureInfo.CurrentCulture.TwoLetterISOLanguageName.Equals("en")) {
				return;
			}

			ResourceSet defaultResourceSet = Strings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo("en-US"), true, true);
			if (defaultResourceSet == null) {
				ASF.ArchiLogger.LogNullError(nameof(defaultResourceSet));
				return;
			}

			HashSet<DictionaryEntry> defaultStringObjects = new HashSet<DictionaryEntry>(defaultResourceSet.Cast<DictionaryEntry>());
			if (defaultStringObjects.Count == 0) {
				ASF.ArchiLogger.LogNullError(nameof(defaultStringObjects));
				return;
			}

			ResourceSet currentResourceSet = Strings.ResourceManager.GetResourceSet(CultureInfo.CurrentCulture, true, true);
			if (currentResourceSet == null) {
				ASF.ArchiLogger.LogNullError(nameof(currentResourceSet));
				return;
			}

			HashSet<DictionaryEntry> currentStringObjects = new HashSet<DictionaryEntry>(currentResourceSet.Cast<DictionaryEntry>());
			if (currentStringObjects.Count >= defaultStringObjects.Count) {
				// Either we have 100% finished translation, or we're missing it entirely and using en-US
				HashSet<DictionaryEntry> testStringObjects = new HashSet<DictionaryEntry>(currentStringObjects);
				testStringObjects.ExceptWith(defaultStringObjects);

				// If we got 0 as final result, this is the missing language
				// Otherwise it's just a small amount of strings that happen to be the same
				if (testStringObjects.Count == 0) {
					currentStringObjects = testStringObjects;
				}
			}

			if (currentStringObjects.Count < defaultStringObjects.Count) {
				float translationCompleteness = currentStringObjects.Count / (float) defaultStringObjects.Count;
				ASF.ArchiLogger.LogGenericInfo(string.Format(Strings.TranslationIncomplete, CultureInfo.CurrentCulture.Name, translationCompleteness.ToString("P1")));
			}
		}

		private static async Task InitGlobalDatabaseAndServices() {
			string globalDatabaseFile = Path.Combine(SharedInfo.ConfigDirectory, SharedInfo.GlobalDatabaseFileName);

			if (!File.Exists(globalDatabaseFile)) {
				ASF.ArchiLogger.LogGenericInfo(Strings.Welcome);
				ASF.ArchiLogger.LogGenericWarning(Strings.WarningPrivacyPolicy);
				await Task.Delay(15 * 1000).ConfigureAwait(false);
			}

			GlobalDatabase = GlobalDatabase.Load(globalDatabaseFile);
			if (GlobalDatabase == null) {
				ASF.ArchiLogger.LogGenericError(string.Format(Strings.ErrorDatabaseInvalid, globalDatabaseFile));
				await Task.Delay(5 * 1000).ConfigureAwait(false);
				await Exit(1).ConfigureAwait(false);
				return;
			}

			ArchiWebHandler.Init();
			IPC.Initialize(GlobalConfig.IPCHost, GlobalConfig.IPCPort);
			OS.Init(GlobalConfig.Headless);
			WebBrowser.Init();

			WebBrowser = new WebBrowser(ASF.ArchiLogger, true);
		}

		private static async Task<bool> InitShutdownSequence() {
			if (ShutdownSequenceInitialized) {
				return false;
			}

			ShutdownSequenceInitialized = true;

			IPC.Stop();

			if (Bot.Bots.Count == 0) {
				return true;
			}

			IEnumerable<Task> tasks = Bot.Bots.Values.Select(bot => Task.Run(() => bot.Stop(false)));

			switch (GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					foreach (Task task in tasks) {
						await Task.WhenAny(task, Task.Delay(WebBrowser.MaxRetries * 1000)).ConfigureAwait(false);
					}

					break;
				default:
					await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(Bot.Bots.Count * WebBrowser.MaxRetries * 1000)).ConfigureAwait(false);
					break;
			}

			LogManager.Flush();
			return true;
		}

		private static void Main(string[] args) {
			Init(args).Wait();

			// Wait for signal to shutdown
			ShutdownResetEvent.Wait();

			// We got a signal to shutdown
			Exit().Wait();
		}

		private static void ParsePostInitArgs(IEnumerable<string> args) {
			if (args == null) {
				ASF.ArchiLogger.LogNullError(nameof(args));
				return;
			}

			foreach (string arg in args) {
				switch (arg) {
					case "":
						break;
					case "--server":
						IPC.Start();
						break;
					default:
						if (arg.StartsWith("--", StringComparison.Ordinal)) {
							if (arg.StartsWith("--cryptkey=", StringComparison.Ordinal) && (arg.Length > 11)) {
								CryptoHelper.SetEncryptionKey(arg.Substring(11));
							}
						}

						break;
				}
			}
		}

		private static void ParsePreInitArgs(IEnumerable<string> args) {
			if (args == null) {
				ASF.ArchiLogger.LogNullError(nameof(args));
				return;
			}

			foreach (string arg in args) {
				switch (arg) {
					case "":
						break;
					default:
						if (arg.StartsWith("--", StringComparison.Ordinal)) {
							if (arg.StartsWith("--path=", StringComparison.Ordinal) && (arg.Length > 7)) {
								Directory.SetCurrentDirectory(arg.Substring(7));
							}
						}

						break;
				}
			}
		}

		private static async Task Shutdown() {
			if (!await InitShutdownSequence().ConfigureAwait(false)) {
				return;
			}

			ShutdownResetEvent.Set();
		}

		private static async void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs e) {
			if (e?.ExceptionObject == null) {
				ASF.ArchiLogger.LogNullError(nameof(e) + " || " + nameof(e.ExceptionObject));
				return;
			}

			ASF.ArchiLogger.LogFatalException((Exception) e.ExceptionObject);
			await Exit(1).ConfigureAwait(false);
		}

		private static void UnobservedTaskExceptionHandler(object sender, UnobservedTaskExceptionEventArgs e) {
			if (e?.Exception == null) {
				ASF.ArchiLogger.LogNullError(nameof(e) + " || " + nameof(e.Exception));
				return;
			}

			ASF.ArchiLogger.LogFatalException(e.Exception);

			// Normally we should abort the application here, but many tasks are in fact failing in SK2 code which we can't easily fix
			// Thanks Valve.
			e.SetObserved();
		}
	}
}