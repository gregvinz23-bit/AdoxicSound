using System.Windows;
using Application = System.Windows.Application;

namespace AdoxicSound;

public partial class EditorWindow : Window
{
    private App App => (App)Application.Current;
    private readonly MainWindow _main;

    public EditorWindow(MainWindow main)
    {
        _main = main;
        Owner = main;
        InitializeComponent();
        Closed += (_, _) => _main.OnEditorClosed();
    }
}
