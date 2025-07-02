#if UNITY_EDITOR
namespace UnityEditor.Experimental.Patching
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Runtime.InteropServices;
	using System.Text.RegularExpressions;
	using UnityEditor;
	using UnityEngine;

	/// <summary>
	/// Editor script to fix Git submodule issues when using a submodule with Unity and Visuals Studio 2022, which requires the project
	/// files to be in the submodule directory tree.
	/// </summary>
	/// <remarks>
	/// Tested with Unity 6000.1 (6.1) and Visual Studio 2022.
	/// </remarks>
	[InitializeOnLoad]
	public class UnityVSProjectLocationPatcher : AssetPostprocessor
	{
		private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

		public static string OnGeneratedSlnSolution(string path, string content)
		{
			RewriteSolutionFile(ref content);

			return content;
		}

		public static string OnGeneratedCSProject(string path, string content)
		{
			if (path.StartsWith(Path.Combine(ProjectRoot, "Library"), StringComparison.OrdinalIgnoreCase))
				return content;

			Match match = Regex.Match(content, @"<ProjectGuid>\s*(?<projectGuid>{[^<]+})\s*</ProjectGuid>.*?<AssemblyName>\s*(?<assemblyName>[^<]+)\s*</AssemblyName>", RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace);

			if (match.Success)
			{
				string projectGuid = match.Groups["projectGuid"].Value;
				string assemblyName = match.Groups["assemblyName"].Value;

				Dictionary<string, string> map = GetUnityAssemblyLocations();

				if (map.TryGetValue(assemblyName, out string asmdefPath))
				{
					string newPath = Path.Combine(asmdefPath, assemblyName + ".csproj");
					RewriteProjectFile(ref content, path, newPath, ref map);

					//UnityEngine.Debug.Log($"Rewriting project file {path} to {newPath}");

					string targetDir = Path.GetDirectoryName(newPath);
					Directory.CreateDirectory(targetDir);

					// We may not have the source file if this is the first time the project is generated, but we need it for the link.
					if (!File.Exists(path))
						File.WriteAllText(path, content);

					// Remove existing link to create a new one.
					if (File.Exists(newPath))
						File.Delete(newPath);

					// Create a hard link to the file in the submodule directory.
					bool result = CreateHardLink(newPath, path, IntPtr.Zero);

					if (!result)
						UnityEngine.Debug.LogWarning($"[Error] Failed to create hard link for {path} at {newPath}: {Marshal.GetLastWin32Error()}");
				}
			}

			return content;
		}

		[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, System.IntPtr lpSecurityAttributes);

		private static void RewriteSolutionFile(ref string content)
		{
			List<string> lines = new List<string>(content.Split("\r\n"));
			List<string> outputLines = new List<string>();
			Dictionary<string, string> assemblyLocations = GetUnityAssemblyLocations();
			HashSet<string> seenProjects = new HashSet<string>();

			Regex regex = new Regex(@"Project\(""(?<solutionGuid>[^""]+)""\)\s=\s""(?<projectName>[^""]+)"",\s""[^""]+"",\s""(?<projectGuid>[^""]+)""");

			for (int i = 0; i < lines.Count; i++)
			{
				Match match = regex.Match(lines[i]);

				if (match.Success)
				{
					i++; // skip EndProject.

					string solutionGuid = match.Groups["solutionGuid"].Value;
					string projectName = match.Groups["projectName"].Value;
					string projectGuid = match.Groups["projectGuid"].Value;
					string newProjectPath = $"{projectName}.csproj";

					if (assemblyLocations.TryGetValue(projectName, out string asmdefPath))
						newProjectPath = Path.Combine(asmdefPath, projectName + ".csproj");

					newProjectPath = newProjectPath.Replace(ProjectRoot, "").TrimStart(Path.DirectorySeparatorChar);

					// If we have already seen this project name, we skip it to avoid duplicates.
					if (seenProjects.Contains(projectGuid))
						continue;

					seenProjects.Add(projectGuid);

					outputLines.Add($"Project(\"{solutionGuid}\") = \"{projectName}\", \"{newProjectPath}\", \"{projectGuid}\"");
					outputLines.Add($"EndProject");
				}
				else
				{
					// Keep all non-project lines as-is.
					outputLines.Add(lines[i]);
				}
			}

			// Add a comment at the top of the solution file indicating it was modified by this script.
			outputLines.Insert(3, $"# Modified by UnityVSProjectLocationPatcher");

			// Update the content with the modified lines.
			content = string.Join("\r\n", outputLines);
		}

		private static bool RewriteProjectFile(ref string content, string path, string newPath, ref Dictionary<string, string> map)
		{
			bool changed = false;

			// Get absolute directories of original and new path
			string originalDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
			string newDir = Path.GetDirectoryName(Path.GetFullPath(newPath))!;

			// Regex to match paths inside HintPath tags and Include attributes
			// Example path:				Match?	Reason
			// D:\MyLib\My.dll				No		Starts with drive letter
			// \\Server\Share\file.dll		No		Starts with UNC path (\\)
			// libs\MyLib.dll				Yes		Relative path
			// ..\external\lib\file.dll		Yes		Relative path
			// C:/wrong/slash/format.dll	No		Still a drive letter path

			string pattern = @"(<HintPath>|(?:Compile|None|ProjectReference)\s+Include="")(?<relPath>(?!(?:[A-Za-z]:|\\\\))[^<""\\][^<""\\]*)";

			content = Regex.Replace(content, pattern, match =>
			{
				string prefix = match.Groups[1].Value;
				string relPath = match.Groups["relPath"].Value;

				// Compute absolute path of original file
				string absolutePath = Path.GetFullPath(Path.Combine(originalDir, relPath));

				// Compute new relative path from new location
				string updatedRelPath = Path.GetRelativePath(newDir, absolutePath);

				// MSBuild requires backslashes in paths, so we replace directory separators.
				updatedRelPath = updatedRelPath.Replace(Path.DirectorySeparatorChar, '\\');

				if (updatedRelPath != relPath)
				{
					changed = true;
					return prefix + updatedRelPath;
				}

				return match.Value;
			}, RegexOptions.IgnoreCase);

			return changed;
		}

		/// <summary>
		/// Get a list of Unity asmdef named and locations.
		/// </summary>
		private static Dictionary<string, string> GetUnityAssemblyLocations()
		{
			Dictionary<string, string> map = new Dictionary<string, string>();
			string[] filePaths = Directory.GetFiles(ProjectRoot, "*.asmdef", SearchOption.AllDirectories);

			foreach (string filePath in filePaths)
			{
				if (filePath.StartsWith(Path.Combine(ProjectRoot, "Library"), StringComparison.OrdinalIgnoreCase)
					|| filePath.StartsWith(Path.Combine(ProjectRoot, "Assets"), StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				string json = File.ReadAllText(filePath);
				string pattern = @"""name""\s*:\s*""([^""]+)""";
				Match match = Regex.Match(json, pattern);

				if (match.Success)
				{
					string name = match.Groups[1].Value;
					map[name] = Path.GetDirectoryName(filePath);
				}
			}

			return map;
		}
	}
}
#endif
