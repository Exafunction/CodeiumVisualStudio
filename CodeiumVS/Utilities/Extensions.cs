using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System.Collections.Generic;

namespace CodeiumVS.Utilities;

internal abstract class TextViewExtension<ViewType, ExtensionType> where ViewType : class where ExtensionType : class
{
    protected readonly ViewType _hostView;
    protected static readonly Dictionary<ViewType, ExtensionType> _instances = [];

    public TextViewExtension(ViewType hostView)
    {
        _hostView = hostView;
        _instances.Add(_hostView, this as ExtensionType);
    }

    public static ExtensionType? GetInstance(ViewType hostView)
    {
        return _instances.TryGetValue(hostView, out var instance) ? instance : null;
    }
}

internal class FunctionBlock(string fullname, string name, string @params, TextSpan span)
{
    // full name of the function, including namespaces and classes
    public readonly string FullName = fullname;

    // short name of the function
    public readonly string Name = name;
    
    // parameters, not including the braces
    public readonly string Params = @params;

    // span of the function body
    public readonly TextSpan Span = span;
}