using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System.Collections.Generic;

namespace CodeiumVS.Utilities;

internal abstract class TextViewExtension<T> where T : class
{
    protected ITextView _hostView { get; set; }

    private static readonly Dictionary<ITextView, T> Instances = [];

    public TextViewExtension(ITextView hostView)
    {
        _hostView = hostView;
        Instances.Add(_hostView, this as T);
    }

    public static T? GetInstance(ITextView hostView)
    {
        return Instances.TryGetValue(hostView, out var instance) ? instance : null;
    }
}

internal class FunctionBlock(string name, string @params, TextSpan span)
{
    public readonly string Name = name;
    public readonly string Params = @params;
    public readonly TextSpan Span = span;
}