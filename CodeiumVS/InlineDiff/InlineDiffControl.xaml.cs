using System.Windows;
using System.Windows.Controls;

namespace CodeiumVs.InlineDiff;

public partial class InlineDiffControl : UserControl
{
    public Action? OnRejected;
    public Action? OnAccepted;
    internal readonly InlineDiffView _inlineDiffView;

    internal InlineDiffControl(InlineDiffView inlineDiffView)
    {
        InitializeComponent();
        _inlineDiffView = inlineDiffView;

        DiffContent.Children.Insert(0, _inlineDiffView.Viewer.VisualElement);
        _inlineDiffView.LeftView.ViewportWidthChanged += LeftView_ViewportWidthChanged;
    }

    private void LeftView_ViewportWidthChanged(object sender, EventArgs e)
    {
        ButtonColumn1.Width = new GridLength(ContentBorder.Margin.Left + _inlineDiffView.LeftView.ViewportWidth);
    }

    public void SetContentBorderLeftMargin(double pixels)
    {
        ContentBorder.Margin = new Thickness(pixels, 0, 0, 0);
    }

    private void ButtonReject_Click(object sender, RoutedEventArgs e)
    {
        OnRejected?.Invoke();
    }

    private void ButtonAccept_Click(object sender, RoutedEventArgs e)
    {
        OnAccepted?.Invoke();
    }
}