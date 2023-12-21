using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CodeiumVs.InlineDiff;

public partial class InlineDiffControl : UserControl
{
    private bool _areButtonsOnTop = true;

    public Action? OnRejected;
    public Action? OnAccepted;
    internal readonly InlineDiffView _inlineDiffView;

    public double ButtonsGridHeight => ButtonsGrid.ActualHeight;
    public double TopOffset => _areButtonsOnTop ? ButtonsGridHeight : 0;

    public bool AreButtonsOnTop
    {
        get => _areButtonsOnTop;
        set {
            if (_areButtonsOnTop == value) return;

            MainStackPanel.Children.Clear();

            if (_areButtonsOnTop = value)
            {
                MainStackPanel.Children.Add(ButtonsGrid);
                MainStackPanel.Children.Add(DiffContent);
            }
            else
            {
                MainStackPanel.Children.Add(DiffContent);
                MainStackPanel.Children.Add(ButtonsGrid);
            }
        }
    }

    internal InlineDiffControl(InlineDiffView inlineDiffView)
    {
        InitializeComponent();
        _inlineDiffView = inlineDiffView;

        DiffContent.Children.Insert(0, _inlineDiffView.Viewer.VisualElement);
        _inlineDiffView.LeftView.ViewportWidthChanged += LeftView_ViewportWidthChanged;
    }

    public void SetContentBorderLeftMargin(double pixels)
    {
        ContentBorder.Margin = new Thickness(pixels, 0, 0, 0);
    }

    private void LeftView_ViewportWidthChanged(object sender, EventArgs e)
    {
        ButtonColumn1.Width =
            new GridLength(ContentBorder.Margin.Left + _inlineDiffView.LeftView.ViewportWidth);
    }

    private void ButtonReject_Click(object sender, RoutedEventArgs e) { OnRejected?.Invoke(); }

    private void ButtonAccept_Click(object sender, RoutedEventArgs e) { OnAccepted?.Invoke(); }

    private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) OnRejected?.Invoke();
    }
}
