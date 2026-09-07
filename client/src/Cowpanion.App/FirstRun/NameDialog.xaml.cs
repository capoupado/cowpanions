using System.Windows;
using Cowpanion.Core.Configuration;

namespace Cowpanion.App.FirstRun;

/// <summary>
/// The only focusable window in the app, shown once on first run when displayName is empty (DECISIONS.md).
/// Cancel keeps the prefilled default.
/// </summary>
public partial class NameDialog : Window
{
    public NameDialog(string defaultName)
    {
        InitializeComponent();
        NameBox.Text = defaultName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
        ChosenName = defaultName;
    }

    public string ChosenName { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string name = ConfigValidator.SanitizeDisplayName(NameBox.Text);
        if (name.Length > 0)
        {
            ChosenName = name;
        }
        DialogResult = true;
        Close();
    }
}
