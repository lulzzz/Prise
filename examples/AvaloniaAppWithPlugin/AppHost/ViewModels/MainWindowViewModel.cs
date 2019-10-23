using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Contract;
using Prise.Infrastructure;
using Shared;

namespace AppHost.ViewModels
{
    public class MainWindowViewModel : Shared.ViewModelBase
    {
        public ICommand LoadAllComponentsCommand { get; set; }
        public ICommand LoadComponentCommand { get; set; }
        public List<string> Components { get; set; }
        public UserControl CurrentControl { get; set; }

        private Dictionary<string, IAppComponent> components = new Dictionary<string, IAppComponent>();

        public MainWindowViewModel()
        {
            LoadAllComponentsCommand = new RelayCommand<object>(LoadComponents, true, true);
            LoadComponentCommand = new RelayCommand<object>(LoadComponent, true, false);
        }

        async void LoadComponents(object parameter)
        {
            var pluginLoader = AppServiceLocator.GetService<IPluginLoader<IAppComponent>>();
            var plugins = await pluginLoader.LoadAll();
            foreach (var plugin in plugins)
            {
                components.Add(plugin.GetName(), plugin);
            }
            Components = new List<string>(components.Select(p => p.Key));
            this.RaisePropertyChanged(nameof(Components));
        }

        void LoadComponent(object parameter)
        {
            var plugin = this.components[parameter.ToString()];
            CurrentControl = plugin.Load();
            this.RaisePropertyChanged(nameof(CurrentControl));
        }
    }
}