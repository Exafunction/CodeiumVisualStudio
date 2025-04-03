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
                        $"Windsurf: Failed to move the file why trying to delete it: {path}",
                        "Please see the output windows for more details");
                }
            }
            else
            {
                CodeiumVSPackage.Instance?.Log($"Failed to delete file, exception: {ex}");
                VS.MessageBox.ShowError($"Windsurf: Failed to delete file: {path}",
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
        // Get the directory paths of the file paths
        var directoryPaths = filePaths.Select(Path.GetDirectoryName).Distinct().ToList();
        CodeiumVSPackage.Instance?.Log($"Directories before minimization: {string.Join(", ", directoryPaths)}");
        var result = GetMinimumDirectoryCover(directoryPaths);
        CodeiumVSPackage.Instance?.Log($"Directories after minimization: {string.Join(", ", result)}");
        return result.Where(dir => CountPathSegments(dir) > 1).ToList();
    }


    public static List<string> GetMinimumDirectoryCover(IEnumerable<string> directories)
    {
        // 1. Normalize all paths to full/absolute paths and remove duplicates
        var normalizedDirs = directories
            .Select(d => NormalizePath(d))
            .Distinct()
            .ToList();

        // 2. Sort by ascending number of path segments (shallow first)
        normalizedDirs.Sort((a, b) =>
            CountPathSegments(a).CompareTo(CountPathSegments(b)));

        var coverSet = new List<string>();

        // 3. Greedy selection
        foreach (var dir in normalizedDirs)
        {
            bool isCovered = false;

            // Check if 'dir' is already covered by any directory in coverSet
            foreach (var coverDir in coverSet)
            {
                if (IsSubdirectoryOrSame(coverDir, dir))
                {
                    isCovered = true;
                    break;
                }
            }

            // If not covered, add it to the cover set
            if (!isCovered)
            {
                coverSet.Add(dir);
            }
        }

        return coverSet;
    }

    /// <summary>   
    /// Checks if 'child' is the same or a subdirectory of 'parent'.
    /// </summary>
    private static bool IsSubdirectoryOrSame(string parent, string child)
    {
        // 1. Normalize both directories to their full path (remove extra slashes, etc.).
        string parentFull = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string childFull = Path.GetFullPath(child)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // 2. Append a directory separator at the end of each path to ensure
    //    that "C:\Folder" won’t incorrectly match "C:\Folder2".
    //    e.g. "C:\Folder" -> "C:\Folder\"
    parentFull += Path.DirectorySeparatorChar;
    childFull  += Path.DirectorySeparatorChar;

    // 3. On Windows, paths are case-insensitive. Use OrdinalIgnoreCase
    //    to compare. On non-Windows systems, consider using Ordinal.
    return childFull.StartsWith(parentFull, StringComparison.OrdinalIgnoreCase);
}

    /// <summary>
    /// Normalize a directory path by getting its full path (removing trailing slash, etc).
    /// </summary>
    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Count path segments based on splitting by directory separators.
    /// E.g. "C:\Folder\Sub" -> 3 segments (on Windows).
    /// </summary>
    private static int CountPathSegments(string path)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Count(segment => !string.IsNullOrEmpty(segment));
    }
}
