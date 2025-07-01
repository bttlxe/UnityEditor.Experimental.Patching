#if UNITY_EDITOR
namespace UnityEditor.Experimental
{
	using System;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
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
	public class UnityVSGitSubmoduleSolutionPatcher : AssetPostprocessor
	{
		private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

		private static List<(string solutionGuid, string projectName, string projectGuid)> Projects = new List<(string solutionGuid, string projectName, string projectGuid)>();
		private static Dictionary<string, string> ProjectToSubmodule = new Dictionary<string, string>();

		public static string OnGeneratedSlnSolution(string path, string content)
		{
			RewriteSolutionFile(
				ref content,
				out Projects,
				out ProjectToSubmodule);

			return content;
		}

		public static string OnGeneratedCSProject(string path, string content)
		{
			Match match = Regex.Match(content, @"<ProjectGuid>(?<guid>\{[A-Fa-f0-9\-]+\})</ProjectGuid>");
			if (match.Success)
			{
				string projectGuid = match.Groups["guid"].Value;
				(string projectGuid, string name, string csprojGuid) project = Projects.FirstOrDefault(p => p.projectGuid.Equals(projectGuid, StringComparison.OrdinalIgnoreCase));

				if (project != default)
				{
					if (RewriteProjectFile(ref content, project.name, ProjectToSubmodule))
					{
						ProjectToSubmodule.TryGetValue(project.name, out string submodulePath);

						string targetDir = Path.Combine(ProjectRoot, submodulePath);
						Directory.CreateDirectory(targetDir);

						string sourceFilePath = Path.Combine(ProjectRoot, $"{project.name}.csproj"); // Should equal path parameter.
						string linkFilePath = Path.Combine(targetDir, $"{project.name}.csproj");

						// We may not have the source file if this is the first time the project is generated, but we need it for the link.
						if (!File.Exists(sourceFilePath))
							File.WriteAllText(sourceFilePath, content);

						// Remove existing link to create a new one.
						if (File.Exists(linkFilePath))
							File.Delete(linkFilePath);

						// Create a hard link to the file in the submodule directory.
						bool result = CreateHardLink(linkFilePath, sourceFilePath, IntPtr.Zero);

						if (!result)
							UnityEngine.Debug.LogWarning($"[Error] Failed to create hard link for {sourceFilePath} at {linkFilePath}: {Marshal.GetLastWin32Error()}");
					}
				}
			}

			return content;
		}

		[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, System.IntPtr lpSecurityAttributes);

		private static void RewriteSolutionFile(
			ref string content,
			out List<(string solutionGuid, string projectName, string projectGuid)> projects,
			out Dictionary<string, string> projectToSubmodule)
		{
			List<string> lines = new List<string>(content.Split("\r\n"));
			List<string> outputLines = new List<string>();

			projects = new List<(string solutionGuid, string projectName, string projectGuid)>();
			HashSet<string> seenNames = new HashSet<string>();

			Regex regex = new Regex(@"Project\(""(?<solutionGuid>[^""]+)""\)\s=\s""(?<projectName>[^""]+)"",\s""[^""]+"",\s""(?<projectGuid>[^""]+)""");

			for (int i = 0; i < lines.Count; i++)
			{
				Match match = regex.Match(lines[i]);

				if (match.Success)
				{
					string solutionGuid = match.Groups["solutionGuid"].Value;
					string projectName = match.Groups["projectName"].Value;
					string projectGuid = match.Groups["projectGuid"].Value;

					if (seenNames.Add(projectName))
					{
						// Store the project information but don't add to the output yet.
						projects.Add((solutionGuid, projectName, projectGuid));
					}

					i++; // Skip the next line which is "EndProject".
				}
				else
				{
					// Keep all non-project lines as-is.
					outputLines.Add(lines[i]);
				}
			}

			int insertionLine = outputLines.FindIndex(line => line == "Global") - 1;

			// Get the mapping of project names to submodule paths (only includes projects that are in submodules).
			projectToSubmodule = GetProjectToSubmoduleMap(projects);

			// Add back the projects.
			foreach ((string solutionGuid, string name, string projectGuid) in projects)
			{
				bool hasFilesInSubmodule = projectToSubmodule.TryGetValue(name, out string submodulePath);

				// If a submodule then point to the new .csproj location inside the submodule.
				if (hasFilesInSubmodule)
				{
					string newProjectPath = Path.Combine(submodulePath, $"{name}.csproj");
					//.Replace("\\", "\\\\"); // Escape for C# strings

					outputLines.Insert(++insertionLine, $"Project(\"{solutionGuid}\") = \"{name}\", \"{newProjectPath}\", \"{projectGuid}\"");
					outputLines.Insert(++insertionLine, "EndProject");
				}
				// If the project is not in a submodule, we keep the original project line.
				else
				{
					outputLines.Insert(++insertionLine, $"Project(\"{solutionGuid}\") = \"{name}\", \"{name}.csproj\", \"{projectGuid}\"");
					outputLines.Insert(++insertionLine, "EndProject");
				}
			}

			// Add a comment at the top of the solution file indicating it was modified by this script.
			outputLines.Insert(3, $"# Modified by UnityVSGitSubmoduleSolutionPatcher");

			// Update the content with the modified lines.
			content = string.Join("\r\n", outputLines);
		}



		/// <summary>
		/// Rewrites the project files to remove the submodule path prefix and update the paths to be relative to the submodule.
		/// </summary>
		private static bool RewriteProjectFile(ref string content, string name, Dictionary<string, string> projectToSubmodule)
		{
			// If the project is not in a submodule, we skip it.
			if (!projectToSubmodule.TryGetValue(name, out string submodulePath))
				return false;

			string submoduleFullPath = Path.Combine(ProjectRoot, submodulePath);
			string submodulePrefix = submodulePath + Path.DirectorySeparatorChar;
			string relativePrefix = Path.GetRelativePath(submoduleFullPath, ProjectRoot) + Path.DirectorySeparatorChar;

			// Remove submodule path prefix from content.
			content = content.Replace(submodulePrefix, "");

			// Rewrite paths based on relative prefix.
			content = content.Replace(@"<HintPath>Assets\", $@"<HintPath>{relativePrefix}Assets\");
			content = content.Replace(@"<HintPath>Library\", $@"<HintPath>{relativePrefix}Library\");

			// Rewrite <ProjectReference Include=...>.
			content = Regex.Replace(
				content,
				@"<ProjectReference Include=""([^""]+)""",
				match =>
				{
					string includePath = match.Groups[1].Value;
					string projectName = Path.GetFileNameWithoutExtension(includePath);

					if (projectToSubmodule.ContainsKey(projectName))
					{
						// Is it our submodule, or another?

						return match.Value; // Same submodule = no change
					}

					// External project = rewrite path
					return $@"<ProjectReference Include=""{relativePrefix}{includePath}""";
				}
			);



			// Add a comment indicating the submodule.
			List<string> lines = content.Split(new[] { "\r\n" }, StringSplitOptions.None).ToList();

			string customLine = $"  <!-- Submodule: {submodulePath} -->";
			if (lines.Count >= 3)
				lines.Insert(2, customLine);
			else
				lines.Add(customLine); // fallback if fewer than 3 lines

			content = string.Join("\r\n", lines);

			return true;
		}





		/// <summary>
		/// Gets the paths of all submodules defined in the .gitmodules file, or an empty list if none.
		/// </summary>
		private static List<string> GetSubmodulePaths()
		{
			string gitmodulesPath = Path.Combine(ProjectRoot, ".gitmodules");

			if (!File.Exists(gitmodulesPath))
				return new List<string>();

			List<string> paths = new List<string>();

			foreach (string line in File.ReadLines(gitmodulesPath))
			{
				if (line.TrimStart().StartsWith("path = "))
				{
					string path = line.Split('=')[1].Trim();
					if (!string.IsNullOrWhiteSpace(path))
						paths.Add(path.Replace('/', Path.DirectorySeparatorChar));
				}
			}

			return paths;
		}

		/// <summary>
		/// Gets a dictionary mapping project names to their submodule paths.
		/// </summary>
		/// <remarks>
		/// If a project is not in a submodule, it will not be included in the map.
		/// </remarks>
		private static Dictionary<string, string> GetProjectToSubmoduleMap(List<(string solutionGuid, string name, string projectGuid)> projects)
		{
			List<string> submodulePaths = GetSubmodulePaths();
			Dictionary<string, string> map = new Dictionary<string, string>();

			foreach (string submodulePath in submodulePaths)
			{
				string submoduleGitPath = Path.Combine(ProjectRoot, submodulePath, ".gitunity");

				if (File.Exists(submoduleGitPath))
				{
					// Read the .gitunity file in the submodule and add each line to the list.
					IEnumerable<string> lines = File.ReadLines(submoduleGitPath);
					List<string> projectsInSubmodule = new List<string>();

					foreach (string line in lines)
					{
						string trimmedLine = line.Trim();

						if (!string.IsNullOrWhiteSpace(trimmedLine) && !projectsInSubmodule.Contains(trimmedLine))
							projectsInSubmodule.Add(trimmedLine);
					}

					foreach ((string solutionGuid, string name, string projectGuid) in projects)
					{
						if (projectsInSubmodule.Contains(name))
						{
							// If the project is in the submodule, map it to the submodule path.
							if (!map.ContainsKey(name))
								map[name] = submodulePath;
						}
					}
				}
			}

			return map;
		}
	}
}
#endif
