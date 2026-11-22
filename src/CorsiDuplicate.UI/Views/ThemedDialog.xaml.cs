using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CorsiDuplicate.UI.Views;

public enum DialogKind
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Message / confirmation box in the app's own look, replacing the stock Windows MessageBox.
/// Use <see cref="Show"/> for a single OK button and <see cref="Confirm"/> for Yes/No.
/// </summary>
public partial class ThemedDialog : Window
{
    private ThemedDialog(string title, string message, DialogKind kind, string primary, string? secondary)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primary;
        if (secondary is null)
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SecondaryButton.Content = secondary;
            SecondaryButton.IsCancel = true;
        }

        Dot.Fill = new SolidColorBrush(kind switch
        {
            DialogKind.Warning => (Color)ColorConverter.ConvertFromString("#E8B34B"),
            DialogKind.Error => (Color)ColorConverter.ConvertFromString("#EA6F64"),
            _ => (Color)ColorConverter.ConvertFromString("#3FCA80"),
        });

        if (secondary is null)
        {
            PrimaryButton.IsCancel = true;
        }

        // Borderless window: allow dragging by the card.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Button)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        };
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Secondary_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static Window? ActiveOwner() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w is not ThemedDialog)
        ?? Application.Current?.MainWindow;

    private static bool ShowCore(string title, string message, DialogKind kind, string primary, string? secondary)
    {
        var dialog = new ThemedDialog(title, message, kind, primary, secondary);
        var owner = ActiveOwner();
        if (owner is { IsLoaded: true } && owner.WindowState != WindowState.Minimized)
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true;
    }

    /// <summary>Informational / error notice with a single OK button.</summary>
    public static void Show(string title, string message, DialogKind kind = DialogKind.Info) =>
        ShowCore(title, message, kind, "OK", null);

    /// <summary>Yes/No question; returns true on Yes.</summary>
    public static bool Confirm(string title, string message, DialogKind kind = DialogKind.Warning,
        string yes = "Sim", string no = "Não") =>
        ShowCore(title, message, kind, yes, no);
}
