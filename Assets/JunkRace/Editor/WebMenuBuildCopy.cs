using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace JunkRace.EditorTools
{
    sealed class WebMenuBuildCopy : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        const string MenuFolder = "Junkrace-menu";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform == BuildTarget.WebGL)
                GetMenuPaths(report.summary.outputPath);
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL) return;

            var (source, destination) = GetMenuPaths(report.summary.outputPath);
            CopyDirectory(source, destination);
        }

        static (string source, string destination) GetMenuPaths(string outputPath)
        {
            string project = Path.GetDirectoryName(Application.dataPath);
            string source = Path.Combine(project, MenuFolder);
            string output = Path.GetFullPath(outputPath, project);
            string destination = Path.Combine(output, MenuFolder);

            if (!Directory.Exists(source))
                throw new BuildFailedException($"Required WebGL menu folder is missing: {source}");
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)
                || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException($"WebGL build output would overlap the source menu folder: {source}");

            return (source, destination);
        }

        static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            foreach (var directory in Directory.EnumerateDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
