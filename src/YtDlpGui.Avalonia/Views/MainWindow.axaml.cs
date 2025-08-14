using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using YtDlpGui.AvaloniaApp.ViewModels;

namespace YtDlpGui.AvaloniaApp
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
