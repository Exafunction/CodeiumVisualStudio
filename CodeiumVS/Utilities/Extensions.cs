﻿using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using System.Collections.Generic;
using System.Diagnostics;

namespace CodeiumVS.Utilities;

internal abstract class PropertyOwnerExtension<OwnerType, ExtensionType> : IDisposable
    where OwnerType     : IPropertyOwner
    where ExtensionType : class
{
    protected bool _disposed = false;
    protected readonly OwnerType _owner;

    public PropertyOwnerExtension(OwnerType owner)
    {
        _owner = owner;
        _owner.Properties.AddProperty(typeof(ExtensionType), this as ExtensionType);
    }

    public static ExtensionType? GetInstance(OwnerType owner)
    {
        return owner.Properties.TryGetProperty(typeof(ExtensionType), out ExtensionType instance) ? instance : null;
    }

    public static ExtensionType GetOrCreate(OwnerType owner, Func<ExtensionType> creator)
    {
        return GetInstance(owner) ?? creator();
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _owner.Properties.RemoveProperty(typeof(ExtensionType));
    }
}

internal abstract class TextViewExtension<ViewType, ExtensionType> : PropertyOwnerExtension<ViewType, ExtensionType>
    where ViewType      : ITextView
    where ExtensionType : class
{
    protected ViewType _hostView => _owner;

    public TextViewExtension(ViewType hostView) : base(hostView)
    {
        _hostView.Closed += HostView_Closed;
    }

    private void HostView_Closed(object sender, EventArgs e)
    {
        Dispose();
    }

    public override void Dispose()
    {
        base.Dispose();
        _hostView.Closed -= HostView_Closed;
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

