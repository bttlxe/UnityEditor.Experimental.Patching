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
		private static Dictionary<string, string> FoundAssemblyLocations;

		static UnityVSProjectLocationPatcher()
		{
		}

		/// <summary>
		/// Invoked before Unity generates C# project and solution files. Allows customization or prevention of Unity's
		/// default project file generation behavior.
		/// </summary>
		/// <remarks>This method can be used to perform pre-generation tasks, such as caching assembly locations or
		/// modifying project generation settings. Returning <see langword="true"/> disables Unity's  default project file
		/// generation, enabling custom handling.</remarks>
		/// <returns><see langword="true"/> to prevent Unity from generating the default project files;  otherwise, <see
		/// langword="false"/> to allow Unity to proceed with its default behavior.</returns>
		public static bool OnPreGeneratingCSProjectFiles()
		{
			// This method is called before generating the C# project and solution files.
			// It can be used to disable Unity's default behavior of generating project files.

			// Cache the assembly locations to avoid repeated (slow) disk access.
			FoundAssemblyLocations = GetUnityAssemblyLocations();

			return false; // Return true to prevent Unity from generating the default project files.
		}

		/// <summary>
		/// Modifies the content of a generated solution file.
		/// </summary>
		/// <param name="path">The file path of the solution file.</param>
		/// <param name="content">The content of the solution file to be modified. This parameter is passed by reference and updated during the
		/// operation.</param>
		/// <returns>The modified content of the solution file.</returns>
		public static string OnGeneratedSlnSolution(string path, string content)
		{
			// If we haven't cached the assembly locations, we do it now.
			if (FoundAssemblyLocations == null)
				FoundAssemblyLocations = GetUnityAssemblyLocations();

			RewriteSolutionFile(ref content);

			return content;
		}

		/// <summary>
		/// Processes a generated C# project file and modifies its content based on specific conditions.
		/// </summary>
		/// <remarks>This method checks the project file's path and content to determine whether modifications are
		/// necessary. If the file resides outside the "Library" directory and matches specific patterns, the method may
		/// rewrite the project file, create necessary directories, and establish hard links.</remarks>
		/// <param name="path">The file path of the generated C# project file.</param>
		/// <param name="content">The content of the generated C# project file.</param>
		/// <returns>The potentially modified content of the C# project file. If no modifications are made, the original content is
		/// returned.</returns>
		public static string OnGeneratedCSProject(string path, string content)
		{
			if (path.StartsWith(Path.Combine(ProjectRoot, "Library"), StringComparison.OrdinalIgnoreCase))
				return content;

			Match match = Regex.Match(content, @"<ProjectGuid>\s*(?<projectGuid>{[^<]+})\s*</ProjectGuid>.*?<AssemblyName>\s*(?<assemblyName>[^<]+)\s*</AssemblyName>", RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace);

			if (match.Success)
			{
				//string projectGuid = match.Groups["projectGuid"].Value;
				string assemblyName = match.Groups["assemblyName"].Value;

				if (FoundAssemblyLocations.TryGetValue(assemblyName, out string asmdefPath))
				{
					string newPath = Path.Combine(asmdefPath, assemblyName + ".csproj");
					RewriteProjectFile(ref content, assemblyName, path, newPath);

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

		/// <summary>
		/// Creates a hard link between the specified file and an existing file.
		/// </summary>
		/// <remarks>This method is a platform invocation of the Windows API function <c>CreateHardLink</c>. It is
		/// only supported on Windows. Ensure that both <paramref name="lpFileName"/> and <paramref
		/// name="lpExistingFileName"/> are valid paths and that the  file system supports hard links.</remarks>
		/// <param name="lpFileName">The name of the new file to be created. This must be a fully qualified path.</param>
		/// <param name="lpExistingFileName">The name of the existing file. This must be a fully qualified path.</param>
		/// <param name="lpSecurityAttributes">Reserved for future use. Must be <see langword="IntPtr.Zero"/>.</param>
		/// <returns><see langword="true"/> if the hard link is successfully created; otherwise, <see langword="false"/>.</returns>
		[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, System.IntPtr lpSecurityAttributes);

		/// <summary>
		/// Modifies the content of a solution file to update project paths and remove duplicate project entries.
		/// </summary>
		/// <remarks>This method processes the solution file content line by line, updating project paths based on
		/// predefined mappings and removing duplicate project entries. It also inserts a comment at the top of the solution
		/// file indicating that it was modified.  The method ensures that project paths are updated relative to the project
		/// root and avoids duplicating entries for projects with the same GUID.</remarks>
		/// <param name="content">A reference to the string containing the solution file content. The method updates this string with the modified
		/// solution file content.</param>
		private static void RewriteSolutionFile(ref string content)
		{
			List<string> lines = new(content.Split("\r\n"));
			List<string> outputLines = new();
			HashSet<string> seenProjects = new();

			Regex regex = new(@"Project\(""(?<solutionGuid>[^""]+)""\)\s=\s""(?<projectName>[^""]+)"",\s""[^""]+"",\s""(?<projectGuid>[^""]+)""");

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

					if (FoundAssemblyLocations.TryGetValue(projectName, out string asmdefPath))
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

		/// <summary>
		/// Updates the content of a project file by rewriting relative paths to absolute paths based on the specified
		/// original and new directories.
		/// </summary>
		/// <remarks>This method processes the content of a project file to replace relative paths found in specific
		/// XML tags (e.g., <c>&lt;OutputPath&gt;</c>, <c>HintPath</c>, and <c>Include</c> attributes) with their absolute
		/// equivalents. It uses the original and new file paths to compute the absolute paths, ensuring that the rewritten
		/// paths are consistent with the specified directories.  The method is designed to handle relative paths only.
		/// Absolute paths (e.g., those starting with a drive letter or UNC paths) are ignored.</remarks>
		/// <param name="content">The content of the project file to be modified. This parameter is passed by reference and will be updated with the
		/// rewritten paths.</param>
		/// <param name="path">The original file path used to determine the base directory for resolving relative paths.</param>
		/// <param name="newPath">The new file path used to determine the target base directory for resolving relative paths.</param>
		/// <returns><see langword="true"/> if the content was modified; otherwise, <see langword="false"/>.</returns>
		private static bool RewriteProjectFile(ref string content, string assemblyName, string path, string newPath)
		{
			// Example path:				Match?	Reason
			// D:\MyLib\My.dll				No		Starts with drive letter
			// \\Server\Share\file.dll		No		Starts with UNC path (\\)
			// libs\MyLib.dll				Yes		Relative path
			// ..\external\lib\file.dll		Yes		Relative path
			// C:/wrong/slash/format.dll	No		Still a drive letter path

			bool changed = false;

			// Get absolute directories of original and new path
			string originalDir = Path.GetDirectoryName(Path.GetFullPath(path));
			string newDir = Path.GetDirectoryName(Path.GetFullPath(newPath));

			// <OutputPath>Temp\bin\Debug\</OutputPath>
			string pattern = @"<OutputPath>(.*?)</OutputPath>";

			content = Regex.Replace(content, pattern, match =>
			{
				string tempPath = Path.Combine(originalDir, match.Groups[1].Value);
				string objPath = Path.Combine(originalDir, "obj");
				string docPath = Path.Combine(originalDir, "Library", "ScriptAssemblies", assemblyName + ".xml");

				changed = true;
				return @$"<OutputPath>{tempPath}</OutputPath>{"\r\n"}    <BaseIntermediateOutputPath>{objPath}</BaseIntermediateOutputPath>{"\r\n"}    <DocumentationFile>{docPath}</DocumentationFile>{"\r\n"}    <GenerateDocumentationFile>true</GenerateDocumentationFile>";
			}, RegexOptions.IgnoreCase);

			// Regex to match paths inside HintPath tags and Include attributes
			pattern = @"(<HintPath>|(?:Compile|None|ProjectReference)\s+Include="")(?<relPath>(?!(?:[A-Za-z]:|\\\\))[^<""\\][^<""\\]*)";

			content = Regex.Replace(content, pattern, match =>
			{
				string prefix = match.Groups[1].Value;
				string relPath = match.Groups["relPath"].Value;

				// Compute absolute path of original file.
				string absolutePath = Path.GetFullPath(Path.Combine(originalDir, relPath));

				changed = true;
				return prefix + absolutePath;
			}, RegexOptions.IgnoreCase);

			// Add to the NoWarn section if it is not already present.
			content = Regex.Replace(content, @"(<NoWarn>\s*[^<]*?)(</NoWarn>)", match =>
			{
				var nowarn = match.Groups[1].Value;
				var suffixes = new List<string>();

				if (!nowarn.Contains("1591"))
					suffixes.Add("1591");
				if (!nowarn.Contains("1587"))
					suffixes.Add("1587");
				if (!nowarn.Contains("1584"))
					suffixes.Add("1584");
				if (!nowarn.Contains("1574"))
					suffixes.Add("1574");
				if (!nowarn.Contains("1570"))
					suffixes.Add("1570");
				if (!nowarn.Contains("1572"))
					suffixes.Add("1572");
				if (!nowarn.Contains("1573"))
					suffixes.Add("1573");
				if (!nowarn.Contains("0282"))
					suffixes.Add("0282");

				// Append only the missing codes
				var updatedNoWarn = suffixes.Count > 0 ? $"{nowarn};{string.Join(";", suffixes)}" : nowarn;

				return $"{updatedNoWarn}{match.Groups[2].Value}";
			});

			return changed;
		}

		/// <summary>
		/// Get a list of Unity asmdef named and locations.
		/// </summary>
		private static Dictionary<string, string> GetUnityAssemblyLocations()
		{
			Dictionary<string, string> map = new();
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
