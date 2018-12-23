using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using Tools.DotNETCommon;

namespace UnrealBuildTool
{
	class FASTBuild : ActionExecutor
	{
		static string m_strExePath = "";
		static WindowsCompiler ms_eWindowsCompiler = WindowsCompiler.Default;
		static DirectoryReference ms_dirVSInstall;
		static DirectoryReference ms_dirVCInstall;

		VCEnvironment m_vcEnv = null;

		// allow distribution or not.
		private bool m_bEnableDistribution = true;
		// only enable distributed compilation when jobs number > logical cpu cores x 2.
		private int iCompileActionNumbers = 0;

		// Module.ProxyLODMeshReduction.cpp which can't be done with pre-process, openvdb was built with /GL.
		private HashSet<string> ForceLocalCompileModules = new HashSet<string>() { "Module.ProxyLODMeshReduction" };

		private enum FBBuildType
		{
			Windows,
			XBOne,
			PS4
		}

		private FBBuildType m_eBuildType = FBBuildType.Windows;

		struct _SDKsInstalled
		{
			public bool DXSDK;
			public bool DurangoXDK;
			public bool SCE_ORBIS_SDK;
		}

		private _SDKsInstalled SDKsInstalled;

		public override string Name
		{
			get { return "FASTBuild"; }
		}

		public static bool IsAvailable()
		{
			bool bAvailable = true;
			string enableUECode = Environment.GetEnvironmentVariable("FASTBuild_UE_Code_Compilation_Support");
			if (!string.IsNullOrEmpty(enableUECode))
				bAvailable = enableUECode != "0";
			if (!bAvailable)
				return false;

			String[] arguments = Environment.GetCommandLineArgs();
			foreach (string Arg in arguments)
			{
				string LowercaseArg = Arg.ToLowerInvariant();
				if (LowercaseArg == "-2017")
				{
					ms_eWindowsCompiler = WindowsCompiler.VisualStudio2017;
				}
				else if (LowercaseArg == "-2015")
				{
					ms_eWindowsCompiler = WindowsCompiler.VisualStudio2015;
				}
			}

			if (ms_eWindowsCompiler == WindowsCompiler.Default)
			{
				ms_eWindowsCompiler = WindowsPlatform.GetDefaultCompiler(null);
			}

			if (ms_eWindowsCompiler == WindowsCompiler.Default)
				return false;

			if (!WindowsPlatform.TryGetVSInstallDir(ms_eWindowsCompiler, out ms_dirVSInstall))
			{
				Console.WriteLine(string.Format("Failed to find install path of compiler {0}.", ms_eWindowsCompiler == WindowsCompiler.VisualStudio2015 ? "VS2015" : "VS2017"));
				return false;
			}
			else
				ms_dirVCInstall = DirectoryReference.Combine(ms_dirVSInstall, "VC");

			if (m_strExePath != "")
			{
				return File.Exists(m_strExePath);
			}

			// Get the name of the FASTBuild executable.
			string fbuild = "fbuild";
			if (BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Win64 || BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Win32)
			{
				fbuild = "fbuild.exe";
			}

			// Search the path for it
			string PathVariable = Environment.GetEnvironmentVariable("PATH");
			foreach (string SearchPath in PathVariable.Split(Path.PathSeparator))
			{
				try
				{
					string PotentialPath = Path.Combine(SearchPath, fbuild);
					if (File.Exists(PotentialPath))
					{
						m_strExePath = PotentialPath;
						return true;
					}
				}
				catch (ArgumentException)
				{
					// PATH variable may contain illegal characters; just ignore them.
				}
			}
			return false;
		}

		private void DetectBuildType(List<Action> Actions)
		{
			foreach (Action action in Actions)
			{
				if (action.ActionType != ActionType.Compile && action.ActionType != ActionType.Link)
					continue;

				if (action.CommandPath.Contains("orbis"))
				{
					m_eBuildType = FBBuildType.PS4;
					return;
				}
				//else if (action.CommandArguments.Contains("XboxOne") || action.CommandPath.Contains("Durango") || action.CommandArguments.Contains("Durango"))
				else if (action.CommandArguments.Contains("Intermediate\\Build\\XboxOne"))
				{
					m_eBuildType = FBBuildType.XBOne;
					return;
				}
				else if (action.CommandPath.Contains("Microsoft")) //Not a great test.
				{
					m_eBuildType = FBBuildType.Windows;
					return;
				}
			}
		}

		private bool IsMSVC() { return m_eBuildType == FBBuildType.Windows || m_eBuildType == FBBuildType.XBOne; }
		private bool IsXBOnePDBUtil(Action action) { return action.CommandPath.Contains("XboxOnePDBFileUtil.exe"); }
		private string GetCompilerName()
		{
			switch (m_eBuildType)
			{
				default:
				case FBBuildType.XBOne:
				case FBBuildType.Windows: return "UE4Compiler";
				case FBBuildType.PS4: return "UE4PS4Compiler";
			}
		}

		//Run FASTBuild on the list of actions. Relies on fbuild.exe being in the path.
		public override bool ExecuteActions(List<Action> Actions, bool bLogDetailedActionStats)
		{
			bool FASTBuildResult = true;
			if (Actions.Count > 0)
			{
				DetectBuildType(Actions);

				string FASTBuildFilePath = Path.Combine(UnrealBuildTool.EngineDirectory.FullName, "Intermediate", "Build", "fbuild.bff");
				if (CreateBffFile(Actions, FASTBuildFilePath))
				{
					FASTBuildResult = ExecuteBffFile(FASTBuildFilePath);
				}
				else
				{
					FASTBuildResult = false;
				}
			}
			return FASTBuildResult;
		}

		private void AddText(string StringToWrite)
		{
			byte[] Info = new System.Text.UTF8Encoding(true).GetBytes(StringToWrite);
			bffOutputFileStream.Write(Info, 0, Info.Length);
		}


		private string SubstituteEnvironmentVariables(string commandLineString)
		{
			string outputString;
			if (SDKsInstalled.DurangoXDK == false && commandLineString.Contains("$(DurangoXDK)"))
				throw new BuildException("Durango XDK not detected!");
			else
				outputString = commandLineString.Replace("$(DurangoXDK)", "$DurangoXDK$");

			if (SDKsInstalled.SCE_ORBIS_SDK == false && commandLineString.Contains("$(SCE_ORBIS_SDK_DIR)"))
				throw new BuildException("SCE_ORBIS SDK not detected!");
			else
				outputString = outputString.Replace("$(SCE_ORBIS_SDK_DIR)", "$SCE_ORBIS_SDK_DIR$");

			if (SDKsInstalled.DXSDK == false && commandLineString.Contains("$(DXSDK_DIR)"))
			{
				outputString = outputString.Replace("$(DXSDK_DIR)", "ThirdParty\\Windows\\DirectX");
			}
			else
				outputString = outputString.Replace("$(DXSDK_DIR)", "$DXSDK_DIR$");

			outputString = outputString.Replace("$(CommonProgramFiles)", "$CommonProgramFiles$");

			return outputString;
		}

		private Dictionary<string, string> ParseCommandLineOptions(string CompilerCommandLine, string[] SpecialOptions, out string ForkedCmd, out List<string> InputFiles, out List<string> IncludeFiles, bool SaveResponseFile = false)
		{
			Dictionary<string, string> ParsedCompilerOptions = new Dictionary<string, string>();
			InputFiles = new List<string>();
			IncludeFiles = new List<string>();

			// Make sure we substituted the known environment variables with corresponding BFF friendly imported vars
			CompilerCommandLine = SubstituteEnvironmentVariables(CompilerCommandLine);

			// Some tricky defines /DTROUBLE=\"\\\" abc  123\\\"\" aren't handled properly by either Unreal or Fastbuild, but we do our best.
			char[] SpaceChar = { ' ' };
			string[] RawTokens = CompilerCommandLine.Trim().Split(' ');
			List<string> ProcessedTokens = new List<string>();
			bool QuotesOpened = false;
			string PartialToken = "";
			string ResponseFilePath = "";

			if (RawTokens.Length >= 1 && RawTokens[0].StartsWith("@\"")) //Response files are in 4.13 by default. Changing VCToolChain to not do this is probably better.
			{
				string responseCommandline = RawTokens[0];

				// If we had spaces inside the response file path, we need to reconstruct the path.
				for (int i = 1; i < RawTokens.Length; ++i)
				{
					responseCommandline += " " + RawTokens[i];
				}

				ResponseFilePath = responseCommandline.Substring(2, responseCommandline.Length - 3); // bit of a bodge to get the @"response.txt" path...
				try
				{
					string ResponseFileText = File.ReadAllText(ResponseFilePath);

					// Make sure we substituted the known environment variables with corresponding BFF friendly imported vars
					ResponseFileText = SubstituteEnvironmentVariables(ResponseFileText);

					string[] Separators = { "\n", " ", "\r" };
					if (File.Exists(ResponseFilePath))
						RawTokens = ResponseFileText.Split(Separators, StringSplitOptions.RemoveEmptyEntries); //Certainly not ideal 
				}
				catch (Exception e)
				{
					Console.WriteLine("Looks like a response file in: " + CompilerCommandLine + ", but we could not load it! " + e.Message);
					ResponseFilePath = "";
				}
			}

			// Raw tokens being split with spaces may have split up some two argument options and 
			// paths with multiple spaces in them also need some love
			for (int i = 0; i < RawTokens.Length; ++i)
			{
				string Token = RawTokens[i];
				if (string.IsNullOrEmpty(Token))
				{
					if (ProcessedTokens.Count > 0 && QuotesOpened)
					{
						string CurrentToken = ProcessedTokens.Last();
						CurrentToken += " ";
					}

					continue;
				}

				int numQuotes = 0;
				// Look for unescaped " symbols, we want to stick those strings into one token.
				for (int j = 0; j < Token.Length; ++j)
				{
					if (Token[j] == '\\') //Ignore escaped quotes
						++j;
					else if (Token[j] == '"')
						numQuotes++;
				}

				// Defines can have escaped quotes and other strings inside them
				// so we consume tokens until we've closed any open unescaped parentheses.
				if ((Token.StartsWith("/D") || Token.StartsWith("-D")) && !QuotesOpened)
				{
					if (numQuotes == 0 || numQuotes == 2)
					{
						ProcessedTokens.Add(Token);
					}
					else
					{
						PartialToken = Token;
						++i;
						bool AddedToken = false;
						for (; i < RawTokens.Length; ++i)
						{
							string NextToken = RawTokens[i];
							if (string.IsNullOrEmpty(NextToken))
							{
								PartialToken += " ";
							}
							else if (!NextToken.EndsWith("\\\"") && NextToken.EndsWith("\"")) //Looking for a token that ends with a non-escaped "
							{
								ProcessedTokens.Add(PartialToken + " " + NextToken);
								AddedToken = true;
								break;
							}
							else
							{
								PartialToken += " " + NextToken;
							}
						}
						if (!AddedToken)
						{
							Console.WriteLine("Warning! Looks like an unterminated string in tokens. Adding PartialToken and hoping for the best. Command line: " + CompilerCommandLine);
							ProcessedTokens.Add(PartialToken);
						}
					}
					continue;
				}

				if (!QuotesOpened)
				{
					if (numQuotes % 2 != 0) //Odd number of quotes in this token
					{
						PartialToken = Token + " ";
						QuotesOpened = true;
					}
					else
					{
						ProcessedTokens.Add(Token);
					}
				}
				else
				{
					if (numQuotes % 2 != 0) //Odd number of quotes in this token
					{
						ProcessedTokens.Add(PartialToken + Token);
						QuotesOpened = false;
					}
					else
					{
						PartialToken += Token + " ";
					}
				}
			}

			//Processed tokens should now have 'whole' tokens, so now we look for any specified special options
			foreach (string specialOption in SpecialOptions)
			{
				for (int i = 0; i < ProcessedTokens.Count; ++i)
				{
					if (ProcessedTokens[i] == specialOption && i + 1 < ProcessedTokens.Count)
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i + 1];
						ProcessedTokens[i] = "[##]" + specialOption;
						ProcessedTokens[i + 1] = "";
						break;
					}
					else if (ProcessedTokens[i].StartsWith(specialOption))
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i].Replace(specialOption, null);
						ProcessedTokens[i] = "[##]" + specialOption;
						break;
					}
				}
			}

			//The search for the input and include file... we take the first non-argument we can find
			for (int i = 0; i < ProcessedTokens.Count; ++i)
			{
				string Token = ProcessedTokens[i];
				if (Token.Length == 0)
				{
					continue;
				}

				if (Token == "/l" || Token == "/D" || Token == "-D" || Token == "-x") // Skip tokens with values, I for cpp includes, l for resource compiler includes
				{
					++i;
				}
				else if (Token == "/I")
				{
					IncludeFiles.Add(ProcessedTokens[i + 1].Trim('\"'));
					ProcessedTokens[i] = string.Format("[##]IncludeFile{0:000000000000}", IncludeFiles.Count - 1);
					ProcessedTokens[i + 1] = "";
				}
				else if (!Token.StartsWith("/") && !Token.StartsWith("-") && !Token.StartsWith("[##]"))
				{
					InputFiles.Add(Token.Trim('\"'));
					ProcessedTokens[i] = string.Format("[##]InputFile{0:000000000000}", InputFiles.Count - 1);
				}

			}

			ForkedCmd = string.Join(" ", ProcessedTokens) + " ";
			for (int i = ProcessedTokens.Count - 1; i >= 0; --i)
			{
				if (ProcessedTokens[i].StartsWith("[##]") || string.IsNullOrEmpty(ProcessedTokens[i]))
					ProcessedTokens.RemoveAt(i);
			}

			ParsedCompilerOptions["OtherOptions"] = string.Join(" ", ProcessedTokens) + " ";

			if (SaveResponseFile && !string.IsNullOrEmpty(ResponseFilePath))
			{
				ParsedCompilerOptions["@"] = ResponseFilePath;
				ForkedCmd += "[##]@ ";
			}

			return ParsedCompilerOptions;
		}

		private bool UnforkCmd(string forkedCmd, Dictionary<string, string> specialOptions, List<string> inputFiles, List<string> includeFiles, out string unforkedCmd)
		{
			unforkedCmd = string.Copy(forkedCmd);

			foreach (var item in specialOptions)
			{
				//Console.WriteLine(item.Key + item.Value);
				unforkedCmd = unforkedCmd.Replace("[##]" + item.Key, item.Value);
			}
			for (int i = 0; i < inputFiles.Count; ++i)
			{
				string removeUeslessSpace = "";
				if (string.IsNullOrEmpty(inputFiles[i]))
					removeUeslessSpace = " ";
				unforkedCmd = unforkedCmd.Replace(string.Format("[##]InputFile{0:000000000000}{1}", i, removeUeslessSpace), inputFiles[i]);
			}
			for (int i = 0; i < includeFiles.Count; ++i)
			{
				string removeUeslessSpace = "";
				if (string.IsNullOrEmpty(includeFiles[i]))
				{
					removeUeslessSpace = " ";
					unforkedCmd = unforkedCmd.Replace(string.Format("[##]IncludeFile{0:000000000000}{1}", i, removeUeslessSpace), "");
				}
				else
					unforkedCmd = unforkedCmd.Replace(string.Format("[##]IncludeFile{0:000000000000}{1}", i, removeUeslessSpace), string.Format("/I \"{0}\"", includeFiles[i]));
			}

			if (unforkedCmd.Contains("[##]"))
			{
				Console.WriteLine("We found '[##]' in unforked commandline, maybe something is going wrong!");
				Console.WriteLine(unforkedCmd);
				return false;
			}

			unforkedCmd = unforkedCmd.Replace("/we4668", "/wd4668");

			return true;
		}

		private void StdUnforkedOption(Dictionary<string, string> unforkedOptions)
		{
			List<string> keys = new List<string>(unforkedOptions.Keys);
			foreach (var item in keys)
			{
				//if (item != "InputFile")
				unforkedOptions[item] = item + " " + unforkedOptions[item];
			}
		}

		private void EmptyInputFiles(List<string> inputFiles)
		{
			for (int i = 0; i < inputFiles.Count; ++i)
			{
				inputFiles[i] = "";
			}
		}

		private bool GetResFilesFromInputs(List<string> inputFiles, out List<string> resFiles, out List<string> otherFiles)
		{
			resFiles = new List<string>();
			otherFiles = new List<string>();
			for (int i = 0; i < inputFiles.Count; ++i)
			{
				if (inputFiles[i].LastIndexOf(".res") == inputFiles[i].Length - 4)
					resFiles.Add(inputFiles[i]);
				else
					otherFiles.Add(inputFiles[i]);
			}

			return true;
		}

		private List<Action> SortActions(List<Action> InActions)
		{
			List<Action> Actions = InActions;

			int NumSortErrors = 0;
			for (int ActionIndex = 0; ActionIndex < InActions.Count; ActionIndex++)
			{
				Action Action = InActions[ActionIndex];
				foreach (FileItem Item in Action.PrerequisiteItems)
				{
					if (Item.ProducingAction != null && InActions.Contains(Item.ProducingAction))
					{
						int DepIndex = InActions.IndexOf(Item.ProducingAction);
						if (DepIndex > ActionIndex)
						{
							NumSortErrors++;
						}
					}
				}
			}
			if (NumSortErrors > 0)
			{
				Actions = new List<Action>();
				var UsedActions = new HashSet<int>();
				for (int ActionIndex = 0; ActionIndex < InActions.Count; ActionIndex++)
				{
					if (UsedActions.Contains(ActionIndex))
					{
						continue;
					}
					Action Action = InActions[ActionIndex];
					foreach (FileItem Item in Action.PrerequisiteItems)
					{
						if (Item.ProducingAction != null && InActions.Contains(Item.ProducingAction))
						{
							int DepIndex = InActions.IndexOf(Item.ProducingAction);
							if (UsedActions.Contains(DepIndex))
							{
								continue;
							}
							Actions.Add(Item.ProducingAction);
							UsedActions.Add(DepIndex);
						}
					}
					Actions.Add(Action);
					UsedActions.Add(ActionIndex);
				}
				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					Action Action = Actions[ActionIndex];
					foreach (FileItem Item in Action.PrerequisiteItems)
					{
						if (Item.ProducingAction != null && Actions.Contains(Item.ProducingAction))
						{
							int DepIndex = Actions.IndexOf(Item.ProducingAction);
							if (DepIndex > ActionIndex)
							{
								Console.WriteLine("Action is not topologically sorted.");
								Console.WriteLine("  {0} {1}", Action.CommandPath, Action.CommandArguments);
								Console.WriteLine("Dependency");
								Console.WriteLine("  {0} {1}", Item.ProducingAction.CommandPath, Item.ProducingAction.CommandArguments);
								throw new BuildException("Cyclical Dependency in action graph.");
							}
						}
					}
				}
			}

			return Actions;
		}

		private string GetOptionValue(Dictionary<string, string> OptionsDictionary, string Key, Action Action, bool ProblemIfNotFound = false)
		{
			string Value = string.Empty;
			if (OptionsDictionary.TryGetValue(Key, out Value))
			{
				return Value.Trim(new Char[] { '\"' });
			}

			if (ProblemIfNotFound)
			{
				Console.WriteLine("We failed to find " + Key + ", which may be a problem.");
				Console.WriteLine("Action.CommandArguments: " + Action.CommandArguments);
			}

			return Value;
		}

		public string GetRegistryValue(string keyName, string valueName, object defaultValue)
		{
			object returnValue = (string)Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\SOFTWARE\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = (string)Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Wow6432Node\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			returnValue = Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\SOFTWARE\\Wow6432Node\\" + keyName, valueName, defaultValue);
			if (returnValue != null)
				return returnValue.ToString();

			return defaultValue.ToString();
		}

		public bool VerifyFileExists(string file, bool raiseException = false)
		{
			bool exists = File.Exists(file);
			if (!exists)
			{
				if (raiseException)
					throw new BuildException(string.Format("File doesn't exist! '{0}'", file));
				else
					Console.WriteLine(string.Format("File doesn't exist! '{0}'", file));
			}

			return exists;
		}

		private void WriteEnvironmentSetup()
		{
			try
			{
				// This may fail if the caller emptied PATH; we try to ignore the problem since
				// it probably means we are building for another platform.
				if (m_eBuildType == FBBuildType.Windows || m_eBuildType == FBBuildType.XBOne)
				{
					m_vcEnv = VCEnvironment.Create(ms_eWindowsCompiler, CppPlatform.Win64, null, null);
				}
			}
			catch (Exception) { }

			if (m_vcEnv == null)
			{
				throw new BuildException("Failed to get Visual Studio environment.");
			}


			// Copy environment into a case-insensitive dictionary for easier key lookups
			Dictionary<string, string> envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
			{
				envVars[(string)entry.Key] = (string)entry.Value;
			}

			if (envVars.ContainsKey("CommonProgramFiles"))
			{
				AddText("#import CommonProgramFiles\n");
			}

			if (envVars.ContainsKey("DXSDK_DIR"))
			{
				AddText("#import DXSDK_DIR\n");
				SDKsInstalled.DXSDK = true;
			}
			else
				SDKsInstalled.DXSDK = false;

			if (envVars.ContainsKey("DurangoXDK"))
			{
				AddText("#import DurangoXDK\n");
				SDKsInstalled.DurangoXDK = true;
			}
			else
				SDKsInstalled.DurangoXDK = false;

			if (envVars.ContainsKey("SCE_ORBIS_SDK_DIR"))
			{
				AddText("#import SCE_ORBIS_SDK_DIR");
				SDKsInstalled.SCE_ORBIS_SDK = true;
			}
			else
				SDKsInstalled.SCE_ORBIS_SDK = false;

			DirectoryReference VCToolChainRoot = DirectoryReference.Combine(m_vcEnv.ToolChainDir, "bin", "HostX64", "x64"); ;
			if (m_vcEnv != null)
			{
				string platformVersionNumber = "VSVersionUnknown";

				switch (m_vcEnv.Compiler)
				{
					case WindowsCompiler.VisualStudio2015:
						platformVersionNumber = "140";
						VCToolChainRoot = DirectoryReference.Combine(m_vcEnv.ToolChainDir, "bin", "amd64");
						break;

					case WindowsCompiler.VisualStudio2017:
						// For now we are working with the 140 version, might need to change to 141 or 150 depending on the version of the Toolchain you chose
						// to install
						platformVersionNumber = "140";
						break;

					default:
						string exceptionString = "Error: Unsupported Visual Studio Version.";
						Console.WriteLine(exceptionString);
						throw new BuildException(exceptionString);
				}


				AddText(string.Format(".WindowsSDKBasePath = '{0}'\n", m_vcEnv.WindowsSdkDir));

				AddText("Compiler('UE4ResourceCompiler') \n{\n");
				AddText(string.Format("\t.Executable = '{0}'\n", m_vcEnv.ResourceCompilerPath.FullName));
				AddText("\t.CompilerFamily  = 'custom'\n");
				AddText("}\n\n");


				AddText("Compiler('UE4Compiler') \n{\n");

				AddText(string.Format("\t.Root = '{0}'\n", VCToolChainRoot));
				AddText("\t.Executable = '$Root$/cl.exe'\n");
				VerifyFileExists(FileReference.Combine(VCToolChainRoot, "cl.exe").FullName, raiseException: true); 
				AddText("\t.ExtraFiles =\n\t{\n");
				AddText("\t\t'$Root$/c1.dll'\n");
				VerifyFileExists(FileReference.Combine(VCToolChainRoot, "c1.dll").FullName, raiseException: true); 
				AddText("\t\t'$Root$/c1xx.dll'\n");
				VerifyFileExists(FileReference.Combine(VCToolChainRoot, "c1xx.dll").FullName, raiseException: true); 
				AddText("\t\t'$Root$/c2.dll'\n");
				VerifyFileExists(FileReference.Combine(VCToolChainRoot, "c2.dll").FullName, raiseException: true); 

				if (File.Exists(FileReference.Combine(VCToolChainRoot, "1033", "clui.dll").FullName)) //Check English first...
				{
					AddText("\t\t'$Root$/1033/clui.dll'\n");
				}
				else
				{
					var numericDirectories = Directory.GetDirectories(VCToolChainRoot.ToString()).Where(d => Path.GetFileName(d).All(char.IsDigit));
					var cluiDirectories = numericDirectories.Where(d => Directory.GetFiles(d, "clui.dll").Any());
					if (cluiDirectories.Any())
					{
						AddText(string.Format("\t\t'$Root$/{0}/clui.dll'\n", Path.GetFileName(cluiDirectories.First())));
					}
					else
						throw new BuildException("Can not find clui.dll!");
				}
				AddText("\t\t'$Root$/mspdbsrv.exe'\n");
				VerifyFileExists(FileReference.Combine(VCToolChainRoot, "mspdbsrv.exe").FullName); 
				AddText("\t\t'$Root$/mspdbcore.dll'\n");
				VerifyFileExists(FileReference.Combine(VCToolChainRoot, "mspdbcore.dll").FullName); 

				AddText(string.Format("\t\t'$Root$/mspft{0}.dll'\n", platformVersionNumber));
				VerifyFileExists(FileReference.Combine(VCToolChainRoot, string.Format("mspft{0}.dll", platformVersionNumber)).FullName, raiseException: true); 
				AddText(string.Format("\t\t'$Root$/msobj{0}.dll'\n", platformVersionNumber));
				VerifyFileExists(FileReference.Combine(VCToolChainRoot, string.Format("msobj{0}.dll", platformVersionNumber)).FullName, raiseException: true); 
				AddText(string.Format("\t\t'$Root$/mspdb{0}.dll'\n", platformVersionNumber));
				VerifyFileExists(FileReference.Combine(VCToolChainRoot, string.Format("mspdb{0}.dll", platformVersionNumber)).FullName, raiseException: true); 

				if (m_vcEnv.Compiler == WindowsCompiler.VisualStudio2015)
				{
					string strFile = string.Format("{0}/redist/x64/Microsoft.VC{1}.CRT/msvcp{2}.dll", ms_dirVCInstall, platformVersionNumber, platformVersionNumber);
					AddText(string.Format("\t\t'{0}'\n", strFile));
					VerifyFileExists(strFile, raiseException: true);

					strFile = string.Format("{0}/redist/x64/Microsoft.VC{1}.CRT/vccorlib{2}.dll", ms_dirVCInstall, platformVersionNumber, platformVersionNumber);
					AddText(string.Format("\t\t'{0}'\n", strFile));
					VerifyFileExists(strFile, raiseException: true);
				}
				else
				{
					bool bNeedToSearch = false;
					string vsRedistVerFileName = FileReference.Combine(ms_dirVCInstall, "Auxiliary", "Build", "Microsoft.VCRedistVersion.default.txt").FullName;
					if (!File.Exists(vsRedistVerFileName))
					{
						bNeedToSearch = true;
						Console.WriteLine(string.Format("'{0}' does not exist, it will msvcp{1}.dll and vccorlib{1}.dll on vs installed directory.", vsRedistVerFileName, platformVersionNumber));
					}
					else
					{
						string Version = null;
						try
						{
							Version = File.ReadAllText(vsRedistVerFileName).Trim();
						}
						catch (System.Exception ex)
						{
							Console.WriteLine(string.Format("Failed to read file {0}!\nException message: {1}\nIt will search msvcp{2}.dll and vccorlib{2}.dll on local directories.", vsRedistVerFileName, ex.Message, platformVersionNumber));
							bNeedToSearch = true;
						}

						if (string.IsNullOrEmpty(Version))
							bNeedToSearch = true;
						else
						{
							string strFile = string.Format("{0}/Redist/MSVC/{1}/x64/Microsoft.VC141.CRT/msvcp{2}.dll", ms_dirVCInstall, Version, platformVersionNumber);
							AddText(string.Format("\t\t'{0}'\n", strFile));
							VerifyFileExists(strFile, raiseException: true);
							strFile = string.Format("{0}/Redist/MSVC/{1}/x64/Microsoft.VC141.CRT/vccorlib{2}.dll", ms_dirVCInstall, Version, platformVersionNumber);
							AddText(string.Format("\t\t'{0}'\n", strFile));
							VerifyFileExists(strFile, raiseException: true);
						}
					}

					if (bNeedToSearch)
					{
						string msvcpDLL = string.Format("msvcp{0}.dll", platformVersionNumber);
						string vccorlibDLL = string.Format("vccorlib{0}.dll", platformVersionNumber);

						// check the ToolChainDir first.
						string msvcpDLLPath = FileReference.Combine(VCToolChainRoot, msvcpDLL).FullName;
						if (File.Exists(msvcpDLLPath))
							AddText(string.Format("\t\t'{0}'\n", msvcpDLLPath));
						else
						{
							// search in all sub directories of ms_dirVCInstall.
							msvcpDLLPath = null;
							var files = Directory.GetFiles(ms_dirVCInstall.ToString(), msvcpDLL, SearchOption.AllDirectories);
							foreach (var f in files)
							{
								if (f.Contains("onecore"))
									continue;
								if (f.Contains("x64"))
								{
									msvcpDLLPath = f;
									break;
								}
							}
							if (!string.IsNullOrEmpty(msvcpDLLPath))
							{
								AddText(string.Format("\t\t'{0}'\n", msvcpDLLPath));
								Console.WriteLine(string.Format("Searched out {0} with path '{1}'.", msvcpDLL, msvcpDLLPath));
							}
							else
								throw new BuildException(string.Format("Can not find {0}!", msvcpDLL));
						}

						// check the ToolChainDir first.
						string vccorlibDLLPath = FileReference.Combine(VCToolChainRoot, vccorlibDLL).FullName;
						if (File.Exists(vccorlibDLLPath))
							AddText(string.Format("\t\t'{0}'\n", vccorlibDLLPath));
						else
						{
							// search in all sub directories of ms_dirVCInstall.
							vccorlibDLLPath = null;
							var files = Directory.GetFiles(ms_dirVCInstall.ToString(), vccorlibDLL, SearchOption.AllDirectories);
							foreach (var f in files)
							{
								if (f.Contains("onecore"))
									continue;
								if (f.Contains("x64"))
								{
									vccorlibDLLPath = f;
									break;
								}

							}
							if (!string.IsNullOrEmpty(vccorlibDLLPath))
							{
								AddText(string.Format("\t\t'{0}'\n", vccorlibDLLPath));
								Console.WriteLine(string.Format("Searched out {0} with path '{1}'.", vccorlibDLL, vccorlibDLLPath));
							}
							else
								throw new BuildException(string.Format("Can not find {0}!", vccorlibDLL));
						}
					}
				}

				AddText("\t}\n"); //End extra files

				AddText("}\n\n"); //End compiler
			}

			if (envVars.ContainsKey("SCE_ORBIS_SDK_DIR"))
			{
				// TODO: Check files exist.
				AddText(string.Format(".SCE_ORBIS_SDK_DIR = '{0}'\n", envVars["SCE_ORBIS_SDK_DIR"]));
				AddText(string.Format(".PS4BasePath = '{0}/host_tools/bin'\n\n", envVars["SCE_ORBIS_SDK_DIR"]));
				AddText("Compiler('UE4PS4Compiler') \n{\n");
				AddText("\t.Executable = '$PS4BasePath$/orbis-clang.exe'\n");
				AddText("}\n\n");
			}

			AddText("Settings \n{\n");

			//Start Environment
			AddText("\t.Environment = \n\t{\n");

			string includePaths = "";
			string libPaths = "";

			if (m_vcEnv != null)
			{
				AddText(string.Format("\t\t\"PATH={0}\\Common7\\IDE\\;{1}\",\n", ms_dirVSInstall, VCToolChainRoot));
			}
			if (envVars.ContainsKey("TMP"))
				AddText(string.Format("\t\t\"TMP={0}\",\n", envVars["TMP"]));
			if (envVars.ContainsKey("SystemRoot"))
				AddText(string.Format("\t\t\"SystemRoot={0}\",\n", envVars["SystemRoot"]));
			if (envVars.ContainsKey("INCLUDE"))
				//AddText(string.Format("\t\t\"INCLUDE={0}\",\n", envVars["INCLUDE"]));
				includePaths = string.Format("{0};", envVars["INCLUDE"]);

			if (envVars.ContainsKey("LIB"))
				//AddText(string.Format("\t\t\"LIB={0}\",\n", envVars["LIB"]));
				libPaths = string.Format("{0};", envVars["LIB"]);

			foreach (DirectoryReference IncludePath in m_vcEnv.IncludePaths)
			{
				includePaths += string.Format("{0};", IncludePath);
			}
			if (!string.IsNullOrEmpty(includePaths))
				AddText(string.Format("\t\t\"INCLUDE={0}\",\n", includePaths));


			foreach (DirectoryReference LibPath in m_vcEnv.LibraryPaths)
			{
				libPaths += string.Format("{0};", LibPath);
			}
			if (!string.IsNullOrEmpty(libPaths))
				AddText(string.Format("\t\t\"LIB={0}\",\n", libPaths));


			AddText("\t}\n"); //End environment
			AddText("}\n\n"); //End Settings
		}

		private bool AddCompileAction(List<Action> Actions, int ActionIndex, List<int> DependencyIndices)
		{
			Action Action = Actions[ActionIndex];

			string CompilerName = GetCompilerName();
			if (Action.CommandPath.Contains("rc.exe"))
			{
				CompilerName = "UE4ResourceCompiler";
			}

			string[] SpecialCompilerOptions = { "/Fo", "/fo", "/Yc", "/Yu", "/Fp", "-o" };
			string forkedCmd = "";
			List<string> InputFiles = null;
			List<string> IncludeFiles = null;
			var ParsedCompilerOptions = ParseCommandLineOptions(Action.CommandArguments, SpecialCompilerOptions, out forkedCmd, out InputFiles, out IncludeFiles);

			if (m_vcEnv != null)
			{
				for (int i = 0; i < IncludeFiles.Count; ++i)
				{
					for (int j = 0; j < m_vcEnv.IncludePaths.Count; ++j)
					{
						if (IncludeFiles[i] == m_vcEnv.IncludePaths[j].FullName)
							IncludeFiles[i] = "";
					}
				}
			}

			string OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, IsMSVC() ? "/Fo" : "-o", Action, ProblemIfNotFound: !IsMSVC());

			if (IsMSVC() && string.IsNullOrEmpty(OutputObjectFileName)) // Didn't find /Fo, try /fo
			{
				OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, "/fo", Action, ProblemIfNotFound: true);
			}

			if (string.IsNullOrEmpty(OutputObjectFileName)) //No /Fo or /fo, we're probably in trouble.
			{
				Console.WriteLine("We have no OutputObjectFileName. Bailing.");
				return false;
			}

			string IntermediatePath = Path.GetDirectoryName(OutputObjectFileName);
			if (string.IsNullOrEmpty(IntermediatePath))
			{
				Console.WriteLine("We have no IntermediatePath. Bailing.");
				Console.WriteLine("Our Action.CommandArguments were: " + Action.CommandArguments);
				return false;
			}

			if (InputFiles.Count != 1)
			{
				Console.WriteLine(string.Format("We have wrong number of input files, number {0}.", InputFiles.Count));
				return false;
			}
			string InputFile = string.Copy(InputFiles[0]);

			AddText(string.Format("ObjectList('Action_{0}')\n{{\n", ActionIndex));
			AddText(string.Format("\t.Compiler = '{0}' \n", CompilerName));
			List<string> InputFilesWithQuote = InputFiles.ConvertAll(x => string.Format("'{0}'", x));
			AddText(string.Format("\t.CompilerInputFiles = {{ {0} }} \n", string.Join(",", InputFilesWithQuote.ToArray())));
			AddText(string.Format("\t.CompilerOutputPath = '{0}'\n", IntermediatePath));

			if (!Action.bCanExecuteRemotely || !Action.bCanExecuteRemotelyWithSNDBS || ForceLocalCompileModules.Contains(Path.GetFileNameWithoutExtension(InputFile)))
				AddText(string.Format("\t.AllowDistribution = false\n"));

			//string OtherCompilerOptions = GetOptionValue(ParsedCompilerOptions, "OtherOptions", Action);
			string CompilerOutputExtension = ".unset";

			if (ParsedCompilerOptions.ContainsKey("/Yc")) //Create PCH
			{
				string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yc", Action, ProblemIfNotFound: true);
				string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);

				Dictionary<string, string> unforkedOptions = new Dictionary<string, string>(ParsedCompilerOptions);
				StdUnforkedOption(unforkedOptions);
				unforkedOptions["/Yc"] = string.Format("/Yu\"{0}\"", PCHIncludeHeader);
				unforkedOptions["/Fp"] = string.Format("/Fp\"{0}\"", PCHOutputFile);
				InputFiles[0] = "\"%1\"";
				unforkedOptions["/Fo"] = "/Fo\"%2\"";
				string unforkedCmd = "";
				if (!UnforkCmd(forkedCmd, unforkedOptions, InputFiles, IncludeFiles, out unforkedCmd))
					return false;
				AddText(string.Format("\t.CompilerOptions = '{0} '\n", unforkedCmd));
				unforkedOptions["/Yc"] = string.Format("/Yc\"{0}\"", PCHIncludeHeader);
				unforkedOptions["/Fp"] = "/Fp\"%2\"";
				InputFiles[0] = "\"%1\"";
				unforkedOptions["/Fo"] = string.Format("/Fo\"{0}\"", OutputObjectFileName);
				if (!UnforkCmd(forkedCmd, unforkedOptions, InputFiles, IncludeFiles, out unforkedCmd))
					return false;
				AddText(string.Format("\t.PCHOptions = '{0} '\n", unforkedCmd));

				AddText(string.Format("\t.PCHInputFile = '{0}'\n", InputFile));
				AddText(string.Format("\t.PCHOutputFile = '{0}'\n", PCHOutputFile));
				CompilerOutputExtension = ".obj";
			}
			else if (ParsedCompilerOptions.ContainsKey("/Yu")) //Use PCH
			{
				++iCompileActionNumbers;

				string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yu", Action, ProblemIfNotFound: true);
				string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);
				string PCHToForceInclude = PCHOutputFile.Replace(".pch", "");

				Dictionary<string, string> unforkedOptions = new Dictionary<string, string>(ParsedCompilerOptions);
				StdUnforkedOption(unforkedOptions);
				unforkedOptions["/Yu"] = string.Format("/FI\"{0}\" /Yu\"{1}\"", PCHToForceInclude, PCHIncludeHeader);
				unforkedOptions["/Fp"] = string.Format("/Fp\"{0}\"", PCHOutputFile);
				InputFiles[0] = "\"%1\"";
				unforkedOptions["/Fo"] = "/Fo\"%2\"";
				string unforkedCmd = "";
				if (!UnforkCmd(forkedCmd, unforkedOptions, InputFiles, IncludeFiles, out unforkedCmd))
					return false;
				AddText(string.Format("\t.CompilerOptions = '{0} '\n", unforkedCmd));
				CompilerOutputExtension = Path.GetExtension(InputFile) + ".obj";
			}
			else
			{
				++iCompileActionNumbers;

				if (CompilerName == "UE4ResourceCompiler")
				{
					Dictionary<string, string> unforkedOptions = new Dictionary<string, string>(ParsedCompilerOptions);
					StdUnforkedOption(unforkedOptions);
					unforkedOptions["/fo"] = "/fo\"%2\"";
					InputFiles[0] = "\"%1\"";
					string unforkedCmd = "";
					if (!UnforkCmd(forkedCmd, unforkedOptions, InputFiles, IncludeFiles, out unforkedCmd))
						return false;
					AddText(string.Format("\t.CompilerOptions = '{0} '\n", unforkedCmd));
					CompilerOutputExtension = Path.GetExtension(InputFile) + ".res";
				}
				else
				{
					if (IsMSVC())
					{
						Dictionary<string, string> unforkedOptions = new Dictionary<string, string>(ParsedCompilerOptions);
						StdUnforkedOption(unforkedOptions);
						unforkedOptions["/Fo"] = "/Fo\"%2\"";
						InputFiles[0] = "\"%1\"";
						string unforkedCmd = "";
						if (!UnforkCmd(forkedCmd, unforkedOptions, InputFiles, IncludeFiles, out unforkedCmd))
							return false;
						AddText(string.Format("\t.CompilerOptions = '{0} '\n", unforkedCmd));
						CompilerOutputExtension = Path.GetExtension(InputFile) + ".obj";
					}
					else
					{
						Dictionary<string, string> unforkedOptions = new Dictionary<string, string>(ParsedCompilerOptions);
						StdUnforkedOption(unforkedOptions);
						unforkedOptions["-o"] = "-o \"%2\"";
						InputFiles[0] = "\"%1\"";
						string unforkedCmd = "";
						if (!UnforkCmd(forkedCmd, unforkedOptions, InputFiles, IncludeFiles, out unforkedCmd))
							return false;
						AddText(string.Format("\t.CompilerOptions = '{0} '\n", unforkedCmd));
						CompilerOutputExtension = Path.GetExtension(InputFile) + ".o";
					}
				}
			}

			AddText(string.Format("\t.CompilerOutputExtension = '{0}' \n", CompilerOutputExtension));

			if (DependencyIndices.Count > 0)
			{
				List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("\t\t'Action_{0}', ;{1}", x, Actions[x].StatusDescription));
				AddText(string.Format("\t.PreBuildDependencies = {{\n{0}\n\t}} \n", string.Join("\n", DependencyNames.ToArray())));
			}

			AddText(string.Format("}}\n\n"));

			return true;
		}

		private bool ResolveInputFilesWithActions(List<Action> Actions, List<int> DependencyIndices, List<string> InputFiles, bool removePCHandRes = false)
		{
			for (int i = InputFiles.Count - 1; i >= 0; --i)
			{
				string input = InputFiles[i];
				int matchedDependencyIdx = -1;
				foreach (int idx in DependencyIndices)
				{
					foreach (var item in Actions[idx].ProducedItems)
					{
						string itemStr = item.ToString();
						//if (input.Contains(item.ToString()))
						if (input == itemStr)
						{
							matchedDependencyIdx = idx;
							break;
						}
					}
					if (matchedDependencyIdx >= 0)
						break;
				}

				if (removePCHandRes && matchedDependencyIdx >= 0)
				{
					foreach (var item in Actions[matchedDependencyIdx].ProducedItems)
					{
						string itemStr = item.ToString();
						if (itemStr.Contains(".pch") || itemStr.Contains(".res"))
						{
							InputFiles.RemoveAt(i);
							matchedDependencyIdx = -1;
							break;
						}
					}
				}

				if (matchedDependencyIdx >= 0)
					InputFiles[i] = string.Format("Action_{0}", matchedDependencyIdx);
			}

			return true;
		}

		private bool AddLinkAction(List<Action> Actions, int ActionIndex, List<int> DependencyIndices)
		{
			Action Action = Actions[ActionIndex];
			string[] SpecialLinkerOptions = { "/OUT:", "@", "-o" };
			string forkedCmd = "";
			List<string> InputFiles = null;
			List<string> IncludeFiles = null;
			var ParsedLinkerOptions = ParseCommandLineOptions(Action.CommandArguments, SpecialLinkerOptions, out forkedCmd, out InputFiles, out IncludeFiles, SaveResponseFile: true);

			string OutputFile;
			if (IsXBOnePDBUtil(Action))
			{
				OutputFile = ParsedLinkerOptions["OtherOptions"].Trim(' ').Trim('"');
			}
			else if (IsMSVC())
			{
				OutputFile = GetOptionValue(ParsedLinkerOptions, "/OUT:", Action, ProblemIfNotFound: true);
			}
			else //PS4
			{
				OutputFile = GetOptionValue(ParsedLinkerOptions, "-o", Action, ProblemIfNotFound: false);
			}

			if (string.IsNullOrEmpty(OutputFile))
			{
				Console.WriteLine("Failed to find output file. Bailing.");
				return false;
			}

			string ResponseFilePath = GetOptionValue(ParsedLinkerOptions, "@", Action);
			string OtherCompilerOptions = GetOptionValue(ParsedLinkerOptions, "OtherOptions", Action);

			if (IsXBOnePDBUtil(Action))
			{
				AddText(string.Format("Exec('Action_{0}')\n{{\n", ActionIndex));
				AddText(string.Format("\t.ExecExecutable = '{0}'\n", Action.CommandPath));
				AddText(string.Format("\t.ExecArguments = '{0}'\n", Action.CommandArguments));

				AddText(string.Format("\t.ExecInput = {{ {0} }} \n", string.Join(",", InputFiles.ToArray())));
				AddText(string.Format("\t.ExecOutput = '{0}' \n", OutputFile));
				AddText(string.Format("\t.PreBuildDependencies = {{ {0} }} \n", string.Join(",", InputFiles.ToArray())));
				AddText(string.Format("}}\n\n"));
			}
			else if (Action.CommandPath.Contains("lib.exe") || Action.CommandPath.Contains("orbis-snarl"))
			{
				AddText(string.Format("Library('Action_{0}')\n{{\n", ActionIndex));
				AddText(string.Format("\t.Compiler = '{0}'\n", GetCompilerName()));
				if (IsMSVC())
					AddText(string.Format("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /c'\n"));
				else
					AddText(string.Format("\t.CompilerOptions = '\"%1\" -o \"%2\" -c'\n"));
				AddText(string.Format("\t.CompilerOutputPath = \"{0}\"\n", Path.GetDirectoryName(OutputFile)));

				AddText(string.Format("\t.Librarian = '{0}' \n", Action.CommandPath));

				Dictionary<string, string> unforkedOptions = new Dictionary<string, string>(ParsedLinkerOptions);
				StdUnforkedOption(unforkedOptions);
				if (IsMSVC())
					unforkedOptions["/OUT:"] = "/OUT:\"%2\"";
				else
					unforkedOptions["-o"] = "-o \"%2\"";
				if (!string.IsNullOrEmpty(ResponseFilePath))
					//unforkedOptions["@"] = string.Format("@\"{0}\"", ResponseFilePath);
					unforkedOptions["@"] = "";
				List<string> forkedInputFiles = new List<string>(InputFiles);
				EmptyInputFiles(forkedInputFiles);
				forkedInputFiles[0] = "\"%1\" ";
				string unforkedCmd = "";
				if (!UnforkCmd(forkedCmd, unforkedOptions, forkedInputFiles, IncludeFiles, out unforkedCmd))
					return false;

				string extraOption = "";
				if (IsMSVC())
					extraOption = "/ignore:4042";
				AddText(string.Format("\t.LibrarianOptions = '{0} {1}'\n", extraOption, unforkedCmd));
				ResolveInputFilesWithActions(Actions, DependencyIndices, InputFiles, removePCHandRes: true);

				List<string> inputNames = InputFiles.ConvertAll(x => string.Format("'{0}'", x));
				AddText(string.Format("\t.LibrarianAdditionalInputs = {{ {0} }} \n", string.Join(",\n\t", inputNames.ToArray())));

				if (DependencyIndices.Count > 0)
				{
					List<string> PrebuildDependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));
					AddText(string.Format("\t.PreBuildDependencies = {{ {0} }} \n", string.Join(",", PrebuildDependencyNames.ToArray())));
				}

				AddText(string.Format("\t.LibrarianOutput = '{0}' \n", OutputFile));
				AddText(string.Format("}}\n\n"));
			}
			else if (Action.CommandPath.Contains("link.exe") || Action.CommandPath.Contains("orbis-clang"))
			{
				AddText(string.Format("Executable('Action_{0}')\n{{ \n", ActionIndex));
				AddText(string.Format("\t.Linker = '{0}' \n", Action.CommandPath));
				AddText(string.Format("\t.Libraries = {{ '{0}' }} \n", ResponseFilePath));
				if (IsMSVC())
				{
					if (m_eBuildType == FBBuildType.XBOne)
					{
						AddText(string.Format("\t.LinkerOptions = '/TLBOUT:\"%1\" /Out:\"%2\" @\"{0}\" {1} ' \n", ResponseFilePath, OtherCompilerOptions)); // The TLBOUT is a huge bodge to consume the %1.
					}
					else
					{
						AddText(string.Format("\t.LinkerOptions = '/TLBOUT:\"%1\" /Out:\"%2\" @\"{0}\" ' \n", ResponseFilePath)); // The TLBOUT is a huge bodge to consume the %1.
					}
				}
				else
				{
					AddText(string.Format("\t.LinkerOptions = '{0} -MQ \"%1\"' \n", OtherCompilerOptions)); // The MQ is a huge bodge to consume the %1.
				}

				if (DependencyIndices.Count > 0)
				{
					List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("\t\t'Action_{0}', //{1}", x, Actions[x].StatusDescription));
					AddText(string.Format("\t.PreBuildDependencies = {{\n{0}\n\t}} \n", string.Join("\n", DependencyNames.ToArray())));
				}

				AddText(string.Format("\t.LinkerOutput = '{0}' \n", OutputFile));
				AddText(string.Format("}}\n\n"));
			}

			return true;

		}

		private bool AddBuildProjectAction(List<Action> Actions, int ActionIndex, List<int> DependencyIndices)
		{
			Action Action = Actions[ActionIndex];
			
			if (Action.CommandDescription == "Copy")
			{
				//CopyAction.CommandArguments = String.Format("/C \"copy /Y \"{0}\" \"{1}\" 1>nul\"", SourceFile, TargetFile);
				string paramPart1 = "copy /Y \"";
				string paramPart2 = "\" \"";
				string paramPart3 = "\" 1>nul\"";
				int pos1 = Action.CommandArguments.IndexOf(paramPart1, 0);
				if (pos1 < 0)
					return false;
				int pos2 = Action.CommandArguments.IndexOf(paramPart2, pos1 + paramPart1.Length);
				if (pos2 < 0)
					return false;
				int pos3 = Action.CommandArguments.IndexOf(paramPart3, pos2 + paramPart2.Length);
				if (pos3 < 0)
					return false;
				string srcFile = Action.CommandArguments.Substring(pos1 + paramPart1.Length, pos2 - (pos1 + paramPart1.Length));
				string destFile = Action.CommandArguments.Substring(pos2 + paramPart2.Length, pos3 - (pos2 + paramPart2.Length));

				AddText(string.Format("Copy('Action_{0}')\n{{\n", ActionIndex));
				AddText(string.Format("\t.Source = '{0}'\n", srcFile));
				AddText(string.Format("\t.Dest = '{0}'\n", destFile));

				if (DependencyIndices.Count > 0)
				{
					List<string> PrebuildDependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));
					AddText(string.Format("\t.PreBuildDependencies = {{ {0} }} \n", string.Join(",", PrebuildDependencyNames.ToArray())));
				}

				AddText(string.Format("}}\n\n"));
			}
			else
			{
				Console.WriteLine("Unknown build project action detected!\n\t" + Action.CommandPath + ": " + Action.CommandArguments);
				return false;
			}

			return true;

		}

		private FileStream bffOutputFileStream = null;

		private bool CreateBffFile(List<Action> InActions, string BffFilePath)
		{
			System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();

			List<Action> Actions = SortActions(InActions);

			try
			{
				bffOutputFileStream = new FileStream(BffFilePath, FileMode.Create, FileAccess.Write);

				WriteEnvironmentSetup(); //Compiler, environment variables and base paths

				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					Action Action = Actions[ActionIndex];

					// Resolve dependencies
					List<int> DependencyIndices = new List<int>();
					foreach (FileItem Item in Action.PrerequisiteItems)
					{
						if (Item.ProducingAction != null)
						{
							int ProducingActionIndex = Actions.IndexOf(Item.ProducingAction);
							if (ProducingActionIndex >= 0)
							{
								DependencyIndices.Add(ProducingActionIndex);
							}
						}
					}

					switch (Action.ActionType)
					{
						case ActionType.Compile:
							if (!AddCompileAction(Actions, ActionIndex, DependencyIndices))
								return false;
							break;
						case ActionType.Link:
							if (!AddLinkAction(Actions, ActionIndex, DependencyIndices))
								return false;
							break;
						case ActionType.BuildProject:
							if (!AddBuildProjectAction(Actions, ActionIndex, DependencyIndices))
								return false;
							break;
						default: Console.WriteLine("Fastbuild is ignoring an unsupported action: " + Action.ActionType.ToString()); break;
					}
				}

				AddText("Alias( 'all' ) \n{\n");
				AddText("\t.Targets = { \n");
				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					AddText(string.Format("\t\t'Action_{0}'{1}", ActionIndex, ActionIndex < (Actions.Count - 1) ? ",\n" : "\n\t}\n"));
				}
				AddText("}\n");

				bffOutputFileStream.Close();
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception while creating bff file: " + e.ToString());
				return false;
			}

			timer.Stop();
			Console.WriteLine(string.Format("Create bff file in {0} seconds.", timer.Elapsed.TotalSeconds));
			return true;
		}

		private bool ExecuteBffFile(string BffFilePath)
		{
			System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();

			string distArgument = m_bEnableDistribution ? "-dist" : "";

			// disable distribution when compilation job less than CPU logic cores * 2.
			if (m_bEnableDistribution && iCompileActionNumbers < Environment.ProcessorCount * 2)
			{
				distArgument = "";
				Console.WriteLine(string.Format("Distributed compiliation will be disabled since jobs number [{0}] was < double CPU logic number [{1} x 2]", iCompileActionNumbers, Environment.ProcessorCount));
			}

			//Interesting flags for FASTBuild: -nostoponerror, -verbose, -monitor (if FASTBuild Monitor Visual Studio Extension is installed!)
			//The -clean is to bypass the FastBuild internal dependencies checks (cached in the fdb) as it could create some conflicts with UBT.
			//			Basically we want FB to stupidly compile what UBT tells it to.
			string FBCommandLine = string.Format("-monitor -summary {0} -ide -clean -config {1} ", distArgument, BffFilePath);

			ProcessStartInfo FBStartInfo = new ProcessStartInfo(string.IsNullOrEmpty(m_strExePath) ? "fbuild" : m_strExePath, FBCommandLine);

			FBStartInfo.UseShellExecute = false;
			FBStartInfo.WorkingDirectory = Path.Combine(UnrealBuildTool.EngineDirectory.MakeRelativeTo(DirectoryReference.GetCurrentDirectory()), "Source");

			try
			{
				Process FBProcess = new Process();
				FBProcess.StartInfo = FBStartInfo;

				FBStartInfo.RedirectStandardError = true;
				FBStartInfo.RedirectStandardOutput = true;
				FBProcess.EnableRaisingEvents = true;

				DataReceivedEventHandler OutputEventHandler = (Sender, Args) =>
				{
					if (Args.Data != null)
						Console.WriteLine(Args.Data);
				};

				FBProcess.OutputDataReceived += OutputEventHandler;
				FBProcess.ErrorDataReceived += OutputEventHandler;

				FBProcess.Start();

				FBProcess.BeginOutputReadLine();
				FBProcess.BeginErrorReadLine();

				FBProcess.WaitForExit();

				timer.Stop();
				Console.WriteLine(string.Format("Execute bff file in {0} seconds.", timer.Elapsed.TotalSeconds));
				return FBProcess.ExitCode == 0;
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception launching fbuild process. Is it in your path?" + e.ToString());
				return false;
			}
		}
	}	
}