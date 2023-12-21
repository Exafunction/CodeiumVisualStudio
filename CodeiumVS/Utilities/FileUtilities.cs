using System.IO;

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
}
