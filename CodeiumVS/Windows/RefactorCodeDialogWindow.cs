using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CodeiumVS;

public partial class RefactorCodeDialogWindow : DialogWindow
{
    private class RefactorData(string text, ImageMoniker image, string? prompt = null, List<Packets.Language>? whiteListLanguages = null)
    {
        public string text = text;
        public string prompt = prompt ?? text;
        public ImageMoniker image = image;
        public List<Packets.Language>? whiteListLanguages = whiteListLanguages;
    };

    private Grid Grid => Content as Grid;
    private ScrollViewer ScrollViewer => Grid.Children[0] as ScrollViewer; 
    private StackPanel Panel => ScrollViewer.Content as StackPanel; 

    private string? Result = null;

    private static RefactorCodeDialogWindow? Instance = null;

    public RefactorCodeDialogWindow()
    {
        InitializeComponent();
        InputPrompt.LostKeyboardFocus += (e, s) =>
        {
            InputPrompt.Focus();
        };

    }

    public static RefactorCodeDialogWindow GetOrCreate()
    {
        return Instance ??= new RefactorCodeDialogWindow();
    }

    public async Task<string?> ShowAndGetPromptAsync(Languages.LangInfo languageInfo, double? x = null, double? y = null)
    {
        NewContext(languageInfo);

        if (x != null) Left = x.Value;
        if (y != null) Top = y.Value;

        await this.ShowDialogAsync();
        return Result;
    }

    private void CloseDialog()
    {
        Visibility = Visibility.Hidden;
        InputPrompt.Text = string.Empty;
    }

    private void NewContext(Languages.LangInfo languageInfo)
    {
        Result = null;

        RefactorData[] CommandPresets =
        [
            new RefactorData("Add comment and docstrings to the code", KnownMonikers.AddComment),
            new RefactorData("Add logging statements so that it can be easily debugged", KnownMonikers.StartLogging),

            new RefactorData("Add type annotations to the code", KnownMonikers.NewType,
                "Add type annotations to this code block, including the function arguments and return type." +
                " Modify the docstring to reflect the types.",
                [Packets.Language.LANGUAGE_CSHARP, Packets.Language.LANGUAGE_TYPESCRIPT, Packets.Language.LANGUAGE_PYTHON]
            ),

            new RefactorData("Clean up this code", KnownMonikers.CleanData,
                "Clean up this code by standardizing variable names, removing debugging statements, " +
                "improving readability, and more. Explain what you did to clean it up in a short and concise way."
            ),

            new RefactorData("Check for bugs and null pointers", KnownMonikers.Spy,
                "Check for bugs such as null pointer references, unhandled exceptions, and more." +
                " If you don't see anything obvious, reply that things look good and that the user" +
                " can reply with a stack trace to get more information."
            ),

            new RefactorData("Implement the code for the TODO comment", KnownMonikers.ImplementInterface),
            new RefactorData("Fix mypy and pylint errors and warnings", KnownMonikers.DocumentWarning, null, [Packets.Language.LANGUAGE_PYTHON]),

            new RefactorData("Make this code strongly typed", KnownMonikers.TypePrivate,
                "Make this code strongly typed, including the function arguments and return type." +
                " Modify the docstring to reflect the types."
            ),

            new RefactorData("Make this faster and more efficient", KnownMonikers.EventPublic),

            new RefactorData("Make this code a functional React component", KnownMonikers.AddComponent, null,
                [Packets.Language.LANGUAGE_TYPESCRIPT, Packets.Language.LANGUAGE_TSX]
            ),
            new RefactorData("Create a Typescript interface to define the component props", KnownMonikers.ImplementInterface, null,
                [Packets.Language.LANGUAGE_TYPESCRIPT, Packets.Language.LANGUAGE_TSX]
            ),

            new RefactorData("Use async / await instead of promises", KnownMonikers.AsynchronousMessage, null,
                [Packets.Language.LANGUAGE_TYPESCRIPT, Packets.Language.LANGUAGE_JAVASCRIPT, Packets.Language.LANGUAGE_TSX]
            ),

            new RefactorData("Verbosely comment this code so that I can understand what's going on", KnownMonikers.CommentCode),
        ];

        // remove old buttons
        // TODO: find a better way to do this, just hide them by language, but that interferes with the search
        if (Panel.Children.Count > 1)
            Panel.Children.RemoveRange(1, Panel.Children.Count - 1);

        foreach (RefactorData data in CommandPresets)
        {
            if (data.whiteListLanguages != null && !data.whiteListLanguages.Contains(languageInfo.Type)) continue;

            TextBlock textBlock = new();
            textBlock.Inlines.Add(new CrispImage()
            {
                Moniker = data.image,
                Margin = new Thickness(0, 0, 3, -3)
            });

            textBlock.Inlines.Add(data.text);

            Button btn = new() { Content = textBlock };
            btn.Click += (s, e) => { ReturnResult(data.prompt); };

            Panel.Children.Add(btn);
        }
    }

    private void ReturnResult(string result)
    {
        Result = result;
        CloseDialog();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        // set dark title bar
        bool value = true;
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        DwmSetWindowAttribute(hwnd, 20, ref value, System.Runtime.InteropServices.Marshal.SizeOf(value));
    }

    protected override void OnDeactivated(EventArgs e)
    {
        CloseDialog();
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref bool attrValue, int attrSize);
    
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }
    
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            CloseDialog();
    }

    private void InputPrompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (InputPrompt.Text.Length == 0)
        {
            foreach (var child in Panel.Children)
            {
                if (child is Button btn && btn.Content is TextBlock)
                {
                    btn.Visibility = Visibility.Visible;
                }
            }
            InputPromptHint.Visibility = Visibility.Visible;
            return;
        }

        InputPromptHint.Visibility = Visibility.Collapsed;
        string search = InputPrompt.Text.ToLower();

        foreach (var child in Panel.Children)
        {
            if (child is Button btn && btn.Content is TextBlock textBlock)
            {
                var el = textBlock.Inlines.ElementAt(1) as System.Windows.Documents.Run;

                btn.Visibility = el.Text.ToLower().Contains(search) ?
                    Visibility.Visible : Visibility.Collapsed;
            }
        }

    }

    private void InputPrompt_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Return)
        {
            ReturnResult(InputPrompt.Text);
        }
    }
}
