using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodeiumVS.Utilities;

internal static class FileUtilities
{
    /// <summary>
    /// Delete a file in a safe way, if the file is in use, rename it instead
    /// </summary>
    /// <param name="path"></param>
    internal static void DeleteSafe(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            if (ex is UnauthorizedAccessException || ex is IOException)
            {
                // what's more beatiful than nested exceptions...
                try
                {
                    string orignalFileName = Path.GetFileName(path);
                    string randomPath =
                        Path.Combine(Path.GetDirectoryName(path),
                                     orignalFileName + "_deleted_" + Path.GetRandomFileName());
                    File.Move(path, randomPath);
                }
                catch (Exception ex2)
                {
                    CodeiumVSPackage.Instance?.Log(
                        $"Failed to move the file why trying to delete it, exception: {ex2}");
                    VS.MessageBox.ShowError(
                        $"Codeium: Failed to move the file why trying to delete it: {path}",
                        "Please see the output windows for more details");
                }
            }
            else
            {
                CodeiumVSPackage.Instance?.Log($"Failed to delete file, exception: {ex}");
                VS.MessageBox.ShowError($"Codeium: Failed to delete file: {path}",
                                        "Please see the output windows for more details");
            }
        }
    }

    /// <summary>
    /// Finds the minimum set of directories that encompass all the given files.
    /// For example, given ["E:/a/b/c.txt", "E:/a/b/d/e.cpp"], returns ["E:/a/b"]
    /// </summary>
    /// <param name="filePaths">List of absolute file paths</param>
    /// <returns>List of directory paths that collectively contain all input files with minimum redundancy</returns>
    internal static List<string> FindMinimumEncompassingDirectories(IEnumerable<string> filePaths)
    {
        if (filePaths == null || !filePaths.Any())
            return new List<string>();

        // Get all parent directories for each file
        var allPaths = filePaths.Select(path => 
        {
            var parents = new List<string>();
            var dir = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(dir))
            {
                parents.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
            return parents;
        }).ToList();

        // Find directories that contain files
        var directoryCounts = new Dictionary<string, HashSet<int>>();
        for (int i = 0; i < allPaths.Count; i++)
        {
            foreach (var dir in allPaths[i])
            {
                if (!directoryCounts.ContainsKey(dir))
                    directoryCounts[dir] = new HashSet<int>();
                directoryCounts[dir].Add(i);
            }
        }

        var result = new List<string>();
        var coveredFiles = new HashSet<int>();
        
        // While we haven't covered all files
        while (coveredFiles.Count < allPaths.Count)
        {
            // Find directory that covers most uncovered files
            var bestDir = directoryCounts
                .Where(kvp => kvp.Value.Except(coveredFiles).Any())
                .OrderByDescending(kvp => kvp.Value.Except(coveredFiles).Count())
                .ThenBy(kvp => kvp.Key.Count(c => c == Path.DirectorySeparatorChar)) // Prefer deeper directories
                .FirstOrDefault();

            if (bestDir.Key == null)
                break;

            result.Add(bestDir.Key);
            coveredFiles.UnionWith(bestDir.Value);
        }

        // Filter out paths that are too shallow (less than 3 levels deep)
        return result.Where(dir => dir.Count(c => c == Path.DirectorySeparatorChar) >= 2).ToList();
    }
}
