using Microsoft.VisualStudio.Shell.Interop;

namespace CodeiumVS;

public class OutputWindow
{
    private Guid OutputWindowGuid = new("1a9b495d-6b1a-4075-b243-98d0a5663052");
    private readonly IVsOutputWindowPane outputPane;

    public OutputWindow()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (ServiceProvider.GlobalProvider.GetService(typeof(SVsOutputWindow))
                is IVsOutputWindow obj)
        {
            obj.CreatePane(ref OutputWindowGuid, "Codeium", 1, 0);
            obj.GetPane(ref OutputWindowGuid, out outputPane);
        }
    }

    internal void WriteLine(in string v)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        outputPane.OutputStringThreadSafe(v + "\n");
    }
}
